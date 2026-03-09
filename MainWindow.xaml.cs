#pragma warning disable IDE0060 // Suppress 'Remove unused parameter' for XAML UI Event Handlers
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RobotControllerApp.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using System.Threading;
using System.IO;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace RobotControllerApp
{
    public class DetectedObjectViewModel
    {
        public string Name { get; set; } = "";
        public Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush { get; set; } = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? CroppedImage { get; set; }
        public byte[]? CropJpgBytes { get; set; }
        public bool IsAlreadyInLibrary { get; set; }
        public double PreviewOpacity => IsAlreadyInLibrary ? 0.3 : 1.0;

        // Bounding box in 0-1000 normalised space (from Gemini)
        public double UvYmin { get; set; }
        public double UvXmin { get; set; }
        public double UvYmax { get; set; }
        public double UvXmax { get; set; }
        public double AngleDegrees { get; set; }
    }

    public class GeneratedBananaImageModel
    {
        public string Name { get; set; } = "";
        public Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush { get; set; } = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? ImageSource { get; set; }
    }
    public class LibraryItemConfig
    {
        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = "#FFFFFF";
        public string ImageFileName { get; set; } = "";
        public string ModelFileName { get; set; } = "";
        public string DateAdded { get; set; } = "";
    }

    public class LibraryItemViewModel
    {
        public string Name { get; set; } = "";
        public Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush { get; set; } = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        public string DateAdded { get; set; } = "";
        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? ImageSource { get; set; }
    }

    public sealed partial class MainWindow : Window
    {
        private readonly RelayServerHost _relayServer;
        private readonly RobotBridgeService _robotBridge;   // Robot 1
        private readonly RobotBridgeService _robotBridge2;  // Robot 2
        private readonly AppSettings _settings;

        // Network Performance History
        private readonly List<double> _unityLatencyHistory = [];
        private readonly List<double> _internetLatencyHistory = [];
        private DispatcherTimer? _networkTimer;
        private readonly Ping _pinger = new();
        private bool _isNetworkPinging = false;
        private const int MaxHistory = 300; // 5 minutes (at 1 ping / second)
        private double _latencyMaxMs = 150.0; // Y-axis scale, driven by LatencyScaleSlider

        // Custom Telemetry from Unity Client
        private string _questLocation = "Unknown Location";
        private string _questPublicIp = "";
        private float _questRxKbps = 0f;
        private float _questTxKbps = 0f;

        // Guard flag: prevents Toggled event firing when toggle is updated programmatically
        private bool _updatingToggle = false;

        private byte[]? _latestWebcamFrameBytes;
        private readonly System.Collections.ObjectModel.ObservableCollection<DetectedObjectViewModel> _detectedObjects = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<DetectedObjectViewModel> _selectedForBanana = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<GeneratedBananaImageModel> _bananaImages = new();

        private double _totalGeminiCost = 0.0;
        private double _totalBananaCost = 0.0;

        private System.Collections.ObjectModel.ObservableCollection<LibraryItemViewModel> _libraryItems = new();
        private List<LibraryItemConfig> _libraryConfig = new();

        private bool _isCalibFrozen = false;

        private DispatcherTimer _autoScanTimer;

        private static string LibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotControllerApp", "Library");
        private static string LibraryJsonPath => Path.Combine(LibraryPath, "library.json");

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "Telepresence Control Station";
            this.ExtendsContentIntoTitleBar = true;

            // Customize TitleBar buttons for visibility on dark theme
            if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = this.AppWindow.TitleBar;
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;

                // Set Taskbar and Window Icon
                try
                {
                    this.AppWindow.SetIcon("Assets/AppLogo.png");
                }
                catch { }
            }

            DetectedObjectsList.ItemsSource = _detectedObjects;
            SelectedObjectsList.ItemsSource = _selectedForBanana;
            BananaImagesList.ItemsSource = _bananaImages;
            LibraryList.ItemsSource = _libraryItems;

            _autoScanTimer = new DispatcherTimer();
            _autoScanTimer.Interval = TimeSpan.FromSeconds(7.5);
            _autoScanTimer.Tick += async (s, e) =>
            {
                if (AutoScanToggle.IsChecked != true) return;
                if (GenerationProgress.IsActive) return; // Wait until current scan finishes
                await AnalyzeSceneAsync();
            };

            _ = LoadLibraryAsync();

            // Initialize Services
            _settings = AppSettings.Load();
            _relayServer = new RelayServerHost();
            _robotBridge = new RobotBridgeService() { RobotId = "Robot_Niryo_01", HasCamera = true };
            _robotBridge2 = new RobotBridgeService() { RobotId = "Robot_Niryo_02", HasCamera = false };

            // Initialize Settings UI values
            RelayPortInput.Text = _settings.RelayPort.ToString();
            RobotIpInput.Text = _settings.RobotIp;
            Robot2IpInput.Text = _settings.Robot2Ip;
            OrangeApiKeyInput.Password = _settings.OrangeApiKey;
            GeminiApiKeyInput.Password = _settings.GeminiApiKey;
            TripoApiKeyInput.Password = _settings.TripoApiKey;
            Use3DApiModeToggle.IsOn = _settings.Use3DApiMode;
            UpdateModelModeBadge(_settings.Use3DApiMode);

            // Update Hub Card Status (Initialize as Waiting for Hub to start or Unity to connect)
            RelayActiveText.Text = "WAITING";
            RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
            RelayIcon.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];

            StartNetworkMonitoring();


            // Wire up Logs
            RelayServerHost.OnLog += Log;
            RobotBridgeService.OnLog += Log;
            // Immediate status update on connect/disconnect — no waiting for the 2s poll
            _robotBridge.OnInstanceConnectionChanged += (connected) =>
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateRobotStatus(connected);
                    if (!connected) ClearHardwareInfoBox(R1StatusStr, R1RpiTemp, R1CalibStatus, R1MotorTemp, R1HwErrors);
                });
            _robotBridge2.OnInstanceConnectionChanged += (connected) =>
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateRobot2Status(connected);
                    if (!connected) ClearHardwareInfoBox(R2StatusStr, R2RpiTemp, R2CalibStatus, R2MotorTemp, R2HwErrors);
                });

            // Sync Learning Mode toggles with actual robot state
            // _updatingToggle prevents Toggled event from re-firing when we set IsOn programmatically
            _robotBridge.OnLearningModeChanged += (isOn) => this.DispatcherQueue.TryEnqueue(() =>
            {
                _updatingToggle = true;
                R1LearningToggle.IsOn = isOn;
                _updatingToggle = false;
            });
            _robotBridge2.OnLearningModeChanged += (isOn) => this.DispatcherQueue.TryEnqueue(() =>
            {
                _updatingToggle = true;
                R2LearningToggle.IsOn = isOn;
                _updatingToggle = false;
            });

            // Wire hardware info boxes
            _robotBridge.OnRobotStatusUpdated += (s) => this.DispatcherQueue.TryEnqueue(() =>
                R1StatusStr.Text = s);
            _robotBridge2.OnRobotStatusUpdated += (s) => this.DispatcherQueue.TryEnqueue(() =>
                R2StatusStr.Text = s);

            _robotBridge.OnHardwareStatusUpdated += (hw) => this.DispatcherQueue.TryEnqueue(() =>
                UpdateHardwareInfoBox(hw, R1RpiTemp, R1CalibStatus, R1MotorTemp, R1HwErrors));
            _robotBridge2.OnHardwareStatusUpdated += (hw) => this.DispatcherQueue.TryEnqueue(() =>
                UpdateHardwareInfoBox(hw, R2RpiTemp, R2CalibStatus, R2MotorTemp, R2HwErrors));

            RelayServerHost.OnUnityConnectionChanged += (connected) =>
            {
                this.DispatcherQueue.TryEnqueue(() => UpdateExpertStatus(connected));
            };

            RelayServerHost.OnUnityTelemetryReceived += (loc, rx, tx, pubIp) => this.DispatcherQueue.TryEnqueue(() =>
            {
                _questLocation = loc;
                _questRxKbps = rx;
                _questTxKbps = tx;
                if (!string.IsNullOrEmpty(pubIp)) _questPublicIp = pubIp;
            });

            // Telemetry Subscriptions
            RelayServerHost.OnJointsReceived += (joints) => this.DispatcherQueue.TryEnqueue(() =>
            {
                TelemJoints.Text = "[" + string.Join(", ", System.Linq.Enumerable.Select(joints, j => j.ToString("0.00"))) + "]";
            });

            RelayServerHost.OnImageStatsUpdated += (fps, total) => this.DispatcherQueue.TryEnqueue(() =>
            {
                TelemFps.Text = fps.ToString();
                if (fps < 10) TelemFps.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                else if (fps > 20) TelemFps.Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGreen);
                else TelemFps.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);

                TelemTotalImages.Text = total.ToString();
            });




            // Robot 2 Telemetry
            RelayServerHost.OnRobot2JointsReceived += (joints) => this.DispatcherQueue.TryEnqueue(() =>
            {
                TelemJoints2.Text = "[" + string.Join(", ", System.Linq.Enumerable.Select(joints, j => j.ToString("0.00"))) + "]";
            });

            RelayServerHost.OnRobotStateReceived += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
            });


            DateTime lastUnityMsg = DateTime.MinValue;
            // Rate-limit IK forwarding: max 10 Hz per robot to avoid flooding rosbridge
            DateTime _lastIkSendR1 = DateTime.MinValue;
            DateTime _lastIkSendR2 = DateTime.MinValue;

            RelayServerHost.OnUnityMessageReceived += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
                var now = DateTime.Now;
                lastUnityMsg = now;

                // Only parse IK pos/rot from telemetry messages — skip all other Unity/ROS traffic
                if (!msg.Contains("\"op\":\"unity_telemetry\"")) return;

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(msg);
                    var root = doc.RootElement;

                    // Determine target robot — robotId field routes to the correct IK panel
                    bool isRobot2 = root.TryGetProperty("robotId", out var rid) &&
                                    rid.GetString() == "Robot_Niryo_02";

                    double px = 0, py = 0, pz = 0;
                    double qx = 0, qy = 0, qz = 0, qw = 1;
                    bool hasPos = false, hasRot = false;

                    // Flexible parsing for Position
                    if (root.TryGetProperty("pos", out System.Text.Json.JsonElement pos) || root.TryGetProperty("position", out pos))
                    {
                        if (pos.ValueKind == System.Text.Json.JsonValueKind.Array && pos.GetArrayLength() >= 3)
                        { px = pos[0].GetDouble(); py = pos[1].GetDouble(); pz = pos[2].GetDouble(); hasPos = true; }
                        else if (pos.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (pos.TryGetProperty("x", out var vx)) px = vx.GetDouble();
                            if (pos.TryGetProperty("y", out var vy)) py = vy.GetDouble();
                            if (pos.TryGetProperty("z", out var vz)) pz = vz.GetDouble();
                            hasPos = true;
                        }
                        if (hasPos)
                        {
                            string posText = $"Pos: [{px:0.00}, {py:0.00}, {pz:0.00}]";
                            if (isRobot2) TelemIKPos2.Text = posText;
                            else TelemIKPos.Text = posText;
                        }
                    }

                    // Flexible parsing for Rotation (quaternion)
                    if (root.TryGetProperty("rot", out System.Text.Json.JsonElement rot) || root.TryGetProperty("rotation", out rot))
                    {
                        if (rot.ValueKind == System.Text.Json.JsonValueKind.Array && rot.GetArrayLength() >= 4)
                        { qx = rot[0].GetDouble(); qy = rot[1].GetDouble(); qz = rot[2].GetDouble(); qw = rot[3].GetDouble(); hasRot = true; }
                        else if (rot.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            if (rot.TryGetProperty("x", out var vx)) qx = vx.GetDouble();
                            if (rot.TryGetProperty("y", out var vy)) qy = vy.GetDouble();
                            if (rot.TryGetProperty("z", out var vz)) qz = vz.GetDouble();
                            if (rot.TryGetProperty("w", out var vw)) qw = vw.GetDouble();
                            hasRot = true;
                        }
                        if (hasRot)
                        {
                            string rotText = $"Rot: [{qx:0.00}, {qy:0.00}, {qz:0.00}, {qw:0.00}]";
                            if (isRobot2) TelemIKRot2.Text = rotText;
                            else TelemIKRot.Text = rotText;
                        }
                    }

                    // ── Forward pose to physical robot via rosbridge ──────────────────
                    if (hasPos && hasRot)
                    {
                        // Quaternion → RPY (Euler angles in radians)
                        double sinr_cosp = 2 * (qw * qx + qy * qz);
                        double cosr_cosp = 1 - 2 * (qx * qx + qy * qy);
                        double roll = Math.Atan2(sinr_cosp, cosr_cosp);

                        double sinp = 2 * (qw * qy - qz * qx);
                        double pitch = Math.Abs(sinp) >= 1 ? Math.CopySign(Math.PI / 2, sinp) : Math.Asin(sinp);

                        double siny_cosp = 2 * (qw * qz + qx * qy);
                        double cosy_cosp = 1 - 2 * (qy * qy + qz * qz);
                        double yaw = Math.Atan2(siny_cosp, cosy_cosp);

                        // Rate-limit: skip if last send was < 100ms ago (10 Hz max)
                        bool r1Ready = !isRobot2 && (now - _lastIkSendR1).TotalMilliseconds >= 100;
                        bool r2Ready = isRobot2 && (now - _lastIkSendR2).TotalMilliseconds >= 100;

                        if (r1Ready || r2Ready)
                        {
                            // Build Niryo rosbridge move command
                            // cmd_type 2 = POSE (position + rpy)
                            string cmd = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                op = "call_service",
                                service = "/niryo_robot_arm_commander/robot_move_command",
                                type = "niryo_robot_msgs/RobotMoveCommand",
                                args = new
                                {
                                    cmd_type = 2,       // POSE
                                    position = new { x = px, y = py, z = pz },
                                    rpy = new { roll, pitch, yaw },
                                    dist_smoothing = 0.0
                                }
                            });

                            if (!isRobot2)
                            {
                                _lastIkSendR1 = now;
                                _ = _robotBridge.SendDirectToRobotAsync(cmd);
                            }
                            else
                            {
                                _lastIkSendR2 = now;
                                _ = _robotBridge2.SendDirectToRobotAsync(cmd);
                            }
                        }
                    }
                }
                catch { /* silently ignore malformed telemetry */ }

            });

            RelayServerHost.OnImageReceived += (imageBytes) => this.DispatcherQueue.TryEnqueue(async () =>
            {

                try
                {
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using (var ms = new System.IO.MemoryStream(imageBytes))
                    {
                        await bitmap.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
                    }
                    CameraImage.Source = bitmap;

                    // Transition UI
                    if (CameraImage.Visibility == Visibility.Collapsed)
                    {
                        CameraImage.Visibility = Visibility.Visible;
                        CameraOfflineState.Visibility = Visibility.Collapsed;
                        RobotFeedBadgeText.Text = "LIVE";
                        RobotFeedBadgeText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
                        RobotFeedBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 0, 0)); // Red background for LIVE
                        RobotFeedDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 0, 0)); // Bright Red Dot
                    }
                }
                catch (Exception ex)
                {
                    Log($"[UI] Failed to render camera frame: {ex.Message}");
                }
            });


            this.AppWindow.Closing += AppWindow_Closing;

            // Full Screen but Resizable
            if (this.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;

                var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(this.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    var workArea = displayArea.WorkArea;
                    this.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(workArea.X, workArea.Y, workArea.Width, workArea.Height));
                }
                else
                {
                    presenter.Maximize();
                }
            }

            StartNetworkMonitoring();
            _ = TraceHubLocation(); // Initial async trace
            StatusPulseAnimation.Begin();

            // Initialize Webcam — enumerate cameras and auto-select
            _ = LoadCameraList();
        }

        private async Task LoadLibraryAsync()
        {
            try
            {
                if (!Directory.Exists(LibraryPath))
                    Directory.CreateDirectory(LibraryPath);

                if (File.Exists(LibraryJsonPath))
                {
                    string json = await File.ReadAllTextAsync(LibraryJsonPath);
                    _libraryConfig = System.Text.Json.JsonSerializer.Deserialize<List<LibraryItemConfig>>(json) ?? new List<LibraryItemConfig>();

                    foreach (var item in _libraryConfig)
                    {
                        var vm = new LibraryItemViewModel { Name = item.Name, DateAdded = item.DateAdded };
                        try
                        {
                            if (!string.IsNullOrEmpty(item.ColorHex))
                            {
                                var uiColor = Microsoft.UI.ColorHelper.FromArgb(255,
                                    Convert.ToByte(item.ColorHex.Substring(1, 2), 16),
                                    Convert.ToByte(item.ColorHex.Substring(3, 2), 16),
                                    Convert.ToByte(item.ColorHex.Substring(5, 2), 16));
                                vm.ColorBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(uiColor);
                            }
                        }
                        catch { }

                        string imgPath = Path.Combine(LibraryPath, item.ImageFileName);
                        if (File.Exists(imgPath))
                        {
                            byte[] bytes = await File.ReadAllBytesAsync(imgPath);
                            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                            using var ms = new System.IO.MemoryStream(bytes);
                            await bitmap.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
                            vm.ImageSource = bitmap;
                        }
                        _libraryItems.Add(vm);
                    }
                }
            }
            catch { }
        }

        private void SaveLibrary()
        {
            try
            {
                if (!Directory.Exists(LibraryPath))
                    Directory.CreateDirectory(LibraryPath);

                string json = System.Text.Json.JsonSerializer.Serialize(_libraryConfig);
                File.WriteAllText(LibraryJsonPath, json);
            }
            catch { }
        }

        private OpenCvSharp.VideoCapture? _cvCapture;
        private CancellationTokenSource? _cvCaptureCts;
        private int _operatorFpsCount = 0;
        private int _operatorFramesTotal = 0;
        private DateTime _operatorLastFpsReset = DateTime.Now;
        private Windows.Devices.Enumeration.DeviceInformationCollection? _videoDevices;

        // ── Camera Calibration ──────────────────────────────────────────────────
        private readonly RobotControllerApp.Services.CameraCalibrationService _calibService = new();
        private RobotControllerApp.Services.CameraPose? _lastValidPose;

        /// <summary>Start the selected camera safely using OpenCvSharp (DirectShow).</summary>
        private async Task StartCameraByIndex(int index)
        {
            if (_videoDevices == null || index < 0 || index >= _videoDevices.Count) return;

            // ── Cleanup existing session safely ─────────────────────────────────
            _cvCaptureCts?.Cancel();

            if (_cvCapture != null)
            {
                try
                {
                    _cvCapture.Release();
                    _cvCapture.Dispose();
                }
                catch { }
                _cvCapture = null;
            }

            LocalWebcamPreview.Source = null;
            OperatorFeedBadgeText.Text = "OFFLINE";
            OperatorFeedBadgeText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
            OperatorFeedBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 51));
            OperatorFeedDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 136, 136, 136));
            TelemOperatorFps.Text = "0.0";
            _operatorFpsCount = 0;

            // ── Initialize new capture session ──────────────────────────────────
            try
            {
                var selected = _videoDevices[index];

                // DirectShow natively parses webcams (including MJPG Creative streams) without 
                // WinUI 3 pipeline crashing. Index mapping matches DeviceInformation array.
                _cvCapture = new OpenCvSharp.VideoCapture(index, OpenCvSharp.VideoCaptureAPIs.DSHOW);

                if (!_cvCapture.IsOpened())
                {
                    Log($"[Webcam] Failed to open stream for '{selected.Name}' (DirectShow)");
                    return;
                }

                // Force reliable fast definition for Operator feed
                _cvCapture.Set(OpenCvSharp.VideoCaptureProperties.FrameWidth, 1280);
                _cvCapture.Set(OpenCvSharp.VideoCaptureProperties.FrameHeight, 720);

                Log($"[Webcam] Streaming: {selected.Name}");

                _cvCaptureCts = new CancellationTokenSource();
                var token = _cvCaptureCts.Token;

                _operatorLastFpsReset = DateTime.Now;

                _ = Task.Run(async () =>
                {
                    using var mat = new OpenCvSharp.Mat();
                    while (!token.IsCancellationRequested && _cvCapture != null && _cvCapture.IsOpened())
                    {
                        if (_cvCapture.Read(mat) && !mat.Empty())
                        {
                            _operatorFramesTotal++;
                            _operatorFpsCount++;
                            int fps = 0;
                            int total = _operatorFramesTotal;
                            bool updateCounters = false;

                            if ((DateTime.Now - _operatorLastFpsReset).TotalSeconds >= 1)
                            {
                                fps = _operatorFpsCount;
                                _operatorFpsCount = 0;
                                _operatorLastFpsReset = DateTime.Now;
                                updateCounters = true;
                            }

                            byte[] frameBytes = mat.ToBytes(".jpg");

                            // Send directly to the Hub's HTTP operator image endpoint cache
                            RelayServerHost.CurrentManager?.UpdateLatestOperatorImage(frameBytes);

                            DispatcherQueue?.TryEnqueue(async () =>
                            {
                                if (token.IsCancellationRequested) return;

                                try
                                {
                                    if (OperatorFeedBadgeText.Text != "LIVE")
                                    {
                                        OperatorFeedBadgeText.Text = "LIVE";
                                        OperatorFeedBadgeText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
                                        OperatorFeedBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 0, 0)); // Red background
                                        OperatorFeedDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 0, 0)); // Bright Red Dot
                                    }

                                    if (updateCounters)
                                    {
                                        TelemOperatorFps.Text = fps.ToString("0.0");
                                        TelemOperatorTotalImages.Text = total.ToString();
                                    }

                                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();

                                    if (DashboardView.Visibility == Visibility.Visible || ContextView.Visibility == Visibility.Visible)
                                    {
                                        using (var ms = new System.IO.MemoryStream(frameBytes))
                                        {
                                            await bitmap.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
                                        }
                                        if (DashboardView.Visibility == Visibility.Visible) LocalWebcamPreview.Source = bitmap;
                                        if (ContextView.Visibility == Visibility.Visible) ContextWebcamPreview.Source = bitmap;
                                    }

                                    _latestWebcamFrameBytes = frameBytes;
                                }
                                catch { }
                            });
                        }

                        try
                        {
                            await Task.Delay(33, token).ConfigureAwait(false); // ~30 fps cap
                        }
                        catch (TaskCanceledException) { break; }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Log($"[Webcam] Failed to start '{(_videoDevices?[index].Name ?? "?")}': {ex.Message}");
                if (_cvCapture != null)
                {
                    try { _cvCapture.Release(); _cvCapture.Dispose(); } catch { }
                    _cvCapture = null;
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>Enumerate cameras and populate the ComboBox.</summary>
        private async Task LoadCameraList()
        {
            try
            {
                _videoDevices = await Windows.Devices.Enumeration.DeviceInformation
                    .FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);

                CameraComboBox.Items.Clear();

                if (_videoDevices.Count == 0)
                {
                    Log("[Webcam] No cameras found.");
                    return;
                }

                foreach (var device in _videoDevices)
                    CameraComboBox.Items.Add(device.Name);

                // Auto-select Creative camera if present, else first available
                int defaultIdx = 0;
                for (int i = 0; i < _videoDevices.Count; i++)
                {
                    if (_videoDevices[i].Name.Contains("Creative", StringComparison.OrdinalIgnoreCase))
                    { defaultIdx = i; break; }
                }

                CameraComboBox.SelectedIndex = defaultIdx;
                Log($"[Webcam] {_videoDevices.Count} camera(s) found. Selected: {_videoDevices[defaultIdx].Name}");
            }
            catch (Exception ex)
            {
                Log($"[Webcam] Failed to enumerate cameras: {ex.Message}");
            }
        }


        private async void CameraComboBox_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
        {
            int idx = CameraComboBox.SelectedIndex;
            if (idx >= 0) await StartCameraByIndex(idx);
        }

        private async void RefreshCamerasButton_Click(object sender, RoutedEventArgs e)
        {
            Log("[Webcam] Refreshing camera list...");
            await LoadCameraList();
        }

        private void UpdateRobotStatus(bool isConnected)
        {
            var successBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var mutedBrush = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            Robot1ActiveText.Text = isConnected ? "ACTIVE" : "WAITING";
            Robot1ActiveText.Foreground = isConnected ? successBrush : mutedBrush;
            Robot1Icon.Foreground = isConnected ? successBrush : mutedBrush;
            if (Robot1StatusIndicator != null) Robot1StatusIndicator.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateRobot2Status(bool isConnected)
        {
            var successBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var mutedBrush = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            Robot2ActiveText.Text = isConnected ? "ACTIVE" : "WAITING";
            Robot2ActiveText.Foreground = isConnected ? successBrush : mutedBrush;
            Robot2Icon.Foreground = isConnected ? successBrush : mutedBrush;
            if (Robot2StatusIndicator != null) Robot2StatusIndicator.Visibility = isConnected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDebugBadge(TextBlock badge, bool connected)
        {
            if (badge == null) return;
            var successBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var mutedBrush = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            badge.Text = connected ? "ACTIVE" : "WAITING";
            badge.Foreground = connected ? successBrush : mutedBrush;
        }

        /// <summary>
        /// Polls the relay ConnectionManager every 2 s and updates robot status cards.
        /// Uses the relay bridge connection (ConnectionManager) as primary truth — a robot is
        /// considered ACTIVE if its bridge WebSocket to the relay hub is open, regardless of
        /// whether the ROS socket to the physical robot is established.
        /// </summary>
        private void StartRelayStatusPoll()
        {
            var pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            pollTimer.Tick += (_, _) =>
            {
                var manager = RelayServerHost.CurrentManager;

                // ACTIVE only if the physical ROS socket to the Pi is open.
                // Relay-only connection (bridge up but Pi down) = NOT active.
                bool r1 = _robotBridge.IsConnected;
                bool r2 = _robotBridge2.IsConnected;

                string r1Text = Robot1ActiveText.Text;
                string r2Text = Robot2ActiveText.Text;

                if (r1 && r1Text != "ACTIVE") UpdateRobotStatus(true);
                else if (!r1 && r1Text == "ACTIVE") UpdateRobotStatus(false);

                if (r2 && r2Text != "ACTIVE") UpdateRobot2Status(true);
                else if (!r2 && r2Text == "ACTIVE") UpdateRobot2Status(false);



                // Update Network topology IP labels
                if (manager != null)
                {
                    R1IpText.Text = _robotBridge.IsConnected ? _settings.RobotIp : (r1 ? $"{_settings.RobotIp} (bridge)" : "Offline");
                    R2IpText.Text = _robotBridge2.IsConnected ? _settings.Robot2Ip : (r2 ? $"{_settings.Robot2Ip} (bridge)" : "Offline");
                }
            };
            pollTimer.Start();
        }

        private void UpdateHardwareInfoBox(
            RobotControllerApp.Services.HardwareInfo hw,
            TextBlock rpiTb, TextBlock calibTb, TextBlock motorTb, TextBlock errTb)
        {
            // RPi temperature
            rpiTb.Text = $"{hw.RpiTemp}°C";
            rpiTb.Foreground = hw.RpiTemp >= 65
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 80, 80))   // red
                : hw.RpiTemp >= 50
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 165, 0)) // orange
                    : (SolidColorBrush)Application.Current.Resources["Brush.Text.Primary"];

            // Calibration state
            if (hw.CalibrationInProgress)
            {
                calibTb.Text = "In progress…";
                calibTb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 210, 121));
            }
            else if (hw.CalibrationNeeded)
            {
                calibTb.Text = "Required";
                calibTb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 80, 80));
            }
            else
            {
                calibTb.Text = "OK";
                calibTb.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 80, 200, 120));
            }

            // Max motor temperature
            motorTb.Text = $"{hw.MaxMotorTemp}°C";
            motorTb.Foreground = hw.MaxMotorTemp >= 60
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 80, 80))
                : hw.MaxMotorTemp >= 45
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 165, 0))
                    : (SolidColorBrush)Application.Current.Resources["Brush.Text.Primary"];

            // Errors
            errTb.Text = hw.ErrorCount == 0 ? "None" : hw.ErrorCount.ToString();
            errTb.Foreground = hw.ErrorCount > 0
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 80, 80))
                : (SolidColorBrush)Application.Current.Resources["Brush.Text.Primary"];
        }

        private void ClearHardwareInfoBox(
            TextBlock statusTb, TextBlock rpiTb, TextBlock calibTb, TextBlock motorTb, TextBlock errTb)
        {
            var defaultBrush = (SolidColorBrush)Application.Current.Resources["Brush.Text.Primary"];
            statusTb.Text = "\u2014"; statusTb.Foreground = defaultBrush;
            rpiTb.Text = "\u2014"; rpiTb.Foreground = defaultBrush;
            calibTb.Text = "\u2014"; calibTb.Foreground = defaultBrush;
            motorTb.Text = "\u2014"; motorTb.Foreground = defaultBrush;
            errTb.Text = "\u2014"; errTb.Foreground = defaultBrush;
        }

        private async Task TraceHubLocation()
        {
            try
            {
                using var client = new HttpClient();
                // Get Hub Public IP
                var ipResp = await client.GetStringAsync("https://api.ipify.org");
                string publicIp = ipResp.Trim();

                // Get Location via IP-API
                var locResp = await client.GetStringAsync($"http://ip-api.com/json/{publicIp}");
                using var doc = System.Text.Json.JsonDocument.Parse(locResp);
                var root = doc.RootElement;

                string city = root.TryGetProperty("city", out var c) ? (c.GetString() ?? "Unknown") : "Unknown";
                string country = root.TryGetProperty("country", out var co) ? (co.GetString() ?? "") : "";
                string isp = root.TryGetProperty("isp", out var i) ? (i.GetString() ?? "") : "";

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    HubIpText.Text = publicIp;
                    HubLocText.Text = $"{city}, {country} ({isp})";
                    // Log($"[Trace] Hub located in {city}, {country}");
                });
            }
            catch (Exception ex)
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    HubIpText.Text = "Hub (Local Only)";
                    HubLocText.Text = "Trace failed or offline";
                });
                Log($"[Trace] Hub location trace failed: {ex.Message}");
            }
        }


        private bool _isClosing = false;

        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_isClosing) return;

            // Cancel the immediate close so we can show a prompt and clean up properly
            args.Cancel = true;

            var dialog = new ContentDialog
            {
                Title = "Close Application",
                Content = "Are you sure you want to close the Hub? This will cleanly stop all services and disconnect robots.",
                PrimaryButtonText = "Close Hub",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            ContentDialogResult result = ContentDialogResult.None;
            try
            {
                result = await dialog.ShowAsync();
            }
            catch
            {
                // If a ContentDialog is already open (e.g. an error message), ShowAsync will throw.
                // In this case, we just assume the user wants to force close.
                result = ContentDialogResult.Primary;
            }

            if (result == ContentDialogResult.Primary)
            {
                _isClosing = true;
                Log("Stopping services...");

                try
                {
                    _cvCaptureCts?.Cancel();
                    if (_cvCapture != null)
                    {
                        try { _cvCapture.Release(); _cvCapture.Dispose(); } catch { }
                        _cvCapture = null;
                    }

                    if (_robotBridge != null) await _robotBridge.StopAsync();
                    if (_robotBridge2 != null) await _robotBridge2.StopAsync();
                    if (_relayServer != null) await _relayServer.StopAsync();
                }
                catch { }

                // Full application exit
                Application.Current.Exit();
                Environment.Exit(0);
            }
        }

        private async void NavView_Loaded(object _, RoutedEventArgs __)
        {
            // Initial Selection
            if (NavView.MenuItems.Count > 0)
                NavView.SelectedItem = NavView.MenuItems[0];

            // Auto Connect Sequence
            await StartSystem();
        }

        private async Task StartSystem()
        {
            Log("🚀 Initializing Expert Telepresence Hub...");

            // Step 1: Start Relay Server (Background)
            await Task.Delay(500);
            Log($"Starting Hub Relay Server (Port {_settings.RelayPort})...");

            _relayServer.Port = _settings.RelayPort;
            _relayServer.PublicUrl = _settings.PublicUrl;

            _ = Task.Run(async () => await _relayServer.StartAsync());

            // (Relay server listening — no status caption element)

            // Step 2: Start Robot Bridges
            await Task.Delay(1000);

            // Robot 1
            Log($"Starting Robot 1 Bridge (Target: {_settings.RobotIp})...");
            _robotBridge.RosIp = SanitizeIp(_settings.RobotIp);
            _robotBridge.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";
            _robotBridge.Start();
            UpdateRobotStatus(false);

            // Robot 2
            Log($"Starting Robot 2 Bridge (Target: {_settings.Robot2Ip})...");
            _robotBridge2.RosIp = SanitizeIp(_settings.Robot2Ip);
            _robotBridge2.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";
            _robotBridge2.Start();
            UpdateRobot2Status(false);

            // Step 3: Start relay status poll (keeps dashboard in sync with actual relay state)
            StartRelayStatusPoll();

            // Step 4: Ready
            Log("System Ready. Waiting for connections...");
        }

        private static string SanitizeIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return ip;
            if (ip.Contains("://"))
            {
                try { return new Uri(ip).Host; } catch { }
            }
            if (ip.Contains(':')) return ip.Split(':')[0];
            return ip.Trim();
        }

        private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("Saving settings and restarting services...");

                // Update Settings Object
                if (int.TryParse(RelayPortInput.Text, out int port))
                {
                    _settings.RelayPort = port;
                    _relayServer.Port = port;
                }

                _settings.RobotIp = RobotIpInput.Text.Trim();
                _settings.Robot2Ip = Robot2IpInput.Text.Trim();
                _settings.OrangeApiKey = OrangeApiKeyInput.Password.Trim();
                _settings.GeminiApiKey = GeminiApiKeyInput.Password.Trim();
                _settings.TripoApiKey = TripoApiKeyInput.Password.Trim();
                _settings.Use3DApiMode = Use3DApiModeToggle.IsOn;
                _settings.Save();
                _robotBridge.RosIp = SanitizeIp(_settings.RobotIp);
                _robotBridge2.RosIp = SanitizeIp(_settings.Robot2Ip);
                _robotBridge.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";
                _robotBridge2.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";

                // Persist to Disk
                _settings.Save();

                // Stop Services safely
                try
                {
                    Log("Stopping services...");
                    await _robotBridge.StopAsync();
                    await _robotBridge2.StopAsync();
                    await _relayServer.StopAsync();
                }
                catch (Exception stopEx)
                {
                    Log($"[Warning] Service stop failed: {stopEx.Message}");
                }

                Log("Services stopped. Re-initializing with new configuration...");

                // Safety delay to ensure OS releases the port
                await Task.Delay(1000);

                // Restart Services
                try
                {
                    _ = Task.Run(async () => await _relayServer.StartAsync());
                    _robotBridge.Start();
                    _robotBridge2.Start();

                    UpdateRobotStatus(false);
                    UpdateRobot2Status(false);

                    var dialog = new ContentDialog
                    {
                        Title = "Settings Saved",
                        Content = "The Expert Telepresence Hub has restarted with your new configuration.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
                catch (Exception startEx)
                {
                    Log($"[Error] Service restart failed: {startEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Critical] Save Settings Error: {ex.Message}");
            }
        }

        // ─── Log state ─────────────────────────────────────────────────────────

        private class LogEntry
        {
            public string Message { get; set; } = string.Empty;
            public int Count { get; set; } = 1;
            public Run? RunNode { get; set; }
            public Paragraph? ParagraphNode { get; set; }
        }
        private List<LogEntry> _recentLogs = new();
        private bool _isUserScrolledUp = false; // True when user has scrolled away from bottom

        private void Log(string message)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                var color = Microsoft.UI.Colors.LightGray;

                if (message.Contains("Error") || message.Contains("Failed") || message.Contains("Critical") || message.Contains("Exception"))
                    color = Microsoft.UI.Colors.Red;
                else if (message.Contains("Warning") || message.Contains("Timeout") || message.Contains("Pending"))
                    color = Microsoft.UI.Colors.Orange;
                else if (message.Contains("Connected") || message.Contains("Success") || message.Contains('✓') || message.Contains("Ready"))
                    color = Microsoft.UI.Colors.LightGreen;
                else if (message.Contains("[Relay]"))
                    color = Microsoft.UI.Colors.Cyan;
                else if (message.Contains("[ROS]"))
                    color = Microsoft.UI.Colors.Magenta;
                else if (message.Contains("[Bridge]"))
                    color = Microsoft.UI.Colors.Yellow;

                // ── Stacking: identical recent messages update in-place ────
                var existing = _recentLogs.FirstOrDefault(l => l.Message == message);

                if (existing != null && existing.RunNode != null && existing.ParagraphNode != null)
                {
                    existing.Count++;
                    string baseText = $"[{DateTime.Now:HH:mm:ss}] {message}";
                    existing.RunNode.Text = $"{baseText}  ×{existing.Count}";

                    // Move it to the bottom so it's visible as the most recent activity
                    if (ConsoleLog.Blocks.Contains(existing.ParagraphNode))
                    {
                        ConsoleLog.Blocks.Remove(existing.ParagraphNode);
                        ConsoleLog.Blocks.Add(existing.ParagraphNode);
                    }

                    // Mark as most recently updated in tracking list
                    _recentLogs.Remove(existing);
                    _recentLogs.Add(existing);
                }
                else
                {
                    var run = new Run()
                    {
                        Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
                        Foreground = new SolidColorBrush(color)
                    };
                    var p = new Paragraph();
                    p.Inlines.Add(run);
                    ConsoleLog.Blocks.Add(p);

                    // Keep buffer size manageable
                    if (ConsoleLog.Blocks.Count > 300) ConsoleLog.Blocks.RemoveAt(0);

                    // Track new entry
                    var newEntry = new LogEntry
                    {
                        Message = message,
                        Count = 1,
                        RunNode = run,
                        ParagraphNode = p
                    };
                    _recentLogs.Add(newEntry);
                    if (_recentLogs.Count > 20) _recentLogs.RemoveAt(0);
                }

                // ── Auto-scroll: only scroll if user is already at the bottom ──
                if (!_isUserScrolledUp)
                {
                    LogScroll.UpdateLayout();
                    LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
                }
            });
        }

        /// <summary>Detected when user scrolls the log manually.</summary>
        private void LogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (LogScroll == null) return;
            // Consider "at bottom" if within 40px of the scrollable end
            double distanceFromBottom = LogScroll.ScrollableHeight - LogScroll.VerticalOffset;
            _isUserScrolledUp = distanceFromBottom > 40;
            ScrollToBottomBtn.Visibility = _isUserScrolledUp ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Jump-to-bottom button click — snaps log to latest entry.</summary>
        private void ScrollToBottomBtn_Click(object sender, RoutedEventArgs e)
        {
            _isUserScrolledUp = false;
            ScrollToBottomBtn.Visibility = Visibility.Collapsed;
            LogScroll.UpdateLayout();
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, false);
        }


        private void NavView_SelectionChanged(NavigationView _, NavigationViewSelectionChangedEventArgs args)
        {
            // Hide all views first
            DashboardView.Visibility = Visibility.Collapsed;
            TelemetryView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            NetworkView.Visibility = Visibility.Collapsed;
            ContextView.Visibility = Visibility.Collapsed;
            Preview3DView.Visibility = Visibility.Collapsed;

            // Show selected view
            if (args.IsSettingsSelected)
            {
                SettingsView.Visibility = Visibility.Visible;
            }
            else if (args.SelectedItem is NavigationViewItem item && item.Tag != null)
            {
                switch (item.Tag.ToString())
                {
                    case "home":
                        DashboardView.Visibility = Visibility.Visible;
                        break;
                    case "telemetry":
                        TelemetryView.Visibility = Visibility.Visible;
                        break;
                    case "settings":
                        SettingsView.Visibility = Visibility.Visible;
                        break;
                    case "network":
                        NetworkView.Visibility = Visibility.Visible;
                        break;
                    case "context":
                        ContextView.Visibility = Visibility.Visible;
                        break;
                    case "preview3d":
                        Preview3DView.Visibility = Visibility.Visible;
                        if (CalibCameraComboBox.Items.Count == 0)
                            PopulateCalibCameraList();
                        var initTask = InitSceneWebViewAsync();
                        break;
                }
            }
        }

        private bool _webViewReady = false;
        private RobotControllerApp.Services.CameraPose? _savedPose;

        private async Task InitSceneWebViewAsync()
        {
            if (_webViewReady)
            {
                await PushObjectsToSceneAsync();
                return;
            }
            try
            {
                await SceneWebView.EnsureCoreWebView2Async();
                SceneWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                SceneWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                string assetsDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
                    "Assets");
                
                SceneWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "app.local", assetsDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                SceneWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "library.local", LibraryPath, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                SceneWebView.Source = new Uri("http://app.local/scene3d.html");
                await Task.Delay(800);
                _webViewReady = true;

                // Load saved camera pose from calibration (fixed camera session)
                string jsonPath = RobotControllerApp.Services.CameraCalibrationService.SavedPosePath;
                if (File.Exists(jsonPath))
                {
                    string poseJson = await Task.Run(() => File.ReadAllText(jsonPath));
                    _savedPose = System.Text.Json.JsonSerializer.Deserialize<RobotControllerApp.Services.CameraPose>(poseJson);

                    poseJson = poseJson.Replace("\\", "\\\\").Replace("'", "\\'");
                    await SceneWebView.ExecuteScriptAsync($"setCameraPose('{poseJson}');");

                    _lastValidPose = _savedPose;
                    _isCalibFrozen = true;
                    FreezeCalibToggle.IsOn = true;
                    FreezeCalibToggle.IsEnabled = true;
                    CalibDetectionIcon.Glyph = "\uE73E";
                    CalibDetectionStatus.Text = "Grid detected — Loaded from saved calibration";
                    CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                    CalibDetectionIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                }

                await PushObjectsToSceneAsync();
            }
            catch (Exception ex)
            {
                Log($"[3D Preview] WebView2 init failed: {ex.Message}");
            }
        }

        private async Task PushObjectsToSceneAsync()
        {
            if (!_webViewReady) return;
            try
            {
                // Camera intrinsics from calibration service
                double fx = RobotControllerApp.Services.CameraCalibrationService.Fx;
                double fy = RobotControllerApp.Services.CameraCalibrationService.Fy;
                double cx = RobotControllerApp.Services.CameraCalibrationService.Cx;
                double cy = RobotControllerApp.Services.CameraCalibrationService.Cy;
                int frameW = RobotControllerApp.Services.CameraCalibrationService.FrameW;
                int frameH = RobotControllerApp.Services.CameraCalibrationService.FrameH;

                // Use stored pose values, or fallback if none
                var pose = _lastValidPose ?? _savedPose;
                double camX = pose?.X ?? 0;
                double camY = pose?.Y ?? 0;
                double camZ = pose?.Z ?? 1.0;

                // Extract Transposed Matrix R^T (if null, fallback to identity)
                double r11 = pose?.R11 ?? 1.0, r12 = pose?.R12 ?? 0.0, r13 = pose?.R13 ?? 0.0;
                double r21 = pose?.R21 ?? 0.0, r22 = pose?.R22 ?? 1.0, r23 = pose?.R23 ?? 0.0;
                double r31 = pose?.R31 ?? 0.0, r32 = pose?.R32 ?? 0.0, r33 = pose?.R33 ?? 1.0;

                double safeCamZ = Math.Max(0.01, Math.Abs(camZ));

                    var items = _detectedObjects.Select(obj =>
                    {
                        double uNorm = (obj.UvXmin + obj.UvXmax) / 2.0 / 1000.0;
                        double vNorm = (obj.UvYmin + obj.UvYmax) / 2.0 / 1000.0;
                        double pixU = uNorm * frameW;
                        double pixV = vNorm * frameH;

                        // 1. Cast a straight optical ray out of the Camera Lens
                        double rayCamX = (pixU - cx) / fx;
                        double rayCamY = (pixV - cy) / fy;
                        double rayCamZ = 1.0;

                        // 2. Rotate that ray into the Real World (Table Space) using R^T
                        double rayObjX = r11 * rayCamX + r12 * rayCamY + r13 * rayCamZ;
                        double rayObjY = r21 * rayCamX + r22 * rayCamY + r23 * rayCamZ;
                        double rayObjZ = r31 * rayCamX + r32 * rayCamY + r33 * rayCamZ;

                        // 3. Find exactly where the rotated ray hits the table surface (Z = 0)
                        double t = 0;
                        if (rayObjZ < -1e-4) // Ensure ray points downwards towards the table
                        {
                            t = -safeCamZ / rayObjZ;
                        }

                        double worldX = camX;
                        double worldY = camY;
                        double sizeW = 0.05, sizeH = 0.05;

                        // 4. Apply the physical intersection distance (t)
                        if (t > 0)
                        {
                            worldX = camX + (t * rayObjX);
                            worldY = camY + (t * rayObjY);

                            // Scale bounding box by the actual physical distance to the table
                            double bboxWNorm = (obj.UvXmax - obj.UvXmin) / 1000.0;
                            double bboxHNorm = (obj.UvYmax - obj.UvYmin) / 1000.0;
                            sizeW = t * bboxWNorm * frameW / fx;
                            sizeH = t * bboxHNorm * frameH / fy;
                        }

                        var libItem = _libraryConfig.FirstOrDefault(x => string.Equals(x.Name, obj.Name, StringComparison.OrdinalIgnoreCase));
                        string modelUrl = libItem != null && !string.IsNullOrEmpty(libItem.ModelFileName) 
                            ? $"http://library.local/{libItem.ModelFileName}" 
                            : "";

                        return new
                        {
                            label = obj.Name,
                            worldX,
                            worldY,
                            sizeW,
                            sizeH,
                            angleRad = obj.AngleDegrees * Math.PI / 180.0,
                            modelUrl
                        };
                    }).ToList();

                string json = System.Text.Json.JsonSerializer.Serialize(items);
                json = json.Replace("\\", "\\\\").Replace("'", "\\'");
                await SceneWebView.ExecuteScriptAsync($"setDetectedObjects('{json}');");

                // Also refresh camera pose overlay
                if (_lastValidPose != null)
                    await PushCameraPoseAsync(_lastValidPose);
            }
            catch (Exception ex)
            {
                Log($"[3D Preview] Push failed: {ex.Message}");
            }
        }

        private async Task PushCameraPoseAsync(RobotControllerApp.Services.CameraPose pose)
        {
            if (!_webViewReady) return;
            try
            {
                var poseObj = new { 
                    pose.X, pose.Y, pose.Z, 
                    pose.Rx, pose.Ry, pose.Rz,
                    pose.R11, pose.R12, pose.R13,
                    pose.R21, pose.R22, pose.R23,
                    pose.R31, pose.R32, pose.R33
                };
                string json = System.Text.Json.JsonSerializer.Serialize(poseObj);
                json = json.Replace("\\", "\\\\").Replace("'", "\\'");
                await SceneWebView.ExecuteScriptAsync($"setCameraPose('{json}');");
            }
            catch { /* WebView not ready yet */ }
        }

        private async void LivePosePusher(RobotControllerApp.Services.CameraPose pose)
        {
            if (pose.IsValid)
            {
                DispatcherQueue?.TryEnqueue(async () =>
                {
                    await PushCameraPoseAsync(pose);
                    if (_detectedObjects.Count > 0)
                    {
                        await PushObjectsToSceneAsync();
                    }
                });
            }
        }

        private void RefreshFeed_Click(object sender, RoutedEventArgs e)
        {
            Log("Refreshing camera feed connection...");
            CameraImage.Visibility = Visibility.Collapsed;
            CameraOfflineState.Visibility = Visibility.Visible;
        }

        // ── DEBUG PANEL ─────────────────────────────────────────────────────────

        private static string BuildLearningModeCommand(bool activate) =>
            // Service confirmed: /niryo_robot/learning_mode/activate
            // Type:  niryo_robot_msgs/SetBool  (custom Niryo type, field is 'value')
            // Note:  std_srvs/SetBool would use 'data', but Niryo uses their own SetBool
            System.Text.Json.JsonSerializer.Serialize(new
            {
                op = "call_service",
                service = "/niryo_robot/learning_mode/activate",
                type = "niryo_robot_msgs/SetBool",
                args = new { value = activate }
            });

        private static string BuildHomeCommand() =>
            // Publish directly to the follow_joint_trajectory controller action goal topic.
            // Confirmed running: /niryo_robot_follow_joint_trajectory_controller/query_state
            System.Text.Json.JsonSerializer.Serialize(new
            {
                op = "publish",
                // /command is the direct joint_trajectory_controller input topic
                // simpler than action goal — no goal_id stamping required
                // confirmed in: rostopic list | grep follow_joint
                topic = "/niryo_robot_follow_joint_trajectory_controller/command",
                type = "trajectory_msgs/JointTrajectory",
                msg = new
                {
                    header = new { seq = 0, stamp = new { secs = 0, nsecs = 0 }, frame_id = "" },
                    joint_names = new[] { "joint_1", "joint_2", "joint_3", "joint_4", "joint_5", "joint_6" },
                    points = new[]
                    {
                        new
                        {
                            positions     = new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
                            velocities    = new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
                            accelerations = Array.Empty<double>(),
                            effort        = Array.Empty<double>(),
                            time_from_start = new { secs = 4, nsecs = 0 }
                        }
                    }
                }
            });

        private async void R1LearningToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingToggle) return; // programmatic update — don't send ROS command
            bool on = R1LearningToggle.IsOn;
            string cmd = BuildLearningModeCommand(on);
            bool ok = await SendDebugCommand(_robotBridge, cmd);
            Log(ok ? $"✅ R1 — Learning mode {(on ? "ON" : "OFF")}"
                   : "❌ R1 — Learning mode: ROS not connected");
        }

        private async void R2LearningToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingToggle) return; // programmatic update — don't send ROS command
            bool on = R2LearningToggle.IsOn;
            string cmd = BuildLearningModeCommand(on);
            bool ok = await SendDebugCommand(_robotBridge2, cmd);
            Log(ok ? $"✅ R2 — Learning mode {(on ? "ON" : "OFF")}"
                   : "❌ R2 — Learning mode: ROS not connected");
        }


        private async void R1HomeButton_Click(object sender, RoutedEventArgs e)
        {
            string cmd = BuildHomeCommand();
            bool ok = await SendDebugCommand(_robotBridge, cmd);
            Log(ok ? "✅ R1 — Moving to home [0 0 0 0 0 0]"
                   : "❌ R1 — Home: ROS not connected");
        }

        private async void R2HomeButton_Click(object sender, RoutedEventArgs e)
        {
            string cmd = BuildHomeCommand();
            bool ok = await SendDebugCommand(_robotBridge2, cmd);
            Log(ok ? "✅ R2 — Moving to home [0 0 0 0 0 0]"
                   : "❌ R2 — Home: ROS not connected");
        }

        private async void R1CalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            // Step 1 — tell the robot calibration is needed (matches Niryo Studio "Request new calibration")
            bool step1 = await SendDebugCommand(_robotBridge, BuildRequestNewCalibration());
            if (!step1) { Log("❌ R1 — Calibration: ROS not connected"); return; }
            Log("⏳ R1 — Calibration requested, waiting for robot to be ready...");

            // Step 2 — wait for the flag to propagate, then launch physical auto-calibration
            await Task.Delay(1500);
            bool step2 = await SendDebugCommand(_robotBridge, BuildCalibrationCommand());
            Log(step2 ? "✅ R1 — Auto-calibration started (robot will move ~2-4 min)"
                       : "❌ R1 — calibrate_motors failed");
        }

        private async void R2CalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            bool step1 = await SendDebugCommand(_robotBridge2, BuildRequestNewCalibration());
            if (!step1) { Log("❌ R2 — Calibration: ROS not connected"); return; }
            Log("⏳ R2 — Calibration requested, waiting for robot to be ready...");

            await Task.Delay(1500);
            bool step2 = await SendDebugCommand(_robotBridge2, BuildCalibrationCommand());
            Log(step2 ? "✅ R2 — Auto-calibration started (robot will move ~2-4 min)"
                       : "❌ R2 — calibrate_motors failed");
        }

        /// <summary>Step 1: sets calibration_needed=True on the robot (mirrors Niryo Studio "Request new calibration" button).</summary>
        private static string BuildRequestNewCalibration() =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                op = "call_service",
                service = "/niryo_robot/joints_interface/request_new_calibration",
                type = "niryo_robot_msgs/SetInt",
                args = new { value = 1 }
            });

        /// <summary>Step 2: triggers physical AUTO calibration movement (mirrors Niryo Studio "Auto Calibration" button).</summary>
        private static string BuildCalibrationCommand() =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                op = "call_service",
                service = "/niryo_robot/joints_interface/calibrate_motors",
                type = "niryo_robot_msgs/SetInt",
                args = new { value = 1 }
            });


        /// <summary>
        /// Sends a command to a robot bridge and returns true if the ROS socket was open.
        /// Logs the raw JSON to the app log for debugging.
        /// </summary>
        private async Task<bool> SendDebugCommand(RobotBridgeService bridge, string json)
        {
            if (!bridge.IsConnected)
            {
                // Log($"[Debug] Command dropped — ROS WebSocket not connected for {bridge.RobotId}. " +
                //     $"Check that rosbridge_server is running on {bridge.RosIp}:{bridge.RosPort}.");
                return false;
            }
            await bridge.SendDirectToRobotAsync(json);
            return true;
        }

        // WhatsApp block removed completely
        private void StartNetworkMonitoring()
        {
            _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _networkTimer.Tick += async (s, e) =>
            {
                if (_isNetworkPinging) return;
                _isNetworkPinging = true;

                try
                {
                    // 1. Measure expert latency (Unity Client)
                    double unityLat = 0;
                    string? expertTarget = RelayServerHost.UnityClientIp;
                    // Fallback to static IP from settings if not connected
                    if (string.IsNullOrEmpty(expertTarget)) expertTarget = _settings.ExpertIp;

                    // Ping the actual Quest device IP.
                    // If it's loopback (Unity Editor on same PC) or empty, fall back to
                    // the Cloudflare tunnel so the graph always shows real WAN latency.
                    if (!string.IsNullOrEmpty(expertTarget))
                    {
                        if (expertTarget.StartsWith("::ffff:")) expertTarget = expertTarget[7..];
                    }

                    bool expertIsLoopback = string.IsNullOrEmpty(expertTarget)
                        || expertTarget == "127.0.0.1"
                        || expertTarget == "localhost"
                        || expertTarget == "::1";

                    string pingTarget = expertIsLoopback ? "niryo.dmzs-lab.com" : expertTarget;
                    try
                    {
                        var reply = await _pinger.SendPingAsync(pingTarget, 1000);
                        if (reply.Status == IPStatus.Success) unityLat = reply.RoundtripTime;
                    }
                    catch { }


                    // 2. Measure Robot 1 latency (Ethernet)
                    double r1Lat = 0;
                    if (!string.IsNullOrEmpty(_settings.RobotIp))
                    {
                        try
                        {
                            string host = ExtractIp(_settings.RobotIp);
                            var reply = await _pinger.SendPingAsync(host, 500);
                            if (reply.Status == IPStatus.Success) r1Lat = reply.RoundtripTime;
                        }
                        catch { }
                    }

                    // 3. Measure Robot 2 latency (Ethernet)
                    double r2Lat = 0;
                    if (!string.IsNullOrEmpty(_settings.Robot2Ip))
                    {
                        try
                        {
                            string host = ExtractIp(_settings.Robot2Ip);
                            var reply = await _pinger.SendPingAsync(host, 500);
                            if (reply.Status == IPStatus.Success) r2Lat = reply.RoundtripTime;
                        }
                        catch { }
                    }

                    // Update dashboard and discovery labels before internet ping
                    UpdateDashboardAndDiscovery(unityLat, r1Lat, r2Lat);

                    // 4. Measure Internet Latency (Real-time)
                    double internetLat = 0;
                    try
                    {
                        var reply = await _pinger.SendPingAsync("google.com", 1000);
                        if (reply.Status == IPStatus.Success)
                        {
                            internetLat = reply.RoundtripTime;
                            InternetLatencyText.Text = $"{internetLat} ms";
                        }
                    }
                    catch { }

                    // Update histories
                    UpdateHistory(_unityLatencyHistory, unityLat, MaxHistory);
                    UpdateHistory(_internetLatencyHistory, internetLat, MaxHistory);

                    // Redraw graphs
                    DrawNetworkGraph();

                    UpdateLatencyStats();
                }
                finally
                {
                    _isNetworkPinging = false;
                }
            };
            _networkTimer.Start();

        }

        private void UpdateExpertStatus(bool connected)
        {
            var green = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var muted = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            RelayActiveText.Text = connected ? "ACTIVE" : "WAITING";
            RelayActiveText.Foreground = connected ? green : muted;
            RelayIcon.Foreground = connected ? green : muted;
            if (RelayStatusIndicator != null) RelayStatusIndicator.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDashboardAndDiscovery(double uLat, double r1Lat, double r2Lat)
        {
            // 1. Dashboard updates (Latencies)
            bool isExpertWsConnected = RelayServerHost.UnityClientConnected;
            // Add a 3-second buffer to prevent flickering due to dropped ICMP pings over Wi-Fi
            int uCount = _unityLatencyHistory.Count;
            bool isExpertReachable = uLat > 0 || (uCount > 0 && _unityLatencyHistory.Skip(System.Math.Max(0, uCount - 3)).Any(v => v > 0));
            // Robot connection truth: relay bridge open (ConnectionManager) OR physical ROS socket alive
            // Using same logic as StartRelayStatusPoll to keep Network and Dashboard in sync.
            var cm = RelayServerHost.CurrentManager;
            bool isR1Connected = (cm?.IsRobotConnected("Robot_Niryo_01") ?? false) || _robotBridge.IsConnected;
            bool isR2Connected = (cm?.IsRobotConnected("Robot_Niryo_02") ?? false) || _robotBridge2.IsConnected;

            // ---------- Dashboard Status Cards ----------
            var successBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var mutedBrush = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            var warnBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];

            // Remote Expert only — Robot 1 and Robot 2 are handled exclusively by StartRelayStatusPoll
            // to avoid ICMP ping flapping overriding the stable relay WebSocket state.
            bool expertActive = isExpertWsConnected || isExpertReachable;
            RelayActiveText.Text = expertActive ? "ACTIVE" : "WAITING";
            RelayActiveText.Foreground = expertActive ? successBrush : mutedBrush;
            RelayIcon.Foreground = expertActive ? successBrush : mutedBrush;
            if (RelayStatusIndicator != null) RelayStatusIndicator.Visibility = expertActive ? Visibility.Visible : Visibility.Collapsed;

            // 2. Discovery updates
            // Prefer the public IP reported by the Quest itself (from telemetry)
            string? expertDisplayIp = !string.IsNullOrEmpty(_questPublicIp)
                ? _questPublicIp
                : RelayServerHost.UnityClientIp;
            // Strip IPv6-mapped IPv4 prefix (e.g. "::ffff:127.0.0.1" → "127.0.0.1")
            if (!string.IsNullOrEmpty(expertDisplayIp) && expertDisplayIp.StartsWith("::ffff:"))
                expertDisplayIp = expertDisplayIp[7..];
            if (string.IsNullOrEmpty(expertDisplayIp)) expertDisplayIp = _settings.ExpertIp;
            if (string.IsNullOrEmpty(expertDisplayIp)) expertDisplayIp = "--";


            if (!isExpertWsConnected && !isExpertReachable)
            {
                QuestIpText.Text = "Offline";
            }
            else
            {
                QuestIpText.Text = expertDisplayIp;
            }
            R1IpText.Text = isR1Connected ? ExtractIp(_settings.RobotIp) : "Offline";
            R2IpText.Text = isR2Connected ? ExtractIp(_settings.Robot2Ip) : "Offline";

            if (isExpertWsConnected)
            {
                QuestRelayText.Text = "CONNECTED";
                QuestRelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                QuestRelayDot.Fill = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                QuestLocText.Text = (string.IsNullOrEmpty(_questLocation) || _questLocation == "Unknown" || _questLocation == "Unknown Location")
                    ? "--" : _questLocation;
            }
            else if (isExpertReachable)
            {
                QuestRelayText.Text = "REACHABLE";
                QuestRelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
                QuestRelayDot.Fill = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
                QuestLocText.Text = "--";
            }
            else
            {
                QuestRelayText.Text = "OFFLINE";
                QuestRelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
                QuestRelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
                QuestLocText.Text = "--";
            }

            if (isR1Connected)
            {
                R1RelayText.Text = "CONNECTED";
                R1RelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                R1RelayDot.Fill = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            }
            else
            {
                R1RelayText.Text = "OFFLINE";
                R1RelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
                R1RelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
            }

            if (isR2Connected)
            {
                R2RelayText.Text = "CONNECTED";
                R2RelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                R2RelayDot.Fill = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            }
            else
            {
                R2RelayText.Text = "OFFLINE";
                R2RelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
                R2RelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
            }
        }

        private static string ExtractIp(string input)
        {
            if (string.IsNullOrEmpty(input)) return "N/A";
            if (input.StartsWith("ws://"))
            {
                return input[5..].Split(':')[0];
            }
            return input;
        }

        private void UpdateLatencyStats()
        {
            UpdateStatTexts(_unityLatencyHistory, QuestMinText, QuestMaxText, QuestAvgText);
            UpdateStatTexts(_internetLatencyHistory, InternetMinText, InternetMaxText, InternetAvgText);
        }

        private static void UpdateStatTexts(List<double> history, TextBlock minT, TextBlock maxT, TextBlock avgT)
        {
            var valid = history.Where(v => v > 0).ToList();
            if (valid.Count == 0)
            {
                minT.Text = "-- ms";
                maxT.Text = "-- ms";
                avgT.Text = "-- ms";
                return;
            }

            minT.Text = $"{valid.Min():F0} ms";
            maxT.Text = $"{valid.Max():F0} ms";
            avgT.Text = $"{valid.Average():F0} ms";
        }

        private static void UpdateHistory(List<double> history, double val, int max)
        {
            history.Add(val);
            if (history.Count > max) history.RemoveAt(0);
        }

        private void DrawNetworkGraph()
        {
            // Only draw if the view is visible to save resources
            if (NetworkView.Visibility != Visibility.Visible) return;

            UpdatePath(UnityPath, _unityLatencyHistory);
            DrawPeakIndicator(_unityLatencyHistory);
        }

        private void DrawPeakIndicator(List<double> history)
        {
            var valid = history.Where(v => v > 0).ToList();
            if (valid.Count < 2)
            {
                PeakDot.Visibility = Visibility.Collapsed;
                PeakLabel.Visibility = Visibility.Collapsed;
                return;
            }

            double maxVal = valid.Max();
            int peakIdx = history.LastIndexOf(maxVal); // rightmost occurrence

            double width = LatencyCanvas.ActualWidth > 0 ? LatencyCanvas.ActualWidth : 800;
            double height = LatencyCanvas.ActualHeight > 0 ? LatencyCanvas.ActualHeight : 120;
            double stepX = width / (MaxHistory - 1);
            double maxMs = _latencyMaxMs;
            double scaleY = height / maxMs;

            double x = peakIdx * stepX;
            double y = height - (Math.Min(maxVal, maxMs) * scaleY);

            // Centre the 8×8 dot on the data point
            Canvas.SetLeft(PeakDot, x - 4);
            Canvas.SetTop(PeakDot, y - 4);
            PeakDot.Visibility = Visibility.Visible;

            // Place label just above the dot, clamp to canvas left edge
            PeakLabel.Text = $"{maxVal:F0} ms";
            Canvas.SetLeft(PeakLabel, Math.Max(0, x - 16));
            Canvas.SetTop(PeakLabel, Math.Max(0, y - 18));
            PeakLabel.Visibility = Visibility.Visible;
        }


        private void UpdatePath(Microsoft.UI.Xaml.Shapes.Polyline polyline, List<double> history)
        {
            polyline.Points.Clear();
            if (history.Count < 2) return;

            // Use fixed dimensions or actual dimensions if available
            double width = LatencyCanvas.ActualWidth > 0 ? LatencyCanvas.ActualWidth : 800;
            double height = LatencyCanvas.ActualHeight > 0 ? LatencyCanvas.ActualHeight : 120;

            double stepX = width / (MaxHistory - 1);
            double maxHeight = _latencyMaxMs;
            double scaleY = height / maxHeight;

            for (int i = 0; i < history.Count; i++)
            {
                double x = i * stepX;

                // Skip 0 values to naturally interpolate a beautiful continuous curve
                if (history[i] <= 0) continue;

                // Clip value to maxHeight for display
                double val = Math.Min(history[i], maxHeight);
                double y = height - (val * scaleY);
                polyline.Points.Add(new Windows.Foundation.Point(x, y));
            }
        }

        private void LatencyScaleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _latencyMaxMs = e.NewValue;

            // Update readout label
            if (ScaleLabel != null)
                ScaleLabel.Text = $"{_latencyMaxMs:F0}ms";

            // Update Y-axis tick labels at 75%, 50%, 25% of scale
            if (YLabel75 != null) YLabel75.Text = $"{_latencyMaxMs * 0.75:F0}ms";
            if (YLabel50 != null) YLabel50.Text = $"{_latencyMaxMs * 0.50:F0}ms";
            if (YLabel25 != null) YLabel25.Text = $"{_latencyMaxMs * 0.25:F0}ms";
            if (YLabel0 != null) YLabel0.Text = "0ms";

            // Immediately redraw with new scale
            DrawNetworkGraph();
        }

        private Windows.UI.Color ColorFromLabel(string label)
        {
            string l = label.ToLower();
            if (l.Contains("dark blue") || l.Contains("navy")) return Windows.UI.Color.FromArgb(255, 0, 0, 128);
            if (l.Contains("blue")) return Microsoft.UI.Colors.Blue;
            if (l.Contains("red")) return Microsoft.UI.Colors.Red;
            if (l.Contains("lime")) return Microsoft.UI.Colors.Lime;
            if (l.Contains("green")) return Windows.UI.Color.FromArgb(255, 0, 153, 0);
            if (l.Contains("yellow")) return Microsoft.UI.Colors.Yellow;
            if (l.Contains("orange")) return Microsoft.UI.Colors.Orange;
            if (l.Contains("grey") || l.Contains("gray")) return Microsoft.UI.Colors.Gray;
            if (l.Contains("black")) return Windows.UI.Color.FromArgb(255, 51, 51, 51);
            if (l.Contains("pink")) return Microsoft.UI.Colors.Pink;
            if (l.Contains("purple") || l.Contains("magenta")) return Microsoft.UI.Colors.Purple;
            if (l.Contains("brown")) return Microsoft.UI.Colors.Brown;
            return Microsoft.UI.Colors.White;
        }

        private async void GenerateObjectImagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_settings.OrangeApiKey))
            {
                await new ContentDialog() { Title = "No API Key", Content = "Please define the Orange API Key in Settings.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            if (_latestWebcamFrameBytes == null)
            {
                await new ContentDialog() { Title = "No Camera Frame", Content = "Start the webcam first.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            await AnalyzeSceneAsync();
        }

        private async Task AnalyzeSceneAsync()
        {
            if (_latestWebcamFrameBytes == null || _latestWebcamFrameBytes.Length == 0) return;

            GenerateObjectImagesBtn.IsEnabled = false;
            GenerationProgress.Visibility = Visibility.Visible;
            GenerationProgress.IsActive = true;

            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(15);
                string url = "https://llmproxy.ai.orange/v1/chat/completions";

                string base64Image = Convert.ToBase64String(_latestWebcamFrameBytes);

                string prompt =
                    "Identify all distinct physical objects (tools, parts, shapes) visible on the table. " +
                    "IGNORE the chessboard/checkerboard calibration target. IGNORE any robot arms or parts of the robot. " +
                    "For each, return normalised bounding box corners [ymin, xmin, ymax, xmax] in range 0–1000, " +
                    "an estimated physical 'angle_degrees' (from 0 to 360, where 0 means pointing forward/away from the camera, 90 pointing to the right of the table) representing the object spatial orientation, " +
                    "and a label starting with the object's colour. " +
                    "RETURN JSON: { \"items\": [ { \"ymin\": 100, \"xmin\": 100, \"ymax\": 200, \"xmax\": 200, \"angle_degrees\": 45, \"label\": \"blue cube\" } ] }";

                string safePrompt = prompt.Replace("\"", "\\\"").Replace("\n", "\\n");

                string selectedModel = "vertex_ai/gemini-2.0-flash";

                string body =
                    $"{{\"model\": \"{selectedModel}\", \"temperature\": 0.0, " +
                    $"\"response_format\": {{\"type\": \"json_object\"}}, " +
                    $"\"messages\": [{{\"role\": \"user\", \"content\": [" +
                    $"{{\"type\": \"text\", \"text\": \"{safePrompt}\"}}, " +
                    $"{{\"type\": \"image_url\", \"image_url\": {{\"url\": \"data:image/jpeg;base64,{base64Image}\"}}}}]}}]}}";

                var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OrangeApiKey);

                var response = await client.PostAsync(url, content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(responseString);
                    _detectedObjects.Clear();

                    if (doc.RootElement.TryGetProperty("usage", out var usageProp))
                    {
                        double pTokens = usageProp.TryGetProperty("prompt_token_count", out var pt) ? pt.GetDouble() : 0.0;
                        double cTokens = usageProp.TryGetProperty("candidates_token_count", out var ct) ? ct.GetDouble() : 0.0;

                        // Fallback checking standard OpenAI-style properties if Google ones are empty
                        if (pTokens == 0) pTokens = usageProp.TryGetProperty("prompt_tokens", out var pt2) ? pt2.GetDouble() : 0.0;
                        if (cTokens == 0) cTokens = usageProp.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetDouble() : 0.0;

                        // Pricing per 1M tokens based on selected model
                        double priceInX1M = 0;
                        double priceOutX1M = 0;

                        switch (selectedModel)
                        {
                            case "vertex_ai/gemini-2.0-flash":
                                priceInX1M = 0.15; priceOutX1M = 0.60; break;
                            case "vertex_ai/gemini-2.5-flash":
                                priceInX1M = 0.30; priceOutX1M = 2.50; break;
                            case "vertex_ai/gemini-2.5-flash-lite":
                                priceInX1M = 0.10; priceOutX1M = 0.40; break;
                            default:
                                priceInX1M = 0.15; priceOutX1M = 0.60; break;
                        }

                        double costUsd = (pTokens / 1_000_000.0 * priceInX1M) + (cTokens / 1_000_000.0 * priceOutX1M);
                        double costEur = costUsd * 0.94; // Approx USD -> EUR
                        _totalGeminiCost += costEur;
                        GeminiCostText.Text = $"Estimated Gemini Cost: {_totalGeminiCost:0.00000} €";
                    }

                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var messageContent = choices[0].GetProperty("message").GetProperty("content").GetString();
                        if (!string.IsNullOrEmpty(messageContent))
                        {
                            int s = messageContent.IndexOf('{');
                            int eIdx = messageContent.LastIndexOf('}');
                            if (s >= 0 && eIdx > s)
                            {
                                string jsonStr = messageContent.Substring(s, eIdx - s + 1);
                                using var itemsDoc = System.Text.Json.JsonDocument.Parse(jsonStr);
                                if (itemsDoc.RootElement.TryGetProperty("items", out var itemsArray))
                                {
                                    using var sourceMat = OpenCvSharp.Cv2.ImDecode(_latestWebcamFrameBytes, OpenCvSharp.ImreadModes.Color);
                                    int imgW = sourceMat.Width;
                                    int imgH = sourceMat.Height;

                                    foreach (var item in itemsArray.EnumerateArray())
                                    {
                                        string label = item.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? "Unknown" : "Unknown";

                                        int ymin = 0, xmin = 0, ymax = 0, xmax = 0;
                                        if (item.TryGetProperty("box_2d", out var box2d) && box2d.GetArrayLength() >= 4)
                                        {
                                            ymin = box2d[0].GetInt32();
                                            xmin = box2d[1].GetInt32();
                                            ymax = box2d[2].GetInt32();
                                            xmax = box2d[3].GetInt32();
                                        }
                                        else
                                        {
                                            int.TryParse(item.GetProperty("ymin").ToString(), out ymin);
                                            int.TryParse(item.GetProperty("xmin").ToString(), out xmin);
                                            int.TryParse(item.GetProperty("ymax").ToString(), out ymax);
                                            int.TryParse(item.GetProperty("xmax").ToString(), out xmax);
                                        }

                                        double angleDegrees = 0;
                                        if (item.TryGetProperty("angle_degrees", out var angleProp))
                                            angleProp.TryGetDouble(out angleDegrees);

                                        // Ignore objects cut off by the edge of the camera
                                        if (xmin < 10 || ymin < 10 || xmax > 990 || ymax > 990) continue;

                                        int pixelXMin = (int)(xmin * imgW / 1000.0);
                                        int pixelYMin = (int)(ymin * imgH / 1000.0);
                                        int pixelXMax = (int)(xmax * imgW / 1000.0);
                                        int pixelYMax = (int)(ymax * imgH / 1000.0);

                                        int width = pixelXMax - pixelXMin;
                                        int height = pixelYMax - pixelYMin;
                                        int padX = (int)(width * 0.40);
                                        int padY = (int)(height * 0.40);

                                        pixelXMin = Math.Max(0, pixelXMin - padX);
                                        pixelYMin = Math.Max(0, pixelYMin - padY);
                                        pixelXMax = Math.Min(imgW, pixelXMax + padX);
                                        pixelYMax = Math.Min(imgH, pixelYMax + padY);

                                        var rect = new OpenCvSharp.Rect(pixelXMin, pixelYMin, pixelXMax - pixelXMin, pixelYMax - pixelYMin);
                                        rect.Width = Math.Min(imgW - rect.X, rect.Width);
                                        rect.Height = Math.Min(imgH - rect.Y, rect.Height);

                                        if (rect.Width > 0 && rect.Height > 0)
                                        {
                                            using var cropMat = new OpenCvSharp.Mat(sourceMat, rect);
                                            byte[] cropJpgBytes = cropMat.ImEncode(".jpg");

                                            var bitmap = new BitmapImage();
                                            using var ms = new System.IO.MemoryStream(cropJpgBytes);
                                            await bitmap.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));

                                            string objName = label.ToUpper();
                                            bool isAlreadyInLibrary = _libraryConfig.Any(x => x.Name.Equals(objName, StringComparison.OrdinalIgnoreCase));

                                            var uiColor = ColorFromLabel(label);


                                            _detectedObjects.Add(new DetectedObjectViewModel
                                            {
                                                Name = objName,
                                                ColorBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(uiColor),
                                                CroppedImage = bitmap,
                                                CropJpgBytes = cropJpgBytes,
                                                IsAlreadyInLibrary = isAlreadyInLibrary,
                                                UvXmin = xmin,
                                                UvYmin = ymin,
                                                UvXmax = xmax,
                                                UvYmax = ymax,
                                                AngleDegrees = angleDegrees
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (response.IsSuccessStatusCode)
                {
                    // Live-sync to 3D preview if open
                    if (_webViewReady) _ = PushObjectsToSceneAsync();
                }
                else
                {
                    if (AutoScanToggle.IsChecked != true)
                        await new ContentDialog() { Title = "API Error", Content = $"Error from Orange API:\n{responseString}", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                if (AutoScanToggle.IsChecked != true)
                    await new ContentDialog() { Title = "Execution Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
            }
            finally
            {
                if (AutoScanToggle.IsChecked != true)
                {
                    GenerateObjectImagesBtn.IsEnabled = true;
                    GenerateObjectImagesBtn.Content = "Analyze Scene Manually";
                }
                GenerationProgress.IsActive = false;
                GenerationProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void AutoScanToggle_Click(object sender, RoutedEventArgs e)
        {
            if (AutoScanToggle.IsChecked == true)
            {
                AutoScanToggle.Content = "■ Stop Auto-Scan";
                AutoScanToggle.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 150, 0));

                GenerateObjectImagesBtn.IsEnabled = false;
                GenerateObjectImagesBtn.Content = "Auto-Scan Running...";

                _autoScanTimer.Start();
                _ = AnalyzeSceneAsync();
            }
            else
            {
                AutoScanToggle.Content = "Auto-Scan (8x/min)";
                AutoScanToggle.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 51));

                GenerateObjectImagesBtn.IsEnabled = true;
                GenerateObjectImagesBtn.Content = "Analyze Scene Manually";

                _autoScanTimer.Stop();
            }
        }

        private void AddSelectedObject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DetectedObjectViewModel item)
            {
                if (!_selectedForBanana.Any(x => x.Name == item.Name))
                {
                    _selectedForBanana.Add(item);
                    UpdateBananaCost();
                }
            }
        }

        private void RemoveSelectedObject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DetectedObjectViewModel item)
            {
                _selectedForBanana.Remove(item);
                UpdateBananaCost();
            }
        }

        private void UpdateBananaCost()
        {
            double newObjectsCount = _selectedForBanana.Count;
            double costEuro = (newObjectsCount * 0.0672) * 0.94; // approx USD to EUR
            BananaCostText.Text = $"Queue: {costEuro:0.00} € | Total Spent: {_totalBananaCost:0.00} €";
        }

        private void DeleteLibraryObject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LibraryItemViewModel item)
            {
                _libraryItems.Remove(item);
                string name = item.Name;

                var configData = _libraryConfig.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (configData != null)
                {
                    _libraryConfig.Remove(configData);
                    SaveLibrary();

                    try
                    {
                        string imgPath = Path.Combine(LibraryPath, configData.ImageFileName);
                        if (File.Exists(imgPath)) File.Delete(imgPath);

                        // Delete the explicitly linked 3D model if present
                        if (!string.IsNullOrEmpty(configData.ModelFileName))
                        {
                            string linkedGlbPath = Path.Combine(LibraryPath, configData.ModelFileName);
                            if (File.Exists(linkedGlbPath)) File.Delete(linkedGlbPath);
                        }

                        // Fallback: Also delete the associated 3D model if it was generated before the JSON link fix
                        string safeName = string.Join("_", item.Name.Split(Path.GetInvalidFileNameChars()));
                        string glbFileNameFallback = $"{safeName}_3DModel.glb";
                        string glbPath = Path.Combine(LibraryPath, glbFileNameFallback);
                        if (File.Exists(glbPath)) File.Delete(glbPath);
                        
                        // Clean up the incorrectly spaced one just in case they have it from before
                        string oldBrokenGlbPath = Path.Combine(LibraryPath, $"{item.Name.Replace(" ", "_")}_3DModel.glb");
                        if (File.Exists(oldBrokenGlbPath)) File.Delete(oldBrokenGlbPath);
                    }
                    catch { }
                }

                // Re-enable scene detection item if it exists in the current session
                var sceneItems = _detectedObjects.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var sceneItem in sceneItems)
                {
                    sceneItem.IsAlreadyInLibrary = false;
                    var idx = _detectedObjects.IndexOf(sceneItem);
                    if (idx >= 0) _detectedObjects[idx] = sceneItem; // trigger redraw
                }
            }
        }

        private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(LibraryPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{LibraryPath}\"",
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception)
            {
                // Ignore silent failure or log if necessary
            }
        }

        private void Generate3DModel_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LibraryItemViewModel item)
            {
                string safeName = string.Join("_", item.Name.Split(Path.GetInvalidFileNameChars()));
                string glbPath = Path.Combine(LibraryPath, $"{safeName}_3DModel.glb");

                var configData = _libraryConfig.FirstOrDefault(c => string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                
                if (configData != null && !string.IsNullOrEmpty(configData.ModelFileName))
                {
                    glbPath = Path.Combine(LibraryPath, configData.ModelFileName);
                }

                if (File.Exists(glbPath))
                {
                    btn.Content = "Generated";
                    btn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
                    btn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                    btn.IsEnabled = false;
                }
                else
                {
                    btn.Content = "To 3D Model";
                    btn.ClearValue(Button.BackgroundProperty);
                    btn.ClearValue(Button.ForegroundProperty);
                    btn.IsEnabled = true;
                }
            }
        }

        // ── Tripo mode badge helper ───────────────────────────────────────────
        private void UpdateModelModeBadge(bool apiMode)
        {
            if (ModelModeBadge == null || ModelModeBadgeText == null) return;
            if (apiMode)
            {
                ModelModeBadgeText.Text = "CLOUD API";
                ModelModeBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                ModelModeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 204, 106));
            }
            else
            {
                ModelModeBadgeText.Text = "LOCAL";
                ModelModeBadgeText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 85, 153, 255));
                ModelModeBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 64, 128));
            }
        }

        private void Use3DApiModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool isOn = Use3DApiModeToggle.IsOn;
            _settings.Use3DApiMode = isOn;
            _settings.Save();
            UpdateModelModeBadge(isOn);
        }

        private async void Generate3DModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LibraryItemViewModel item)
            {
                var configData = _libraryConfig.FirstOrDefault(x => x.Name == item.Name);
                if (configData == null) return;

                if (_settings.Use3DApiMode)
                    await Generate3DModel_ApiAsync(btn, item, configData);
                else
                    await Generate3DModel_LocalAsync(btn, item, configData);
            }
        }

        // ── LOCAL TripoSR mode ────────────────────────────────────────────────
        private async Task Generate3DModel_LocalAsync(Button btn, LibraryItemViewModel item, LibraryItemConfig configData)
        {
            btn.IsEnabled = false;
            string originalContent = btn.Content?.ToString() ?? "To 3D Model";
            btn.Content = "Starting TripoSR...";

            bool serverWasAlreadyRunning = false;
            System.Diagnostics.Process? tripoProcess = null;
            bool generationSuccess = false;

            try
            {
                // Check if server is already running
                try
                {
                    using var testClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var res = await testClient.GetAsync("http://127.0.0.1:7860/");
                    serverWasAlreadyRunning = true;
                }
                catch { }

                if (!serverWasAlreadyRunning)
                {
                    tripoProcess = new System.Diagnostics.Process();
                    tripoProcess.StartInfo.FileName = @"C:\Users\QYTH4815\TripoSR-windows\run.bat";
                    tripoProcess.StartInfo.WorkingDirectory = @"C:\Users\QYTH4815\TripoSR-windows";
                    tripoProcess.StartInfo.UseShellExecute = true;
                    tripoProcess.StartInfo.CreateNoWindow = false;
                    tripoProcess.Start();
                }

                btn.Content = "Generating 3D...";

                string imgPath = Path.Combine(LibraryPath, configData.ImageFileName);
                byte[] pngImageData = await File.ReadAllBytesAsync(imgPath);
                string base64Image = Convert.ToBase64String(pngImageData);
                string dataUri = $"data:image/png;base64,{base64Image}";

                var requestBody = new
                {
                    data = new object[]
                    {
                        new { path = imgPath, url = dataUri }
                    }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromMinutes(10);

                HttpResponseMessage? response = null;
                int maxRetries = 90;
                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        response = await client.PostAsync("http://127.0.0.1:7860/api/predict", content);
                        break;
                    }
                    catch
                    {
                        if (i == maxRetries - 1) throw;
                        await Task.Delay(2000);
                    }
                }

                if (response == null || !response.IsSuccessStatusCode)
                    throw new Exception("Le serveur TripoSR n'a pas répondu. Vérifiez que run.bat fonctionne correctement.");

                var responseString = await response.Content.ReadAsStringAsync();

                using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(responseString))
                {
                    var resultData = doc.RootElement.GetProperty("data")[0];
                    string? fileNameStr = null;

                    if (resultData.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        fileNameStr = resultData.GetString();
                    }
                    else if (resultData.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (resultData.TryGetProperty("path", out var pathProp) && pathProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            fileNameStr = pathProp.GetString();
                        else if (resultData.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            fileNameStr = nameProp.GetString();
                        else if (resultData.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == System.Text.Json.JsonValueKind.String)
                            fileNameStr = urlProp.GetString();
                    }

                    if (!string.IsNullOrEmpty(fileNameStr))
                    {
                        string fileUrl = fileNameStr.StartsWith("http")
                            ? fileNameStr
                            : $"http://127.0.0.1:7860/file={fileNameStr}";

                        byte[] glbBytes = await client.GetByteArrayAsync(fileUrl);

                        string safeName = string.Join("_", item.Name.Split(Path.GetInvalidFileNameChars()));
                        string glbFileName = $"{safeName}_3DModel.glb";
                        string glbPath = Path.Combine(LibraryPath, glbFileName);
                        await File.WriteAllBytesAsync(glbPath, glbBytes);

                        configData.ModelFileName = glbFileName;
                        SaveLibrary();

                        generationSuccess = true;
                    }
                    else
                    {
                        throw new Exception("Format de réponse inattendu. Json reçu :\n" + responseString.Substring(0, Math.Min(responseString.Length, 500)));
                    }
                }
            }
            catch (Exception ex)
            {
                await new ContentDialog() { Title = "Erreur TripoSR", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
            }
            finally
            {
                if (!serverWasAlreadyRunning && tripoProcess != null && !tripoProcess.HasExited)
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskkill", $"/F /T /PID {tripoProcess.Id}")
                        {
                            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                            CreateNoWindow = true
                        })?.WaitForExit();
                    }
                    catch { }
                }
                if (generationSuccess)
                {
                    btn.Content = "Generated";
                    btn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
                    btn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                }
                else
                {
                    btn.Content = "To 3D Model";
                    btn.IsEnabled = true;
                }
            }
        }

        // ── CLOUD API Tripo3D mode ────────────────────────────────────────────
        private async Task Generate3DModel_ApiAsync(Button btn, LibraryItemViewModel item, LibraryItemConfig configData)
        {
            if (string.IsNullOrWhiteSpace(_settings.TripoApiKey))
            {
                await new ContentDialog() { Title = "Clé API manquante", Content = "Veuillez définir la clé Tripo3D API dans les paramètres.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            btn.IsEnabled = false;
            string originalContent = btn.Content?.ToString() ?? "To 3D Model";
            bool generationSuccess = false;

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.TripoApiKey);
                client.Timeout = TimeSpan.FromMinutes(10);

                // ── 1. Upload image ──────────────────────────────────────────
                btn.Content = "Uploading image...";
                string imgPath = Path.Combine(LibraryPath, configData.ImageFileName);
                byte[] imgBytes = await File.ReadAllBytesAsync(imgPath);

                string uploadTaskId;
                using (var formData = new MultipartFormDataContent())
                {
                    var imageContent = new ByteArrayContent(imgBytes);
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    formData.Add(imageContent, "file", "image.png");

                    var uploadRes = await client.PostAsync("https://api.tripo3d.ai/v2/openapi/upload", formData);
                    var uploadStr = await uploadRes.Content.ReadAsStringAsync();
                    if (!uploadRes.IsSuccessStatusCode)
                        throw new Exception($"Erreur upload Tripo3D ({uploadRes.StatusCode}):\n{uploadStr}");

                    using var uploadDoc = System.Text.Json.JsonDocument.Parse(uploadStr);
                    var uploadData = uploadDoc.RootElement.GetProperty("data");
                    string fileToken = uploadData.GetProperty("image_token").GetString()
                        ?? throw new Exception("Tripo3D: image_token non reçu.");

                    // ── 2. Create image-to-model task ────────────────────────
                    btn.Content = "Creating task...";
                    var taskPayload = new
                    {
                        type = "image_to_model",
                        file = new { type = "png", file_token = fileToken },
                        // Turbo is blazing fast and generates lightweight low-fidelity models perfect for our 3D tabletop preview
                        model_version = "Turbo-v1.0-20250506", 
                        texture = true
                    };
                    var taskJson = System.Text.Json.JsonSerializer.Serialize(taskPayload);
                    var taskContent = new StringContent(taskJson, System.Text.Encoding.UTF8, "application/json");

                    var taskRes = await client.PostAsync("https://api.tripo3d.ai/v2/openapi/task", taskContent);
                    var taskStr = await taskRes.Content.ReadAsStringAsync();
                    if (!taskRes.IsSuccessStatusCode)
                        throw new Exception($"Erreur création task Tripo3D ({taskRes.StatusCode}):\n{taskStr}");

                    using var taskDoc = System.Text.Json.JsonDocument.Parse(taskStr);
                    uploadTaskId = taskDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString()
                        ?? throw new Exception("Tripo3D: task_id non reçu.");
                }

                // ── 3. Poll status ───────────────────────────────────────────
                btn.Content = "Generating 3D (API)...";
                string? glbUrl = null;
                for (int attempt = 0; attempt < 120; attempt++) // max ~4 min
                {
                    await Task.Delay(2000);
                    var pollRes = await client.GetAsync($"https://api.tripo3d.ai/v2/openapi/task/{uploadTaskId}");
                    var pollStr = await pollRes.Content.ReadAsStringAsync();
                    if (!pollRes.IsSuccessStatusCode)
                        throw new Exception($"Erreur poll Tripo3D ({pollRes.StatusCode}):\n{pollStr}");

                    using var pollDoc = System.Text.Json.JsonDocument.Parse(pollStr);
                    var pollData = pollDoc.RootElement.GetProperty("data");
                    string status = pollData.GetProperty("status").GetString() ?? "";
                    int progress = pollData.TryGetProperty("progress", out var prog) ? prog.GetInt32() : 0;

                    btn.Content = $"Generating 3D (API) {progress}%...";

                    if (status == "success")
                    {
                        var output = pollData.GetProperty("output");
                        glbUrl = output.TryGetProperty("model", out var modelProp) ? modelProp.GetString() : null;
                        glbUrl ??= output.TryGetProperty("pbr_model", out var pbrProp) ? pbrProp.GetString() : null;
                        glbUrl ??= output.TryGetProperty("base_model", out var baseProp) ? baseProp.GetString() : null;
                        break;
                    }
                    else if (status is "failed" or "cancelled" or "banned" or "expired")
                    {
                        throw new Exception($"Tripo3D task terminée avec statut : {status}");
                    }
                }

                if (string.IsNullOrEmpty(glbUrl))
                    throw new Exception("Tripo3D: timeout dépassé ou URL du modèle introuvable.");

                // ── 4. Download GLB ──────────────────────────────────────────
                btn.Content = "Downloading GLB...";
                byte[] glbBytes = await client.GetByteArrayAsync(glbUrl);

                string safeName = string.Join("_", item.Name.Split(Path.GetInvalidFileNameChars()));
                string glbFileName = $"{safeName}_3DModel.glb";
                string glbPath = Path.Combine(LibraryPath, glbFileName);
                await File.WriteAllBytesAsync(glbPath, glbBytes);

                configData.ModelFileName = glbFileName;
                SaveLibrary();

                generationSuccess = true;
            }
            catch (Exception ex)
            {
                await new ContentDialog() { Title = "Erreur Tripo3D API", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
            }
            finally
            {
                if (generationSuccess)
                {
                    btn.Content = "Generated";
                    btn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
                    btn.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
                }
                else
                {
                    btn.Content = "To 3D Model";
                    btn.IsEnabled = true;
                }
            }
        }

        private async void GenerateBananaProImagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                await new ContentDialog() { Title = "No API Key", Content = "Please define the Google Gemini API Key in Settings.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            if (_selectedForBanana.Count == 0)
            {
                await new ContentDialog() { Title = "No Detected Objects", Content = "Analyze the scene first and select objects to get cropped images.", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                return;
            }

            GenerateBananaProImagesBtn.IsEnabled = false;
            BananaProgress.IsActive = true;
            BananaProgress.Visibility = Visibility.Visible;

            try
            {
                _bananaImages.Clear();
                using var client = new HttpClient();
                string url = $"https://generativelanguage.googleapis.com/v1alpha/models/gemini-3.1-flash-image-preview:generateContent?key={_settings.GeminiApiKey}";

                foreach (var obj in _selectedForBanana.ToList())
                {
                    if (obj.IsAlreadyInLibrary) continue;

                    if (obj.CropJpgBytes == null || obj.CropJpgBytes.Length == 0) continue;

                    string base64Image = Convert.ToBase64String(obj.CropJpgBytes);

                    bool isGreenish = obj.Name.Contains("GREEN", StringComparison.OrdinalIgnoreCase) || obj.Name.Contains("LIME", StringComparison.OrdinalIgnoreCase) || obj.Name.Contains("TEAL", StringComparison.OrdinalIgnoreCase);
                    string chromaColorName = isGreenish ? "MAGENTA (#FF00FF)" : "NEON GREEN (#00FF00)";

                    string promptText = $"Extract the physical {obj.Name} shown in this image preserving 100% of its original shape, text, labels, proportions, and perspective. Subtly enhance the object's colors so they look natural and realistic, but slightly cleaner than the raw camera photo. Keep it completely faithful to the original photo input visually, geometrically, and texturally. Your job is to extract this specific {obj.Name} and place it on a pure, solid {chromaColorName} chroma key background. Completely ERASE any other objects (like hands, tools, overlapping items, furniture, floors). IMPORTANT: The {chromaColorName} background must be completely flat, unshaded, with ABSOLUTELY NO SHADOWS, no ambient occlusion, and no reflections under or around the object. Provide a 1:1 square output where the {obj.Name} occupies about 60% of the canvas.";

                    var payload = new
                    {
                        contents = new[] {
                            new {
                                role = "user",
                                parts = new object[] {
                                    new { text = promptText },
                                    new { inlineData = new { mimeType = "image/jpeg", data = base64Image } }
                                }
                            }
                        }
                    };

                    var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(url, content);
                    var responseString = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(responseString);
                        var candidates = doc.RootElement.GetProperty("candidates");
                        foreach (var candidate in candidates.EnumerateArray())
                        {
                            var parts = candidate.GetProperty("content").GetProperty("parts");
                            foreach (var part in parts.EnumerateArray())
                            {
                                if (part.TryGetProperty("inlineData", out var inlineData))
                                {
                                    string? b64 = inlineData.GetProperty("data").GetString();
                                    if (!string.IsNullOrEmpty(b64))
                                    {
                                        byte[] bytes = Convert.FromBase64String(b64);
                                        byte[] pngBytes;

                                        try
                                        {
                                            // Process the Gemini output through OpenCV to remove chroma key background
                                            using var src = OpenCvSharp.Mat.FromImageData(bytes, OpenCvSharp.ImreadModes.Color);
                                            using var argb = new OpenCvSharp.Mat();
                                            OpenCvSharp.Cv2.CvtColor(src, argb, OpenCvSharp.ColorConversionCodes.BGR2BGRA);

                                            using var mask = new OpenCvSharp.Mat();

                                            if (isGreenish)
                                            {
                                                // Magenta is high Red and Blue, low Green (OpenCV is BGR)
                                                OpenCvSharp.Cv2.InRange(src, new OpenCvSharp.Scalar(150, 0, 150), new OpenCvSharp.Scalar(255, 120, 255), mask);
                                            }
                                            else
                                            {
                                                // Neon Green is high Green, low Red and Blue
                                                OpenCvSharp.Cv2.InRange(src, new OpenCvSharp.Scalar(0, 150, 0), new OpenCvSharp.Scalar(120, 255, 120), mask);
                                            }

                                            // Slightly expand the mask and soften it to remove fringing artifacts cleanly
                                            using var kernel = OpenCvSharp.Cv2.GetStructuringElement(OpenCvSharp.MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
                                            OpenCvSharp.Cv2.Dilate(mask, mask, kernel);
                                            OpenCvSharp.Cv2.GaussianBlur(mask, mask, new OpenCvSharp.Size(3, 3), 0);

                                            using var maskInv = new OpenCvSharp.Mat();
                                            OpenCvSharp.Cv2.BitwiseNot(mask, maskInv);

                                            // Apply mask to alpha channel
                                            var channels = OpenCvSharp.Cv2.Split(argb);
                                            maskInv.CopyTo(channels[3]); // Transparent where mask was chroma color
                                            OpenCvSharp.Cv2.Merge(channels, argb);

                                            pngBytes = argb.ImEncode(".png");
                                            foreach (var c in channels) c.Dispose();
                                        }
                                        catch
                                        {
                                            pngBytes = bytes; // Fallback
                                        }

                                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                                        using (var ms = new System.IO.MemoryStream(pngBytes))
                                        {
                                            await bitmap.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
                                        }

                                        _bananaImages.Add(new GeneratedBananaImageModel { Name = obj.Name, ColorBrush = obj.ColorBrush, ImageSource = bitmap });

                                        // Now that we have the 3D render with transparent bg, we add it to persistent Library
                                        string fileName = Guid.NewGuid().ToString() + ".png";
                                        string filePath = Path.Combine(LibraryPath, fileName);
                                        File.WriteAllBytes(filePath, pngBytes);

                                        var uiColor = obj.ColorBrush.Color;
                                        var hexColor = $"#{uiColor.R:X2}{uiColor.G:X2}{uiColor.B:X2}";
                                        var newConfig = new LibraryItemConfig
                                        {
                                            Name = obj.Name,
                                            ColorHex = hexColor,
                                            ImageFileName = fileName,
                                            DateAdded = DateTime.Now.ToString("g")
                                        };
                                        _libraryConfig.Insert(0, newConfig);
                                        SaveLibrary();

                                        var newVm = new LibraryItemViewModel
                                        {
                                            Name = obj.Name,
                                            ColorBrush = obj.ColorBrush,
                                            DateAdded = newConfig.DateAdded,
                                            ImageSource = bitmap
                                        };
                                        _libraryItems.Insert(0, newVm);
                                        _totalBananaCost += (0.0672 * 0.94); // Approx USD -> EUR


                                        obj.IsAlreadyInLibrary = true;
                                        // Update UI opacity immediately
                                        int objIdx = _detectedObjects.IndexOf(obj);
                                        if (objIdx >= 0) _detectedObjects[objIdx] = obj;

                                        // Remove from the selection queue since it was successfully processed
                                        _selectedForBanana.Remove(obj);
                                        UpdateBananaCost();
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        await new ContentDialog() { Title = $"API Error for {obj.Name}", Content = $"Error from Gemini:\n{responseString}", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                await new ContentDialog() { Title = "Execution Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync();
            }
            finally
            {
                GenerateBananaProImagesBtn.IsEnabled = true;
                BananaProgress.IsActive = false;
                BananaProgress.Visibility = Visibility.Collapsed;
            }
        }
        // ════════════════════════════════════════════════════════════════════════
        // CAMERA CALIBRATION HANDLERS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>Fill the calibration camera ComboBox from the already-enumerated _videoDevices.</summary>
        private void PopulateCalibCameraList()
        {
            if (_videoDevices == null) return;
            CalibCameraComboBox.Items.Clear();
            foreach (var d in _videoDevices)
                CalibCameraComboBox.Items.Add(d.Name);

            // Auto-select XiaoMi (over-the-board camera) if present
            for (int i = 0; i < _videoDevices.Count; i++)
            {
                if (_videoDevices[i].Name.Contains("XiaoMi", StringComparison.OrdinalIgnoreCase) ||
                    _videoDevices[i].Name.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase))
                { CalibCameraComboBox.SelectedIndex = i; return; }
            }
            if (_videoDevices.Count > 1) CalibCameraComboBox.SelectedIndex = 1;
            else if (_videoDevices.Count > 0) CalibCameraComboBox.SelectedIndex = 0;
        }

        /// <summary>Camera selection changed — enable the Start button.</summary>
        private void CalibCameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Stop any running detection before user switches camera
            if (_calibService != null)
            {
                _calibService.Stop();
                SetCalibStopped();
            }
            StartCalibDetectionBtn.IsEnabled = CalibCameraComboBox.SelectedIndex >= 0;

            // Restore toggle logic if a pose is loaded
            if (_lastValidPose != null && _isCalibFrozen)
            {
                FreezeCalibToggle.IsOn = true;
                FreezeCalibToggle.IsEnabled = true;
            }
        }

        private void ShowCalibrationPlaneToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_webViewReady)
            {
                bool show = ShowCalibrationPlaneToggle.IsOn;
                _ = SceneWebView.ExecuteScriptAsync($"if (window.toggleCalibrationPlane) window.toggleCalibrationPlane({(show ? "true" : "false")});");
            }
        }

        private void CameraFeedOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_webViewReady)
            {
                string js = $"if (window.setCameraFeedOpacity) window.setCameraFeedOpacity({e.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture)});";
                _ = SceneWebView.ExecuteScriptAsync(js);
            }
        }

        /// <summary>Toggle detection on/off.</summary>
        private void StartCalibDetectionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_calibService == null) return;

            bool running = StartCalibDetectionBtn.Content?.ToString()?.StartsWith("■") == true;
            if (running)
            {
                _calibService.Stop();
                SetCalibStopped();
            }
            else
            {
                int idx = CalibCameraComboBox.SelectedIndex;
                if (idx < 0) return;
                try
                {
                    _calibService.OnFrame -= OnCalibFrame;
                    _calibService.OnPose -= OnCalibPose;
                    _calibService.OnPose -= LivePosePusher;

                    _calibService.OnFrame += OnCalibFrame;
                    _calibService.OnPose += OnCalibPose;
                    _calibService.OnPose += LivePosePusher;

                    _calibService.StartDetection(idx);

                    _isCalibFrozen = false;
                    FreezeCalibToggle.IsOn = false;
                    FreezeCalibToggle.IsEnabled = false;

                    CalibOfflineState.Visibility = Visibility.Collapsed;
                    CalibDetectionBanner.Visibility = Visibility.Visible;
                    StartCalibDetectionBtn.Content = "■  Stop Detection";
                    Log($"[Calib] Detection started (camera index {idx})");
                }
                catch (Exception ex)
                {
                    Log($"[Calib] Failed to start detection: {ex.Message}");
                }
            }
        }

        // ── Event callbacks from CameraCalibrationService (background thread) ──

        private void OnCalibFrame(byte[] jpeg)
        {
            _latestWebcamFrameBytes = jpeg; // Always update memory state

            DispatcherQueue?.TryEnqueue(async () =>
            {
                try
                {
                    if (Preview3DView.Visibility == Visibility.Visible)
                    {
                        var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        using var ms = new System.IO.MemoryStream(jpeg);
                        await bmp.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
                        CalibCameraPreview.Source = bmp;

                        if (_webViewReady && !_isCalibFrozen)
                        {
                            string b64 = Convert.ToBase64String(jpeg);
                            string js = $"if (window.updateCameraFeed) window.updateCameraFeed('data:image/jpeg;base64,{b64}');";
                            _ = SceneWebView.ExecuteScriptAsync(js);
                        }
                    }

                    if (ContextView.Visibility == Visibility.Visible)
                    {
                        var bmp2 = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        using var ms2 = new System.IO.MemoryStream(jpeg);
                        await bmp2.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms2));
                        ContextWebcamPreview.Source = bmp2;
                    }
                }
                catch { }
            });
        }

        private void OnCalibPose(RobotControllerApp.Services.CameraPose pose)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isCalibFrozen) return;

                if (pose.IsValid)
                {
                    _lastValidPose = pose;

                    CalibDetectionIcon.Glyph = "\uE73E";  // Checkmark
                    CalibDetectionStatus.Text = "Grid detected — pose estimated";
                    CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                    CalibDetectionIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                    FreezeCalibToggle.IsEnabled = true;
                }
                else
                {
                    CalibDetectionIcon.Glyph = "\uE783";  // Warning
                    CalibDetectionStatus.Text = "Grid not detected";
                    CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 68, 68));
                    CalibDetectionIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 68, 68));
                    if (_lastValidPose == null) FreezeCalibToggle.IsEnabled = false;
                }
            });
        }

        /// <summary>Copy pose to clipboard.</summary>
        private void CalibCopyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_lastValidPose == null) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText($"tvec: X={_lastValidPose.X:0.000} Y={_lastValidPose.Y:0.000} Z={_lastValidPose.Z:0.000}\n" +
                       $"rvec: Rx={_lastValidPose.Rx:0.000} Ry={_lastValidPose.Ry:0.000} Rz={_lastValidPose.Rz:0.000}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Log("[Calib] Pose copied to clipboard.");
        }

        /// <summary>Save pose to a text file next to the grid PNG.</summary>
        private void FreezeCalibToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (FreezeCalibToggle.IsOn != true)
            {
                // UNFREEZE Logic
                _isCalibFrozen = false;
                FreezeCalibToggle.IsEnabled = _lastValidPose != null;
                
                CalibDetectionIcon.Glyph = "\uE783"; 
                CalibDetectionStatus.Text = "Grid not detected (Refreshing...)";
                CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 68, 68));
                
                if (_calibService != null)
                {
                    _calibService.OnPose -= LivePosePusher;
                    _calibService.OnPose += LivePosePusher;
                }
                return;
            }

            if (_lastValidPose == null)
            {
                FreezeCalibToggle.IsOn = false;
                return;
            }
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string path = Path.Combine(dir, "camera_pose.txt");
                string text = $"[Camera Calibration \u2014 {DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n" +
                              $"[Camera World Position (cam_world = -R^T * tvec)]\r\n" +
                              $"X = {_lastValidPose.X:0.000000} m  (right of board)\r\n" +
                              $"Y = {_lastValidPose.Y:0.000000} m  (forward of board)\r\n" +
                              $"Z = {_lastValidPose.Z:0.000000} m  (height above board)\r\n\r\n" +
                              $"[Raw tvec (board-in-camera)]\r\n" +
                              $"Tx = {_lastValidPose.TvecX:0.000000}\r\n" +
                              $"Ty = {_lastValidPose.TvecY:0.000000}\r\n" +
                              $"Tz = {_lastValidPose.TvecZ:0.000000}  (≈ height when cam looks down)\r\n\r\n" +
                              $"[Rotation Vector - rvec]\r\n" +
                              $"Rx = {_lastValidPose.Rx:0.000000}\r\n" +
                              $"Ry = {_lastValidPose.Ry:0.000000}\r\n" +
                              $"Rz = {_lastValidPose.Rz:0.000000}\r\n\r\n" +
                              $"Grid: {RobotControllerApp.Services.CameraCalibrationService.GridCols}x" +
                              $"{RobotControllerApp.Services.CameraCalibrationService.GridRows} ArUco markers  " +
                              $"MarkerSize={RobotControllerApp.Services.CameraCalibrationService.MarkerLength * 100:0.0}cm\r\n";

                // Save JSON for 3D preview (camera fixed, single calibration session)
                string jsonPath = RobotControllerApp.Services.CameraCalibrationService.SavedPosePath;
                string json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    _lastValidPose.X,
                    _lastValidPose.Y,
                    _lastValidPose.Z,
                    _lastValidPose.Rx,
                    _lastValidPose.Ry,
                    _lastValidPose.Rz,
                    _lastValidPose.TvecX,
                    _lastValidPose.TvecY,
                    _lastValidPose.TvecZ,
                    _lastValidPose.R11,
                    _lastValidPose.R12,
                    _lastValidPose.R13,
                    _lastValidPose.R21,
                    _lastValidPose.R22,
                    _lastValidPose.R23,
                    _lastValidPose.R31,
                    _lastValidPose.R32,
                    _lastValidPose.R33
                });

                // Write synchronously on UI thread so it doesn't freeze or lock
                File.WriteAllText(jsonPath, json);

                // Live update the 3D scene ONLY if visible to prevent WebView2 deadlock
                if (_webViewReady && Preview3DView.Visibility == Visibility.Visible)
                {
                    string poseJsonStr = json.Replace("\\", "\\\\").Replace("'", "\\'");
                    _ = SceneWebView.ExecuteScriptAsync($"setCameraPose('{poseJsonStr}');");
                }

                _isCalibFrozen = true;
                if (_calibService != null) _calibService.OnPose -= LivePosePusher;
                
                FreezeCalibToggle.IsEnabled = true;

                Log($"[Calib] Pose saved to {jsonPath}");
            }
            catch (Exception ex)
            {
                FreezeCalibToggle.IsOn = false;
                Log($"[Calib] Save failed: {ex.Message}");
            }
        }


        /// <summary>Reset calibration UI to stopped state.</summary>
        private void SetCalibStopped()
        {
            if (_calibService != null)
            {
                _calibService.OnPose -= LivePosePusher;
            }
            
            // Do not unfreeze the saved pose just because we stopped detection manually!
            // Start Detection button is independent of Freeze toggle.
            if (!_isCalibFrozen)
            {
                FreezeCalibToggle.IsOn = false;
                FreezeCalibToggle.IsEnabled = _lastValidPose != null;
            }

            StartCalibDetectionBtn.Content = "▶  Start Detection";
            CalibDetectionBanner.Visibility = Visibility.Collapsed;
            Log("[Calib] Detection stopped.");
        }
    }
}
