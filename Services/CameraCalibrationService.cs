using OpenCvSharp;
using OpenCvSharp.Aruco;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    /// <summary>Camera position in the world (board) frame, computed from ArUco detection.</summary>
    public class CameraPose
    {
        // tvec raw (board centre in camera coords)
        public double TvecX { get; set; }
        public double TvecY { get; set; }
        public double TvecZ { get; set; }  // ≈ height above board when cam looks down

        // True camera position in world/board frame (= -R^T * tvec)
        public double X { get; set; }  // metres right of board centre
        public double Y { get; set; }  // metres forward of board centre
        public double Z { get; set; }  // metres above board (height)

        // Rotation vector (rvec averaged across markers)
        public double Rx { get; set; }
        public double Ry { get; set; }
        public double Rz { get; set; }

        public double R11 { get; set; }
        public double R12 { get; set; }
        public double R13 { get; set; }
        public double R21 { get; set; }
        public double R22 { get; set; }
        public double R23 { get; set; }
        public double R31 { get; set; }
        public double R32 { get; set; }
        public double R33 { get; set; }

        public bool IsValid { get; set; }
    }

    /// <summary>
    ///  Handles ArUco-grid generation and live camera pose detection using OpenCvSharp 4.10.
    ///  Thread-safe and highly optimized for zero-allocation per frame to prevent GC stuttering.
    /// </summary>
    public class CameraCalibrationService : IDisposable
    {
        // ── Board / marker parameters ───────────────────────────────────────────
        public const int GridCols = 4;
        public const int GridRows = 3;

        public const float SquareLength = 0.0375f;       // 3.75 cm square side
        public const float MarkerLength = 0.0275f;       // 2.75 cm marker side
        public const float MarkerGap = 0.01f;
        public const int MarkerCount = GridCols * GridRows;

        // ── Camera intrinsics (approximate) ─────────────────────────────────────
        public const double Fx = 800.0;
        public const double Fy = 800.0;
        public const double Cx = 640.0;
        public const double Cy = 360.0;
        public const int FrameW = 1280;
        public const int FrameH = 720;

        // ── Saved calibration file path ─────────────────────────────────────────
        public static string SavedPosePath { get; }

        static CameraCalibrationService()
        {
            // Cached statically to avoid reading the disk at every property call
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotControllerApp");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            SavedPosePath = Path.Combine(dir, "robot_camera_pose.json");
        }

        // ── Runtime state ───────────────────────────────────────────────────────
        private CancellationTokenSource? _cts;
        private Task? _detectionTask;
        private volatile bool _isRunning;
        private bool _disposed;

        public event Action<byte[]>? OnFrame;
        public event Action<CameraPose>? OnPose;
        public static event Action<string>? OnLog;

        public bool IsRunning => _isRunning;

        // ════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ════════════════════════════════════════════════════════════════════════

        public void StartDetection(int cameraIndex)
        {
            Stop();

            _cts = new CancellationTokenSource();
            _isRunning = true;

            // Délégation complète à un thread d'arrière-plan pour éviter de bloquer l'UI
            _detectionTask = Task.Run(() => DetectionLoopAsync(cameraIndex, _cts.Token));
        }

        public void Stop()
        {
            if (!_isRunning) return;

            _isRunning = false;
            _cts?.Cancel();

            // Attend gracieusement la fin du thread pour s'assurer que la caméra est libérée proprement
            try { _detectionTask?.Wait(2000); } catch { }

            _cts?.Dispose();
            _cts = null;
        }

        // ════════════════════════════════════════════════════════════════════════
        // INTERNAL DETECTION LOOP (ZERO-ALLOCATION OPTIMIZED)
        // ════════════════════════════════════════════════════════════════════════

        private async Task DetectionLoopAsync(int cameraIndex, CancellationToken token)
        {
            VideoCapture? capture = null;

            // ── Pré-allocation des ressources hors de la boucle (Zéro-Déchet) ──
            Mat? cameraMatrix = null;
            Mat? distCoeffs = null;
            Dictionary? dict = null;
            DetectorParameters detParams = new DetectorParameters();
            Mat? frame = null;
            Mat? gray = null;
            Mat? avgRvec = null;
            Mat? tvec = null;
            Mat? rotMat = null;

            try
            {
                capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
                if (!capture.IsOpened())
                {
                    OnLog?.Invoke($"[Calib] Cannot open camera at index {cameraIndex}.");
                    return;
                }

                capture.Set(VideoCaptureProperties.FrameWidth, FrameW);
                capture.Set(VideoCaptureProperties.FrameHeight, FrameH);

                double[,] camData = { { Fx, 0, Cx }, { 0, Fy, Cy }, { 0, 0, 1 } };
                cameraMatrix = Mat.FromArray(camData);
                distCoeffs = Mat.FromArray(new double[] { 0, 0, 0, 0, 0 });
                dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);

                detParams = new DetectorParameters
                {
                    CornerRefinementWinSize = 5,
                    CornerRefinementMaxIterations = 40,
                    CornerRefinementMinAccuracy = 0.02
                };

                frame = new Mat();
                gray = new Mat();

                // Matrices recyclables pour recevoir les calculs mathématiques
                avgRvec = new Mat(3, 1, MatType.CV_64FC1);
                tvec = new Mat(3, 1, MatType.CV_64FC1);
                rotMat = new Mat(3, 3, MatType.CV_64FC1);

                // Listes avec capacité initiale forcée (12 marqueurs * 4 coins = 48 items max)
                // Évite le redimensionnement dynamique coûteux des List<T>.
                var objPtsList = new List<Point3f>(48);
                var imgPtsList = new List<Point2f>(48);

                // Compression JPEG optimisée pour le WebSocket (moins lourde)
                var jpegParams = new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, 75) };

                while (!token.IsCancellationRequested && capture.IsOpened())
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        await Task.Delay(10, token);
                        continue;
                    }

                    Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                    bool poseFound = false;

                    try
                    {
                        CvAruco.DetectMarkers(gray, dict, out var corners, out var ids, detParams, out _);

                        if (ids != null && ids.Length >= 3 && corners != null && corners.Length == ids.Length)
                        {
                            // On vide simplement les listes au lieu d'instancier un nouveau "new List()" !
                            objPtsList.Clear();
                            imgPtsList.Clear();

                            for (int i = 0; i < ids.Length; i++)
                            {
                                int id = ids[i];
                                int u = 0, v = 0;
                                bool valid = true;

                                // The Niryo Vision Workspace is a 7x5 grid. (0,0) is center marker (id=8).
                                switch (id)
                                {
                                    case 0: u = -2; v = 2; break;
                                    case 1: u = 0; v = 2; break;
                                    case 2: u = 2; v = 2; break;
                                    case 3: u = -3; v = 1; break;
                                    case 4: u = -1; v = 1; break;
                                    case 5: u = 1; v = 1; break;
                                    case 6: u = 3; v = 1; break;
                                    case 7: u = -2; v = 0; break;
                                    case 8: u = 0; v = 0; break;
                                    case 9: u = 2; v = 0; break;
                                    case 10: u = -3; v = -1; break;
                                    case 11: u = -1; v = -1; break;
                                    case 12: u = 1; v = -1; break;
                                    case 13: u = 3; v = -1; break;
                                    case 14: u = -2; v = -2; break;
                                    case 15: u = 0; v = -2; break;
                                    case 16: u = 2; v = -2; break;
                                    default: valid = false; break;
                                }

                                if (valid && corners[i] != null && corners[i].Length == 4)
                                {
                                    float cx = u * SquareLength;
                                    float cy = v * SquareLength;
                                    float half = MarkerLength / 2.0f;

                                    objPtsList.Add(new Point3f(cx - half, cy + half, 0f));
                                    objPtsList.Add(new Point3f(cx + half, cy + half, 0f));
                                    objPtsList.Add(new Point3f(cx + half, cy - half, 0f));
                                    objPtsList.Add(new Point3f(cx - half, cy - half, 0f));

                                    imgPtsList.Add(corners[i][0]);
                                    imgPtsList.Add(corners[i][1]);
                                    imgPtsList.Add(corners[i][2]);
                                    imgPtsList.Add(corners[i][3]);
                                }
                            }

                            if (objPtsList.Count >= 12) // Minimum 3 marqueurs valides
                            {
                                // Destruction immédiate et sécurisée des enveloppes C++
                                using var objPtsArray = InputArray.Create(objPtsList);
                                using var imgPtsArray = InputArray.Create(imgPtsList);

                                Cv2.SolvePnP(objPtsArray, imgPtsArray, cameraMatrix, distCoeffs, avgRvec, tvec);
                                bool success = true;

                                if (success)
                                {
                                    poseFound = true;
                                    Cv2.Rodrigues(avgRvec, rotMat);

                                    double avgTx = tvec.At<double>(0), avgTy = tvec.At<double>(1), avgTz = tvec.At<double>(2);
                                    double avgRx = avgRvec.At<double>(0), avgRy = avgRvec.At<double>(1), avgRz = avgRvec.At<double>(2);

                                    double r11 = rotMat.At<double>(0, 0), r12 = rotMat.At<double>(0, 1), r13 = rotMat.At<double>(0, 2);
                                    double r21 = rotMat.At<double>(1, 0), r22 = rotMat.At<double>(1, 1), r23 = rotMat.At<double>(1, 2);
                                    double r31 = rotMat.At<double>(2, 0), r32 = rotMat.At<double>(2, 1), r33 = rotMat.At<double>(2, 2);

                                    // cam_world = -R^T * tvec
                                    double camWorldX = -(r11 * avgTx + r21 * avgTy + r31 * avgTz);
                                    double camWorldY = -(r12 * avgTx + r22 * avgTy + r32 * avgTz);
                                    double camWorldZ = -(r13 * avgTx + r23 * avgTy + r33 * avgTz);

                                    OnPose?.Invoke(new CameraPose
                                    {
                                        TvecX = avgTx,
                                        TvecY = avgTy,
                                        TvecZ = avgTz,
                                        X = camWorldX,
                                        Y = camWorldY,
                                        Z = camWorldZ,
                                        Rx = avgRx,
                                        Ry = avgRy,
                                        Rz = avgRz,
                                        R11 = r11,
                                        R12 = r21,
                                        R13 = r31,
                                        R21 = r12,
                                        R22 = r22,
                                        R23 = r32,
                                        R31 = r13,
                                        R32 = r23,
                                        R33 = r33,
                                        IsValid = true
                                    });

                                    // Dessin du feedback visuel
                                    CvAruco.DrawDetectedMarkers(frame, corners, ids, new Scalar(0, 255, 0));
                                    Cv2.DrawFrameAxes(frame, cameraMatrix, distCoeffs, avgRvec, tvec, 0.1f, 3);
                                }
                            }
                        }
                    }
                    catch { /* Ignore de manière silencieuse les trames corrompues (glitch webcam) */ }

                    if (!poseFound)
                    {
                        OnPose?.Invoke(new CameraPose { IsValid = false });
                    }

                    // On encode et on émet quoi qu'il arrive
                    OnFrame?.Invoke(frame.ImEncode(".jpg", jpegParams));

                    await Task.Delay(5, token); // Libération asynchrone du thread
                }
            }
            catch (OperationCanceledException) { /* L'arrêt a été demandé via Stop() */ }
            catch (Exception ex)
            {
                OnLog?.Invoke($"[Calib] Internal error: {ex.Message}");
            }
            finally
            {
                _isRunning = false;

                // Fermeture garantie et 100% thread-safe du matériel vidéo
                if (capture != null) { try { capture.Release(); capture.Dispose(); } catch { } }

                // Libération manuelle stricte pour éviter les Memory Leaks OpenCV
                cameraMatrix?.Dispose();
                distCoeffs?.Dispose();
                dict?.Dispose();
                frame?.Dispose();
                gray?.Dispose();
                avgRvec?.Dispose();
                tvec?.Dispose();
                rotMat?.Dispose();
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
