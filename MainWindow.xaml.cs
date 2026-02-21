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
        private readonly RobotBridgeService _robotBridge;
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

        // Custom Telemetry from Unity Client
        private string _questLocation = "Unknown Location";
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
            _robotBridge = new RobotBridgeService();

            // Initialize Settings UI values
            RelayPortInput.Text = _settings.RelayPort.ToString();
            PublicUrlInput.Text = _settings.PublicUrl;
            ExpertIpInput.Text = _settings.ExpertIp;
            RobotIpInput.Text = _settings.RobotIp;
            Robot2IpInput.Text = _settings.Robot2Ip;

            // Update Hub Card Status (Initialize as Waiting for Hub to start or Unity to connect)
            RelayStatusText.Text = "Hub Service Primary";
            RelayActiveText.Text = "WAITING";
            RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
            RelayIcon.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];

            StartNetworkMonitoring();


            // Wire up Logs
            RelayServerHost.OnLog += Log;
            RobotBridgeService.OnLog += Log;
            RobotBridgeService.OnRosConnectionChanged += (connected) =>
            {
                this.DispatcherQueue.TryEnqueue(() => UpdateRobotStatus(connected));
            };

            RelayServerHost.OnUnityConnectionChanged += (connected) =>
            {
                this.DispatcherQueue.TryEnqueue(() => UpdateExpertStatus(connected));
            };

            RelayServerHost.OnUnityTelemetryReceived += (loc, rx, tx) => this.DispatcherQueue.TryEnqueue(() =>
            {
                _questLocation = loc;
                _questRxKbps = rx;
                _questTxKbps = tx;
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
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(msg);
                    if (doc.RootElement.TryGetProperty("msg", out var m))
                    {
                        if (m.TryGetProperty("robot_status", out var s))
                            TelemMode.Text = s.ToString();
                        else if (m.TryGetProperty("state", out var st))
                            TelemMode.Text = st.ToString();
                    }
                }
                catch { }
            });

            DateTime lastUnityMsg = DateTime.MinValue;

            RelayServerHost.OnUnityMessageReceived += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
                var now = DateTime.Now;
                lastUnityMsg = now;


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
                catch (Exception ex)
                {
                    if (msg.Contains("pos")) Log($"[Parse Error] {ex.Message}");
                }
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

        private MediaCapture? _mediaCapture;
        private MediaPlayer? _webcamPlayer;   // owns the frame source, handed to MediaPlayerElement
        private Windows.Devices.Enumeration.DeviceInformationCollection? _videoDevices;

        /// <summary>Start the selected camera using MediaPlayer + SetMediaPlayer (WinUI 3 correct pattern).</summary>
        private async Task StartCameraByIndex(int index)
        {
            if (_videoDevices == null || index < 0 || index >= _videoDevices.Count) return;

            // ── Cleanup existing session ────────────────────────────────────────
            if (_webcamPlayer != null)
            {
                _webcamPlayer.Pause();
                DispatcherQueue.TryEnqueue(() => LocalWebcamPreview.SetMediaPlayer(null));
                _webcamPlayer.Dispose();
                _webcamPlayer = null;
            }
            if (_mediaCapture != null)
            {
                try { await _mediaCapture.StopPreviewAsync(); } catch { }
                _mediaCapture.Dispose();
                _mediaCapture = null;
            }

            // ── Initialize new capture session ──────────────────────────────────
            try
            {
                var selected = _videoDevices[index];
                _mediaCapture = new MediaCapture();

                await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = selected.Id,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    PhotoCaptureSource = PhotoCaptureSource.VideoPreview,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu
                });

                // Prefer VideoPreview stream; fall back to any available source
                var frameSource = _mediaCapture.FrameSources.Values
                    .FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoPreview)
                    ?? _mediaCapture.FrameSources.Values.FirstOrDefault();

                if (frameSource != null)
                {
                    // WinUI 3 correct approach: MediaPlayer → SetMediaPlayer()
                    _webcamPlayer = new MediaPlayer();
                    _webcamPlayer.AutoPlay = true;
                    _webcamPlayer.Source = Windows.Media.Core.MediaSource
                                                .CreateFromMediaFrameSource(frameSource);

                    // Must assign on the UI thread
                    DispatcherQueue.TryEnqueue(() => LocalWebcamPreview.SetMediaPlayer(_webcamPlayer));

                    Log($"[Webcam] Streaming: {selected.Name}");
                }
                else
                {
                    Log($"[Webcam] No usable frame source for: {selected.Name}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Webcam] Failed to start '{(_videoDevices?[index].Name ?? "?")}': {ex.Message}");
                _mediaCapture = null;
            }
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
                    if (_videoDevices[i].Name.IndexOf("Creative", StringComparison.OrdinalIgnoreCase) >= 0)
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
            if (isConnected)
            {
                RobotStatusText.Text = "Connected to ROS Bridge";
                RobotStatusText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                Robot1Icon.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                RobotConnectingState.Visibility = Visibility.Collapsed;
            }
            else
            {
                RobotStatusText.Text = "Searching for Robot...";
                RobotStatusText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
                Robot1Icon.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
                RobotConnectingState.Visibility = Visibility.Visible;
            }
        }

        private void UpdateRobot2Status(bool isConnected)
        {
            if (isConnected)
            {
                Robot2StatusText.Text = "Robot 2: Connected";
                Robot2StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                Robot2Icon.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                Robot2ConnectingState.Visibility = Visibility.Collapsed;
            }
            else
            {
                Robot2StatusText.Text = "Robot 2: Offline";
                Robot2StatusText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Primary"];
                Robot2Icon.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Primary"];
                Robot2ConnectingState.Visibility = Visibility.Collapsed; // Hide search for placeholder 2
            }
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


        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            args.Cancel = true;

            var dialog = new ContentDialog
            {
                Title = "Stop Services?",
                Content = "Do you want to stop the Relay Server and Robot Bridge cleanly before exiting?",
                PrimaryButtonText = "Yes, Stop & Exit",
                CloseButtonText = "Cancel",
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Log("Stopping services...");
                await _robotBridge.StopAsync();
                await _relayServer.StopAsync();

                // Remove handler to avoid loop
                sender.Closing -= AppWindow_Closing;
                this.Close();
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
            Log($"Starting Hub Relay Server for Quest 3 (Port {_settings.RelayPort})...");

            _relayServer.Port = _settings.RelayPort;
            _relayServer.PublicUrl = _settings.PublicUrl;

            _ = Task.Run(async () => await _relayServer.StartAsync());

            RelayStatusText.Text = $"Listening (Port {_settings.RelayPort})";

            // Step 2: Start Robot Bridge (Client)
            await Task.Delay(1000);
            Log($"Starting Robot Bridge Service (Target: {_settings.RobotIp})...");

            string sanitizedRosIp = _settings.RobotIp;
            if (sanitizedRosIp.Contains("://"))
            {
                // Extract hostname/IP from URI
                try { sanitizedRosIp = new Uri(sanitizedRosIp).Host; } catch { }
            }
            else if (sanitizedRosIp.Contains(':'))
            {
                sanitizedRosIp = sanitizedRosIp.Split(':')[0];
            }

            _robotBridge.RosIp = sanitizedRosIp;
            _robotBridge.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot";
            _robotBridge.Start();

            UpdateRobotStatus(false);

            // Initialize Robot 2 (Visual Only)
            UpdateRobot2Status(false);

            // Step 3: Ready
            Log("System Ready. Waiting for connections...");
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

                _settings.PublicUrl = PublicUrlInput.Text.Trim();
                _relayServer.PublicUrl = _settings.PublicUrl;

                _settings.ExpertIp = ExpertIpInput.Text.Trim();
                _settings.RobotIp = RobotIpInput.Text.Trim();
                _settings.Robot2Ip = Robot2IpInput.Text.Trim();
                _settings.Save();
                _robotBridge.RelayServerUrl = $"ws://localhost:{_settings.RelayPort}/robot"; // Robot 1 Config
                                                                                             // Note: Robot 2 connection logic is not yet implemented in service, only stored in settings.

                // Persist to Disk
                _settings.Save();

                // Stop Services safely
                try
                {
                    Log("Stopping services...");
                    await _robotBridge.StopAsync();
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

                    RelayStatusText.Text = $"Listening (Port {_settings.RelayPort})";

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

            try
            {
                double downMbps = -1;
                double upMbps = -1;

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);

                // ── 1. DOWNLOAD TEST ──────────────────────────────────────────────────
                // 25 MB streamed in chunks — avoids TCP slow-start skew and RAM spike.
                // At 100 Mbps this takes ~2s; at 400 Mbps ~0.5s — both reliable ranges.
                try
                {
                    long totalBytes = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    using var response = await client.GetAsync(
                        "https://speed.cloudflare.com/__down?bytes=25000000",
                        HttpCompletionOption.ResponseHeadersRead);

                    using var stream = await response.Content.ReadAsStreamAsync();
                    byte[] buf = new byte[65536]; // 64 KB chunks
                    int read;
                    while ((read = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
                        totalBytes += read;

                    sw.Stop();
                    if (sw.Elapsed.TotalSeconds > 0)
                        downMbps = (totalBytes * 8.0 / 1_000_000.0) / sw.Elapsed.TotalSeconds;
                }
                catch { downMbps = -1; }

                // ── 2. UPLOAD TEST ────────────────────────────────────────────────────
                // 4 MB of random data — gives a reasonable signal on any connection.
                try
                {
                    byte[] upData = new byte[4_000_000]; // 4 MB
                    new Random().NextBytes(upData);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var content = new ByteArrayContent(upData);
                    var resp = await client.PostAsync("https://speed.cloudflare.com/__up", content);
                    sw.Stop();

                    if (resp.IsSuccessStatusCode && sw.Elapsed.TotalSeconds > 0)
                        upMbps = (upData.Length * 8.0 / 1_000_000.0) / sw.Elapsed.TotalSeconds;
                }
                catch { upMbps = -1; }

                DispatcherQueue.TryEnqueue(() =>
                {
                    // Reset colors
                    InternetSpeedText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Primary"];
                    InternetUploadText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 210, 121, 255));

                    if (downMbps >= 0)
                    {
                        InternetSpeedText.Text = $"{downMbps:F1} Mbps";
                        UpdateHistory(_speedHistory, downMbps, MaxSpeedHistory);
                        DrawSpeedGraph();
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
        }

        private void StartSpeedTestInterval()
        {
            _speedTestTimer?.Stop();
            _speedTestTimer = new DispatcherTimer();
            _speedTestTimer.Interval = TimeSpan.FromMinutes(1); // Run every 60s to save bandwidth
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
                double upLow = _uploadHistory.Min();
                double upHigh = _uploadHistory.Max();
                double upAvg = _uploadHistory.Average();
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

        private void DrawSpeedGraph()
        {
            if (NetworkView.Visibility != Visibility.Visible) return;

            SpeedPath.Points.Clear();
            UploadPath.Points.Clear();
            if (_speedHistory.Count < 2) return;

            double width = SpeedCanvas.ActualWidth > 0 ? SpeedCanvas.ActualWidth : 400;
            double height = SpeedCanvas.ActualHeight > 0 ? SpeedCanvas.ActualHeight : 100;

            // If we have few points, space them out so the graph doesn't look "stuck" on the left
            int divisor = Math.Max(_speedHistory.Count, MaxSpeedHistory);
            double stepX = width / (divisor - 1);

            double maxSpeed = Math.Max(100.0, _speedHistory.Max() * 1.2);
            if (_uploadHistory.Count > 0)
            {
                maxSpeed = Math.Max(maxSpeed, _uploadHistory.Max() * 1.2);
            }
            double scaleY = height / maxSpeed;

            for (int i = 0; i < _speedHistory.Count; i++)
            {
                double x = i * stepX;
                double val = Math.Min(_speedHistory[i], maxSpeed);
                double y = height - (val * scaleY);
                SpeedPath.Points.Add(new Windows.Foundation.Point(x, y));
            }

            for (int i = 0; i < _uploadHistory.Count; i++)
            {
                double x = i * stepX;
                double val = Math.Min(_uploadHistory[i], maxSpeed);
                double y = height - (val * scaleY);
                UploadPath.Points.Add(new Windows.Foundation.Point(x, y));
            }
        }

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

                var p = new Paragraph();
                var run = new Run() { Text = $"[{DateTime.Now:HH:mm:ss}] {message}" };
                run.Foreground = new SolidColorBrush(color);
                p.Inlines.Add(run);

                ConsoleLog.Blocks.Add(p);

                // Keep buffer size manageable
                if (ConsoleLog.Blocks.Count > 200) ConsoleLog.Blocks.RemoveAt(0);
            });
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
            _networkTimer = new DispatcherTimer();
            _networkTimer.Interval = TimeSpan.FromMilliseconds(1000);
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

                    if (!string.IsNullOrEmpty(expertTarget))
                    {
                        try
                        {
                            string host = expertTarget;
                            if (host == "::1" || host.ToLower() == "localhost") host = "127.0.0.1";
                            var reply = await _pinger.SendPingAsync(host, 1000);
                            if (reply.Status == IPStatus.Success) unityLat = reply.RoundtripTime;
                        }
                        catch { }
                    }

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
                    DrawSpeedGraph();
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
            if (connected)
            {
                RelayActiveText.Text = "ACTIVE";
                RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                RelayStatusText.Text = "Expert (Quest) Connected";
            }
            else
            {
                RelayActiveText.Text = "WAITING";
                RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Warning"];
                RelayStatusText.Text = "Awaiting Quest Connection...";
            }
        }

        private void UpdateDashboardAndDiscovery(double uLat, double r1Lat, double r2Lat)
        {
            // 1. Dashboard updates (Latencies)
            bool isExpertWsConnected = RelayServerHost.UnityClientConnected;
            // Add a 3-second buffer to prevent flickering due to dropped ICMP pings over Wi-Fi
            int uCount = _unityLatencyHistory.Count;
            bool isExpertReachable = uLat > 0 || (uCount > 0 && _unityLatencyHistory.Skip(System.Math.Max(0, uCount - 3)).Any(v => v > 0));
            // Robot 1 is logically connected ONLY if the bridge is sending heartbeats
            bool isR1Connected = _robotBridge.IsConnected || _robotBridge.LastLatencyMs > 0;

            // Robot 2 is logically connected if we can reach its IP (as it has no specific bridge software yet)
            bool isR2Connected = r2Lat > 0 && !string.IsNullOrEmpty(_settings.Robot2Ip);

            // Expert: use WebSocket latency if ICMP failing
            double displayULat = uLat;
            if (isExpertWsConnected && displayULat <= 0 && RelayServerHost.LastQuestLatencyMs > 0)
                displayULat = RelayServerHost.LastQuestLatencyMs;

            // Robot 1: If ping failed but bridge reports heartbeat latency, use that
            double displayR1Lat = r1Lat;
            if (isR1Connected && displayR1Lat <= 0)
                displayR1Lat = _robotBridge.LastLatencyMs;

            // DISPLAY LOGIC: Show ms if reachable or connected
            RelayLatencyText.Text = (displayULat > 0) ? $"{displayULat:F0}" : "--";
            Robot1LatencyText.Text = (isR1Connected && displayR1Lat > 0) ? $"{displayR1Lat:F0}" : "--";
            Robot2LatencyText.Text = (isR2Connected) ? $"{r2Lat:F0}" : "--";

            // Dashboard Status Update based on Ping (if WS is offline)
            if (!isExpertWsConnected)
            {
                if (isExpertReachable)
                {
                    RelayActiveText.Text = "QUEST REACHABLE";
                    RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                }
                else
                {
                    RelayActiveText.Text = "WAITING FOR QUEST";
                    RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Error"];
                }
            }
            else
            {
                RelayActiveText.Text = "ACTIVE";
                RelayActiveText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            }

            // 2. Discovery updates
            string? expertDisplayIp = RelayServerHost.UnityClientIp;
            if (string.IsNullOrEmpty(expertDisplayIp)) expertDisplayIp = _settings.ExpertIp;
            if (string.IsNullOrEmpty(expertDisplayIp)) expertDisplayIp = "quest-3"; // Default fallback

            if (!isExpertWsConnected && !isExpertReachable)
            {
                QuestIpText.Text = "Disconnected";
            }
            else
            {
                QuestIpText.Text = expertDisplayIp;
            }
            R1IpText.Text = (isR1Connected || displayR1Lat > 0) ? ExtractIp(_settings.RobotIp) : "Disconnected";
            R2IpText.Text = r2Lat > 0 ? ExtractIp(_settings.Robot2Ip) : "Offline";

            if (isExpertWsConnected)
            {
                QuestRelayText.Text = "CONNECTED";
                QuestRelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                QuestRelayDot.Fill = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                QuestLocText.Text = $"{_questLocation}  ↓ {_questRxKbps:0.0} KB/s  ↑ {_questTxKbps:0.0} KB/s";
            }
            else if (isExpertReachable)
            {
                QuestRelayText.Text = "REACHABLE";
                QuestRelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                QuestRelayDot.Fill = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                QuestLocText.Text = "Tailscale Mesh";
            }
            else
            {
                QuestRelayText.Text = "SEARCHING";
                QuestRelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
                QuestRelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
                QuestLocText.Text = "N/A";
            }

            if (isR1Connected || displayR1Lat > 0)
            {
                R1RelayText.Text = "ROS BRIDGE";
                R1RelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
                R1RelayDot.Fill = (SolidColorBrush)Application.Current.Resources["Brush.Status.Success"];
            }
            else
            {
                R1RelayText.Text = "SEARCHING";
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
                R2RelayText.Text = "PENDING";
                R2RelayText.Foreground = (SolidColorBrush)Application.Current.Resources["Brush.Text.Muted"];
                R2RelayDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
            }
        }

        private static string ExtractIp(string input)
        {
            if (string.IsNullOrEmpty(input)) return "N/A";
            if (input.StartsWith("ws://"))
            {
                return input.Substring(5).Split(':')[0];
            }
            return input;
        }

        private void UpdateLatencyStats()
        {
            UpdateStatTexts(_unityLatencyHistory, QuestMinText, QuestMaxText, QuestAvgText);
            UpdateStatTexts(_internetLatencyHistory, InternetMinText, InternetMaxText, InternetAvgText);
        }

        private void UpdateStatTexts(List<double> history, TextBlock minT, TextBlock maxT, TextBlock avgT)
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
        }

        private void UpdatePath(Microsoft.UI.Xaml.Shapes.Polyline polyline, List<double> history)
        {
            polyline.Points.Clear();
            if (history.Count < 2) return;

            // Use fixed dimensions or actual dimensions if available
            double width = LatencyCanvas.ActualWidth > 0 ? LatencyCanvas.ActualWidth : 800;
            double height = LatencyCanvas.ActualHeight > 0 ? LatencyCanvas.ActualHeight : 120;

            double stepX = width / (MaxHistory - 1);
            double maxHeight = 300.0; // 300ms max scale
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
    }
}
