using OpenCvSharp;
using OpenCvSharp.Aruco;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    /// <summary>Camera position in the world (board) frame, computed from ArUco detection.</summary>
    public class CameraPose
    {
        // tvec raw (board centre in camera coords) — kept for reference
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
    ///   Handles ArUco-grid generation and live camera pose detection using
    ///   OpenCvSharp 4.10 (the version installed wraps only CvAruco — no CharucoBoard).
    ///
    ///   Strategy:
    ///     • Generate a PNG that lays N×M ArUco markers in a regular grid.
    ///     • During live detection, detect those markers and call EstimatePoseSingleMarkers.
    ///     • Average Z (height) across all visible markers; report X/Y from centroid.
    /// </summary>
    public class CameraCalibrationService : IDisposable
    {
        // ── Board / marker parameters ───────────────────────────────────────────
        public const int GridCols = 4;           // columns of markers on the sheet
        public const int GridRows = 3;           // rows of markers on the sheet
        public const float MarkerLength = 0.03f;       // physical marker side  (3 cm)
        public const float MarkerGap = 0.01f;       // physical gap between markers (1 cm)
        public const int MarkerCount = GridCols * GridRows;    // 12 markers (0–11)

        private const int MarkerPx = 150;
        private const int GapPx = 50;
        private const int BorderPx = 80;

        // ── Camera intrinsics (approximate — replace with real calibration) ──
        public const double Fx = 800.0;
        public const double Fy = 800.0;
        public const double Cx = 640.0;
        public const double Cy = 360.0;
        public const int FrameW = 1280;
        public const int FrameH = 720;

        // ── Saved calibration file path (JSON) — written by Save Pose ───────
        public static string SavedPosePath =>
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "robot_camera_pose.json");

        // ── Runtime state ───────────────────────────────────────────────────────
        private VideoCapture? _capture;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        // ── UI callbacks ────────────────────────────────────────────────────────
        /// <summary>Fired for every captured frame — JPEG bytes ready for BitmapImage.</summary>
        public event Action<byte[]>? OnFrame;
        /// <summary>Fired after each detection attempt (valid or not).</summary>
        public event Action<CameraPose>? OnPose;

        // ════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ════════════════════════════════════════════════════════════════════════



        /// <summary>Start the live detection loop on DirectShow camera at <paramref name="cameraIndex"/>.</summary>
        public void StartDetection(int cameraIndex)
        {
            Stop();

            _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
                throw new InvalidOperationException(
                    $"Cannot open camera at index {cameraIndex}.");

            _capture.Set(VideoCaptureProperties.FrameWidth, 1280);
            _capture.Set(VideoCaptureProperties.FrameHeight, 720);

            _cts = new CancellationTokenSource();
            _ = Task.Run(() => DetectionLoop(_cts.Token), _cts.Token);
        }

        /// <summary>Stop detection and release the camera handle.</summary>
        public void Stop()
        {
            _cts?.Cancel();
            if (_capture != null)
            {
                try { _capture.Release(); _capture.Dispose(); } catch { }
                _capture = null;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // INTERNAL DETECTION LOOP
        // ════════════════════════════════════════════════════════════════════════

        private void DetectionLoop(CancellationToken token)
        {
            // Approximate intrinsics — good enough for height measurement.
            // Replace fx/fy with calibrated values for mm-level accuracy.
            double[,] camData = { { 800, 0, 640 }, { 0, 800, 360 }, { 0, 0, 1 } };
            using var cameraMatrix = Mat.FromArray(camData);
            using var distCoeffs = Mat.FromArray(new double[] { 0, 0, 0, 0, 0 });

            using var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
            var detParams = new DetectorParameters();     // no IDisposable in 4.10

            using var frame = new Mat();
            using var gray = new Mat();

            // Output arrays
            using var rvecs = new Mat();
            using var tvecs = new Mat();

            while (!token.IsCancellationRequested && _capture is { } cap && cap.IsOpened())
            {
                if (!cap.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(10);
                    continue;
                }

                // ── Detection ──

                // ── Detection ────────────────────────────────────────────────
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

                try
                {
                    CvAruco.DetectMarkers(gray, dict,
                        out var corners, out var ids,
                        detParams, out _);

                    if (ids != null && ids.Length >= 3)
                    {
                        // Compute the true Board pose (world origin = board top left)
                        // using all detected marker corners simultaneously!
                        var objPtsList = new System.Collections.Generic.List<Point3f>();
                        var imgPtsList = new System.Collections.Generic.List<Point2f>();

                        for (int i = 0; i < ids.Length; i++)
                        {
                            int id = ids[i];
                            int col = id % GridCols;
                            int row = id / GridCols;

                            // X goes right, Y goes DOWN (forward on table, matching the physical printed ArUco board).
                            float cx = (float)((col - (GridCols - 1) / 2.0f) * (MarkerLength + MarkerGap));

                            // FIXED: 'row' is now positive-going so Y goes DOWN.
                            float cy = (float)((row - (GridRows - 1) / 2.0f) * (MarkerLength + MarkerGap));

                            float half = MarkerLength / 2.0f;

                            // Corners: TL, TR, BR, BL 
                            // Because Y now goes down, Top-Left is (-half, -half)
                            objPtsList.Add(new Point3f(cx - half, cy - half, 0f)); // TL
                            objPtsList.Add(new Point3f(cx + half, cy - half, 0f)); // TR
                            objPtsList.Add(new Point3f(cx + half, cy + half, 0f)); // BR
                            objPtsList.Add(new Point3f(cx - half, cy + half, 0f)); // BL

                            imgPtsList.Add(corners[i][0]);
                            imgPtsList.Add(corners[i][1]);
                            imgPtsList.Add(corners[i][2]);
                            imgPtsList.Add(corners[i][3]);
                        }

                        // Convert to standard arrays so OpenCvSharp implicitly converts to InputArray
                        Point3f[] objPts = objPtsList.ToArray();
                        Point2f[] imgPts = imgPtsList.ToArray();

                        using var avgRvec = new Mat(3, 1, MatType.CV_64FC1);
                        using var tvec = new Mat(3, 1, MatType.CV_64FC1);

                        // Solve for the entire board at once -> Rock stable Gizmo & Pose!
                        Cv2.SolvePnP(InputArray.Create(objPts), InputArray.Create(imgPts), cameraMatrix, distCoeffs, avgRvec, tvec, false, SolvePnPFlags.Ippe);

                        double avgTx = tvec.At<double>(0), avgTy = tvec.At<double>(1), avgTz = tvec.At<double>(2);
                        double avgRx = avgRvec.At<double>(0), avgRy = avgRvec.At<double>(1), avgRz = avgRvec.At<double>(2);

                        // Build R from rvec using Rodrigues
                        using var rotMat = new Mat(3, 3, MatType.CV_64FC1);
                        Cv2.Rodrigues(avgRvec, rotMat);

                        // cam_world = -R^T * tvec  (R^T[r,c] = R[c,r])
                        double camWorldX = -(rotMat.At<double>(0, 0) * avgTx + rotMat.At<double>(1, 0) * avgTy + rotMat.At<double>(2, 0) * avgTz);
                        double camWorldY = -(rotMat.At<double>(0, 1) * avgTx + rotMat.At<double>(1, 1) * avgTy + rotMat.At<double>(2, 1) * avgTz);
                        double camWorldZ = -(rotMat.At<double>(0, 2) * avgTx + rotMat.At<double>(1, 2) * avgTy + rotMat.At<double>(2, 2) * avgTz);

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
                            R11 = rotMat.At<double>(0, 0),
                            R12 = rotMat.At<double>(1, 0),
                            R13 = rotMat.At<double>(2, 0),
                            R21 = rotMat.At<double>(0, 1),
                            R22 = rotMat.At<double>(1, 1),
                            R23 = rotMat.At<double>(2, 1),
                            R31 = rotMat.At<double>(0, 2),
                            R32 = rotMat.At<double>(1, 2),
                            R33 = rotMat.At<double>(2, 2),
                            IsValid = true
                        });

                        // Draw visual feedback
                        CvAruco.DrawDetectedMarkers(frame, corners, ids, new Scalar(0, 255, 0));
                        using var drawTvec = Mat.FromArray(new double[] { avgTx, avgTy, avgTz });
                        Cv2.DrawFrameAxes(frame, cameraMatrix, distCoeffs, avgRvec, drawTvec, 0.1f, 3);

                        // Send JPEG to UI with drawings
                        OnFrame?.Invoke(frame.ImEncode(".jpg"));

                        Thread.Sleep(5);
                        continue;
                    }
                }
                catch { /* silently ignore per-frame errors */ }

                // Send JPEG to UI even if no detections
                OnFrame?.Invoke(frame.ImEncode(".jpg"));

                OnPose?.Invoke(new CameraPose { IsValid = false });
                Thread.Sleep(5);
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        public void Dispose()
        {
            if (!_disposed) { Stop(); _disposed = true; }
        }
    }
}
