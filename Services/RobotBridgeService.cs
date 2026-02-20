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
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        Log($"[ROS] Failed to connect to local robot ({RosIp}). Details: {ex.Message}");
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
                // WAIT for ROS Connection first
                // We don't want to connect to the Relay until we are actually connected to the Robot.
                // This ensures the Relay Server knows that if we are connected, the Robot is too.
                while ((_robotWebSocket == null || _robotWebSocket.State != WebSocketState.Open) && !token.IsCancellationRequested)
                {
                    await Task.Delay(1000, token);
                }

                if (token.IsCancellationRequested) break;

                try
                {
                    _relayWebSocket = new ClientWebSocket();
                    Log($"[Bridge] Connecting to Relay {relayUrl}...");

                    await _relayWebSocket.ConnectAsync(new Uri(relayUrl), token);
                    Log("[Bridge] ✓ Connected to Relay Server");

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
                        await SendToRobot(message); // Forward to Robot
                    }
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        Log($"[Bridge] Failed to connect to Relay Server. Details: {ex.Message}");
                }

                if (!token.IsCancellationRequested) await Task.Delay(3000, token);
            }
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
                Log("[Bridge] ⚠️ Cannot forward command: Not connected to Robot (ROS).");
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
