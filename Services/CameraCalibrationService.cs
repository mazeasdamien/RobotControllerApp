using OpenCvSharp;
using OpenCvSharp.Aruco;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    /// <summary>Position of the camera relative to the ArUco grid centre.</summary>
    public class CameraPose
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }  // height above board
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

        private const int MarkerPx = 150;         // pixels per marker side in the PNG
        private const int GapPx = 50;          // pixels of gap in the PNG
        private const int BorderPx = 80;          // white border around the grid

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

        /// <summary>
        ///   Generate the ArUco marker grid PNG scaled to fill <paramref name="width"/>×
        ///   <paramref name="height"/> pixels, and write it to <paramref name="outputPath"/>.
        /// </summary>
        public string GenerateGrid(int width, int height, string outputPath)
        {
            using var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);

            // ── Compute natural grid size (un-stretched) ─────────────────────
            int naturalW = BorderPx * 2 + GridCols * MarkerPx + (GridCols - 1) * GapPx;
            int naturalH = BorderPx * 2 + GridRows * MarkerPx + (GridRows - 1) * GapPx;

            // ── Build grid at natural size ───────────────────────────────────
            using var grid = new Mat(naturalH, naturalW, MatType.CV_8UC1, new Scalar(255));

            int markerId = 0;
            using var markerImg = new Mat();
            for (int row = 0; row < GridRows; row++)
            {
                for (int col = 0; col < GridCols; col++)
                {
                    int x = BorderPx + col * (MarkerPx + GapPx);
                    int y = BorderPx + row * (MarkerPx + GapPx);

                    dict.GenerateImageMarker(markerId++, MarkerPx, markerImg);

                    // Copy the marker into the grid image
                    using var roi = new Mat(grid, new Rect(x, y, MarkerPx, MarkerPx));
                    markerImg.CopyTo(roi);
                }
            }

            // ── Resize to target screen resolution ──────────────────────────
            using var scaled = new Mat();
            Cv2.Resize(grid, scaled, new Size(width, height), 0, 0, InterpolationFlags.Linear);
            scaled.ImWrite(outputPath);

            return outputPath;
        }

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

                // ── Send JPEG to UI ──────────────────────────────────────────
                OnFrame?.Invoke(frame.ImEncode(".jpg"));

                // ── Detection ────────────────────────────────────────────────
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

                try
                {
                    CvAruco.DetectMarkers(gray, dict,
                        out var corners, out var ids,
                        detParams, out _);

                    if (ids != null && ids.Length >= 3)
                    {
                        // Estimate pose per marker — MarkerLength is physical side in metres
                        CvAruco.EstimatePoseSingleMarkers(
                            corners, MarkerLength,
                            cameraMatrix, distCoeffs,
                            rvecs, tvecs);

                        // Average Z and centroid X/Y across all visible markers
                        double sumX = 0, sumY = 0, sumZ = 0;
                        int n = ids.Length;

                        for (int i = 0; i < n; i++)
                        {
                            var t = tvecs.At<Vec3d>(0, i);
                            sumX += t.Item0;
                            sumY += t.Item1;
                            sumZ += t.Item2;
                        }

                        OnPose?.Invoke(new CameraPose
                        {
                            X = sumX / n,
                            Y = sumY / n,
                            Z = sumZ / n,
                            IsValid = true
                        });
                        Thread.Sleep(5);
                        continue;
                    }
                }
                catch { /* silently ignore per-frame errors */ }

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
