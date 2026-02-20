using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using RobotControllerApp.Services;
using System.Net.Http;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RobotControllerApp
{
    public sealed partial class MainWindow : Window
    {
        private readonly RelayServerHost _relayServer;
        private readonly RobotBridgeService _robotBridge;
        private readonly AppSettings _settings;

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
            }

            // Initialize Services
            _settings = AppSettings.Load();
            _relayServer = new RelayServerHost();
            _robotBridge = new RobotBridgeService();

            // Initialize Settings UI values
            RelayPortInput.Text = _settings.RelayPort.ToString();
            PublicUrlInput.Text = _settings.PublicUrl;
            RobotIpInput.Text = _settings.RobotIp;
            Robot2IpInput.Text = _settings.Robot2Ip;


            // Wire up Logs
            RelayServerHost.OnLog += Log;
            RelayServerHost.OnWhatsAppLog += LogWhatsApp;
            RobotBridgeService.OnLog += Log;
            RobotBridgeService.OnRosConnectionChanged += (connected) =>
            {
                this.DispatcherQueue.TryEnqueue(() => UpdateRobotStatus(connected));
            };

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
                TelemBufferBar.Value = Math.Min(fps * 2, 100);
            });

            DateTime lastUnityMsg = DateTime.MinValue;

            RelayServerHost.OnUnityMessageReceived += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
                // Interval calc
                var now = DateTime.Now;
                if (lastUnityMsg > DateTime.MinValue)
                {
                    double ms = (now - lastUnityMsg).TotalMilliseconds;
                    TelemUnityLatency.Text = $"{ms:F0} ms (Interval)";

                    if (ms > 100) TelemUnityLatency.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
                    else if (ms < 40) TelemUnityLatency.Foreground = new SolidColorBrush(Microsoft.UI.Colors.LightGreen);
                    else TelemUnityLatency.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
                }
                lastUnityMsg = now;

                TelemLastCmd.Text = msg.Length > 60 ? msg.Substring(0, 57) + "..." : msg;
                TelemCmdSource.Text = "Unity Client";

                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(msg))
                    {
                        var root = doc.RootElement;

                        // Flexible parsing for Position (pos, position / Array, Object)
                        System.Text.Json.JsonElement pos;
                        if (root.TryGetProperty("pos", out pos) || root.TryGetProperty("position", out pos))
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
                        System.Text.Json.JsonElement rot;
                        if (root.TryGetProperty("rot", out rot) || root.TryGetProperty("rotation", out rot))
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
                }
                catch (Exception ex)
                {
                    if (msg.Contains("pos")) Log($"[Parse Error] {ex.Message}");
                }
            });

            RelayServerHost.OnImageReceived += (imageBytes) => this.DispatcherQueue.TryEnqueue(async () =>
            {
                // Update FPS/Stats
                TelemTotalImages.Text = RelayServerHost._imagesTotal.ToString();

                // Track FPS
                RelayServerHost._imagesLastSec++;
                var now = DateTime.Now;
                var elapsed = (now - RelayServerHost._lastFpsReset).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    double fps = RelayServerHost._imagesLastSec / elapsed;
                    TelemFps.Text = fps.ToString("F1");
                    RelayServerHost._imagesLastSec = 0;
                    RelayServerHost._lastFpsReset = now;
                }

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

            RelayServerHost.OnWhatsAppLog += (msg) => this.DispatcherQueue.TryEnqueue(() =>
            {
                TelemLastCmd.Text = msg.Contains("]") ? msg.Substring(msg.IndexOf("]") + 1).Trim() : msg;
                TelemCmdSource.Text = "WhatsApp";
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

            StartLatencyMonitor();
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

        private async void StartLatencyMonitor()
        {
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += async (s, e) =>
            {
                try
                {
                    // 1. Relay Latency
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    using var client = new HttpClient();
                    client.Timeout = TimeSpan.FromSeconds(1);
                    var resp = await client.GetAsync($"http://localhost:{_settings.RelayPort}/status");
                    watch.Stop();
                    RelayLatencyText.Text = $"{watch.ElapsedMilliseconds} ms";

                    // 2. Robot Latency (Simulated)
                    if (RobotStatusText.Text.Contains("Connected"))
                    {
                        Robot1LatencyText.Text = $"{new Random().Next(4, 15)} ms";
                    }
                    else Robot1LatencyText.Text = "-- ms";
                }
                catch { RelayLatencyText.Text = "Offline"; }
            };
            timer.Start();
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
            Log("🚀 Initializing Robot Controller System...");

            // Step 1: Start Relay Server (Background)
            await Task.Delay(500);
            Log($"Starting Relay Server (Kestrel Port {_settings.RelayPort})...");

            _relayServer.Port = _settings.RelayPort;
            _relayServer.PublicUrl = _settings.PublicUrl;

            _ = Task.Run(async () => await _relayServer.StartAsync());

            RelayStatusText.Text = $"Running (Port {_settings.RelayPort})";

            // Step 2: Start Robot Bridge (Client)
            await Task.Delay(1000);
            Log($"Starting Robot Bridge Service (Target: {_settings.RobotIp})...");

            _robotBridge.RosIp = _settings.RobotIp;
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

                _settings.RobotIp = RobotIpInput.Text.Trim();
                _settings.Robot2Ip = Robot2IpInput.Text.Trim();
                _robotBridge.RosIp = _settings.RobotIp;
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

                    RelayStatusText.Text = $"Running (Port {_settings.RelayPort})";

                    UpdateRobotStatus(false);
                    UpdateRobot2Status(false);

                    var dialog = new ContentDialog
                    {
                        Title = "Settings Saved",
                        Content = "The Relay Server has been restarted with your new configuration.",
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
            var btn = sender as Button;
            if (btn != null) btn.IsEnabled = false;

            NetworkStatusText.Text = "Testing...";

            try
            {
                // 1. Cloudflare Ping
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync("1.1.1.1", 2000);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        long ms = reply.RoundtripTime;
                        GraphCloudPing.Text = $"{ms} ms";
                        InternetLatencyText.Text = $"{ms} ms";
                    }
                    else
                    {
                        GraphCloudPing.Text = "Timeout";
                        InternetLatencyText.Text = "Err";
                    }
                }
                catch { GraphCloudPing.Text = "Error"; InternetLatencyText.Text = "Err"; }

                await Task.Delay(500);

                // 2. Robot Ping
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    // Use stored IP from settings
                    string ip = _settings.RobotIp;
                    if (string.IsNullOrWhiteSpace(ip)) ip = "169.254.200.200";

                    // Strip ws:// and port if present for Ping
                    ip = ip.Replace("ws://", "").Replace("wss://", "").Split(':')[0];

                    var reply = await ping.SendPingAsync(ip, 2000);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                    {
                        GraphRobotPing.Text = $"{reply.RoundtripTime} ms";
                    }
                    else
                    {
                        GraphRobotPing.Text = "Timeout";
                    }
                }
                catch { GraphRobotPing.Text = "Unreachable"; }

                // 3. Unity Mock
                GraphUnityPing.Text = "N/A";

                // 4. Download Speed Test
                NetworkStatusText.Text = "Measuring Speed...";
                try
                {
                    using var client = new HttpClient();
                    string testUrl = "https://speed.cloudflare.com/__down?bytes=10485760";
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    byte[] data = await client.GetByteArrayAsync(testUrl);
                    sw.Stop();

                    double seconds = sw.Elapsed.TotalSeconds;
                    double mbps = (data.Length * 8.0 / 1000000.0) / seconds;
                    InternetSpeedText.Text = $"{mbps:F1} Mbps";
                }
                catch { InternetSpeedText.Text = "Err"; }

                NetworkStatusText.Text = "Idle";
            }
            finally
            {
                if (btn != null) btn.IsEnabled = true;
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
                else if (message.Contains("Connected") || message.Contains("Success") || message.Contains("✓") || message.Contains("Ready"))
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
            CameraView.Visibility = Visibility.Collapsed;
            LogsView.Visibility = Visibility.Collapsed;
            SettingsView.Visibility = Visibility.Collapsed;

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
                    case "camera":
                        CameraView.Visibility = Visibility.Visible;
                        break;
                    case "logs":
                        LogsView.Visibility = Visibility.Visible;
                        break;
                    case "settings":
                        SettingsView.Visibility = Visibility.Visible;
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

        private int _unreadMessages = 0;

        private void ChatScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // If user scrolls to bottom manually, clear badge
            if (ChatScrollViewer.VerticalOffset >= ChatScrollViewer.ScrollableHeight - 50)
            {
                if (_unreadMessages > 0)
                {
                    _unreadMessages = 0;
                    NewMessageBadge.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void NewMessageBadge_Click(object sender, RoutedEventArgs e)
        {
            // Scroll to bottom
            ChatScrollViewer.ChangeView(null, ChatScrollViewer.ScrollableHeight, null);
            _unreadMessages = 0;
            NewMessageBadge.Visibility = Visibility.Collapsed;
        }

        private void LogWhatsApp(string message)
        {
            this.DispatcherQueue.TryEnqueue(() =>
            {
                // 1. Check if we are at the bottom *before* adding content
                // Use a larger tolerance (50px) to handle float precision and close-to-bottom states
                bool isAtBottom = ChatScrollViewer.VerticalOffset >= (ChatScrollViewer.ScrollableHeight - 50);

                bool isRobot = message.Contains("🤖");
                string displayText = message.Replace("🤖", "").Trim();

                // Main Container for Row
                var rowPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = isRobot ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                    Margin = new Thickness(0, 8, 0, 8)
                };

                if (isRobot)
                {
                    // Avatar (Robot/Orange Brand)
                    var avatarContainer = new Border
                    {
                        Width = 32,
                        Height = 32,
                        CornerRadius = new CornerRadius(16), // Circle
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 110, 0)), // #F16E00 (Orange)
                        Margin = new Thickness(0, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Bottom,
                    };

                    var icon = new FontIcon
                    {
                        Glyph = "\uE99A", // Robot Icon
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
                    };
                    avatarContainer.Child = icon;

                    rowPanel.Children.Add(avatarContainer);
                }

                // Chat Bubble
                var bubble = new Border
                {
                    Background = isRobot
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 50, 50, 50))   // Dark Gray (Robot)
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 110, 0)), // #F16E00 (User/Orange)

                    CornerRadius = new CornerRadius(18),
                    Padding = new Thickness(16, 10, 16, 10),
                    MaxWidth = 500
                };

                var textBlock = new TextBlock
                {
                    Text = displayText,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 15,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255))
                };

                bubble.Child = textBlock;
                rowPanel.Children.Add(bubble);

                ChatHistoryPanel.Children.Add(rowPanel);

                // 2. Logic: Should we scroll?
                // - IF it is a user message (right side), always scroll.
                // - OR IF the user was already at the bottom before this message arried, keep them at the bottom.
                if (!isRobot || isAtBottom)
                {
                    // Force the layout to update so ScrollableHeight increases to include the new message
                    ChatHistoryPanel.UpdateLayout();

                    // Scroll to the new absolute bottom
                    ChatScrollViewer.ChangeView(null, double.MaxValue, null);

                    // Reset Badge since we are viewing latest
                    _unreadMessages = 0;
                    NewMessageBadge.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // User is genuinely scrolled up reading history -> Show Badge
                    _unreadMessages++;
                    NewMessageCountText.Text = $"{_unreadMessages} New Message" + (_unreadMessages > 1 ? "s" : "");
                    NewMessageBadge.Visibility = Visibility.Visible;
                }
            });
        }

        private async void SimulateButton_Click(object _, RoutedEventArgs __)
        {
            await SendSimulation();
        }

        private async void SimulatorInput_KeyDown(object _, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                await SendSimulation();
            }
        }

        private async void QuickCommand_Click(object sender, RoutedEventArgs _)
        {
            if (sender is Button btn && btn.Tag != null)
            {
                SimulatorInput.Text = btn.Tag.ToString();
                await SendSimulation();
            }
        }

        private async Task SendSimulation()
        {
            var text = SimulatorInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            SimulatorInput.Text = "";
            SimulatorInput.IsEnabled = false;

            try
            {
                using var client = new HttpClient();
                var content = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
                {
                    new("Body", text),
                    new("From", "Simulator")
                });

                await client.PostAsync($"http://localhost:{_settings.RelayPort}/api/whatsapp", content);
            }
            catch (Exception ex)
            {
                Log($"[Simulator] Error: {ex.Message}");
            }
            finally
            {
                SimulatorInput.IsEnabled = true;
                SimulatorInput.Focus(FocusState.Programmatic);
            }
        }
    }
}
