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
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;

namespace RobotControllerApp
{
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
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;

                // Set Taskbar and Window Icon
                try
                {
                    this.AppWindow.SetIcon("Assets/AppLogo.png");
                }
                catch { }
            }

            // Initialize Services
            _settings = AppSettings.Load();
            _relayServer = new RelayServerHost();
            _robotBridge = new RobotBridgeService() { RobotId = "Robot_Niryo_01", HasCamera = true };
            _robotBridge2 = new RobotBridgeService() { RobotId = "Robot_Niryo_02", HasCamera = false };

            // Initialize Settings UI values
            RelayPortInput.Text = _settings.RelayPort.ToString();
            RobotIpInput.Text = _settings.RobotIp;
            Robot2IpInput.Text = _settings.Robot2Ip;

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

            RelayServerHost.OnGripperReceived += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(msg);
                    if (doc.RootElement.TryGetProperty("msg", out var m))
                    {
                        if (m.TryGetProperty("state", out var s))
                            TelemGripper.Text = s.ToString().ToUpper();
                        else if (m.TryGetProperty("opened", out var o))
                            TelemGripper.Text = o.GetBoolean() ? "OPEN" : "CLOSED";
                    }
                }
                catch { }
            });

            // Robot 2 Telemetry
            RelayServerHost.OnRobot2JointsReceived += (joints) => this.DispatcherQueue.TryEnqueue(() =>
            {
                TelemJoints2.Text = "[" + string.Join(", ", System.Linq.Enumerable.Select(joints, j => j.ToString("0.00"))) + "]";
            });

            RelayServerHost.OnRobot2GripperReceived += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(msg);
                    if (doc.RootElement.TryGetProperty("msg", out var m))
                    {
                        if (m.TryGetProperty("state", out var s))
                            TelemGripper2.Text = s.ToString().ToUpper();
                        else if (m.TryGetProperty("opened", out var o))
                            TelemGripper2.Text = o.GetBoolean() ? "OPEN" : "CLOSED";
                    }
                }
                catch { }
            });

            RelayServerHost.OnRobotStateReceived += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
            });

            DateTime lastUnityMsg = DateTime.MinValue;

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

                    // Flexible parsing for Position (pos, position / Array, Object)
                    if (root.TryGetProperty("pos", out System.Text.Json.JsonElement pos) || root.TryGetProperty("position", out pos))
                    {
                        string posText;
                        if (pos.ValueKind == System.Text.Json.JsonValueKind.Array && pos.GetArrayLength() >= 3)
                            posText = $"Pos: [{pos[0].GetDouble():0.00}, {pos[1].GetDouble():0.00}, {pos[2].GetDouble():0.00}]";
                        else if (pos.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            double x = 0, y = 0, z = 0;
                            if (pos.TryGetProperty("x", out var vx)) x = vx.GetDouble();
                            if (pos.TryGetProperty("y", out var vy)) y = vy.GetDouble();
                            if (pos.TryGetProperty("z", out var vz)) z = vz.GetDouble();
                            posText = $"Pos: [{x:0.00}, {y:0.00}, {z:0.00}]";
                        }
                        else posText = "";

                        if (!string.IsNullOrEmpty(posText))
                        {
                            if (isRobot2) TelemIKPos2.Text = posText;
                            else TelemIKPos.Text = posText;
                        }
                    }

                    // Flexible parsing for Rotation (rot, rotation / Array, Object)
                    if (root.TryGetProperty("rot", out System.Text.Json.JsonElement rot) || root.TryGetProperty("rotation", out rot))
                    {
                        string rotText;
                        if (rot.ValueKind == System.Text.Json.JsonValueKind.Array && rot.GetArrayLength() >= 4)
                            rotText = $"Rot: [{rot[0].GetDouble():0.00}, {rot[1].GetDouble():0.00}, {rot[2].GetDouble():0.00}, {rot[3].GetDouble():0.00}]";
                        else if (rot.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            double x = 0, y = 0, z = 0, w = 1;
                            if (rot.TryGetProperty("x", out var vx)) x = vx.GetDouble();
                            if (rot.TryGetProperty("y", out var vy)) y = vy.GetDouble();
                            if (rot.TryGetProperty("z", out var vz)) z = vz.GetDouble();
                            if (rot.TryGetProperty("w", out var vw)) w = vw.GetDouble();
                            rotText = $"Rot: [{x:0.00}, {y:0.00}, {z:0.00}, {w:0.00}]";
                        }
                        else rotText = "";

                        if (!string.IsNullOrEmpty(rotText))
                        {
                            if (isRobot2) TelemIKRot2.Text = rotText;
                            else TelemIKRot.Text = rotText;
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
                        Log("[UI] Camera feed active.");
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

        private OpenCvSharp.VideoCapture? _cvCapture;
        private CancellationTokenSource? _cvCaptureCts;
        private int _operatorFpsCount = 0;
        private int _operatorFramesTotal = 0;
        private DateTime _operatorLastFpsReset = DateTime.Now;
        private Windows.Devices.Enumeration.DeviceInformationCollection? _videoDevices;

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
                                    using (var ms = new System.IO.MemoryStream(frameBytes))
                                    {
                                        await bitmap.SetSourceAsync(System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(ms));
                                    }
                                    LocalWebcamPreview.Source = bitmap;
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
                    Log($"[Trace] Hub located in {city}, {country}");
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

            var result = await dialog.ShowAsync();

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

        private string _lastLogMessage = string.Empty;
        private int _lastLogCount = 1;
        private Run? _lastLogRun = null;   // The Run we update in-place for stacking
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

                // ── Stacking: identical consecutive messages update in-place ────
                if (message == _lastLogMessage && _lastLogRun != null)
                {
                    _lastLogCount++;
                    // Strip old counter suffix and re-apply
                    string baseText = $"[{DateTime.Now:HH:mm:ss}] {message}";
                    _lastLogRun.Text = $"{baseText}  ×{_lastLogCount}";
                }
                else
                {
                    // New unique message — create a fresh paragraph
                    _lastLogMessage = message;
                    _lastLogCount = 1;

                    var run = new Run()
                    {
                        Text = $"[{DateTime.Now:HH:mm:ss}] {message}",
                        Foreground = new SolidColorBrush(color)
                    };
                    _lastLogRun = run;

                    var p = new Paragraph();
                    p.Inlines.Add(run);
                    ConsoleLog.Blocks.Add(p);

                    // Keep buffer size manageable
                    if (ConsoleLog.Blocks.Count > 300) ConsoleLog.Blocks.RemoveAt(0);
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
                }
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
                Log($"[Debug] Command dropped — ROS WebSocket not connected for {bridge.RobotId}. " +
                    $"Check that rosbridge_server is running on {bridge.RosIp}:{bridge.RosPort}.");
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
    }
}
