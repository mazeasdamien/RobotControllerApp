using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    public class RobotBridgeService
    {
        public static event Action<string>? OnLog;
        public static event Action<bool>? OnRosConnectionChanged;

        private ClientWebSocket? _robotWebSocket;
        private ClientWebSocket? _relayWebSocket;
        private CancellationTokenSource? _cts;

        // Configuration
        public string RobotId { get; set; } = "Robot_Niryo_01";
        public string RosIp { get; set; } = "169.254.200.200"; // Default from previous context
        public int RosPort { get; set; } = 9090;
        public string RelayServerUrl { get; set; } = "ws://localhost:5000/robot";
        public int TelemetryIntervalMs { get; set; } = 100;
        public long LastLatencyMs { get; private set; } = 0;
        private System.Diagnostics.Stopwatch _pingWatch = new();

        private void Log(string message)
        {
            OnLog?.Invoke(message);
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();

            // Run connection loops in background
            Task.Run(() => ConnectToRobot(_cts.Token));
            Task.Run(() => ConnectToRelay(_cts.Token));
        }

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
            if (_cts != null) { _cts.Dispose(); _cts = null; }
        }

        async Task ConnectToRobot(CancellationToken token)
        {
            var rosUrl = $"ws://{RosIp}:{RosPort}";
            var buffer = new byte[1024 * 1024];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    _robotWebSocket = new ClientWebSocket();
                    Log($"[ROS] Connecting to {rosUrl}...");

                    await _robotWebSocket.ConnectAsync(new Uri(rosUrl), token);
                    Log("[ROS] ✓ Connected to robot");
                    OnRosConnectionChanged?.Invoke(true);

                    await SubscribeToJointStates();

                    while (_robotWebSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        using var ms = new MemoryStream();
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _robotWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            ms.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        var message = Encoding.UTF8.GetString(ms.ToArray());
                        await SendToRelay(message); // Forward to Relay
                    }
                }
                catch (Exception)
                {
                    if (!token.IsCancellationRequested)
                        Log($"[ROS] Failed to connect to local robot ({RosIp}).");
                }
                finally
                {
                    OnRosConnectionChanged?.Invoke(false);
                    // Force disconnect from Relay so the Server knows we are offline
                    try { _relayWebSocket?.Abort(); } catch { }
                }

                if (!token.IsCancellationRequested) await Task.Delay(3000, token);
            }
        }

        async Task ConnectToRelay(CancellationToken token)
        {
            var relayUrl = $"{RelayServerUrl}?robotId={RobotId}";
            var buffer = new byte[1024 * 1024];

            while (!token.IsCancellationRequested)
            {
                // Bridge now connects to Relay immediately to validate the communication link,
                // even if the hardware ROS robot is still booting or offline.

                if (token.IsCancellationRequested) break;

                try
                {
                    _relayWebSocket = new ClientWebSocket();
                    Log($"[Hub] Connecting to Expert Hub at {relayUrl}...");

                    await _relayWebSocket.ConnectAsync(new Uri(relayUrl), token);
                    Log("[Hub] ✓ Connected to Expert Hub");
                    StartRelayHeartbeat();

                    // Register
                    await SendToRelay(JsonSerializer.Serialize(new
                    {
                        type = "registerRobot",
                        robotId = RobotId,
                        timestamp = DateTime.UtcNow
                    }));

                    while (_relayWebSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
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

                        // --- HEARTBEAT PONG (ROS conventions) ---
                        if (message.Contains("\"op\":\"pong\""))
                        {
                            _pingWatch.Stop();
                            LastLatencyMs = _pingWatch.ElapsedMilliseconds;
                            continue;
                        }

                        if (message.Contains("publish") || message.Contains("call_service"))
                        {
                            Log("[Hub] 📥 Received command from Expert Hub, forwarding to ROS...");
                        }
                        await SendToRobot(message); // Forward to Robot
                    }
                }
                catch (Exception)
                {
                    if (!token.IsCancellationRequested)
                        Log($"[Hub] Failed to connect to Expert Hub.");
                }
                finally
                {
                    _pingTimer?.Dispose();
                }

                if (!token.IsCancellationRequested) await Task.Delay(3000, token);
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

        async Task SubscribeToJointStates()
        {
            var subscribeJoints = new
            {
                op = "subscribe",
                topic = "/joint_states",
                type = "sensor_msgs/JointState",
                throttle_rate = TelemetryIntervalMs
            };
            await SendToRobot(JsonSerializer.Serialize(subscribeJoints));
            Log("[ROS] Subscribed to /joint_states");

            var subscribeCamera = new
            {
                op = "subscribe",
                topic = "/niryo_robot_vision/compressed_video_stream",
                type = "sensor_msgs/CompressedImage",
                throttle_rate = 0
            };
            await SendToRobot(JsonSerializer.Serialize(subscribeCamera));
            Log("[ROS] Subscribed to Camera Stream");

            // Additional Telemetry for Research
            var subscribeGripper = new
            {
                op = "subscribe",
                topic = "/niryo_robot_gripper/gripper_state",
                type = "niryo_robot_msgs/GripperState",
                throttle_rate = 500
            };
            await SendToRobot(JsonSerializer.Serialize(subscribeGripper));

            var subscribeState = new
            {
                op = "subscribe",
                topic = "/niryo_robot/robot_state",
                type = "niryo_robot_msgs/RobotState",
                throttle_rate = 1000
            };
            await SendToRobot(JsonSerializer.Serialize(subscribeState));
            Log("[ROS] Subscribed to Gripper and System State");
        }

        async Task SendToRobot(string json)
        {
            if (_robotWebSocket?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _robotWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            else
            {
                Log("[Hub] ⚠️ Cannot forward command: Not connected to Robot (ROS).");
            }
        }

        async Task SendToRelay(string json)
        {
            if (_relayWebSocket?.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _relayWebSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}
