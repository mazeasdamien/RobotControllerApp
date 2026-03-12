#pragma warning disable IDE0060 // Suppress 'Remove unused parameter' for XAML UI Event Handlers
#pragma warning disable CA1416  // WinUI 3 is Windows-only — all APIs require Windows 10.0.17763.0+
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RobotControllerApp.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace RobotControllerApp
{
    public class DetectedObjectViewModel
    {
        public string Name { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.White);
        public BitmapImage? CroppedImage { get; set; }
        public byte[]? CropJpgBytes { get; set; }   // Full-res for local WinUI display
        public byte[]? ThumbJpgBytes { get; set; }  // Compressed thumbnail for WebSocket broadcast
        public bool IsAlreadyInLibrary { get; set; }
        public double PreviewOpacity => IsAlreadyInLibrary ? 0.3 : 1.0;

        public double UvYmin { get; set; }
        public double UvXmin { get; set; }
        public double UvYmax { get; set; }
        public double UvXmax { get; set; }
        public double AngleDegrees { get; set; }
    }

    public class GeneratedBananaImageModel
    {
        public string Name { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.White);
        public BitmapImage? ImageSource { get; set; }
    }

    public class LibraryItemConfig
    {
        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = "#FFFFFF";
        public string ImageFileName { get; set; } = "";
        public string ModelFileName { get; set; } = "";
        public string DateAdded { get; set; } = "";

        // ── Correction offsets to compensate for AI generation errors ──
        // Rotations are in radians (e.g. 1.5708 = 90°)
        public double OffsetRx { get; set; } = 0;
        public double OffsetRy { get; set; } = 0;
        public double OffsetRz { get; set; } = 0;
        /// <summary>Multiplied on top of the auto-scale. 1.0 = no change.</summary>
        public double OffsetScale { get; set; } = 1.0;
    }

    public class LibraryItemViewModel
    {
        public string Name { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.White);
        public string DateAdded { get; set; } = "";
        public BitmapImage? ImageSource { get; set; }
        /// <summary>True when the image file actually exists on disk.</summary>
        public bool HasImage { get; set; } = false;
        /// <summary>True when a 3D model file is recorded AND exists on disk.</summary>
        public bool HasModel { get; set; } = false;
    }

    public sealed partial class MainWindow : Window
    {
        // ── Shared HTTP Client (Prevents socket exhaustion & TIME_WAIT states) ──
        private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

        private readonly RelayServerHost _relayServer;
        private readonly RobotBridgeService _robotBridge;   // Robot 1
        private readonly RobotBridgeService _robotBridge2;  // Robot 2
        private readonly AppSettings _settings;
        private readonly Scene3dBroadcastServer _broadcastServer = new();

        // ── Network Performance History ──
        private readonly List<double> _unityLatencyHistory = [];
        private readonly List<double> _internetLatencyHistory = [];
        private DispatcherTimer? _networkTimer;
        private readonly Ping _pinger = new();
        private bool _isNetworkPinging = false;
        private const int MaxHistory = 300;
        private double _latencyMaxMs = 150.0;

        // ── Telemetry ──
        private string _questLocation = "Unknown Location";
        private string _questPublicIp = "";
        private float _questRxKbps = 0f;
        private float _questTxKbps = 0f;

        private bool _updatingToggle = false;
        private bool _isClosing = false;
        private bool _isDialogOpen = false;
        private bool _feedFrozen = false;
        private bool _isCalibFrozen = false;
        private bool _cameraRobotSwitching = false;

        private byte[]? _latestWebcamFrameBytes;

        // Scan lock using Thread-safe Interlocked pattern (0 = free, 1 = busy)
        private int _analyzeInProgressFlag = 0;

        private readonly ObservableCollection<DetectedObjectViewModel> _detectedObjects = [];
        private readonly ObservableCollection<DetectedObjectViewModel> _selectedForBanana = [];
        private readonly ObservableCollection<GeneratedBananaImageModel> _bananaImages = [];
        private readonly ObservableCollection<LibraryItemViewModel> _libraryItems = [];

        private List<LibraryItemConfig> _libraryConfig = [];

        // ── Costs & IO Locks ──
        private double _totalGeminiCost = 0.0;
        private double _totalBananaCost = 0.0;
        private double _totalTripo3dCost = 0.0;
        private static readonly SemaphoreSlim s_costLock = new(1, 1);
        private static readonly SemaphoreSlim s_libraryLock = new(1, 1);

        private static string CostFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotControllerApp", "total_costs.json");
        private static string LibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotControllerApp", "Library");
        private static string LibraryJsonPath => Path.Combine(LibraryPath, "library.json");

        private CancellationTokenSource? _autoScanCts;

        private const string DefaultBananaPrompt =
            "Return bounding boxes as a JSON array with labels. Never return masks or code fencing. " +
            "Limit to 25 objects. Include as many physical objects as you can identify on the table. " +
            "IGNORE the chessboard/checkerboard calibration target. IGNORE any robot arms or parts of robot arms. " +
            "If an object appears multiple times, name each uniquely using its colour, size, or position. " +
            "Start every label with the object's main colour (e.g. 'red screwdriver', 'blue cube'). " +
            "Also estimate the object's orientation as 'angle_degrees' (0-360, where 0 = pointing away from camera). " +
            "The format should be: " +
            "[{\"box_2d\": [ymin, xmin, ymax, xmax], \"label\": \"<label>\", \"angle_degrees\": <degrees>}] " +
            "All box_2d values must be integers normalized to 0-1000.";

        // ── Video Capture ──
        private OpenCvSharp.VideoCapture? _cvCapture;
        private CancellationTokenSource? _cvCaptureCts;
        private Task? _cameraTask;
        private int _operatorFpsCount = 0;
        private int _operatorFramesTotal = 0;
        private DateTime _operatorLastFpsReset = DateTime.Now;
        private Windows.Devices.Enumeration.DeviceInformationCollection? _videoDevices;

        private readonly CameraCalibrationService _calibService = new();
        private CameraPose? _lastValidPose;
        private CameraPose? _savedPose;

        // ════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════

        public MainWindow()
        {
            this.InitializeComponent();
            this.Title = "Remote Assist HUB";
            this.ExtendsContentIntoTitleBar = true;

            if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = this.AppWindow.TitleBar;
                titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
                try { this.AppWindow.SetIcon("Assets/AppLogo.png"); } catch { }
            }

            this.SetTitleBar(TitleBarDragArea);

            DetectedObjectsList.ItemsSource = _detectedObjects;
            SelectedObjectsList.ItemsSource = _selectedForBanana;
            BananaImagesList.ItemsSource = _bananaImages;
            LibraryList.ItemsSource = _libraryItems;
            _libraryItems.CollectionChanged += (_, _) =>
                LibraryCountBadge.Text = $"{_libraryItems.Count} asset{(_libraryItems.Count == 1 ? "" : "s")}";

            _ = LoadLibraryAsync();

            _settings = AppSettings.Load();
            _relayServer = new RelayServerHost();

            bool r1HasCam = _settings.CameraRobot != 2;
            bool r2HasCam = _settings.CameraRobot == 2;
            _robotBridge = new RobotBridgeService() { RobotId = "Robot_Niryo_01", HasCamera = r1HasCam };
            _robotBridge2 = new RobotBridgeService() { RobotId = "Robot_Niryo_02", HasCamera = r2HasCam };

            _robotBridge.OnCameraFrameReceived += HandleCameraFrame;
            _robotBridge2.OnCameraFrameReceived += HandleCameraFrame;

            LoadSettingsIntoUI();

            RelayActiveText.Text = "WAITING";
            RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
            RelayIcon.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];

            WireUpEvents();

            this.AppWindow.Closing += AppWindow_Closing;

            if (this.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.Maximize();
            }

            StartNetworkMonitoring();
            _ = TraceHubLocation();
            StatusPulseAnimation?.Begin();
            _ = LoadCameraList();
        }

        private void WireUpEvents()
        {
            RelayServerHost.OnLog += Log;
            RobotBridgeService.OnLog += Log;

            _robotBridge.OnInstanceConnectionChanged += (c) => DispatcherQueue.TryEnqueue(() => { UpdateRobotStatus(c); if (!c) ClearHardwareInfoBox(R1StatusStr, R1RpiTemp, R1CalibStatus, R1MotorTemp, R1HwErrors); });
            _robotBridge2.OnInstanceConnectionChanged += (c) => DispatcherQueue.TryEnqueue(() => { UpdateRobot2Status(c); if (!c) ClearHardwareInfoBox(R2StatusStr, R2RpiTemp, R2CalibStatus, R2MotorTemp, R2HwErrors); });

            _robotBridge.OnLearningModeChanged += (isOn) => DispatcherQueue.TryEnqueue(() => { _updatingToggle = true; R1LearningToggle.IsOn = isOn; _updatingToggle = false; });
            _robotBridge2.OnLearningModeChanged += (isOn) => DispatcherQueue.TryEnqueue(() => { _updatingToggle = true; R2LearningToggle.IsOn = isOn; _updatingToggle = false; });

            _robotBridge.OnRobotStatusUpdated += (s) => DispatcherQueue.TryEnqueue(() => R1StatusStr.Text = s);
            _robotBridge2.OnRobotStatusUpdated += (s) => DispatcherQueue.TryEnqueue(() => R2StatusStr.Text = s);

            _robotBridge.OnHardwareStatusUpdated += (hw) => DispatcherQueue.TryEnqueue(() => UpdateHardwareInfoBox(hw, R1RpiTemp, R1CalibStatus, R1MotorTemp, R1HwErrors));
            _robotBridge2.OnHardwareStatusUpdated += (hw) => DispatcherQueue.TryEnqueue(() => UpdateHardwareInfoBox(hw, R2RpiTemp, R2CalibStatus, R2MotorTemp, R2HwErrors));

            RelayServerHost.OnUnityConnectionChanged += (c) => DispatcherQueue.TryEnqueue(() => UpdateExpertStatus(c));
            RelayServerHost.OnUnityTelemetryReceived += (loc, rx, tx, pubIp) => DispatcherQueue.TryEnqueue(() => { _questLocation = loc; _questRxKbps = rx; _questTxKbps = tx; if (!string.IsNullOrEmpty(pubIp)) _questPublicIp = pubIp; });

            RelayServerHost.OnJointsReceived += (joints) =>
            {
                var degAngles = joints.Select(r => (double)(r * 180.0 / Math.PI)).ToArray();
                DispatcherQueue.TryEnqueue(() => TelemJoints.Text = "[" + string.Join(", ", joints.Select(j => j.ToString("0.00"))) + "]");
                _ = _broadcastServer.BroadcastAsync("setRobotJoints", JsonSerializer.Serialize(new { angles = degAngles, robotIdx = 0 }));
            };

            RelayServerHost.OnRobot2JointsReceived += (joints) =>
            {
                var degAngles = joints.Select(r => (double)(r * 180.0 / Math.PI)).ToArray();
                DispatcherQueue.TryEnqueue(() => TelemJoints2.Text = "[" + string.Join(", ", joints.Select(j => j.ToString("0.00"))) + "]");
                _ = _broadcastServer.BroadcastAsync("setRobotJoints", JsonSerializer.Serialize(new { angles = degAngles, robotIdx = 1 }));
            };

            RelayServerHost.OnImageStatsUpdated += (fps, total) => DispatcherQueue.TryEnqueue(() =>
            {
                TelemFps.Text = fps.ToString();
                TelemFps.Foreground = new SolidColorBrush(fps < 10 ? Microsoft.UI.Colors.Red : (fps > 20 ? Microsoft.UI.Colors.LightGreen : Microsoft.UI.Colors.Orange));
                TelemTotalImages.Text = total.ToString();
            });

            RelayServerHost.OnUnityMessageReceived += HandleUnityIKTelemetry;
        }

        private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            await StartSystem();
            _ = StartScene3DServerAsync();
        }

        private void NavView_Loaded(object sender, RoutedEventArgs e) { }

        // ════════════════════════════════════════════════════════════════════════
        // DATA & IO MANAGEMENT
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Safely load a byte array into a BitmapImage without WinUI 3 memory leaks.
        /// Replaces the faulty MemoryStream setup.
        /// </summary>
        private static async Task<BitmapImage> LoadImageFromBytesAsync(byte[] bytes)
        {
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }

        private static string GetSafeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            string safe = new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            return safe.Replace(" ", "_");
        }

        private async Task ShowDialogAsync(string title, string content)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;
            try { await new ContentDialog { Title = title, Content = content, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync(); }
            catch { }
            finally { _isDialogOpen = false; }
        }

        private async Task LoadLibraryAsync()
        {
            try
            {
                if (!Directory.Exists(LibraryPath)) Directory.CreateDirectory(LibraryPath);
                if (File.Exists(LibraryJsonPath))
                {
                    string json = await File.ReadAllTextAsync(LibraryJsonPath);
                    _libraryConfig = JsonSerializer.Deserialize<List<LibraryItemConfig>>(json) ?? [];

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
                                vm.ColorBrush = new SolidColorBrush(uiColor);
                            }
                            string imgPath = Path.Combine(LibraryPath, item.ImageFileName);
                            vm.HasImage = File.Exists(imgPath);
                            if (vm.HasImage)
                            {
                                byte[] bytes = await File.ReadAllBytesAsync(imgPath);
                                vm.ImageSource = await LoadImageFromBytesAsync(bytes);
                            }

                            vm.HasModel = !string.IsNullOrEmpty(item.ModelFileName)
                                          && File.Exists(Path.Combine(LibraryPath, item.ModelFileName));
                        }
                        catch { }
                        _libraryItems.Add(vm);
                    }
                }
            }
            catch { }
        }

        private async Task SaveLibraryAsync()
        {
            await s_libraryLock.WaitAsync();
            try
            {
                if (!Directory.Exists(LibraryPath)) Directory.CreateDirectory(LibraryPath);
                await File.WriteAllTextAsync(LibraryJsonPath, JsonSerializer.Serialize(_libraryConfig));
            }
            catch { }
            finally { s_libraryLock.Release(); }
        }

        private async Task SaveCostsAsync()
        {
            await s_costLock.WaitAsync();
            try
            {
                string dir = Path.GetDirectoryName(CostFilePath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var obj = new { gemini = _totalGeminiCost, banana = _totalBananaCost, tripo = _totalTripo3dCost };
                await File.WriteAllTextAsync(CostFilePath, JsonSerializer.Serialize(obj));
            }
            catch { }
            finally { s_costLock.Release(); }
        }

        private async Task LoadCostsAsync()
        {
            await s_costLock.WaitAsync();
            try
            {
                if (!File.Exists(CostFilePath)) return;
                string json = await File.ReadAllTextAsync(CostFilePath);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("gemini", out var g)) _totalGeminiCost = g.GetDouble();
                if (r.TryGetProperty("banana", out var b)) _totalBananaCost = b.GetDouble();
                if (r.TryGetProperty("tripo", out var t)) _totalTripo3dCost = t.GetDouble();
            }
            catch { }
            finally { s_costLock.Release(); }
            DispatcherQueue.TryEnqueue(UpdateTotalCostDisplay);
        }

        private void UpdateTotalCostDisplay()
        {
            // Scan uses Orange proxy — not tracked. Only Banana + Tripo3D are paid.
            double total = _totalBananaCost + _totalTripo3dCost;
            if (TotalCostText != null) TotalCostText.Text = $"{total:0.0000} €";
            _ = SaveCostsAsync();
        }

        // ════════════════════════════════════════════════════════════════════════
        // SYSTEM STARTUP & SHUTDOWN
        // ════════════════════════════════════════════════════════════════════════

        private async Task StartSystem()
        {
            Log("🚀 Initializing Expert Telepresence Hub...");
            await LoadCostsAsync();

            await Task.Delay(500);
            Log($"Starting Hub Relay Server (Port {_settings.RelayPort})...");
            _relayServer.Port = _settings.RelayPort;
            _relayServer.PublicUrl = _settings.PublicUrl;
            _ = Task.Run(async () => await _relayServer.StartAsync());

            await Task.Delay(1000);
            Log($"Starting Robot 1 Bridge (Target: {_settings.RobotIp})...");
            _robotBridge.RosIp = SanitizeIp(_settings.RobotIp);
            _robotBridge.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";
            _robotBridge.Start();
            UpdateRobotStatus(false);

            Log($"Starting Robot 2 Bridge (Target: {_settings.Robot2Ip})...");
            _robotBridge2.RosIp = SanitizeIp(_settings.Robot2Ip);
            _robotBridge2.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";
            _robotBridge2.Start();
            UpdateRobot2Status(false);

            StartRelayStatusPoll();
            Log("System Ready. Waiting for connections...");
        }

        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_isClosing) return;
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
            try { result = await dialog.ShowAsync(); } catch { result = ContentDialogResult.Primary; }

            if (result == ContentDialogResult.Primary)
            {
                _isClosing = true;
                Log("Stopping services...");
                try
                {
                    _cvCaptureCts?.Cancel();
                    if (_cameraTask != null) { try { await _cameraTask; } catch { } }
                    if (_cvCapture != null) { try { _cvCapture.Release(); _cvCapture.Dispose(); } catch { } _cvCapture = null; }

                    await _robotBridge.StopAsync();
                    await _robotBridge2.StopAsync();
                    await _relayServer.StopAsync();
                }
                catch { }

                Application.Current.Exit();
                Environment.Exit(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // CAMERA HANDLING (DIRECTSHOW via OPENCV)
        // ════════════════════════════════════════════════════════════════════════

        private async Task StartCameraByIndex(int index)
        {
            if (_videoDevices == null || index < 0 || index >= _videoDevices.Count) return;

            // Safely stop previous task to avoid OpenCV lock violations
            _cvCaptureCts?.Cancel();
            if (_cameraTask != null)
            {
                try { await _cameraTask; } catch { }
            }

            if (_cvCapture != null)
            {
                try { _cvCapture.Release(); _cvCapture.Dispose(); } catch { }
                _cvCapture = null;
            }

            TelemOperatorFps.Text = "0.0";
            _operatorFpsCount = 0;

            try
            {
                var selected = _videoDevices[index];
                Log($"[Webcam] Attempting to start stream: {selected.Name}");

                _cvCaptureCts = new CancellationTokenSource();
                var token = _cvCaptureCts.Token;
                _operatorLastFpsReset = DateTime.Now;

                // Move VideoCapture instantiation to background thread to prevent UI freeze
                _cameraTask = Task.Run(async () =>
                {
                    try
                    {
                        using var capture = new OpenCvSharp.VideoCapture(index, OpenCvSharp.VideoCaptureAPIs.DSHOW);
                        if (!capture.IsOpened())
                        {
                            Log($"[Webcam] Failed to open stream for '{selected.Name}' (DirectShow)");
                            return;
                        }

                        capture.Set(OpenCvSharp.VideoCaptureProperties.FrameWidth, 1280);
                        capture.Set(OpenCvSharp.VideoCaptureProperties.FrameHeight, 720);

                        DispatcherQueue.TryEnqueue(() => _cvCapture = capture);

                        using var mat = new OpenCvSharp.Mat();
                        while (!token.IsCancellationRequested && capture.IsOpened())
                        {
                            if (capture.Read(mat) && !mat.Empty())
                            {
                                Interlocked.Increment(ref _operatorFramesTotal);
                                Interlocked.Increment(ref _operatorFpsCount);

                                int currentFps = _operatorFpsCount;
                                int total = _operatorFramesTotal;
                                bool updateCounters = false;

                                if ((DateTime.Now - _operatorLastFpsReset).TotalSeconds >= 1)
                                {
                                    currentFps = Interlocked.Exchange(ref _operatorFpsCount, 0);
                                    _operatorLastFpsReset = DateTime.Now;
                                    updateCounters = true;
                                }

                                byte[] frameBytes = mat.ToBytes(".jpg");
                                RelayServerHost.CurrentManager?.UpdateLatestOperatorImage(frameBytes);

                                DispatcherQueue?.TryEnqueue(async () =>
                                {
                                    if (token.IsCancellationRequested) return;

                                    try
                                    {
                                        if (updateCounters)
                                        {
                                            TelemOperatorFps.Text = currentFps.ToString("0.0");
                                            TelemOperatorTotalImages.Text = total.ToString();
                                        }

                                        if (ContextWebcamPreview != null) ContextWebcamPreview.Source = await LoadImageFromBytesAsync(frameBytes);

                                        if (!_feedFrozen && _broadcastServer != null && _broadcastServer.ConnectedClients > 0)
                                        {
                                            string b64 = Convert.ToBase64String(frameBytes);
                                            _ = _broadcastServer.BroadcastAsync("updateCameraFeed", JsonSerializer.Serialize($"data:image/jpeg;base64,{b64}"));
                                        }

                                        _latestWebcamFrameBytes = frameBytes;
                                    }
                                    catch { }
                                });
                            }
                            try { await Task.Delay(33, token).ConfigureAwait(false); } catch (TaskCanceledException) { break; }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"[Webcam] Worker thread crashed: {ex.Message}");
                    }
                }, token);
            }
            catch (Exception ex)
            {
                Log($"[Webcam] Initialization failed: {ex.Message}");
            }
        }

        private async Task LoadCameraList()
        {
            try
            {
                _videoDevices = await Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(Windows.Devices.Enumeration.DeviceClass.VideoCapture);
                if (_videoDevices.Count == 0) { Log("[Webcam] No cameras found."); return; }

                int defaultIdx = 0;
                for (int i = 0; i < _videoDevices.Count; i++)
                {
                    if (_videoDevices[i].Name.Contains("Creative", StringComparison.OrdinalIgnoreCase)) { defaultIdx = i; break; }
                }

                Log($"[Webcam] {_videoDevices.Count} camera(s) found. Selected: {_videoDevices[defaultIdx].Name}");

                DispatcherQueue.TryEnqueue(() =>
                {
                    DashboardCameraCombo.Items.Clear();
                    CalibCameraComboBox.Items.Clear();
                    foreach (var dev in _videoDevices)
                    {
                        DashboardCameraCombo.Items.Add(dev.Name);
                        CalibCameraComboBox.Items.Add(dev.Name);
                    }
                    DashboardCameraCombo.SelectedIndex = defaultIdx;
                    CalibCameraComboBox.SelectedIndex = defaultIdx;
                });
                await StartCameraByIndex(defaultIdx);
            }
            catch (Exception ex) { Log($"[Webcam] Enumeration failed: {ex.Message}"); }
        }

        private async void DashboardCameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = DashboardCameraCombo.SelectedIndex;
            if (idx >= 0) await StartCameraByIndex(idx);
        }

        private async void RefreshCamerasButton_Click(object sender, RoutedEventArgs e)
        {
            Log("[Webcam] Refreshing camera list...");
            await LoadCameraList();
        }

        private void HandleCameraFrame(string robotId, byte[] imageBytes)
        {
            RelayServerHost.CurrentManager?.UpdateLatestImage(imageBytes);
        }

        // ════════════════════════════════════════════════════════════════════════
        // AI GENERATION (GEMINI / TRIPO 3D)
        // ════════════════════════════════════════════════════════════════════════

        private async void GenerateObjectImagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                await ShowDialogAsync("No API Key", "Please define the Google Gemini API Key in Settings.");
                return;
            }
            if (_latestWebcamFrameBytes == null)
            {
                await ShowDialogAsync("No Camera Frame", "Start the webcam first.");
                return;
            }
            await AnalyzeSceneAsync();
        }

        private async Task AnalyzeSceneAsync()
        {
            if (_latestWebcamFrameBytes == null || _latestWebcamFrameBytes.Length == 0) return;

            // Thread-safe lock to avoid concurrent API flooding
            if (Interlocked.CompareExchange(ref _analyzeInProgressFlag, 1, 0) == 1)
            {
                Log("[Scene3D] Scan already in progress — ignoring concurrent request.");
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (GenerateObjectImagesBtn != null) GenerateObjectImagesBtn.IsEnabled = false;
                if (GenerationProgress != null) { GenerationProgress.Visibility = Visibility.Visible; GenerationProgress.IsActive = true; }
            });

            try
            {
                // Use Orange LLM proxy with gemini-2.5-flash-lite (OpenAI-compatible endpoint)
                string orangeApiKey = _settings.OrangeApiKey;
                string orangeBaseUrl = (string.IsNullOrWhiteSpace(_settings.OrangeApiUrl)
                    ? "https://llmproxy.ai.orange"
                    : _settings.OrangeApiUrl.TrimEnd('/'));
                string url = $"{orangeBaseUrl}/v1/chat/completions";

                string base64Image = Convert.ToBase64String(_latestWebcamFrameBytes);
                string prompt = string.IsNullOrWhiteSpace(_settings.BananaPromptTemplate) ? DefaultBananaPrompt : _settings.BananaPromptTemplate;

                var requestBody = new
                {
                    model = "vertex_ai/gemini-2.5-flash-lite",
                    temperature = 0.3,
                    messages = new object[] {
                        new {
                            role = "user",
                            content = new object[] {
                                new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{base64Image}" } },
                                new { type = "text", text = prompt }
                            }
                        }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", orangeApiKey);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = await SharedHttpClient.SendAsync(request, cts.Token);
                string responseString = await response.Content.ReadAsStringAsync(cts.Token);


                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseString);

                    // OpenAI-compatible response: choices[0].message.content
                    string? messageContent = null;
                    if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        messageContent = choices[0].GetProperty("message").GetProperty("content").GetString();


                    var newDetectedObjects = new List<DetectedObjectViewModel>();

                    if (!string.IsNullOrEmpty(messageContent))
                    {
                        int s = messageContent.IndexOf('[');
                        int eIdx = messageContent.LastIndexOf(']');
                        if (s >= 0 && eIdx > s)
                        {
                            string jsonStr = messageContent.Substring(s, eIdx - s + 1);
                            using var itemsDoc = JsonDocument.Parse(jsonStr);

                            if (itemsDoc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                var itemsArray = itemsDoc.RootElement;

                                // Safely decode Image — Check for corruption!
                                using var sourceMat = OpenCvSharp.Cv2.ImDecode(_latestWebcamFrameBytes, OpenCvSharp.ImreadModes.Color);
                                if (!sourceMat.Empty())
                                {
                                    int imgW = sourceMat.Width;
                                    int imgH = sourceMat.Height;

                                    foreach (var item in itemsArray.EnumerateArray())
                                    {
                                        string label = item.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? "Unknown" : "Unknown";
                                        int ymin = 0, xmin = 0, ymax = 0, xmax = 0;

                                        if (item.TryGetProperty("box_2d", out var box2d) && box2d.GetArrayLength() >= 4)
                                        {
                                            ymin = box2d[0].GetInt32(); xmin = box2d[1].GetInt32(); ymax = box2d[2].GetInt32(); xmax = box2d[3].GetInt32();
                                        }
                                        else continue;

                                        double angleDegrees = 0;
                                        if (item.TryGetProperty("angle_degrees", out var angleProp))
                                            angleProp.TryGetDouble(out angleDegrees);

                                        if (xmin < 5 || ymin < 5 || xmax > 995 || ymax > 995) continue;
                                        Log($"[Scene3D] Detected: {label} box=[{ymin},{xmin},{ymax},{xmax}]");

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
                                            const int MaxThumbSide = 240;
                                            OpenCvSharp.Mat thumbMat = cropMat;
                                            bool ownThumb = false;

                                            if (cropMat.Width > MaxThumbSide || cropMat.Height > MaxThumbSide)
                                            {
                                                double scale = Math.Min((double)MaxThumbSide / cropMat.Width, (double)MaxThumbSide / cropMat.Height);
                                                thumbMat = new OpenCvSharp.Mat();
                                                ownThumb = true;
                                                OpenCvSharp.Cv2.Resize(cropMat, thumbMat, new OpenCvSharp.Size((int)(cropMat.Width * scale), (int)(cropMat.Height * scale)));
                                            }

                                            byte[] cropJpgBytes = cropMat.ImEncode(".jpg");
                                            byte[] thumbJpgBytes = thumbMat.ImEncode(".jpg", new OpenCvSharp.ImageEncodingParam(OpenCvSharp.ImwriteFlags.JpegQuality, 72));

                                            if (ownThumb) thumbMat.Dispose();

                                            string objName = label.ToUpper();

                                            // Safely check _libraryConfig
                                            bool isAlreadyInLibrary = false;
                                            DispatcherQueue.TryEnqueue(() =>
                                            {
                                                isAlreadyInLibrary = _libraryConfig.Any(x => string.Equals(x.Name, objName, StringComparison.OrdinalIgnoreCase));
                                            });

                                            newDetectedObjects.Add(new DetectedObjectViewModel
                                            {
                                                Name = objName,
                                                ColorBrush = new SolidColorBrush(ColorFromLabel(label)),
                                                CropJpgBytes = cropJpgBytes,
                                                ThumbJpgBytes = thumbJpgBytes,
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

                    // Must mutate ObservableCollection on UI thread
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        _detectedObjects.Clear();
                        foreach (var dobj in newDetectedObjects)
                        {
                            dobj.CroppedImage = await LoadImageFromBytesAsync(dobj.CropJpgBytes!);
                            _detectedObjects.Add(dobj);
                        }
                        await PushObjectsToSceneAsync();
                    });
                }
                else
                {
                    Log($"[Scene3D] Orange proxy error: {responseString[..Math.Min(200, responseString.Length)]}");
                    _ = _broadcastServer.BroadcastAsync("setDetectedObjects", "[]");
                    if (AutoScanToggle?.IsChecked != true)
                    {
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            try { await new ContentDialog { Title = "API Error", Content = $"Error:\n{responseString}", CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync(); } catch { }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[Scene3D] AnalyzeSceneAsync exception: {ex.Message}");
                _ = _broadcastServer.BroadcastAsync("setDetectedObjects", "[]");
                if (AutoScanToggle?.IsChecked != true)
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try { await new ContentDialog { Title = "Execution Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync(); } catch { }
                    });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _analyzeInProgressFlag, 0); // Release lock
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (AutoScanToggle?.IsChecked != true && GenerateObjectImagesBtn != null)
                    {
                        GenerateObjectImagesBtn.IsEnabled = true;
                        GenerateObjectImagesBtn.Content = "Analyze Scene Manually";
                    }
                    if (GenerationProgress != null)
                    {
                        GenerationProgress.IsActive = false;
                        GenerationProgress.Visibility = Visibility.Collapsed;
                    }
                });
            }
        }

        private void AutoScanToggle_Click(object sender, RoutedEventArgs e)
        {
            if (AutoScanToggle.IsChecked == true)
            {
                AutoScanToggle.Content = "■ Stop Auto-Scan";
                AutoScanToggle.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 150, 0));
                GenerateObjectImagesBtn.IsEnabled = false;
                GenerateObjectImagesBtn.Content = "Auto-Scan Running...";

                _autoScanCts?.Cancel();
                _autoScanCts = new CancellationTokenSource();
                var token = _autoScanCts.Token;

                _ = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        await AnalyzeSceneAsync();
                        if (!token.IsCancellationRequested) await Task.Delay(6000, token).ContinueWith(_ => { }); // Rate-limiting (10x/min)
                    }
                }, token);
            }
            else
            {
                AutoScanToggle.Content = "Auto-Scan (10x/min)";
                AutoScanToggle.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 51, 51, 51));
                GenerateObjectImagesBtn.IsEnabled = true;
                GenerateObjectImagesBtn.Content = "Analyze Scene Manually";
                _autoScanCts?.Cancel();
                _autoScanCts = null;
            }
        }

        private void AddSelectedObject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DetectedObjectViewModel item && !_selectedForBanana.Any(x => x.Name == item.Name))
            {
                _selectedForBanana.Add(item); UpdateBananaCost();
            }
        }

        private void RemoveSelectedObject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DetectedObjectViewModel item)
            {
                _selectedForBanana.Remove(item); UpdateBananaCost();
            }
        }

        private double GetBananaModelPricePerImage() => _settings.BananaModel switch { "gemini-3.1-flash-image-preview" => 0.0125, "gemini-3-pro-image-preview" => 0.03, _ => 0.03 };

        private void UpdateBananaCost()
        {
            double costUsd = _selectedForBanana.Count * GetBananaModelPricePerImage();
            double framingScaleUsd = (_settings.BananaFramingScale > 0) ? (_settings.BananaFramingScale * 0.01) : 0;
            double costEuro = (costUsd + framingScaleUsd) * 0.94;
            BananaCostText.Text = $"Queue: {costEuro:0.00} € | Total Spent: {_totalBananaCost:0.00} €";
        }

        private async void GenerateBananaProImagesBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                await ShowDialogAsync("No API Key", "Please define the Google Gemini API Key in Settings.");
                return;
            }

            if (_selectedForBanana.Count == 0)
            {
                await ShowDialogAsync("No Detected Objects", "Analyze the scene first and select objects.");
                return;
            }

            GenerateBananaProImagesBtn.IsEnabled = false;
            BananaProgress.IsActive = true;
            BananaProgress.Visibility = Visibility.Visible;

            var objectsToProcess = _selectedForBanana.Where(obj => !obj.IsAlreadyInLibrary && obj.CropJpgBytes != null && obj.CropJpgBytes.Length > 0).ToList();
            _bananaImages.Clear();

            _ = Task.Run(async () =>
            {
                try
                {
                    string modelName = string.IsNullOrEmpty(_settings.BananaModel) ? "gemini-2.5-flash-image" : _settings.BananaModel;
                    string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={_settings.GeminiApiKey}";
                    double frameScale = _settings.BananaFramingScale > 0 ? _settings.BananaFramingScale * 100 : 60;
                    string customPromptPart = string.IsNullOrWhiteSpace(_settings.BananaPromptTemplate) ? "preserving 100% of its original shape, text, labels, proportions, and perspective" : _settings.BananaPromptTemplate;

                    foreach (var obj in objectsToProcess)
                    {
                        string base64Image = Convert.ToBase64String(obj.CropJpgBytes!);
                        string promptText = $"Extract the physical {obj.Name} shown in this image {customPromptPart}. Subtly enhance the object's colors so they look natural and realistic, but completely faithful to the original photo. Remove all other objects (hands, tools, furniture, floors). Place the object on a clean, plain WHITE background with no shadows, gradients, or vignettes. Provide a 1:1 square output where the {obj.Name} occupies about {frameScale}% of the canvas.";

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
                            },
                            // CRITICAL: without responseModalities Gemini returns text, not an image
                            generationConfig = new
                            {
                                responseModalities = new[] { "IMAGE", "TEXT" }
                            }
                        };

                        using var request = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                        };

                        using var response = await SharedHttpClient.SendAsync(request);
                        var responseString = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode)
                        {
                            bool imageFound = false;
                            try
                            {
                                using var doc = JsonDocument.Parse(responseString);
                                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                                {
                                    var parts = candidates[0].GetProperty("content").GetProperty("parts");
                                    // Iterate all parts to find inlineData (same pattern as RunBananaEnhancementAsync)
                                    foreach (var part in parts.EnumerateArray())
                                    {
                                        if (part.TryGetProperty("inlineData", out var inlineData))
                                        {
                                            string? b64 = inlineData.GetProperty("data").GetString();
                                            if (!string.IsNullOrEmpty(b64))
                                            {
                                                imageFound = true;
                                                byte[] pngBytes = Convert.FromBase64String(b64);
                                                string fileName = Guid.NewGuid().ToString() + ".png";
                                                string filePath = Path.Combine(LibraryPath, fileName);
                                                await File.WriteAllBytesAsync(filePath, pngBytes);

                                                var uiColor = obj.ColorBrush.Color;
                                                string hexColor = $"#{uiColor.R:X2}{uiColor.G:X2}{uiColor.B:X2}";

                                                DispatcherQueue.TryEnqueue(async () =>
                                                {
                                                    var bitmap = await LoadImageFromBytesAsync(pngBytes);
                                                    _bananaImages.Add(new GeneratedBananaImageModel { Name = obj.Name, ColorBrush = obj.ColorBrush, ImageSource = bitmap });

                                                    var newConfig = new LibraryItemConfig { Name = obj.Name, ColorHex = hexColor, ImageFileName = fileName, DateAdded = DateTime.Now.ToString("g") };

                                                    _libraryConfig.Insert(0, newConfig);
                                                    await SaveLibraryAsync();

                                                    _libraryItems.Insert(0, new LibraryItemViewModel { Name = obj.Name, ColorBrush = obj.ColorBrush, DateAdded = newConfig.DateAdded, ImageSource = bitmap });

                                                    double framingScaleUsd = _settings.BananaFramingScale > 0 ? _settings.BananaFramingScale * 0.01 : 0;
                                                    _totalBananaCost += (GetBananaModelPricePerImage() + framingScaleUsd) * 0.94;
                                                    UpdateTotalCostDisplay();

                                                    obj.IsAlreadyInLibrary = true;
                                                    int objIdx = _detectedObjects.IndexOf(obj);
                                                    if (objIdx >= 0) _detectedObjects[objIdx] = obj; // trigger UI refresh

                                                    _selectedForBanana.Remove(obj);
                                                    UpdateBananaCost();
                                                });
                                                break; // found the image part — done
                                            }
                                        }
                                    }
                                    if (!imageFound)
                                        Log($"[Banana] No image in response for '{obj.Name}'. Parts: {parts.GetRawText()[..Math.Min(300, parts.GetRawText().Length)]}");
                                }
                            }
                            catch (JsonException jex)
                            {
                                Log($"[Banana] JSON parse error for '{obj.Name}': {jex.Message}. Response: {responseString[..Math.Min(200, responseString.Length)]}");
                            }
                        }
                        else
                        {
                            DispatcherQueue.TryEnqueue(async () =>
                            {
                                try { await new ContentDialog { Title = $"API Error for {obj.Name}", Content = responseString, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync(); } catch { }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try { await new ContentDialog { Title = "Execution Error", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync(); } catch { }
                    });
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        GenerateBananaProImagesBtn.IsEnabled = true;
                        BananaProgress.IsActive = false;
                        BananaProgress.Visibility = Visibility.Collapsed;
                    });
                }
            });
        }

        // ── Banana image enhancement (Gemini) — run before Tripo for best 3D quality ──
        /// <summary>
        /// Calls the Gemini image-generation model to produce a clean, white-background
        /// square image of the object — exactly what Tripo3D needs for high-quality meshes.
        /// Returns the enhanced PNG bytes, or <see langword="null"/> if the
        /// Gemini key is not set or the call fails (caller must abort Tripo3D).
        /// </summary>
        private async Task<byte[]?> RunBananaEnhancementAsync(byte[] originalBytes, string objectLabel, Action<string>? progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(_settings.GeminiApiKey))
            {
                progressCallback?.Invoke("⚠️ Banana skipped (no Gemini key)");
                return null;
            }

            try
            {
                progressCallback?.Invoke("🍌 Enhancing image…");

                string modelName = string.IsNullOrEmpty(_settings.BananaModel)
                    ? "gemini-2.5-flash-image"
                    : _settings.BananaModel;
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={_settings.GeminiApiKey}";

                string base64Image = Convert.ToBase64String(originalBytes);

                // "Magic Prompt" — Step 3 of the wrapper pattern:
                // 1. Un-warp the top-down camera perspective → Tripo gets an eye-level photo
                // 2. Strict "NO SHADOWS" directive → prevents Tripo from baking shadows into the mesh base
                // 3. White background → clean box.min.y calculation in Three.js
                string customPromptPart = string.IsNullOrWhiteSpace(_settings.BananaPromptTemplate)
                    ? "preserving 100% of its original shape, text, labels, proportions, and perspective. " +
                      "CRITICAL: Digitally un-warp and rotate the object so it appears perfectly upright " +
                      "and photographed straight-on at eye level, counteracting any top-down camera angle. " +
                      "ABSOLUTELY NO SHADOWS of any kind — not under the object, not beside it. Shadows corrupt 3D reconstruction."
                    : _settings.BananaPromptTemplate;

                double frameScale = _settings.BananaFramingScale > 0 ? _settings.BananaFramingScale * 100 : 60;

                string promptText =
                    $"GENERATE AN IMAGE of ONLY the {objectLabel} — one single isolated object. " +
                    $"IMPORTANT: the output image must contain EXACTLY 1 object: the {objectLabel}. No other objects. " +
                    $"{customPromptPart} " +
                    $"Remove ALL other objects and background. Keep ONLY the {objectLabel}. " +
                    $"Output: the {objectLabel} centered on a plain WHITE background, NO shadows, filling about {frameScale}% of the square canvas.";


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
                    },
                    // CRITICAL: without responseModalities Gemini only returns text — no image is generated!
                    generationConfig = new
                    {
                        responseModalities = new[] { "IMAGE", "TEXT" }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                };

                using var response = await SharedHttpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync();
                    Log($"[Banana] HTTP {(int)response.StatusCode} for '{objectLabel}': {err}");
                    progressCallback?.Invoke($"\u274C Banana HTTP {(int)response.StatusCode}");
                    return null;
                }

                var responseString = await response.Content.ReadAsStringAsync();
                Log($"[Banana] Raw response for '{objectLabel}': {responseString[..Math.Min(400, responseString.Length)]}");

                using var doc = JsonDocument.Parse(responseString);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                {
                    var parts = candidates[0].GetProperty("content").GetProperty("parts");
                    // Gemini image response: iterate parts to find one with inlineData
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("inlineData", out var inlineData))
                        {
                            string? b64 = inlineData.GetProperty("data").GetString();
                            if (!string.IsNullOrEmpty(b64))
                            {
                                var enhanced = Convert.FromBase64String(b64);

                                // Track cost
                                double framingScaleUsd = _settings.BananaFramingScale > 0 ? _settings.BananaFramingScale * 0.01 : 0;
                                _totalBananaCost += (GetBananaModelPricePerImage() + framingScaleUsd) * 0.94;
                                DispatcherQueue.TryEnqueue(() => UpdateTotalCostDisplay());

                                progressCallback?.Invoke("\u2705 Banana done");
                                return enhanced;
                            }
                        }
                    }
                    // Parts present but no image — log what came back
                    Log($"[Banana] Response had no inlineData for '{objectLabel}'. Parts: {parts.GetRawText()[..Math.Min(300, parts.GetRawText().Length)]}");
                }
                else
                {
                    Log($"[Banana] No candidates in response for '{objectLabel}': {responseString[..Math.Min(300, responseString.Length)]}");
                }

                progressCallback?.Invoke("\u274C Banana returned no image");
                return null;
            }
            catch (Exception ex)
            {
                Log($"[Banana] Enhancement failed for '{objectLabel}': {ex.Message} — aborting");
                progressCallback?.Invoke("❌ Banana error");
                return null;
            }
        }

        // ── Single core Tripo3D workflow (reused by WS and UI) ──
        private async Task<(string fileName, double tripoCost)> GenerateTripoModelCoreAsync(byte[] imageBytes, string safeName, Action<string>? progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(_settings.TripoApiKey)) throw new InvalidOperationException("Tripo3D API key is missing in settings.");

            progressCallback?.Invoke("Uploading image...");
            using var formData = new MultipartFormDataContent();
            var imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            formData.Add(imageContent, "file", "image.png");

            using var uploadReq = new HttpRequestMessage(HttpMethod.Post, "https://api.tripo3d.ai/v2/openapi/upload") { Content = formData };
            uploadReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.TripoApiKey);

            using var uploadRes = await SharedHttpClient.SendAsync(uploadReq);
            if (!uploadRes.IsSuccessStatusCode) throw new Exception($"Upload error: {await uploadRes.Content.ReadAsStringAsync()}");

            using var uploadDoc = JsonDocument.Parse(await uploadRes.Content.ReadAsStringAsync());
            string fileToken = uploadDoc.RootElement.GetProperty("data").GetProperty("image_token").GetString() ?? throw new Exception("image_token missing.");

            progressCallback?.Invoke("Creating task...");
            var taskPayload = new { type = "image_to_model", file = new { type = "png", file_token = fileToken }, model_version = "Turbo-v1.0-20250506", texture = true, pbr = false, face_limit = 8000, compress = "geometry", enable_image_autofix = true };

            using var taskReq = new HttpRequestMessage(HttpMethod.Post, "https://api.tripo3d.ai/v2/openapi/task")
            {
                Content = new StringContent(JsonSerializer.Serialize(taskPayload), System.Text.Encoding.UTF8, "application/json")
            };
            taskReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.TripoApiKey);

            using var taskRes = await SharedHttpClient.SendAsync(taskReq);
            if (!taskRes.IsSuccessStatusCode) throw new Exception($"Task creation error: {await taskRes.Content.ReadAsStringAsync()}");

            using var taskDoc = JsonDocument.Parse(await taskRes.Content.ReadAsStringAsync());
            string taskId = taskDoc.RootElement.GetProperty("data").GetProperty("task_id").GetString() ?? throw new Exception("task_id missing.");

            string? glbUrl = null;
            double actualCost = 0;
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(2000);
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"https://api.tripo3d.ai/v2/openapi/task/{taskId}");
                pollReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.TripoApiKey);

                using var pollRes = await SharedHttpClient.SendAsync(pollReq);
                if (!pollRes.IsSuccessStatusCode) throw new Exception($"Polling error: {await pollRes.Content.ReadAsStringAsync()}");

                using var pollDoc = JsonDocument.Parse(await pollRes.Content.ReadAsStringAsync());
                var data = pollDoc.RootElement.GetProperty("data");
                string status = data.GetProperty("status").GetString() ?? "";

                progressCallback?.Invoke($"Generating... {(data.TryGetProperty("progress", out var p) ? p.GetInt32() : 0)}%");

                if (status == "success")
                {
                    var output = data.GetProperty("output");
                    glbUrl = output.TryGetProperty("model", out var m) ? m.GetString() : (output.TryGetProperty("pbr_model", out var pbr) ? pbr.GetString() : (output.TryGetProperty("base_model", out var bm) ? bm.GetString() : null));
                    // Read the actual balance_cost the API deducted for this task
                    if (data.TryGetProperty("balance_cost", out var bc))
                        actualCost = bc.ValueKind == JsonValueKind.Number ? bc.GetDouble() : 0;
                    break;
                }
                else if (status is "failed" or "cancelled" or "banned" or "expired") throw new Exception($"Tripo3D task {status}");
            }

            if (string.IsNullOrEmpty(glbUrl)) throw new TimeoutException("Tripo3D generation timed out.");

            progressCallback?.Invoke("Downloading GLB...");
            byte[] glbBytes = await SharedHttpClient.GetByteArrayAsync(glbUrl);
            string glbFileName = $"{safeName}_3DModel.glb";
            await File.WriteAllBytesAsync(Path.Combine(LibraryPath, glbFileName), glbBytes);

            return (glbFileName, actualCost);
        }

        private async void Generate3DModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LibraryItemViewModel item)
            {
                var configData = _libraryConfig.FirstOrDefault(x => x.Name == item.Name);
                if (configData == null) return;
                await Generate3DModel_ApiAsync(btn, item, configData);
            }
        }

        private async Task Generate3DModel_ApiAsync(Button btn, LibraryItemViewModel item, LibraryItemConfig configData)
        {
            if (string.IsNullOrWhiteSpace(_settings.TripoApiKey))
            {
                await ShowDialogAsync("Clé API manquante", "Veuillez définir la clé Tripo3D API dans les paramètres.");
                return;
            }

            btn.IsEnabled = false;
            string originalContent = btn.Content?.ToString() ?? "To 3D Model";

            _ = Task.Run(async () =>
            {
                bool generationSuccess = false;
                try
                {
                    byte[] rawImgBytes = await File.ReadAllBytesAsync(Path.Combine(LibraryPath, configData.ImageFileName));
                    string safeName = GetSafeFileName(item.Name);

                    // Phase 1 — Banana: enhance the OpenCV crop to a clean white-bg square
                    byte[]? imgBytes = await RunBananaEnhancementAsync(rawImgBytes, item.Name,
                        msg => DispatcherQueue.TryEnqueue(() => btn.Content = msg));

                    if (imgBytes == null)
                    {
                        // Banana returned no image — do NOT proceed to Tripo3D
                        DispatcherQueue.TryEnqueue(() => btn.Content = "❌ Banana failed");
                        return;
                    }

                    // Phase 2 — Tripo3D: generate the 3D model from the enhanced image
                    var (glbFileName, tripoCost) = await GenerateTripoModelCoreAsync(imgBytes, safeName, msg => DispatcherQueue.TryEnqueue(() => btn.Content = msg));

                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        configData.ModelFileName = glbFileName;
                        await SaveLibraryAsync();
                        generationSuccess = true;
                        _totalTripo3dCost += tripoCost;
                        UpdateTotalCostDisplay();
                    });
                }
                catch (Exception ex)
                {
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try { await new ContentDialog { Title = "Erreur Tripo3D API", Content = ex.Message, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot }.ShowAsync(); } catch { }
                    });
                }
                finally
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (generationSuccess)
                        {
                            btn.Content = "Generated";
                            btn.Background = new SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
                            btn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
                        }
                        else
                        {
                            btn.Content = originalContent;
                            btn.IsEnabled = true;
                        }
                    });
                }
            });
        }

        private void Generate3DModel_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LibraryItemViewModel item)
            {
                var configData = _libraryConfig.FirstOrDefault(c => string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                string glbPath = configData != null && !string.IsNullOrEmpty(configData.ModelFileName) ? Path.Combine(LibraryPath, configData.ModelFileName) : Path.Combine(LibraryPath, $"{GetSafeFileName(item.Name)}_3DModel.glb");

                if (File.Exists(glbPath))
                {
                    btn.Content = "Generated";
                    btn.Background = new SolidColorBrush(Microsoft.UI.Colors.SeaGreen);
                    btn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
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

        private void Preview3DBtn_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb && tb.DataContext is LibraryItemViewModel item)
            {
                var configData = _libraryConfig.FirstOrDefault(c => string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                bool hasModel = configData != null && !string.IsNullOrEmpty(configData.ModelFileName) && File.Exists(Path.Combine(LibraryPath, configData.ModelFileName));

                var parent = tb.Parent as FrameworkElement;
                while (parent != null && parent is not Button) parent = parent.Parent as FrameworkElement;
                if (parent is Button btn) btn.Visibility = hasModel ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void Preview3DModel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not LibraryItemViewModel item) return;
            var configData = _libraryConfig.FirstOrDefault(c => string.Equals(c.Name, item.Name, StringComparison.OrdinalIgnoreCase));
            if (configData == null || string.IsNullOrEmpty(configData.ModelFileName) || !File.Exists(Path.Combine(LibraryPath, configData.ModelFileName)))
            {
                await ShowDialogAsync("No 3D Model", "Generate a 3D model first using the 'To 3D Model' button.");
                return;
            }

            string modelUrl = $"http://library.local/{Uri.EscapeDataString(configData.ModelFileName)}";
            string viewerUrl = $"http://app.local/model_preview.html?url={Uri.EscapeDataString(modelUrl)}";

            var webView = new WebView2 { Width = 560, Height = 420 };
            var dialog = new ContentDialog { Title = $"3D Preview — {item.Name}", Content = webView, CloseButtonText = "Close", XamlRoot = this.Content.XamlRoot };

            webView.Loaded += async (_, _) =>
            {
                try
                {
                    await webView.EnsureCoreWebView2Async();
                    webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                    string assetsDir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "Assets");
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local", assetsDir, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                    webView.CoreWebView2.SetVirtualHostNameToFolderMapping("library.local", LibraryPath, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                    webView.Source = new Uri(viewerUrl);
                }
                catch (Exception ex) { Log($"[3D Preview] WebView init failed: {ex.Message}"); }
            };

            await dialog.ShowAsync();
            try { webView.Close(); } catch { }
        }

        private async void DeleteLibraryObject_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is LibraryItemViewModel item)
            {
                _libraryItems.Remove(item);
                var configData = _libraryConfig.FirstOrDefault(x => string.Equals(x.Name, item.Name, StringComparison.OrdinalIgnoreCase));

                if (configData != null)
                {
                    _libraryConfig.Remove(configData);
                    await SaveLibraryAsync();
                    try
                    {
                        string imgPath = Path.Combine(LibraryPath, configData.ImageFileName);
                        if (File.Exists(imgPath)) File.Delete(imgPath);

                        if (!string.IsNullOrEmpty(configData.ModelFileName))
                        {
                            string linkedGlbPath = Path.Combine(LibraryPath, configData.ModelFileName);
                            if (File.Exists(linkedGlbPath)) File.Delete(linkedGlbPath);
                        }

                        string safeName = GetSafeFileName(item.Name);
                        string glbPath = Path.Combine(LibraryPath, $"{safeName}_3DModel.glb");
                        if (File.Exists(glbPath)) File.Delete(glbPath);
                    }
                    catch { }
                }

                var sceneItems = _detectedObjects.Where(x => string.Equals(x.Name, item.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var sceneItem in sceneItems)
                {
                    sceneItem.IsAlreadyInLibrary = false;
                    var idx = _detectedObjects.IndexOf(sceneItem);
                    if (idx >= 0) _detectedObjects[idx] = sceneItem;
                }
            }
        }

        private async void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(LibraryPath))
                {
                    await Task.Run(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{LibraryPath}\"",
                        UseShellExecute = true
                    }));
                }
            }
            catch { }
        }

        private void OpenLibraryJson_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!File.Exists(LibraryJsonPath)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = LibraryJsonPath,
                    UseShellExecute = true   // opens with default .json handler (VS Code / Notepad etc.)
                });
            }
            catch { }
        }

        /// <summary>Shows the red ⚠ overlay on the thumbnail if the image file is missing on disk.</summary>
        private void ThumbnailMissing_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is LibraryItemViewModel vm)
                el.Visibility = vm.HasImage ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Shows the orange '⚠ Missing' badge when JSON records a model but the GLB is gone from disk.</summary>
        private void ModelMissing_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is LibraryItemViewModel vm)
            {
                // Show "Missing" only when JSON has a model name BUT the file is gone.
                // The "✓ Model" badge (Preview3DBtn_Loaded) shows when HasModel=true.
                var cfg = _libraryConfig?.FirstOrDefault(c =>
                    string.Equals(c.Name, vm.Name, StringComparison.OrdinalIgnoreCase));
                bool jsonHasModel = cfg != null && !string.IsNullOrEmpty(cfg.ModelFileName);
                el.Visibility = (jsonHasModel && !vm.HasModel) ? Visibility.Visible : Visibility.Collapsed;
            }
        }


        // ════════════════════════════════════════════════════════════════════════
        // LOGS, UTILS & APP SETTINGS
        // ════════════════════════════════════════════════════════════════════════

        private class LogEntry
        {
            public string Message { get; set; } = string.Empty;
            public int Count { get; set; } = 1;
            public Run? RunNode { get; set; }
            public Paragraph? ParagraphNode { get; set; }
        }

        private readonly List<LogEntry> _recentLogs = [];
        private bool _isUserScrolledUp;

        private void Log(string message)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                var color = Microsoft.UI.Colors.LightGray;
                if (message.Contains("Error") || message.Contains("Failed") || message.Contains("Critical") || message.Contains("Exception")) color = Microsoft.UI.Colors.Red;
                else if (message.Contains("Warning") || message.Contains("Timeout") || message.Contains("Pending")) color = Microsoft.UI.Colors.Orange;
                else if (message.Contains("Connected") || message.Contains("Success") || message.Contains('✓') || message.Contains("Ready")) color = Microsoft.UI.Colors.LightGreen;
                else if (message.Contains("[Relay]")) color = Microsoft.UI.Colors.Cyan;
                else if (message.Contains("[ROS]")) color = Microsoft.UI.Colors.Magenta;
                else if (message.Contains("[Bridge]")) color = Microsoft.UI.Colors.Yellow;

                var existing = _recentLogs.FirstOrDefault(l => l.Message == message);

                if (existing != null && existing.RunNode != null && existing.ParagraphNode != null)
                {
                    existing.Count++;
                    existing.RunNode.Text = $"[{DateTime.Now:HH:mm:ss}] {message}  ×{existing.Count}";
                    if (ConsoleLog.Blocks.Contains(existing.ParagraphNode))
                    {
                        ConsoleLog.Blocks.Remove(existing.ParagraphNode);
                        ConsoleLog.Blocks.Add(existing.ParagraphNode);
                    }
                    _recentLogs.Remove(existing); _recentLogs.Add(existing);
                }
                else
                {
                    var run = new Run() { Text = $"[{DateTime.Now:HH:mm:ss}] {message}", Foreground = new SolidColorBrush(color) };
                    var p = new Paragraph(); p.Inlines.Add(run);
                    ConsoleLog.Blocks.Add(p);
                    if (ConsoleLog.Blocks.Count > 300) ConsoleLog.Blocks.RemoveAt(0);

                    var newEntry = new LogEntry { Message = message, Count = 1, RunNode = run, ParagraphNode = p };
                    _recentLogs.Add(newEntry);
                    if (_recentLogs.Count > 20) _recentLogs.RemoveAt(0);
                }

                if (!_isUserScrolledUp && LogScroll != null)
                {
                    LogScroll.UpdateLayout();
                    LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
                }
            });
        }

        private void LogScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (LogScroll == null) return;
            _isUserScrolledUp = (LogScroll.ScrollableHeight - LogScroll.VerticalOffset) > 40;
            ScrollToBottomBtn.Visibility = _isUserScrolledUp ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ScrollToBottomBtn_Click(object sender, RoutedEventArgs e)
        {
            _isUserScrolledUp = false; ScrollToBottomBtn.Visibility = Visibility.Collapsed;
            LogScroll.UpdateLayout(); LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, false);
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            DashboardView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;
            ContextView.Visibility = Visibility.Collapsed;
            Preview3DView.Visibility = Visibility.Collapsed;

            if (args.IsSettingsSelected) { NavView.IsPaneOpen = true; SettingsView.Visibility = Visibility.Visible; }
            else if (args.SelectedItem is NavigationViewItem item && item.Tag != null)
            {
                switch (item.Tag.ToString())
                {
                    case "home": NavView.IsPaneOpen = true; DashboardView.Visibility = Visibility.Visible; break;
                    case "settings": NavView.IsPaneOpen = true; SettingsView.Visibility = Visibility.Visible; break;
                    case "context": NavView.IsPaneOpen = true; ContextView.Visibility = Visibility.Visible; break;
                    case "preview3d":
                        NavView.IsPaneOpen = false; Preview3DView.Visibility = Visibility.Visible;
                        if (CalibCameraComboBox.Items.Count == 0) PopulateCalibCameraList();
                        _ = StartScene3DServerAsync();
                        break;
                }
            }
        }

        private void SettingsToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay == null) return;
            bool isOpen = SettingsOverlay.Visibility == Visibility.Visible;
            SettingsOverlay.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
            if (!isOpen) LoadSettingsIntoUI();
        }

        private void SettingsOverlayBackdrop_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (SettingsOverlay != null) SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void LoadSettingsIntoUI()
        {
            try
            {
                RelayPortInput.Text = _settings.RelayPort.ToString();
                RobotIpInput.Text = _settings.RobotIp;
                Robot2IpInput.Text = _settings.Robot2Ip;
                OrangeApiKeyInput.Password = _settings.OrangeApiKey;
                OrangeApiUrlInput.Text = _settings.OrangeApiUrl;
                GeminiApiKeyInput.Password = _settings.GeminiApiKey;
                TripoApiKeyInput.Password = _settings.TripoApiKey;
                BananaPromptTextBox.Text = string.IsNullOrWhiteSpace(_settings.BananaPromptTemplate) ? DefaultBananaPrompt : _settings.BananaPromptTemplate;
                BananaScaleSlider.Value = _settings.BananaFramingScale <= 0 ? 0.6 : _settings.BananaFramingScale;
                BananaModelComboBox.SelectedIndex = _settings.BananaModel switch
                {
                    "gemini-3.1-flash-image-preview" => 1,
                    "gemini-3-pro-image-preview" => 2,
                    _ => 0
                };
            }
            catch { }
        }

        private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("Saving settings and restarting services...");
                if (int.TryParse(RelayPortInput.Text, out int port))
                {
                    _settings.RelayPort = port;
                    _relayServer.Port = port;
                }

                _settings.RobotIp = RobotIpInput.Text.Trim();
                _settings.Robot2Ip = Robot2IpInput.Text.Trim();
                _settings.OrangeApiKey = OrangeApiKeyInput.Password.Trim();
                _settings.OrangeApiUrl = OrangeApiUrlInput.Text.Trim();
                _settings.GeminiApiKey = GeminiApiKeyInput.Password.Trim();
                _settings.TripoApiKey = TripoApiKeyInput.Password.Trim();

                _broadcastServer.WhisperApiKey = _settings.OrangeApiKey;
                _broadcastServer.WhisperApiUrl = _settings.OrangeApiUrl;

                _settings.BananaModel = BananaModelComboBox.SelectedIndex switch
                {
                    _ => "gemini-2.5-flash-image"
                };

                _settings.BananaPromptTemplate = BananaPromptTextBox.Text;
                _settings.BananaFramingScale = BananaScaleSlider.Value;
                _settings.Save();

                _robotBridge.RosIp = SanitizeIp(_settings.RobotIp);
                _robotBridge2.RosIp = SanitizeIp(_settings.Robot2Ip);
                _robotBridge.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";
                _robotBridge2.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";

                try
                {
                    Log("Stopping services...");
                    await _robotBridge.StopAsync();
                    await _robotBridge2.StopAsync();
                    await _relayServer.StopAsync();
                }
                catch (Exception stopEx) { Log($"[Warning] Service stop failed: {stopEx.Message}"); }

                await Task.Delay(1000);

                try
                {
                    _ = Task.Run(async () => await _relayServer.StartAsync());
                    _robotBridge.Start();
                    _robotBridge2.Start();

                    UpdateRobotStatus(false); UpdateRobot2Status(false);

                    await ShowDialogAsync("Settings Saved", "Your settings have been saved and applied.");
                }
                catch (Exception startEx) { Log($"[Error] Service restart failed: {startEx.Message}"); }
            }
            catch (Exception ex) { Log($"[Critical] Save Settings Error: {ex.Message}"); }
        }

        private void BananaModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null || BananaModelComboBox == null) return;
            _settings.BananaModel = BananaModelComboBox.SelectedIndex switch
            {
                _ => "gemini-2.5-flash-image"
            };
            UpdateBananaCost();
        }

        private async void CameraRobotRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_cameraRobotSwitching || _settings == null || sender is not RadioButton rb) return;
            int selectedRobot = rb.Tag?.ToString() == "2" ? 2 : 1;
            if (selectedRobot == _settings.CameraRobot) return;

            _settings.CameraRobot = selectedRobot;
            _settings.Save();
            await ApplyCameraRobotAsync(selectedRobot);
        }

        private async Task ApplyCameraRobotAsync(int cameraRobot)
        {
            bool r1HasCam = cameraRobot != 2;
            bool r2HasCam = cameraRobot == 2;
            await _robotBridge.SetCameraEnabledAsync(r1HasCam);
            await _robotBridge2.SetCameraEnabledAsync(r2HasCam);

            UpdateVideoFeedTitle(cameraRobot);
            string robotIdStr = r2HasCam ? "Robot_Niryo_02" : "Robot_Niryo_01";
            BroadcastCameraRobotToUnity(robotIdStr);
            Log($"📷 Camera switched to Robot {cameraRobot} ({robotIdStr})");
        }

        private void UpdateVideoFeedTitle(int cameraRobot)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                _ = _broadcastServer.BroadcastAsync("setCameraRobot", JsonSerializer.Serialize(new { robotIdx = cameraRobot - 1 }));
            });
        }

        private static void BroadcastCameraRobotToUnity(string robotId)
        {
            var manager = RelayServerHost.CurrentManager;
            if (manager == null) return;
            string msg = JsonSerializer.Serialize(new { op = "camera_robot_changed", cameraRobotId = robotId, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            _ = Task.Run(async () => { try { await manager.BroadcastToAllUnityClients(msg); } catch { } });
        }

        // ════════════════════════════════════════════════════════════════════════
        // SCENE 3D WEBSOCKET SERVER & CALIBRATION 
        // ════════════════════════════════════════════════════════════════════════

        private async Task StartScene3DServerAsync()
        {
            if (_broadcastServer.IsRunning) return;
            try
            {
                string assetsDir = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "Assets");

                if (!_broadcastServer.IsRunning)
                {
                    _broadcastServer.AssetsPath = assetsDir;
                    _broadcastServer.LibraryPath = LibraryPath;
                    _broadcastServer.WhisperApiUrl = _settings.OrangeApiUrl;
                    _broadcastServer.WhisperApiKey = _settings.OrangeApiKey;

                    _broadcastServer.OnClientConnected += async () =>
                    {
                        var pose = _lastValidPose ?? _savedPose;
                        if (pose != null)
                        {
                            var poseObj = new { pose.X, pose.Y, pose.Z, pose.Rx, pose.Ry, pose.Rz, pose.R11, pose.R12, pose.R13, pose.R21, pose.R22, pose.R23, pose.R31, pose.R32, pose.R33 };
                            await _broadcastServer.BroadcastAsync("setCameraPose", JsonSerializer.Serialize(poseObj));
                        }
                        await PushObjectsToSceneAsync();
                    };

                    _ = Task.Run(() => _broadcastServer.StartAsync());
                    Log($"[Scene3D] Broadcast server starting on port {Scene3dBroadcastServer.DefaultPort}");

                    _broadcastServer.OnBrowserMessage += (raw) => { try { HandleClientBrowserMessage(raw); } catch { } };
                    UpdateScene3dUrlCard();
                }

                if (File.Exists(CameraCalibrationService.SavedPosePath))
                {
                    string poseJson = await File.ReadAllTextAsync(CameraCalibrationService.SavedPosePath);
                    _savedPose = JsonSerializer.Deserialize<CameraPose>(poseJson);
                    _lastValidPose = _savedPose;
                    _isCalibFrozen = true;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        FreezeCalibToggle.IsOn = true; FreezeCalibToggle.IsEnabled = true;
                        CalibDetectionIcon.Glyph = "\uE73E";
                        CalibDetectionStatus.Text = "Grid detected — Loaded from saved calibration";
                        CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                        CalibDetectionIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                    });
                }

                for (int i = 0; i < 10 && !_broadcastServer.IsRunning; i++) await Task.Delay(50);
                if (_lastValidPose != null) await PushCameraPoseAsync(_lastValidPose);
                await PushObjectsToSceneAsync();
            }
            catch (Exception ex) { Log($"[3D Preview] Server init failed: {ex.Message}"); }
        }

        private async Task PushObjectsToSceneAsync()
        {
            if (!_broadcastServer.IsRunning) return;
            try
            {
                // Synchronize snapshot capture on UI Thread to avoid InvalidOperationException
                List<DetectedObjectViewModel> snapshot = [];
                var tcs = new TaskCompletionSource();
                DispatcherQueue.TryEnqueue(() =>
                {
                    snapshot = _detectedObjects.ToList();
                    tcs.SetResult();
                });
                await tcs.Task;

                double fx = CameraCalibrationService.Fx == 0 ? 1000 : CameraCalibrationService.Fx;
                double fy = CameraCalibrationService.Fy == 0 ? 1000 : CameraCalibrationService.Fy;
                double cx = CameraCalibrationService.Cx, cy = CameraCalibrationService.Cy;
                int frameW = CameraCalibrationService.FrameW, frameH = CameraCalibrationService.FrameH;

                var pose = _lastValidPose ?? _savedPose;
                double camX = pose?.X ?? 0, camY = pose?.Y ?? 0, camZ = pose?.Z ?? 1.0;
                double r11 = pose?.R11 ?? 1.0, r12 = pose?.R12 ?? 0.0, r13 = pose?.R13 ?? 0.0;
                double r21 = pose?.R21 ?? 0.0, r22 = pose?.R22 ?? 1.0, r23 = pose?.R23 ?? 0.0;
                double r31 = pose?.R31 ?? 0.0, r32 = pose?.R32 ?? 0.0, r33 = pose?.R33 ?? 1.0;
                double safeCamZ = Math.Max(0.01, Math.Abs(camZ));

                var items = snapshot.Select(obj =>
                {
                    double uNorm = (obj.UvXmin + obj.UvXmax) / 2.0 / 1000.0, vNorm = (obj.UvYmin + obj.UvYmax) / 2.0 / 1000.0;
                    double pixU = uNorm * frameW, pixV = vNorm * frameH;
                    double rayCamX = (pixU - cx) / fx, rayCamY = (pixV - cy) / fy, rayCamZ = 1.0;
                    double rayObjX = r11 * rayCamX + r12 * rayCamY + r13 * rayCamZ;
                    double rayObjY = r21 * rayCamX + r22 * rayCamY + r23 * rayCamZ;
                    double rayObjZ = r31 * rayCamX + r32 * rayCamY + r33 * rayCamZ;

                    double t = 0; if (rayObjZ < -1e-4) t = -safeCamZ / rayObjZ;
                    double worldX = camX, worldY = camY, sizeW = 0.05, sizeH = 0.05;

                    if (t > 0)
                    {
                        worldX = camX + (t * rayObjX); worldY = camY + (t * rayObjY);
                        sizeW = t * ((obj.UvXmax - obj.UvXmin) / 1000.0) * frameW / fx;
                        sizeH = t * ((obj.UvYmax - obj.UvYmin) / 1000.0) * frameH / fy;
                    }

                    var libItem = _libraryConfig.FirstOrDefault(x => string.Equals(x.Name, obj.Name, StringComparison.OrdinalIgnoreCase));
                    string modelUrl = libItem != null && !string.IsNullOrEmpty(libItem.ModelFileName) ? $"http://library.local/{libItem.ModelFileName}" : "";
                    string modelUrlRemote = libItem != null && !string.IsNullOrEmpty(libItem.ModelFileName) ? $"/library/{libItem.ModelFileName}" : "";

                    var thumbBytes = obj.ThumbJpgBytes ?? obj.CropJpgBytes;
                    string cropBase64 = thumbBytes != null && thumbBytes.Length > 0 ? "data:image/jpeg;base64," + Convert.ToBase64String(thumbBytes) : "";

                    return new
                    {
                        label = obj.Name,
                        worldX,
                        worldY,
                        sizeW,
                        sizeH,
                        angleRad = obj.AngleDegrees * Math.PI / 180.0,
                        modelUrl,
                        modelUrlRemote,
                        cropBase64,
                        hasModel = !string.IsNullOrEmpty(modelUrlRemote),
                        isInLibrary = obj.IsAlreadyInLibrary,
                        // Correction offsets so Three.js can re-orient poorly-generated models
                        offsetRx    = libItem?.OffsetRx    ?? 0.0,
                        offsetRy    = libItem?.OffsetRy    ?? 0.0,
                        offsetRz    = libItem?.OffsetRz    ?? 0.0,
                        offsetScale = libItem?.OffsetScale ?? 1.0
                    };
                }).ToList();

                _ = _broadcastServer.BroadcastAsync("setDetectedObjects", JsonSerializer.Serialize(items));

                // Scan (Orange proxy) is free — only Banana + Tripo costs tracked
                var costs = new { scanEur = 0.0, bananaEur = Math.Round(_totalBananaCost, 4), tripoEur = Math.Round(_totalTripo3dCost, 4), totalEur = Math.Round(_totalBananaCost + _totalTripo3dCost, 4) };
                _ = _broadcastServer.BroadcastAsync("setScanCosts", JsonSerializer.Serialize(costs));

                if (_lastValidPose != null) await PushCameraPoseAsync(_lastValidPose);
            }
            catch (Exception ex) { Log($"[3D Preview] Push failed: {ex.Message}"); }
        }

        private async Task PushCameraPoseAsync(CameraPose pose)
        {
            if (!_broadcastServer.IsRunning) return;
            try
            {
                var poseObj = new { pose.X, pose.Y, pose.Z, pose.Rx, pose.Ry, pose.Rz, pose.R11, pose.R12, pose.R13, pose.R21, pose.R22, pose.R23, pose.R31, pose.R32, pose.R33 };
                _ = _broadcastServer.BroadcastAsync("setCameraPose", JsonSerializer.Serialize(poseObj));
            }
            catch { }
        }

        private async void LivePosePusher(CameraPose pose)
        {
            if (pose.IsValid)
            {
                await PushCameraPoseAsync(pose);
                if (_detectedObjects.Count > 0) await PushObjectsToSceneAsync();
            }
        }

        private static string BuildLearningModeCommand(bool activate) => JsonSerializer.Serialize(new
        { op = "call_service", service = "/niryo_robot/learning_mode/activate", type = "niryo_robot_msgs/SetBool", args = new { value = activate } });

        private static string BuildHomeCommand() => JsonSerializer.Serialize(new
        {
            op = "publish",
            topic = "/niryo_robot_follow_joint_trajectory_controller/command",
            type = "trajectory_msgs/JointTrajectory",
            msg = new
            {
                header = new { seq = 0, stamp = new { secs = 0, nsecs = 0 }, frame_id = "" },
                joint_names = new[] { "joint_1", "joint_2", "joint_3", "joint_4", "joint_5", "joint_6" },
                points = new[] { new { positions = new double[6], velocities = new double[6], accelerations = Array.Empty<double>(), effort = Array.Empty<double>(), time_from_start = new { secs = 4, nsecs = 0 } } }
            }
        });

        private async void R1LearningToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingToggle) return;
            bool on = R1LearningToggle.IsOn;
            bool ok = await SendDebugCommand(_robotBridge, BuildLearningModeCommand(on));
            Log(ok ? $"✅ R1 — Learning mode {(on ? "ON" : "OFF")}" : "❌ R1 — Learning mode: ROS not connected");
        }

        private async void R2LearningToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_updatingToggle) return;
            bool on = R2LearningToggle.IsOn;
            bool ok = await SendDebugCommand(_robotBridge2, BuildLearningModeCommand(on));
            Log(ok ? $"✅ R2 — Learning mode {(on ? "ON" : "OFF")}" : "❌ R2 — Learning mode: ROS not connected");
        }

        private async void R1HomeButton_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await SendDebugCommand(_robotBridge, BuildHomeCommand());
            Log(ok ? "✅ R1 — Moving to home" : "❌ R1 — Home: ROS not connected");
        }

        private async void R2HomeButton_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await SendDebugCommand(_robotBridge2, BuildHomeCommand());
            Log(ok ? "✅ R2 — Moving to home" : "❌ R2 — Home: ROS not connected");
        }

        private async void R1CalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await SendDebugCommand(_robotBridge, BuildRequestNewCalibration())) { Log("❌ R1 — Calibration: ROS not connected"); return; }
            Log("⏳ R1 — Calibration requested, waiting...");
            await Task.Delay(1500);
            Log(await SendDebugCommand(_robotBridge, BuildCalibrationCommand()) ? "✅ R1 — Auto-calibration started" : "❌ R1 — failed");
        }

        private async void R2CalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await SendDebugCommand(_robotBridge2, BuildRequestNewCalibration())) { Log("❌ R2 — Calibration: ROS not connected"); return; }
            Log("⏳ R2 — Calibration requested, waiting...");
            await Task.Delay(1500);
            Log(await SendDebugCommand(_robotBridge2, BuildCalibrationCommand()) ? "✅ R2 — Auto-calibration started" : "❌ R2 — failed");
        }

        private static string BuildRequestNewCalibration() => JsonSerializer.Serialize(new { op = "call_service", service = "/niryo_robot/joints_interface/request_new_calibration", type = "niryo_robot_msgs/SetInt", args = new { value = 1 } });
        private static string BuildCalibrationCommand() => JsonSerializer.Serialize(new { op = "call_service", service = "/niryo_robot/joints_interface/calibrate_motors", type = "niryo_robot_msgs/SetInt", args = new { value = 1 } });

        private static async Task<bool> SendDebugCommand(RobotBridgeService bridge, string json)
        {
            if (!bridge.IsConnected) return false;
            await bridge.SendDirectToRobotAsync(json);
            return true;
        }

        private void StartNetworkMonitoring()
        {
            _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _networkTimer.Tick += async (s, e) =>
            {
                if (_isNetworkPinging) return;
                _isNetworkPinging = true;

                try
                {
                    double unityLat = 0;
                    string expertTarget = !string.IsNullOrEmpty(RelayServerHost.UnityClientIp) ? RelayServerHost.UnityClientIp : _settings.ExpertIp;
                    if (!string.IsNullOrEmpty(expertTarget) && expertTarget.StartsWith("::ffff:")) expertTarget = expertTarget[7..];

                    bool expertIsLoopback = string.IsNullOrEmpty(expertTarget) || expertTarget is "127.0.0.1" or "localhost" or "::1";
                    try
                    {
                        var reply = await _pinger.SendPingAsync(expertIsLoopback ? "niryo.dmzs-lab.com" : expertTarget, 1000);
                        if (reply.Status == IPStatus.Success) unityLat = reply.RoundtripTime;
                    }
                    catch { }

                    double r1Lat = 0, r2Lat = 0;
                    if (!string.IsNullOrEmpty(_settings.RobotIp)) { try { var reply = await _pinger.SendPingAsync(SanitizeIp(_settings.RobotIp), 500); if (reply.Status == IPStatus.Success) r1Lat = reply.RoundtripTime; } catch { } }
                    if (!string.IsNullOrEmpty(_settings.Robot2Ip)) { try { var reply = await _pinger.SendPingAsync(SanitizeIp(_settings.Robot2Ip), 500); if (reply.Status == IPStatus.Success) r2Lat = reply.RoundtripTime; } catch { } }

                    UpdateDashboardAndDiscovery(unityLat, r1Lat, r2Lat);

                    double internetLat = 0;
                    try { var reply = await _pinger.SendPingAsync("8.8.8.8", 1000); if (reply.Status == IPStatus.Success) { internetLat = reply.RoundtripTime; InternetLatencyText.Text = $"{internetLat} ms"; } } catch { }

                    _unityLatencyHistory.Add(unityLat); if (_unityLatencyHistory.Count > MaxHistory) _unityLatencyHistory.RemoveAt(0);
                    _internetLatencyHistory.Add(internetLat); if (_internetLatencyHistory.Count > MaxHistory) _internetLatencyHistory.RemoveAt(0);
                    UpdateLatencyStats();
                }
                finally { _isNetworkPinging = false; }
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
            bool isExpertWsConnected = RelayServerHost.UnityClientConnected;
            int uCount = _unityLatencyHistory.Count;
            bool isExpertReachable = uLat > 0 || (uCount > 0 && _unityLatencyHistory.Skip(Math.Max(0, uCount - 3)).Any(v => v > 0));
            var cm = RelayServerHost.CurrentManager;
            bool isR1Connected = (cm?.IsRobotConnected("Robot_Niryo_01") ?? false) || _robotBridge.IsConnected;
            bool isR2Connected = (cm?.IsRobotConnected("Robot_Niryo_02") ?? false) || _robotBridge2.IsConnected;

            var successBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var mutedBrush = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            var warnBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];

            bool expertActive = isExpertWsConnected || isExpertReachable;
            RelayActiveText.Text = expertActive ? "ACTIVE" : "WAITING";
            RelayActiveText.Foreground = RelayIcon.Foreground = expertActive ? successBrush : mutedBrush;
            if (RelayStatusIndicator != null) RelayStatusIndicator.Visibility = expertActive ? Visibility.Visible : Visibility.Collapsed;

            string expertDisplayIp = (!string.IsNullOrEmpty(_questPublicIp) ? _questPublicIp : RelayServerHost.UnityClientIp) ?? "";
            if (!string.IsNullOrEmpty(expertDisplayIp) && expertDisplayIp.StartsWith("::ffff:")) expertDisplayIp = expertDisplayIp[7..];
            if (string.IsNullOrEmpty(expertDisplayIp)) expertDisplayIp = _settings.ExpertIp;
            if (string.IsNullOrEmpty(expertDisplayIp)) expertDisplayIp = "--";

            QuestIpText.Text = (!isExpertWsConnected && !isExpertReachable) ? "Offline" : expertDisplayIp;
            R1IpText.Text = isR1Connected ? SanitizeIp(_settings.RobotIp) : "Offline";
            R2IpText.Text = isR2Connected ? SanitizeIp(_settings.Robot2Ip) : "Offline";

            if (isExpertWsConnected) { QuestRelayText.Text = "CONNECTED"; QuestRelayText.Foreground = QuestRelayDot.Fill = successBrush; QuestLocText.Text = string.IsNullOrEmpty(_questLocation) || _questLocation == "Unknown" ? "--" : _questLocation; }
            else if (isExpertReachable) { QuestRelayText.Text = "REACHABLE"; QuestRelayText.Foreground = QuestRelayDot.Fill = warnBrush; QuestLocText.Text = "--"; }
            else { QuestRelayText.Text = "OFFLINE"; QuestRelayText.Foreground = mutedBrush; QuestRelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)); QuestLocText.Text = "--"; }

            R1RelayText.Text = isR1Connected ? "CONNECTED" : "OFFLINE";
            R1RelayText.Foreground = R1RelayDot.Fill = isR1Connected ? successBrush : mutedBrush;
            if (!isR1Connected) R1RelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));

            R2RelayText.Text = isR2Connected ? "CONNECTED" : "OFFLINE";
            R2RelayText.Foreground = R2RelayDot.Fill = isR2Connected ? successBrush : mutedBrush;
            if (!isR2Connected) R2RelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
        }

        private void UpdateLatencyStats()
        {
            void SetStat(List<double> h, TextBlock mi, TextBlock ma, TextBlock av) { var v = h.Where(x => x > 0).ToList(); if (v.Count == 0) { mi.Text = ma.Text = av.Text = "-- ms"; return; } mi.Text = $"{v.Min():F0} ms"; ma.Text = $"{v.Max():F0} ms"; av.Text = $"{v.Average():F0} ms"; }
            SetStat(_unityLatencyHistory, QuestMinText, QuestMaxText, QuestAvgText); SetStat(_internetLatencyHistory, InternetMinText, InternetMaxText, InternetAvgText);
        }

        private void LatencyScaleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _latencyMaxMs = e.NewValue;
            if (ScaleLabel != null) ScaleLabel.Text = $"{_latencyMaxMs:F0}ms";
            if (YLabel75 != null) YLabel75.Text = $"{_latencyMaxMs * 0.75:F0}ms";
            if (YLabel50 != null) YLabel50.Text = $"{_latencyMaxMs * 0.50:F0}ms";
            if (YLabel25 != null) YLabel25.Text = $"{_latencyMaxMs * 0.25:F0}ms";
            if (YLabel0 != null) YLabel0.Text = "0ms";
        }

        private static Windows.UI.Color ColorFromLabel(string label)
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

        private void PopulateCalibCameraList()
        {
            if (_videoDevices == null) return;
            CalibCameraComboBox.Items.Clear();
            foreach (var d in _videoDevices) CalibCameraComboBox.Items.Add(d.Name);
            for (int i = 0; i < _videoDevices.Count; i++)
            {
                if (_videoDevices[i].Name.Contains("XiaoMi", StringComparison.OrdinalIgnoreCase) || _videoDevices[i].Name.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase)) { CalibCameraComboBox.SelectedIndex = i; return; }
            }
            CalibCameraComboBox.SelectedIndex = _videoDevices.Count > 1 ? 1 : (_videoDevices.Count > 0 ? 0 : -1);
        }

        private void CalibCameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_calibService != null) { _calibService.Stop(); SetCalibStopped(); }
            StartCalibDetectionBtn.IsEnabled = CalibCameraComboBox.SelectedIndex >= 0;
            if (_lastValidPose != null && _isCalibFrozen) { FreezeCalibToggle.IsOn = true; FreezeCalibToggle.IsEnabled = true; }
        }

        private void ShowCalibrationPlaneToggle_Toggled(object sender, RoutedEventArgs e) { }
        private void CameraFeedOpacitySlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { }

        private void CameraFovSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (_settings != null && Math.Abs(_settings.CameraFovScale - e.NewValue) > 0.001)
            {
                _settings.CameraFovScale = e.NewValue; _settings.Save();
            }
        }

        private void StartCalibDetectionBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_calibService == null) return;
            if (StartCalibDetectionBtn.Content?.ToString()?.StartsWith('\u25a0') == true) { _calibService.Stop(); SetCalibStopped(); }
            else
            {
                int idx = CalibCameraComboBox.SelectedIndex; if (idx < 0) return;
                try
                {
                    _calibService.OnFrame -= OnCalibFrame; _calibService.OnPose -= OnCalibPose; _calibService.OnPose -= LivePosePusher;
                    _calibService.OnFrame += OnCalibFrame; _calibService.OnPose += OnCalibPose; _calibService.OnPose += LivePosePusher;
                    _calibService.StartDetection(idx);
                    _isCalibFrozen = false; FreezeCalibToggle.IsOn = false; FreezeCalibToggle.IsEnabled = false;
                    CalibOfflineState.Visibility = Visibility.Collapsed; CalibDetectionBanner.Visibility = Visibility.Visible;
                    StartCalibDetectionBtn.Content = "■  Stop Detection"; Log($"[Calib] Detection started (camera index {idx})");
                }
                catch (Exception ex) { Log($"[Calib] Failed to start detection: {ex.Message}"); }
            }
        }

        private void OnCalibFrame(byte[] jpeg)
        {
            _latestWebcamFrameBytes = jpeg;
            DispatcherQueue?.TryEnqueue(async () =>
            {
                try
                {
                    if (!_feedFrozen && _broadcastServer != null && _broadcastServer.ConnectedClients > 0)
                        _ = _broadcastServer.BroadcastAsync("updateCameraFeed", JsonSerializer.Serialize($"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}"));

                    if (Preview3DView.Visibility == Visibility.Visible)
                    {
                        CalibCameraPreview.Source = await LoadImageFromBytesAsync(jpeg);
                    }

                    ContextWebcamPreview.Source = await LoadImageFromBytesAsync(jpeg);
                }
                catch { }
            });
        }

        private void OnCalibPose(CameraPose pose)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isCalibFrozen) return;
                if (pose.IsValid)
                {
                    _lastValidPose = pose;
                    CalibDetectionIcon.Glyph = "\uE73E"; CalibDetectionStatus.Text = "Grid detected — pose estimated";
                    CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                    CalibDetectionIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 204, 106));
                    FreezeCalibToggle.IsEnabled = true;
                }
                else
                {
                    CalibDetectionIcon.Glyph = "\uE783"; CalibDetectionStatus.Text = "Grid not detected";
                    CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 68, 68));
                    CalibDetectionIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 68, 68));
                    if (_lastValidPose == null) FreezeCalibToggle.IsEnabled = false;
                }
            });
        }

        private void CalibCopyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_lastValidPose == null) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText($"tvec: X={_lastValidPose.X:0.000} Y={_lastValidPose.Y:0.000} Z={_lastValidPose.Z:0.000}\n" +
                       $"rvec: Rx={_lastValidPose.Rx:0.000} Ry={_lastValidPose.Ry:0.000} Rz={_lastValidPose.Rz:0.000}");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); Log("[Calib] Pose copied to clipboard.");
        }

        private void FreezeCalibToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (FreezeCalibToggle.IsOn != true)
            {
                _isCalibFrozen = false; FreezeCalibToggle.IsEnabled = _lastValidPose != null;
                CalibDetectionIcon.Glyph = "\uE783"; CalibDetectionStatus.Text = "Grid not detected (Refreshing...)";
                CalibDetectionStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 68, 68));
                if (_calibService != null) { _calibService.OnPose -= LivePosePusher; _calibService.OnPose += LivePosePusher; }
                return;
            }

            if (_lastValidPose == null) { FreezeCalibToggle.IsOn = false; return; }
            try
            {
                string jsonPath = CameraCalibrationService.SavedPosePath;
                string json = JsonSerializer.Serialize(new { _lastValidPose.X, _lastValidPose.Y, _lastValidPose.Z, _lastValidPose.Rx, _lastValidPose.Ry, _lastValidPose.Rz, _lastValidPose.TvecX, _lastValidPose.TvecY, _lastValidPose.TvecZ, _lastValidPose.R11, _lastValidPose.R12, _lastValidPose.R13, _lastValidPose.R21, _lastValidPose.R22, _lastValidPose.R23, _lastValidPose.R31, _lastValidPose.R32, _lastValidPose.R33 });

                _ = Task.Run(async () =>
                {
                    await File.WriteAllTextAsync(jsonPath, json);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _isCalibFrozen = true;
                        if (_calibService != null) _calibService.OnPose -= LivePosePusher;
                        FreezeCalibToggle.IsEnabled = true;
                        Log($"[Calib] Pose saved to {jsonPath}");
                    });
                });
            }
            catch (Exception ex) { FreezeCalibToggle.IsOn = false; Log($"[Calib] Save failed: {ex.Message}"); }
        }

        private void SetCalibStopped()
        {
            if (_calibService != null) _calibService.OnPose -= LivePosePusher;
            if (!_isCalibFrozen) { FreezeCalibToggle.IsOn = false; FreezeCalibToggle.IsEnabled = _lastValidPose != null; }
            StartCalibDetectionBtn.Content = "▶  Start Detection"; CalibDetectionBanner.Visibility = Visibility.Collapsed;
            Log("[Calib] Detection stopped.");
        }

        private void HandleClientBrowserMessage(string jsonStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                if (!doc.RootElement.TryGetProperty("type", out var tProp)) return;
                var type = tProp.GetString();

                if (!doc.RootElement.TryGetProperty("payload", out var payload)) doc.RootElement.TryGetProperty("value", out payload);

                bool isLiveFeed = false, isOpacity = false, isFov = false, isFreezeCalib = false, boolVal = false;
                double numVal = 0;

                if (type == "freezeFeed") { isLiveFeed = true; boolVal = payload.ValueKind == JsonValueKind.True || (payload.ValueKind == JsonValueKind.String && payload.GetString() == "true"); }
                else if (type == "opacity") { isOpacity = true; if (payload.ValueKind == JsonValueKind.Number) numVal = payload.GetDouble(); }
                else if (type == "fov") { isFov = true; if (payload.ValueKind == JsonValueKind.Number) numVal = payload.GetDouble(); }
                else if (type == "freezeCalib") { isFreezeCalib = true; boolVal = payload.ValueKind == JsonValueKind.True || (payload.ValueKind == JsonValueKind.String && payload.GetString() == "true"); }

                if (type == "voiceTranscription")
                {
                    string transcribedText = payload.ValueKind == JsonValueKind.String ? payload.GetString() ?? "" : payload.GetRawText().Trim('"');
                    if (!string.IsNullOrWhiteSpace(transcribedText))
                    {
                        Log($"[Voice] Transcription: {transcribedText}");
                        _ = _broadcastServer.BroadcastAsync("voiceTranscription", JsonSerializer.Serialize(transcribedText));
                    }
                    return;
                }

                // ── IK joint teleoperation (from browser IK gizmo) ──────────────────
                if (type == "ik_joints" && doc.RootElement.TryGetProperty("payload", out var ikPayload))
                {
                    if (ikPayload.TryGetProperty("angles", out var anglesProp))
                    {
                        int robotIdx = ikPayload.TryGetProperty("robotIdx", out var ri) ? ri.GetInt32() : 0;

                        // Browser sends degrees → convert to radians for ROS
                        var anglesRad = anglesProp.EnumerateArray()
                            .Select(e => e.GetDouble() * Math.PI / 180.0)
                            .ToArray();

                        if (anglesRad.Length == 6)
                        {
                            var bridge = robotIdx == 0 ? _robotBridge : _robotBridge2;
                            if (bridge.IsConnected)
                                _ = bridge.SendDirectToRobotAsync(BuildIkJointCommand(anglesRad));
                        }
                    }
                    return;
                }

                if (type == "refreshScene")
                {
                    _ = Task.Run(async () => await PushObjectsToSceneAsync());
                    return;
                }

                if (type == "scanScene")
                {
                    Log("[Scene3D] Remote scan request received.");
                    DispatcherQueue.TryEnqueue(async () =>
                    {
                        try
                        {
                            if (_latestWebcamFrameBytes == null || _latestWebcamFrameBytes.Length == 0) { Log("[Scene3D] Scan ignored — no camera frame."); _ = _broadcastServer.BroadcastAsync("setDetectedObjects", "[]"); return; }
                            if (_analyzeInProgressFlag == 1) { Log("[Scene3D] Scan ignored — already in progress."); return; }
                            await AnalyzeSceneAsync();
                        }
                        catch (Exception ex) { Log($"[Scene3D] Remote scan error: {ex.Message}"); _ = _broadcastServer.BroadcastAsync("setDetectedObjects", "[]"); }
                    });
                    return;
                }

                if (type == "generate3DModel")
                {
                    string label = payload.TryGetProperty("label", out var lbProp) ? lbProp.GetString() ?? "object" : "object";
                    string cropB64 = payload.TryGetProperty("cropBase64", out var b64Prop) ? b64Prop.GetString() ?? "" : "";
                    Log($"[Scene3D] Generate3D request for '{label}'");

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string b64Data = cropB64.Contains(',') ? cropB64.Split(',')[1] : cropB64;
                            byte[] rawBytes = Convert.FromBase64String(b64Data);
                            string safeName = GetSafeFileName(label);

                            // Phase 1 — Banana: enhance the OpenCV crop
                            Log($"[Scene3D-3D] '{label}' — running Banana enhancement…");
                            byte[]? imgBytes = await RunBananaEnhancementAsync(rawBytes, label,
                                msg =>
                                {
                                    Log($"[Scene3D-3D] {label}: {msg}");
                                    _ = _broadcastServer.BroadcastAsync("setGen3DProgress", JsonSerializer.Serialize(new { label, status = msg }));
                                });

                            if (imgBytes == null)
                            {
                                // Banana returned no image — do NOT send to Tripo3D
                                Log($"[Scene3D-3D] '{label}' — Banana returned no image, aborting 3D generation.");
                                await _broadcastServer.BroadcastAsync("setGen3DProgress", JsonSerializer.Serialize(new { label, status = "❌ Banana failed" }));
                                return;
                            }

                            // Save banana image to library folder so the asset library can display it
                            string bananaFileName = $"{safeName}_banana.png";
                            string bananaPath = Path.Combine(LibraryPath, bananaFileName);
                            await File.WriteAllBytesAsync(bananaPath, imgBytes);
                            Log($"[Scene3D-3D] '{label}' banana image saved: {bananaFileName}");
                            await _broadcastServer.BroadcastAsync("setBananaImage", JsonSerializer.Serialize(new { label, imageUrl = $"/library/{Uri.EscapeDataString(bananaFileName)}" }));

                            // Phase 2 — Tripo3D: generate the 3D mesh
                            var (glbFileName, tripoCost) = await GenerateTripoModelCoreAsync(imgBytes, safeName,
                                msg =>
                                {
                                    Log($"[Scene3D-3D] {label}: {msg}");
                                    _ = _broadcastServer.BroadcastAsync("setGen3DProgress", JsonSerializer.Serialize(new { label, status = msg }));
                                });

                            DispatcherQueue.TryEnqueue(async () =>
                            {
                                var cfg = _libraryConfig.FirstOrDefault(c => string.Equals(c.Name, label, StringComparison.OrdinalIgnoreCase));
                                bool isNewEntry = cfg == null;
                                if (isNewEntry)
                                {
                                    // First time this object gets a 3D model — create the library entry
                                    cfg = new LibraryItemConfig
                                    {
                                        Name = label,
                                        DateAdded = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                                        ColorHex = "#FFFFFF"
                                    };
                                    _libraryConfig.Insert(0, cfg);
                                }
                                cfg.ModelFileName = glbFileName;
                                if (string.IsNullOrEmpty(cfg.ImageFileName)) cfg.ImageFileName = bananaFileName;
                                _ = SaveLibraryAsync();

                                // ── Sync the UI ObservableCollection ──────────────────────
                                var existingVm = _libraryItems.FirstOrDefault(v => string.Equals(v.Name, label, StringComparison.OrdinalIgnoreCase));
                                if (existingVm != null)
                                {
                                    existingVm.HasModel = true;
                                }
                                else
                                {
                                    // Build a new VM with thumbnail
                                    var vm = new LibraryItemViewModel
                                    {
                                        Name = label,
                                        DateAdded = cfg.DateAdded,
                                        HasModel  = true,
                                        HasImage  = !string.IsNullOrEmpty(bananaFileName)
                                    };
                                    try
                                    {
                                        string imgPath = Path.Combine(LibraryPath, bananaFileName);
                                        if (File.Exists(imgPath))
                                        {
                                            byte[] imgBytes = await File.ReadAllBytesAsync(imgPath);
                                            vm.ImageSource = await LoadImageFromBytesAsync(imgBytes);
                                        }
                                    }
                                    catch { }
                                    _libraryItems.Insert(0, vm);
                                }

                                _totalTripo3dCost += tripoCost; UpdateTotalCostDisplay();
                                Log($"[Scene3D-3D] '{label}' GLB saved. Tripo cost: {tripoCost:0.0000} credits");
                            });

                            string glbServeUrl = $"/library/{Uri.EscapeDataString(glbFileName)}";
                            await _broadcastServer.BroadcastAsync("setModelGlb", JsonSerializer.Serialize(new { label, glbUrl = glbServeUrl }));
                            await _broadcastServer.BroadcastAsync("refreshLibrary", "{}");
                        }
                        catch (Exception ex)
                        {
                            Log($"[Scene3D-3D] Error generating 3D for '{label}': {ex.Message}");
                            _ = _broadcastServer.BroadcastAsync("setGen3DProgress", JsonSerializer.Serialize(new { label, status = $"❌ Error" }));
                        }
                    });
                    return;
                }

                DispatcherQueue.TryEnqueue(() =>
                {
                    if (isLiveFeed) { _feedFrozen = boolVal; Log($"[Scene3D] Feed {(_feedFrozen ? "frozen" : "live")}"); }
                    else if (isOpacity) CameraFeedOpacitySlider.Value = numVal;
                    else if (isFov) { if (_settings != null && Math.Abs(_settings.CameraFovScale - numVal) > 0.001) { _settings.CameraFovScale = numVal; _settings.Save(); } }
                    else if (isFreezeCalib) { if (FreezeCalibToggle.IsOn != boolVal) FreezeCalibToggle.IsOn = boolVal; if (!boolVal && StartCalibDetectionBtn.Content?.ToString()?.StartsWith('\u25a0') != true) StartCalibDetectionBtn_Click(null!, null!); }
                });
            }
            catch { }
        }

        private void UpdateScene3dUrlCard()
        {
            const string publicUrl = "https://scene3d.dmzs-lab.com/preview.html";
            DispatcherQueue.TryEnqueue(() => { Scene3dUrlText.Text = publicUrl; Scene3dUrlText.Tag = publicUrl; });
        }

        private void CopyScene3dUrlBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var url = Scene3dUrlText.Tag as string ?? Scene3dUrlText.Text;
            if (string.IsNullOrEmpty(url)) return;
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(url); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            CopyScene3dUrlBtn.Content = "\uE8FB"; _ = Task.Delay(1500).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => CopyScene3dUrlBtn.Content = "\uE8C8"));
        }

        private void OpenScene3dBtn_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var url = Scene3dUrlText.Tag as string ?? Scene3dUrlText.Text;
            if (!string.IsNullOrEmpty(url)) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception ex) { Log($"[Scene3D] Browser failed: {ex.Message}"); }
        }

        // ════════════════════════════════════════════════════════════════════════
        // ROBOT STATUS HELPERS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>Strips ws:// and port from a ROS bridge URL so it can be used for ping.</summary>
        private static string SanitizeIp(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            // e.g. "ws://169.254.200.200:9090" → "169.254.200.200"
            string host = raw.Replace("ws://", "").Replace("wss://", "");
            int colonIdx = host.IndexOf(':');
            if (colonIdx > 0) host = host[..colonIdx];
            int slashIdx = host.IndexOf('/');
            if (slashIdx > 0) host = host[..slashIdx];
            return host.Trim();
        }

        private void UpdateRobotStatus(bool connected)
        {
            var green = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var muted = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            Robot1ActiveText.Text = connected ? "CONNECTED" : "WAITING";
            Robot1ActiveText.Foreground = connected ? green : muted;
            Robot1Icon.Foreground = connected ? green : muted;
            if (Robot1StatusIndicator != null) Robot1StatusIndicator.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateRobot2Status(bool connected)
        {
            var green = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var muted = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            Robot2ActiveText.Text = connected ? "CONNECTED" : "WAITING";
            Robot2ActiveText.Foreground = connected ? green : muted;
            Robot2Icon.Foreground = connected ? green : muted;
            if (Robot2StatusIndicator != null) Robot2StatusIndicator.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void ClearHardwareInfoBox(TextBlock status, TextBlock temp, TextBlock calib, TextBlock motorTemp, TextBlock errors)
        {
            status.Text = "—"; temp.Text = "—"; calib.Text = "—"; motorTemp.Text = "—"; errors.Text = "—";
        }

        private static void UpdateHardwareInfoBox(HardwareInfo hw, TextBlock temp, TextBlock calib, TextBlock motorTemp, TextBlock errors)
        {
            temp.Text      = hw.RpiTemp > 0         ? $"{hw.RpiTemp:0.0}°C"   : "—";
            calib.Text     = hw.CalibrationNeeded   ? "Needs calib"           : "OK";
            motorTemp.Text = hw.MaxMotorTemp > 0    ? $"{hw.MaxMotorTemp:0}°C" : "—";
            errors.Text    = hw.ErrorCount > 0      ? hw.ErrorCount.ToString() : "—";
        }

        private void StartRelayStatusPoll()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, e) =>
            {
                bool r1 = _robotBridge.IsConnected;
                bool r2 = _robotBridge2.IsConnected;
                DispatcherQueue.TryEnqueue(() => { UpdateRobotStatus(r1); UpdateRobot2Status(r2); });
            };
            timer.Start();
        }

        private async Task TraceHubLocation()
        {
            try
            {
                using var resp = await SharedHttpClient.GetAsync("https://ipinfo.io/json");
                if (!resp.IsSuccessStatusCode) return;
                string body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                string city = doc.RootElement.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                string country = doc.RootElement.TryGetProperty("country", out var co) ? co.GetString() ?? "" : "";
                string ip = doc.RootElement.TryGetProperty("ip", out var i) ? i.GetString() ?? "" : "";
                string loc = $"{city}, {country}".Trim(',', ' ');
                Log($"[Hub] Location: {loc} | IP: {ip}");
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (HubIpText != null) HubIpText.Text = ip;
                    if (HubLocText != null) HubLocText.Text = loc;
                });
            }
            catch { }
        }

        private void HandleUnityIKTelemetry(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                string type = typeProp.GetString() ?? "";

                // ── IK joint teleoperation ───────────────────────────────────────
                if (type == "ik_joints" && root.TryGetProperty("payload", out var payload))
                {
                    if (!payload.TryGetProperty("angles", out var anglesProp)) return;
                    int robotIdx = payload.TryGetProperty("robotIdx", out var ri) ? ri.GetInt32() : 0;

                    // JS sends degrees — convert to radians for ROS
                    var anglesRad = anglesProp.EnumerateArray()
                        .Select(e => e.GetDouble() * Math.PI / 180.0)
                        .ToArray();

                    if (anglesRad.Length != 6) return;

                    var bridge = robotIdx == 0 ? _robotBridge : _robotBridge2;
                    if (bridge.IsConnected)
                    {
                        // Fire-and-forget: don't await to avoid blocking the recv loop
                        _ = bridge.SendDirectToRobotAsync(BuildIkJointCommand(anglesRad));
                    }
                    return;
                }

                // ── IK end-effector position telemetry (display only) ────────────
                if (type == "ik_telemetry")
                {
                    double px = root.TryGetProperty("pos_x", out var px_) ? px_.GetDouble() : 0;
                    double py = root.TryGetProperty("pos_y", out var py_) ? py_.GetDouble() : 0;
                    double pz = root.TryGetProperty("pos_z", out var pz_) ? pz_.GetDouble() : 0;
                    double rx = root.TryGetProperty("rot_x", out var rx_) ? rx_.GetDouble() : 0;
                    double ry = root.TryGetProperty("rot_y", out var ry_) ? ry_.GetDouble() : 0;
                    double rz = root.TryGetProperty("rot_z", out var rz_) ? rz_.GetDouble() : 0;
                    bool isR2 = root.TryGetProperty("robot_idx", out var ri2) && ri2.GetInt32() == 1;

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (isR2)
                        {
                            if (TelemIKPos2 != null) TelemIKPos2.Text = $"X:{px:0.00} Y:{py:0.00} Z:{pz:0.00}";
                            if (TelemIKRot2 != null) TelemIKRot2.Text = $"Rx:{rx:0.0} Ry:{ry:0.0} Rz:{rz:0.0}";
                        }
                        else
                        {
                            if (TelemIKPos != null) TelemIKPos.Text = $"X:{px:0.00} Y:{py:0.00} Z:{pz:0.00}";
                            if (TelemIKRot != null) TelemIKRot.Text = $"Rx:{rx:0.0} Ry:{ry:0.0} Rz:{rz:0.0}";
                        }
                    });
                }
            }
            catch { }
        }

        /// <summary>
        /// Builds a ROS follow_joint_trajectory_controller/command publish message
        /// for the given joint angles (radians). time_from_start is set to 0.2 s
        /// to match the 10 Hz send rate from the browser IK loop.
        /// </summary>
        private static string BuildIkJointCommand(double[] anglesRad) =>
            JsonSerializer.Serialize(new
            {
                op = "publish",
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
                            positions = anglesRad,
                            velocities = new double[6],
                            accelerations = Array.Empty<double>(),
                            effort = Array.Empty<double>(),
                            // 0.2 s matches the 10 Hz send rate — robot interpolates smoothly
                            time_from_start = new { secs = 0, nsecs = 200_000_000 }
                        }
                    }
                }
            });

    }
}