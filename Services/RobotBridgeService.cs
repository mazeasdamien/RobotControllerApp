using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotHub.Services
{
    /// <summary>
    /// Service bridging continuous WebSocket telemetry and commands between a remote ROS master
    /// and the local Relay Hub server. Instantiated per-robot.
    /// </summary>
    public class RobotBridgeService
    {
        /// <summary>Global logging event stream.</summary>
        public static event Action<string>? OnLog;

        /// <summary>Instance-level stream emitting true when the physical robot WebSocket establishes connection.</summary>
        public event Action<bool>? OnInstanceConnectionChanged;

        /// <summary>Fires on each hardware_status update with key metrics.</summary>
        public event Action<HardwareInfo>? OnHardwareStatusUpdated;

        /// <summary>Fires on each robot_status update with the status string.</summary>
        public event Action<string>? OnRobotStatusUpdated;
        /// <summary>Indicates if the WebSocket to the physical robot is currently open.</summary>
        public bool IsConnected { get; private set; }

        private bool _isLearningMode = false; // tracked from /niryo_robot/robot_state

        private ClientWebSocket? _robotWebSocket;
        private ClientWebSocket? _relayWebSocket;
        private CancellationTokenSource? _cts;

        public string RobotId { get; set; } = "Robot_Niryo_01";
        public string RosIp { get; set; } = "169.254.200.200";
        public int RosPort { get; set; } = 9090;
        public string RelayServerUrl { get; set; } = "ws://localhost:5000/robot";
        // Set to 0: deliver every joint_states message at the robot's native publish rate.
        // A non-zero value throttles rosbridge and directly causes perceived joint state lag.
        public int TelemetryIntervalMs { get; set; } = 0;
        /// <summary>Set to false for robots without a camera (skips camera topic subscription).</summary>
        public bool HasCamera { get; set; } = true;

        // Whether the camera is currently subscribed
        private bool _cameraSubscribed = false;

        /// <summary>Most recent measurement of round-trip latency to the local relay.</summary>
        public long LastLatencyMs { get; private set; } = 0;

        private readonly System.Diagnostics.Stopwatch _pingWatch = new();

        private static void Log(string message) => OnLog?.Invoke(message);

        /// <summary>
        /// Initiates the dual asynchronous WebSocket connection loops to the robot and relay.
        /// </summary>
        public void Start()
        {
            // Allow restart: if already running, stop first
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            Task.Run(() => ConnectToRobotAsync(_cts.Token));
            Task.Run(() => ConnectToRelayAsync(_cts.Token));
        }

        /// <summary>
        /// Gracefully terminates active WebSockets and cancels reconnection routines.
        /// </summary>
        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_robotWebSocket != null)
            {
                try { await _robotWebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stop", CancellationToken.None); } catch { }
                _robotWebSocket.Dispose();
                _robotWebSocket = null;
            }
            if (_relayWebSocket != null)
            {
                try { await _relayWebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stop", CancellationToken.None); } catch { }
                _relayWebSocket.Dispose();
                _relayWebSocket = null;
            }
            _cts?.Dispose();
            _cts = null;
        }

        private async Task ConnectToRobotAsync(CancellationToken token)
        {
            var rosUrl = $"ws://{RosIp}:{RosPort}";
            var buffer = new byte[1024 * 1024];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    _robotWebSocket = new ClientWebSocket();
                    await _robotWebSocket.ConnectAsync(new Uri(rosUrl), token);
                    IsConnected = true;
                    OnInstanceConnectionChanged?.Invoke(true);

                    await SubscribeToJointStates();

                    // Reuse a single MemoryStream for the lifetime of the connection
                    // to avoid a heap allocation on every incoming message.
                    using var ms = new MemoryStream(1024 * 512);

                    while (_robotWebSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        ms.SetLength(0); // reset without reallocation
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _robotWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        // Zero-copy decode: read directly from the MemoryStream internal buffer.
                        string message;
                        if (ms.TryGetBuffer(out ArraySegment<byte> seg))
                            message = Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count);
                        else
                            message = Encoding.UTF8.GetString(ms.ToArray());
                        ParseLearningModeIfPresent(message);
                        ParseHardwareStatusIfPresent(message);
                        ParseRobotStatusIfPresent(message);


                        await SendToRelay(message); // Forward to Relay
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // Clean shutdown
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        Log($"[ROS:{RobotId}] Failed to connect to robot ({RosIp}): {ex.Message}");
                }
                finally
                {
                    if (IsConnected)
                    {
                        IsConnected = false;
                        OnInstanceConnectionChanged?.Invoke(false);
                    }

                    // Forcefully drop Relay connection if physical connection drops
                    // This unblocks the pending ReceiveAsync on Relay and updates the Hub status 
                    try { _relayWebSocket?.Abort(); } catch { }
                }

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task ConnectToRelayAsync(CancellationToken token)
        {
            var relayUrl = $"{RelayServerUrl}?robotId={RobotId}";
            var buffer = new byte[1024 * 1024];

            while (!token.IsCancellationRequested)
            {
                if (token.IsCancellationRequested) break;

                // Wait until the physical robot is actually connected before bridging to the Hub
                if (!IsConnected)
                {
                    try { await Task.Delay(1000, token); } catch { break; }
                    continue;
                }

                try
                {
                    _relayWebSocket = new ClientWebSocket();
                    await _relayWebSocket.ConnectAsync(new Uri(relayUrl), token);
                    StartRelayHeartbeat();

                    // Register
                    await SendToRelay(JsonSerializer.Serialize(new
                    {
                        type = "registerRobot",
                        robotId = RobotId,
                        timestamp = DateTime.UtcNow
                    }));

                    while (_relayWebSocket.State == WebSocketState.Open && !token.IsCancellationRequested && IsConnected)
                    {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _relayWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var message = Encoding.UTF8.GetString(ms.ToArray());

                        if (message.Contains("\"op\":\"pong\""))
                        {
                            _pingWatch.Stop();
                            LastLatencyMs = _pingWatch.ElapsedMilliseconds;
                            continue;
                        }

                        await SendToRobotAsync(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        Log($"[Bridge:{RobotId}] Failed to connect to Local Relay: {ex.Message}");
                }
                finally
                {
                    _pingTimer?.Dispose();
                    _pingTimer = null;
                    if (_relayWebSocket != null)
                    {
                        try { _relayWebSocket.Dispose(); } catch { }
                        _relayWebSocket = null;
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(3000, token); } catch (OperationCanceledException) { break; }
                }
            }
        }

        private Timer? _pingTimer;
        private void StartRelayHeartbeat()
        {
            _pingTimer?.Dispose();
            _pingTimer = new Timer(async _ =>
            {
                if (_relayWebSocket?.State == WebSocketState.Open)
                {
                    try
                    {
                        _pingWatch.Restart();
                        await SendToRelay("{\"op\":\"ping\"}");
                    }
                    catch { }
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Quickly scans a rosbridge publish message for /niryo_robot/robot_state
        /// and extracts the learning_mode boolean field to fire OnLearningModeChanged.
        /// </summary>
        private void ParseLearningModeIfPresent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("topic", out var topicEl)) return;
                if (topicEl.GetString() != "/niryo_robot/robot_state") return;

                if (!root.TryGetProperty("msg", out var msg)) return;
                if (!msg.TryGetProperty("learning_mode", out var lmEl)) return;

                bool isLearning = lmEl.GetBoolean();
                if (_isLearningMode != isLearning)
                {
                    _isLearningMode = isLearning;
                    // Refresh STATUS string whenever learning mode changes
                    OnRobotStatusUpdated?.Invoke(DeriveStatusString(false, false));
                }
            }
            catch { /* malformed message — ignore */ }
        }

        private void ParseHardwareStatusIfPresent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("topic", out var t)) return;
                if (t.GetString() != "/niryo_robot_hardware_interface/hardware_status") return;
                if (!root.TryGetProperty("msg", out var msg)) return;

                int rpiTemp = msg.TryGetProperty("rpi_temperature", out var rpiEl) ? rpiEl.GetInt32() : 0;
                bool calibNeeded = msg.TryGetProperty("calibration_needed", out var cnEl) && cnEl.GetBoolean();
                bool calibInProgress = msg.TryGetProperty("calibration_in_progress", out var cipEl) && cipEl.GetBoolean();

                int maxMotorTemp = 0;
                if (msg.TryGetProperty("temperatures", out var tempsEl) && tempsEl.ValueKind == JsonValueKind.Array)
                    foreach (var el in tempsEl.EnumerateArray())
                        maxMotorTemp = Math.Max(maxMotorTemp, (int)el.GetDouble());

                int errorCount = 0;
                if (msg.TryGetProperty("hardware_errors", out var errEl) && errEl.ValueKind == JsonValueKind.Array)
                    foreach (var el in errEl.EnumerateArray())
                        if (el.GetInt32() != 0) errorCount++;

                OnHardwareStatusUpdated?.Invoke(new HardwareInfo(rpiTemp, calibNeeded, calibInProgress, maxMotorTemp, errorCount));

                // Derive STATUS string locally — /niryo_robot_status/robot_status may not exist on older firmware
                OnRobotStatusUpdated?.Invoke(DeriveStatusString(calibInProgress, calibNeeded));
            }
            catch { }
        }

        private void ParseRobotStatusIfPresent(string json)
        {
            // /niryo_robot_status/robot_status may not exist on all firmware versions.
            // We keep the subscription but only use it if the field is present.
            // Status is primarily derived in ParseHardwareStatusIfPresent above.
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("topic", out var t)) return;
                if (t.GetString() != "/niryo_robot_status/robot_status") return;
                if (!root.TryGetProperty("msg", out var msg)) return;
                if (!msg.TryGetProperty("robot_status_str", out var sEl)) return;
                string s = sEl.GetString() ?? "";
                if (!string.IsNullOrEmpty(s))
                    OnRobotStatusUpdated?.Invoke(s); // override derived value with authoritative one
            }
            catch { }
        }

        /// <summary>Builds a human-readable status string from known state flags.</summary>
        private string DeriveStatusString(bool calibInProgress, bool calibNeeded)
        {
            if (calibInProgress) return "CALIBRATING";
            if (calibNeeded) return "NEEDS CALIB";
            if (_isLearningMode) return "LEARNING";
            return "STANDBY";
        }

        private async Task SubscribeToJointStates()
        {
            var subscribeJoints = new
            {
                op = "subscribe",
                topic = "/joint_states",
                type = "sensor_msgs/JointState",
                throttle_rate = TelemetryIntervalMs
            };
            await SendToRobotAsync(JsonSerializer.Serialize(subscribeJoints));

            // Only subscribe to camera if robot has one
            if (HasCamera)
            {
                await SubscribeCameraAsync();
            }

            var subscribeState = new
            {
                op = "subscribe",
                topic = "/niryo_robot/robot_state",
                type = "niryo_robot_msgs/RobotState",
                throttle_rate = 1000
            };
            await SendToRobotAsync(JsonSerializer.Serialize(subscribeState));

            var subscribeHwStatus = new
            {
                op = "subscribe",
                topic = "/niryo_robot_hardware_interface/hardware_status",
                type = "niryo_robot_msgs/HardwareStatus",
                throttle_rate = 2000
            };
            await SendToRobotAsync(JsonSerializer.Serialize(subscribeHwStatus));

            var subscribeRobotStatus = new
            {
                op = "subscribe",
                topic = "/niryo_robot_status/robot_status",
                type = "niryo_robot_msgs/RobotStatus",
                throttle_rate = 1000
            };
            await SendToRobotAsync(JsonSerializer.Serialize(subscribeRobotStatus));
        }

        private async Task SubscribeCameraAsync()
        {
            _cameraSubscribed = true;
            var sub = new
            {
                op = "subscribe",
                topic = "/niryo_robot_vision/compressed_video_stream",
                type = "sensor_msgs/CompressedImage",
                throttle_rate = 0
            };
            await SendToRobotAsync(JsonSerializer.Serialize(sub));
            Log($"[{RobotId}] 📷 Camera subscribed");
        }

        private async Task UnsubscribeCameraAsync()
        {
            _cameraSubscribed = false;
            var unsub = new
            {
                op = "unsubscribe",
                topic = "/niryo_robot_vision/compressed_video_stream"
            };
            await SendToRobotAsync(JsonSerializer.Serialize(unsub));
            Log($"[{RobotId}] Camera unsubscribed");
        }

        /// <summary>
        /// Dynamically enable or disable the camera subscription on this robot.
        /// Safe to call at any time — silently ignored if not connected.
        /// </summary>
        public async Task SetCameraEnabledAsync(bool enable)
        {
            HasCamera = enable;
            if (!IsConnected) return; // Will take effect on next reconnect via HasCamera flag

            if (enable && !_cameraSubscribed)
                await SubscribeCameraAsync();
            else if (!enable && _cameraSubscribed)
                await UnsubscribeCameraAsync();
        }

        private async Task SendToRobotAsync(string json)
        {
            if (_robotWebSocket?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _robotWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        /// <summary>Sends a ROS command (service call or publish) directly to the robot WebSocket. Used by the Debug panel.</summary>
        public Task SendDirectToRobotAsync(string json) => SendToRobotAsync(json);

        async Task SendToRelay(string json)
        {
            if (_relayWebSocket?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _relayWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }

    /// <summary>Snapshot of key metrics from /niryo_robot_hardware_interface/hardware_status.</summary>
    public record HardwareInfo(
        int RpiTemp,
        bool CalibrationNeeded,
        bool CalibrationInProgress,
        int MaxMotorTemp,
        int ErrorCount
    );
}
