using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace RobotHub.Services
{
    /// <summary>
    /// Bridges WebSocket and HTTP connections between the physical ROS robots
    /// and the remote expert's Unity client.
    ///
    /// This class no longer creates its own Kestrel instance — routes are registered
    /// into the host's shared WebApplication via MapRoutes().
    /// </summary>
    public class RobotRelayService
    {
        // ── Static events (consumed by RobotBridgeWorker and TelemetryBus) ─────
        public static event Action<string>? OnLog;
        public static event Action<float[]>? OnJointsReceived;
        public static event Action<float[]>? OnRobot2JointsReceived;
        public static event Action<int, int>? OnImageStatsUpdated;
        public static event Action<byte[]>? OnImageReceived;
        public static event Action<string>? OnRobotStateReceived;
        public static event Action<string, float, float, string>? OnUnityTelemetryReceived;
        public static event Action<bool>? OnUnityConnectionChanged;

        // ── Static state readable by workers ─────────────────────────────────
        public static string? UnityClientIp { get; private set; }
        public static string? RobotBridgeIp { get; private set; }
        public static bool UnityClientConnected { get; private set; }
        public static long LastUnityLatencyMs { get; private set; }
        public static ConnectionManager? CurrentManager { get; private set; }

        // ── FPS tracking ──────────────────────────────────────────────────────
        private static int _imagesTotal;
        private static int _imagesLastSec;
        private static DateTime _lastFpsReset = DateTime.Now;

        private static void Log(string msg) => OnLog?.Invoke($"[Relay] {msg}");

        /// <summary>
        /// Registers all relay routes into the shared host WebApplication.
        /// Must be called before app.Run().
        /// </summary>
        public static void MapRoutes(WebApplication app, ConnectionManager manager)
        {
            CurrentManager = manager;

            // ── WebSocket: robot bridge ───────────────────────────────────────
            app.Map("/robot", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var robotId = context.Request.Query["robotId"].ToString();
                if (string.IsNullOrEmpty(robotId))
                    robotId = $"Robot_{Guid.NewGuid():N}";

                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                RobotBridgeIp = context.Connection.RemoteIpAddress?.ToString();
                Log($"Bridge connected: {robotId} from {RobotBridgeIp}");

                try
                {
                    manager.AddRobotClient(robotId, ws);
                    await HandleRobotConnection(ws, robotId, manager, context.RequestAborted);
                }
                finally
                {
                    manager.RemoveRobotClient(robotId);
                    Log($"Robot disconnected: {robotId}");
                }
            });

            // ── WebSocket: Unity expert ───────────────────────────────────────
            app.Map("/unity", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var robotId = context.Request.Query["robotId"].ToString();
                if (string.IsNullOrEmpty(robotId))
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("robotId required");
                    return;
                }

                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                UnityClientIp = context.Connection.RemoteIpAddress?.ToString();
                UnityClientConnected = true;
                OnUnityConnectionChanged?.Invoke(true);

                string cleanIp = UnityClientIp?.Replace("::ffff:", "") ?? "?";
                Log($"Expert connected: {robotId} ({cleanIp})");

                try
                {
                    manager.AddUnityClient(robotId, ws);
                    await HandleUnityConnection(ws, robotId, manager, context.RequestAborted);
                }
                finally
                {
                    UnityClientConnected = false;
                    LastUnityLatencyMs = 0;
                    OnUnityConnectionChanged?.Invoke(false);
                    manager.RemoveUnityClient(robotId);
                    Log($"Expert disconnected: {robotId}");
                }
            });

            // ── HTTP: latest robot camera frame ───────────────────────────────
            app.MapGet("/image", (ConnectionManager mgr) =>
            {
                var img = mgr.GetLatestImage();
                return img is { Length: > 0 } ? Results.File(img, "image/jpeg") : Results.NotFound("No image yet");
            });

            // ── HTTP: latest operator webcam frame ────────────────────────────
            app.MapGet("/image_operator", (ConnectionManager mgr) =>
            {
                var img = mgr.GetLatestOperatorImage();
                return img is { Length: > 0 } ? Results.File(img, "image/jpeg") : Results.NotFound("No operator image yet");
            });

            // ── HTTP: joint state polling ─────────────────────────────────────
            app.MapGet("/joints", (HttpContext ctx, ConnectionManager mgr) =>
            {
                var id = ctx.Request.Query["robotId"].ToString();
                if (string.IsNullOrEmpty(id)) return Results.BadRequest("robotId required");
                var joints = mgr.GetCurrentJoints(id);
                return joints != null
                    ? Results.Ok(new { positions = joints })
                    : Results.NotFound(new { positions = new float[6] });
            });

            Log($"Relay routes registered on shared host.");
        }

        // ── Robot → Relay handler ─────────────────────────────────────────────

        private static async Task HandleRobotConnection(WebSocket ws, string robotId,
            ConnectionManager manager, CancellationToken token)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count > 0) ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }
                    if (ms.Length == 0) continue;

                    string message;
                    if (ms.TryGetBuffer(out ArraySegment<byte> segment))
                        message = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
                    else
                        message = Encoding.UTF8.GetString(ms.ToArray());

                    // Relay ping — reply and drop (do not forward)
                    if (message.Contains("\"op\":\"ping\""))
                    {
                        await manager.SendToRobotClient(robotId, "{\"op\":\"pong\"}");
                        continue;
                    }

                    // Joint states — cache for HTTP polling, do not forward via WebSocket
                    if (message.Contains("joint_states"))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(message);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("msg", out var msgEl) &&
                                msgEl.TryGetProperty("position", out var posEl) &&
                                posEl.ValueKind == JsonValueKind.Array)
                            {
                                var positions = new float[6];
                                int count = 0;
                                foreach (var p in posEl.EnumerateArray())
                                    if (count < 6) positions[count++] = (float)p.GetDouble();

                                manager.UpdateJoints(robotId, positions);
                                bool isR2 = robotId.EndsWith("02") || robotId.EndsWith("_2");
                                if (isR2) OnRobot2JointsReceived?.Invoke(positions);
                                else OnJointsReceived?.Invoke(positions);

                                // Push joints to Unity via the already-open scene3d-ws WebSocket.
                                // Converts ROS radians to degrees because Unity ArticulationBody
                                // drive.target uses degrees (HubArReceiver contract).
                                var anglesDeg = new float[positions.Length];
                                const float Rad2Deg = 180f / MathF.PI;
                                for (int i = 0; i < positions.Length; i++)
                                    anglesDeg[i] = positions[i] * Rad2Deg;

                                var jointsPayload = JsonSerializer.Serialize(new
                                {
                                    angles   = anglesDeg,
                                    robotIdx = isR2 ? 1 : 0
                                });
                                _ = UnityPushServer.Instance?.BroadcastAsync("setRobotJoints", jointsPayload);
                            }
                        }
                        catch { }
                        continue; // joints delivered via scene3d-ws push; HTTP /joints kept as fallback

                    }

                    // Camera frames — cache for HTTP /image, do not forward via WebSocket
                    if (message.Contains("compressed_video_stream", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            int dataPropIndex = message.IndexOf("\"data\"");
                            if (dataPropIndex != -1)
                            {
                                int colonIndex = message.IndexOf(':', dataPropIndex);
                                int startQuote = message.IndexOf('"', colonIndex + 1);
                                if (startQuote != -1)
                                {
                                    int start = startQuote + 1;
                                    int end = message.IndexOf('"', start);
                                    if (end != -1)
                                    {
                                        string b64 = message[start..end];
                                        if (b64.Length > 100)
                                        {
                                            byte[] imageBytes = Convert.FromBase64String(b64);
                                            manager.UpdateLatestImage(imageBytes);
                                            OnImageReceived?.Invoke(imageBytes);

                                            // Push the camera frame to Unity via the already-open
                                            // scene3d-ws WebSocket. Reuses the already-extracted b64
                                            // string to avoid re-encoding. The latest-wins slot in
                                            // UnityPushServer drops stale frames on slow connections.
                                            bool isR2 = robotId.EndsWith("02") || robotId.EndsWith("_2");
                                            _ = UnityPushServer.Instance?.BroadcastAsync(
                                                isR2 ? "updateCameraFeed2" : "updateCameraFeed",
                                                $"{{\"data\":\"{b64}\"}}");

                                            _imagesTotal++;
                                            _imagesLastSec++;
                                            if ((DateTime.Now - _lastFpsReset).TotalSeconds >= 1)
                                            {
                                                OnImageStatsUpdated?.Invoke(_imagesLastSec, _imagesTotal);
                                                _imagesLastSec = 0;
                                                _lastFpsReset = DateTime.Now;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                        continue; // frames pushed via scene3d-ws; HTTP /image kept as fallback

                    }

                    // Robot state — fire event and forward unconditionally
                    if (message.Contains("robot_state", StringComparison.OrdinalIgnoreCase))
                        OnRobotStateReceived?.Invoke(message);

                    // Forward everything else to Unity
                    await manager.SendToUnityClient(robotId, message);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex) { Log($"Robot handler error: {ex.Message}"); }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // ── Unity → Relay handler ─────────────────────────────────────────────

        private static async Task HandleUnityConnection(WebSocket ws, string robotId,
            ConnectionManager manager, CancellationToken token)
        {
            byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
            var pingWatch = new System.Diagnostics.Stopwatch();

            // Heartbeat: ping Unity every 2 seconds, record round-trip latency
            using var pingTimer = new Timer(async _ =>
            {
                if (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    try
                    {
                        pingWatch.Restart();
                        await manager.SendToUnityClient(robotId, "{\"op\":\"ping\"}");
                    }
                    catch { }
                }
            }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count > 0) ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                        break;
                    }
                    if (ms.Length == 0) continue;

                    string message;
                    if (ms.TryGetBuffer(out ArraySegment<byte> segment))
                        message = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
                    else
                        message = Encoding.UTF8.GetString(ms.ToArray());

                    // Pong from Unity — record latency and drop
                    if (message.Contains("\"op\":\"pong\""))
                    {
                        pingWatch.Stop();
                        LastUnityLatencyMs = pingWatch.ElapsedMilliseconds;
                        continue;
                    }

                    // Unity telemetry — extract metrics, do not forward to robot
                    if (message.Contains("\"op\":\"unity_telemetry\""))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(message);
                            var root = doc.RootElement;
                            string loc = root.TryGetProperty("location", out var l) ? l.GetString() ?? "?" : "?";
                            float rx = root.TryGetProperty("rx_kbps", out var r) ? (float)r.GetDouble() : 0f;
                            float tx = root.TryGetProperty("tx_kbps", out var t) ? (float)t.GetDouble() : 0f;
                            string ip = root.TryGetProperty("public_ip", out var p) ? p.GetString() ?? "" : "";
                            OnUnityTelemetryReceived?.Invoke(loc, rx, tx, ip);
                        }
                        catch { }
                        continue;
                    }

                    // Forward everything else (IK solutions, service calls) to the robot
                    await manager.SendToRobotClient(robotId, message);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex) { Log($"Unity handler error: {ex.Message}"); }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
