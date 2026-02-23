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
        private readonly List<double> _speedHistory = [];
        private readonly List<double> _uploadHistory = [];
        private DispatcherTimer? _networkTimer;
        private DispatcherTimer? _speedTestTimer;
        private readonly Ping _pinger = new();
        private bool _isNetworkPinging = false;
        private const int MaxHistory = 300; // 5 minutes (at 1 ping / second)
        private const int MaxSpeedHistory = 20;
        private double _latencyMaxMs = 150.0; // Y-axis scale, driven by LatencyScaleSlider

        // Custom Telemetry from Unity Client
        private string _questLocation = "Unknown Location";
        private string _questPublicIp = "";
        private float _questRxKbps = 0f;
        private float _questTxKbps = 0f;

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
            _robotBridge = new RobotBridgeService() { RobotId = "Robot_Niryo_01" };
            _robotBridge2 = new RobotBridgeService() { RobotId = "Robot_Niryo_02" };

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
            _robotBridge.OnInstanceConnectionChanged += (connected) => this.DispatcherQueue.TryEnqueue(() => UpdateRobotStatus(connected));
            _robotBridge2.OnInstanceConnectionChanged += (connected) => this.DispatcherQueue.TryEnqueue(() => UpdateRobot2Status(connected));

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

                    // Flexible parsing for Position (pos, position / Array, Object)
                    if (root.TryGetProperty("pos", out System.Text.Json.JsonElement pos) || root.TryGetProperty("position", out pos))
                    {
                        if (pos.ValueKind == System.Text.Json.JsonValueKind.Array && pos.GetArrayLength() >= 3)
                        {
                            TelemIKPos.Text = $"Pos: [{pos[0].GetDouble():0.00}, {pos[1].GetDouble():0.00}, {pos[2].GetDouble():0.00}]";
                        }
                        else if (pos.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            double x = 0, y = 0, z = 0;
                            if (pos.TryGetProperty("x", out var vx)) x = vx.GetDouble();
                            if (pos.TryGetProperty("y", out var vy)) y = vy.GetDouble();
                            if (pos.TryGetProperty("z", out var vz)) z = vz.GetDouble();
                            TelemIKPos.Text = $"Pos: [{x:0.00}, {y:0.00}, {z:0.00}]";
                        }
                    }

                    // Flexible parsing for Rotation (rot, rotation / Array, Object)
                    if (root.TryGetProperty("rot", out System.Text.Json.JsonElement rot) || root.TryGetProperty("rotation", out rot))
                    {
                        if (rot.ValueKind == System.Text.Json.JsonValueKind.Array && rot.GetArrayLength() >= 4)
                        {
                            TelemIKRot.Text = $"Rot: [{rot[0].GetDouble():0.00}, {rot[1].GetDouble():0.00}, {rot[2].GetDouble():0.00}, {rot[3].GetDouble():0.00}]";
                        }
                        else if (rot.ValueKind == System.Text.Json.JsonValueKind.Object)
                        {
                            double x = 0, y = 0, z = 0, w = 1;
                            if (rot.TryGetProperty("x", out var vx)) x = vx.GetDouble();
                            if (rot.TryGetProperty("y", out var vy)) y = vy.GetDouble();
                            if (rot.TryGetProperty("z", out var vz)) z = vz.GetDouble();
                            if (rot.TryGetProperty("w", out var vw)) w = vw.GetDouble();
                            TelemIKRot.Text = $"Rot: [{x:0.00}, {y:0.00}, {z:0.00}, {w:0.00}]";
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
            StartSpeedTestInterval();
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

        /// <summary>
        /// Polls the relay ConnectionManager every 2 s and updates robot status cards.
        /// This is the authoritative source of truth for whether a robot is reachable,
        /// even when the ROS bridge event path has fired a disconnect (e.g. ROS restarting).
        /// </summary>
        private void StartRelayStatusPoll()
        {
            var pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            pollTimer.Tick += (_, _) =>
            {
                // Use the ROS-level IsConnected flag on each bridge:
                // ConnectionManager.IsRobotConnected() only tells us if the bridge
                // WebSocket to the relay is open (always true while the app runs).
                // _robotBridge.IsConnected is set false the moment the ROS socket drops.
                bool r1 = _robotBridge.IsConnected;
                bool r2 = _robotBridge2.IsConnected;

                string r1Text = Robot1ActiveText.Text;
                string r2Text = Robot2ActiveText.Text;

                if (r1 && r1Text != "ACTIVE") UpdateRobotStatus(true);
                else if (!r1 && r1Text == "ACTIVE") UpdateRobotStatus(false);

                if (r2 && r2Text != "ACTIVE") UpdateRobot2Status(true);
                else if (!r2 && r2Text == "ACTIVE") UpdateRobot2Status(false);
            };
            pollTimer.Start();
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
                    _speedTestTimer?.Stop();
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

        private async void RunSpeedTest_Click(object sender, RoutedEventArgs e)
        {
            await RunSpeedTest();
        }

        private async Task RunSpeedTest()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NetworkStatusText.Text = "Testing...";
                RunSpeedTestButton.IsEnabled = false;
            });

            await Task.Run(async () =>
            {
                try
                {
                    double downMbps = -1;
                    double upMbps = -1;

                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(30);

                    // ── 1. DOWNLOAD TEST ──────────────────────────────────────────────────
                    try
                    {
                        long totalBytes = 0;
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        // Some endpoints reject requests without a User-Agent
                        client.DefaultRequestHeaders.TryAddWithoutValidation(
                            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                        using var response = await client.GetAsync(
                            $"https://speed.cloudflare.com/__down?bytes=10000000&nocache={Guid.NewGuid()}",
                            HttpCompletionOption.ResponseHeadersRead);

                        response.EnsureSuccessStatusCode();

                        using var stream = await response.Content.ReadAsStreamAsync();
                        byte[] buf = new byte[65536];
                        int read;
                        while ((read = await stream.ReadAsync(buf.AsMemory())) > 0)
                            totalBytes += read;

                        sw.Stop();
                        if (sw.Elapsed.TotalSeconds > 0 && totalBytes > 1000)
                            downMbps = (totalBytes * 8.0 / 1_000_000.0) / sw.Elapsed.TotalSeconds;
                        else
                            downMbps = -1;
                    }
                    catch
                    {
                        // Fallback: smaller file from a different CDN
                        try
                        {
                            long totalBytes = 0;
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            using var response = await client.GetAsync(
                                "https://proof.ovh.net/files/10Mb.dat",
                                HttpCompletionOption.ResponseHeadersRead);
                            response.EnsureSuccessStatusCode();
                            using var stream = await response.Content.ReadAsStreamAsync();
                            byte[] buf = new byte[65536];
                            int read;
                            while ((read = await stream.ReadAsync(buf.AsMemory())) > 0)
                                totalBytes += read;
                            sw.Stop();
                            if (sw.Elapsed.TotalSeconds > 0 && totalBytes > 1000)
                                downMbps = (totalBytes * 8.0 / 1_000_000.0) / sw.Elapsed.TotalSeconds;
                            else
                                downMbps = -1;
                        }
                        catch { downMbps = -1; }
                    }

                    // ── 2. UPLOAD TEST ────────────────────────────────────────────────────
                    try
                    {
                        byte[] upData = new byte[4_000_000]; // 4 MB
                        new Random().NextBytes(upData);

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        using var content = new ByteArrayContent(upData);
                        var resp = await client.PostAsync($"https://speed.cloudflare.com/__up?nocache={Guid.NewGuid()}", content);
                        sw.Stop();

                        if (resp.IsSuccessStatusCode && sw.Elapsed.TotalSeconds > 0)
                            upMbps = (upData.Length * 8.0 / 1_000_000.0) / sw.Elapsed.TotalSeconds;
                    }
                    catch (Exception)
                    {
                        upMbps = -1;
                    }

                    DispatcherQueue.TryEnqueue(() =>
                {
                    // Reset colors
                    InternetSpeedText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Primary"];
                    InternetUploadText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 210, 121, 255));

                    if (downMbps >= 0)
                    {
                        InternetSpeedText.Text = $"{downMbps:F1} Mbps";
                        UpdateHistory(_speedHistory, downMbps, MaxSpeedHistory);

                        UpdateSpeedStats();
                    }
                    else InternetSpeedText.Text = "Err";

                    if (upMbps >= 0)
                    {
                        InternetUploadText.Text = $"{upMbps:F1} Mbps";
                        UpdateHistory(_uploadHistory, upMbps, MaxSpeedHistory);
                    }
                    else InternetUploadText.Text = "Err";

                    NetworkStatusText.Text = "Idle";
                    RunSpeedTestButton.IsEnabled = true;
                });
                }
                catch
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        NetworkStatusText.Text = "Failed";
                        RunSpeedTestButton.IsEnabled = true;
                    });
                }
            });
        }

        private void StartSpeedTestInterval()
        {
            _speedTestTimer?.Stop();
            _speedTestTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) }; // Run every 60s to save bandwidth
            _nextSpeedTest = DateTime.Now.Add(_speedTestTimer.Interval);

            _speedTestTimer.Tick += async (s, e) =>
            {
                await RunSpeedTest();
                _nextSpeedTest = DateTime.Now.Add(_speedTestTimer.Interval);
            };

            if (AutoMonitorToggle.IsOn)
            {
                _speedTestTimer.Start();
            }
        }

        private void AutoMonitorToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (AutoMonitorToggle.IsOn)
            {
                if (_speedTestTimer != null)
                {
                    _nextSpeedTest = DateTime.Now.Add(_speedTestTimer.Interval);
                    _speedTestTimer.Start();
                }
                if (NetworkStatusText != null) NetworkStatusText.Text = "Resuming...";
            }
            else
            {
                _speedTestTimer?.Stop();
                if (NetworkStatusText != null) NetworkStatusText.Text = "Paused";
            }
        }

        private void UpdateSpeedStats()
        {
            if (_speedHistory.Count == 0) return;

            double low = _speedHistory.Min();
            double high = _speedHistory.Max();
            double avg = _speedHistory.Average();

            SpeedLowText.Text = $"{low:F1} Mbps";
            SpeedHighText.Text = $"{high:F1} Mbps";
            SpeedAvgText.Text = $"{avg:F1} Mbps";

            if (_uploadHistory.Count > 0)
            {
                // Optionally can populate texts for upload here as well if UI handles it. But for now only graph is strictly required. 
            }
        }

        private DateTime _nextSpeedTest = DateTime.MinValue;
        private void UpdateSpeedCountdown()
        {
            if (NetworkView.Visibility != Visibility.Visible) return;
            if (_nextSpeedTest == DateTime.MinValue) return;

            var remaining = _nextSpeedTest - DateTime.Now;
            if (remaining.TotalSeconds > 0)
            {
                if (NetworkStatusText.Text != "Testing...")
                    NetworkStatusText.Text = $"Next in {(int)remaining.TotalSeconds}s";
            }
        }

        // DrawSpeedGraph removed — speed history graph was replaced by topology node cards.

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

            // Re-subscribe just in case (though it's already active)
            // The real 'refresh' happens at the robot/bridge level, 
            // but resetting the UI state gives user feedback.
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
                    UpdateSpeedCountdown();
                }
                finally
                {
                    _isNetworkPinging = false;
                }
            };
            _networkTimer.Start();

            // Run first speed test automatically
            _ = RunSpeedTest();
            StartSpeedTestInterval();
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
            // Robot 1 is logically connected ONLY if the bridge is currently alive with ROS
            bool isR1Connected = _robotBridge.IsConnected;

            // Robot 2 is logically connected if we can reach its IP (as it has no specific bridge software yet)
            bool isR2Connected = r2Lat > 0 && !string.IsNullOrEmpty(_settings.Robot2Ip);

            // ---------- Dashboard Status Cards ----------
            var successBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            var mutedBrush = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
            var warnBrush = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];

            // Remote Expert
            bool expertActive = isExpertWsConnected || isExpertReachable;
            RelayActiveText.Text = expertActive ? "ACTIVE" : "WAITING";
            RelayActiveText.Foreground = expertActive ? successBrush : mutedBrush;
            RelayIcon.Foreground = expertActive ? successBrush : mutedBrush;
            if (RelayStatusIndicator != null) RelayStatusIndicator.Visibility = expertActive ? Visibility.Visible : Visibility.Collapsed;

            // Robot 1
            Robot1ActiveText.Text = isR1Connected ? "ACTIVE" : "WAITING";
            Robot1ActiveText.Foreground = isR1Connected ? successBrush : mutedBrush;
            Robot1Icon.Foreground = isR1Connected ? successBrush : mutedBrush;
            if (Robot1StatusIndicator != null) Robot1StatusIndicator.Visibility = isR1Connected ? Visibility.Visible : Visibility.Collapsed;

            // Robot 2
            Robot2ActiveText.Text = isR2Connected ? "ACTIVE" : "WAITING";
            Robot2ActiveText.Foreground = isR2Connected ? successBrush : mutedBrush;
            Robot2Icon.Foreground = isR2Connected ? successBrush : mutedBrush;
            if (Robot2StatusIndicator != null) Robot2StatusIndicator.Visibility = isR2Connected ? Visibility.Visible : Visibility.Collapsed;

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
            R1IpText.Text = (isR1Connected) ? ExtractIp(_settings.RobotIp) : "Offline";
            R2IpText.Text = (r2Lat > 0) ? ExtractIp(_settings.Robot2Ip) : "Offline";

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
                R2RelayText.Text = "REACHABLE";
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
