using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    /// <summary>
    /// Background ASP.NET Core service bridging WebSocket and HTTP connections between
    /// the physical ROS robots and the remote expert's Unity client.
    /// </summary>
    public class RelayServerHost
    {
        public static event Action<string>? OnLog;
        // Telemetry Events
        public static event Action<float[]>? OnJointsReceived;          // Robot 1
        public static event Action<float[]>? OnRobot2JointsReceived;    // Robot 2
        public static event Action<int, int>? OnImageStatsUpdated;      // FPS, Total
        public static event Action<byte[]>? OnImageReceived;            // Latest base64 decoded frame
        public static event Action<string>? OnUnityMessageReceived;
        public static event Action<string>? OnRobotStateReceived;
        public static event Action<string, float, float, string>? OnUnityTelemetryReceived; // location, rx_kbps, tx_kbps, public_ip

        public static string? UnityClientIp { get; private set; }
        public static string? RobotBridgeIp { get; private set; }
        public static bool UnityClientConnected { get; private set; }
        public static long LastQuestLatencyMs { get; private set; } = 0;
        public static event Action<bool>? OnUnityConnectionChanged;

        private WebApplication? _app;
        private CancellationTokenSource? _cts;
        public static ConnectionManager? CurrentManager { get; private set; }

        // Stats
        private static int _imagesTotal = 0;
        private static int _imagesLastSec = 0;
        private static DateTime _lastFpsReset = DateTime.Now;

        public int Port { get; set; } = 5000;
        public string PublicUrl { get; set; } = "";

        private static void Log(string message)
        {
            OnLog?.Invoke($"[Relay] {message}");
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            try
            {
                var builder = WebApplication.CreateBuilder();

                // Supprime les logs verbeux natifs d'ASP.NET (requêtes HTTP etc.) pour ne pas polluer l'UI
                builder.Logging.ClearProviders();

                // Configure Kestrel to listen on all interfaces
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(Port); // Use configured port
                });

                builder.Services.AddSingleton<ConnectionManager>();
                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                    {
                        policy.AllowAnyOrigin()
                              .AllowAnyHeader()
                              .AllowAnyMethod();
                    });
                });

                var app = builder.Build();
                _app = app;

                app.UseCors();
                app.UseWebSockets(new WebSocketOptions
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(25)
                });

                var connectionManager = app.Services.GetRequiredService<ConnectionManager>();
                CurrentManager = connectionManager;

                // WebSocket endpoint for Robot clients
                app.Map("/robot", async context =>
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        var robotId = context.Request.Query["robotId"].ToString();
                        if (string.IsNullOrEmpty(robotId))
                        {
                            robotId = $"Robot_{Guid.NewGuid():N}";
                        }

                        using var ws = await context.WebSockets.AcceptWebSocketAsync();
                        RobotBridgeIp = context.Connection.RemoteIpAddress?.ToString();
                        Log($"[Hub] Bridge Client Connected: {robotId} from {RobotBridgeIp}");

                        try
                        {
                            connectionManager.AddRobotClient(robotId, ws);
                            await HandleRobotConnection(ws, robotId, connectionManager, token);
                        }
                        finally
                        {
                            connectionManager.RemoveRobotClient(robotId);
                            Log($"[Hub] Robot Disconnected: {robotId}");
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                    }
                });

                // WebSocket endpoint for Unity clients
                app.Map("/unity", async context =>
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        var robotId = context.Request.Query["robotId"].ToString();
                        if (string.IsNullOrEmpty(robotId))
                        {
                            context.Response.StatusCode = 400;
                            await context.Response.WriteAsync("robotId parameter required");
                            return;
                        }

                        using var ws = await context.WebSockets.AcceptWebSocketAsync();
                        UnityClientIp = context.Connection.RemoteIpAddress?.ToString();
                        UnityClientConnected = true;
                        OnUnityConnectionChanged?.Invoke(true);

                        string cleanIp = UnityClientIp?.Replace("::ffff:", "") ?? "?";
                        Log($"🟢 [Relay] Remote Expert connected — {robotId}  ({cleanIp})");

                        try
                        {
                            connectionManager.AddUnityClient(robotId, ws);
                            await HandleUnityConnection(ws, robotId, connectionManager, token);
                        }
                        finally
                        {
                            UnityClientConnected = false;
                            OnUnityConnectionChanged?.Invoke(false);
                            LastQuestLatencyMs = 0;
                            connectionManager.RemoveUnityClient(robotId);

                            Log($"🔴 [Relay] Remote Expert disconnected — {robotId}");
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                    }
                });

                // Image Endpoint (Serves latest cached frame)
                app.MapGet("/image", (ConnectionManager manager) =>
                {
                    var img = manager.GetLatestImage();
                    if (img != null && img.Length > 0)
                    {
                        return Results.File(img, "image/jpeg");
                    }
                    return Results.NotFound("No image received yet");
                });

                app.MapGet("/image_operator", (ConnectionManager manager) =>
                {
                    var img = manager.GetLatestOperatorImage();
                    if (img != null && img.Length > 0)
                    {
                        return Results.File(img, "image/jpeg");
                    }
                    return Results.NotFound("No operator image received yet");
                });

                // 3D Models Endpoints (For Quest 3 to fetch generated assets on the fly)
                app.MapGet("/models/{filename}", (string filename) =>
                {
                    string libraryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotControllerApp", "Library");
                    string filePath = Path.Combine(libraryPath, filename);
                    if (File.Exists(filePath))
                    {
                        return Results.File(filePath, "model/gltf-binary"); // or "application/octet-stream"
                    }
                    return Results.NotFound($"Model {filename} not found");
                });

                app.MapGet("/models", () =>
                {
                    string libraryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobotControllerApp", "Library");
                    if (!Directory.Exists(libraryPath)) return Results.Ok(Array.Empty<string>());

                    var files = Directory.GetFiles(libraryPath, "*.glb");
                    var fileNames = new List<string>(files.Length);
                    foreach (var f in files) fileNames.Add(Path.GetFileName(f));

                    return Results.Ok(fileNames);
                });

                app.MapGet("/", () => "Robot Orange Hub Server - WebSocket endpoints: /robot?robotId=X, /unity?robotId=X");
                app.MapGet("/status", () =>
                {
                    if (CurrentManager == null) return Results.NotFound();
                    dynamic status = CurrentManager.GetStatus();
                    return Results.Ok(new { status = "Hub Active", clients = ((List<string>)status.RobotClients).Count });
                });

                Log($"[Hub] Server active on Port {Port}");

                // Utilisation de StartAsync pour ne pas bloquer le thread appelant
                await app.StartAsync(token);
            }
            catch (Exception ex)
            {
                Log($"[Hub] Critical Error: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
        }

        // --- HANDLERS ---

        private static async Task HandleRobotConnection(WebSocket ws, string robotId, ConnectionManager manager, CancellationToken token)
        {
            // Utilisation d'un ArrayPool pour éviter la fragmentation mémoire LOH (Large Object Heap)
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);

            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
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

                    // Optimisation 0 copie si possible : lecture directe dans le buffer du MemoryStream
                    string message;
                    if (ms.TryGetBuffer(out ArraySegment<byte> segment))
                    {
                        message = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
                    }
                    else
                    {
                        message = Encoding.UTF8.GetString(ms.ToArray());
                    }

                    // --- LATENCY PING (Interception) ---
                    if (message.Contains("\"op\":\"ping\""))
                    {
                        await manager.SendToRobotClient(robotId, "{\"op\":\"pong\"}");
                        continue; // Don't forward heartbeat to Unity/ROS
                    }

                    // --- MESSAGE INTERCEPTION ---

                    // 1. Joint States (Update Position)
                    if (message.Contains("joint_states"))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(message);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("msg", out var msgElement) &&
                                msgElement.TryGetProperty("position", out var posElement) &&
                                posElement.ValueKind == JsonValueKind.Array)
                            {
                                var positions = new float[6];
                                int count = 0;
                                foreach (var p in posElement.EnumerateArray())
                                {
                                    if (count < 6) positions[count++] = (float)p.GetDouble();
                                }
                                manager.UpdateJoints(positions);

                                // Route to the correct robot's event
                                bool isRobot2 = robotId.EndsWith("02") || robotId.EndsWith("_2");
                                if (isRobot2) OnRobot2JointsReceived?.Invoke(positions);
                                else OnJointsReceived?.Invoke(positions);
                            }
                        }
                        catch { /* Parsing error safe ignore */ }
                    }

                    // 2. Camera Image
                    if (message.Contains("compressed_video_stream", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            int dataPropIndex = message.IndexOf("\"data\"");
                            if (dataPropIndex != -1)
                            {
                                int colonIndex = message.IndexOf(':', dataPropIndex);
                                if (colonIndex != -1)
                                {
                                    int startQuote = message.IndexOf('"', colonIndex + 1);
                                    if (startQuote != -1)
                                    {
                                        int start = startQuote + 1;
                                        int end = message.IndexOf('"', start);
                                        if (end != -1)
                                        {
                                            string base64 = message[start..end];
                                            if (base64.Length > 100)
                                            {
                                                byte[] imageBytes = Convert.FromBase64String(base64);
                                                manager.UpdateLatestImage(imageBytes);
                                                OnImageReceived?.Invoke(imageBytes);

                                                if (_imagesTotal == 0) Log("First camera frame received! ✓");

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
                        }
                        catch { }

                        // ─── IMPORTANT: Camera frames are NOT forwarded via WebSocket ───
                        continue;
                    }

                    // 4. Robot System State
                    if (message.Contains("robot_state", StringComparison.OrdinalIgnoreCase) && !message.Contains("gripper_state"))
                    {
                        OnRobotStateReceived?.Invoke(message);
                    }

                    // Relay to Unity client
                    await manager.SendToUnityClient(robotId, message);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException)
            {
                // Normal disconnect (abrupt)
            }
            catch (Exception ex)
            {
                Log($"[Robot Error] {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task HandleUnityConnection(WebSocket ws, string robotId, ConnectionManager manager, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            var pingWatch = new System.Diagnostics.Stopwatch();

            // Start local heartbeat to measure Quest latency
            // Fix: Déplacement de l'envoi vers le manager pour utiliser les verrous et éviter les crashs 
            // d'appels simultanés à ws.SendAsync.
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
                    using var ms = new MemoryStream();
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
                    {
                        message = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);
                    }
                    else
                    {
                        message = Encoding.UTF8.GetString(ms.ToArray());
                    }

                    // Intercept Pong from Quest
                    if (message.Contains("\"op\":\"pong\""))
                    {
                        pingWatch.Stop();
                        LastQuestLatencyMs = pingWatch.ElapsedMilliseconds;
                        continue;
                    }

                    // Intercept Unity Telemetry Payload
                    if (message.Contains("\"op\":\"unity_telemetry\""))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(message);
                            var root = doc.RootElement;
                            string loc = root.TryGetProperty("location", out var l) ? l.GetString() ?? "Unknown" : "Unknown";
                            float rx = root.TryGetProperty("rx_kbps", out var r) ? (float)r.GetDouble() : 0f;
                            float tx = root.TryGetProperty("tx_kbps", out var t) ? (float)t.GetDouble() : 0f;
                            string pubIp = root.TryGetProperty("public_ip", out var p) ? p.GetString() ?? "" : "";
                            OnUnityTelemetryReceived?.Invoke(loc, rx, tx, pubIp);
                        }
                        catch { }

                        OnUnityMessageReceived?.Invoke(message);
                        continue;
                    }

                    OnUnityMessageReceived?.Invoke(message);

                    // Relay to Robot client
                    await manager.SendToRobotClient(robotId, message);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex)
            {
                Log($"[Unity Error] {ex.Message}");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}