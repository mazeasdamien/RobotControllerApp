using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    public class RelayServerHost
    {
        public static event Action<string>? OnLog;
        public static event Action<string>? OnWhatsAppLog;

        // Telemetry Events
        public static event Action<float[]>? OnJointsReceived;
        public static event Action<int, int>? OnImageStatsUpdated; // FPS, Total
        public static event Action<byte[]>? OnImageReceived; // Latest base64 decoded frame
        public static event Action<string>? OnUnityMessageReceived;

        private WebApplication? _app;
        private CancellationTokenSource? _cts;

        // Stats
        public static int _imagesTotal = 0;
        public static int _imagesLastSec = 0;
        public static DateTime _lastFpsReset = DateTime.Now;

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
                app.UseWebSockets();

                var connectionManager = app.Services.GetRequiredService<ConnectionManager>();

                // WebSocket endpoint for Robot clients
                app.Map("/robot", async (HttpContext context) =>
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        var robotId = context.Request.Query["robotId"].ToString();
                        if (string.IsNullOrEmpty(robotId))
                        {
                            robotId = $"Robot_{Guid.NewGuid():N}";
                        }

                        using var ws = await context.WebSockets.AcceptWebSocketAsync();
                        Log($"Bridge Client Connected: {robotId}");

                        try
                        {
                            connectionManager.AddRobotClient(robotId, ws);
                            await HandleRobotConnection(ws, robotId, connectionManager, token);
                        }
                        finally
                        {
                            connectionManager.RemoveRobotClient(robotId);
                            Log($"Robot Disconnected: {robotId}");
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                    }
                });

                // WebSocket endpoint for Unity clients
                app.Map("/unity", async (HttpContext context) =>
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
                        Log($"Unity Connected for robot: {robotId}");

                        connectionManager.AddUnityClient(robotId, ws);
                        await HandleUnityConnection(ws, robotId, connectionManager, token);
                        connectionManager.RemoveUnityClient(robotId);

                        Log($"Unity Disconnected from robot: {robotId}");
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                    }
                });

                // Status endpoint
                app.MapGet("/status", () =>
                {
                    var status = connectionManager.GetStatus();
                    return Results.Json(status);
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

                // WhatsApp Endpoint (Twilio)
                app.MapPost("/api/whatsapp", async (HttpRequest request, ConnectionManager manager) =>
                {
                    // Parse Twilio Form Data
                    var form = await request.ReadFormAsync();
                    string body = form["Body"].ToString().Trim().ToLower();

                    string robotId = manager.GetFirstConnectedRobotId() ?? "Robot_Niryo_01";
                    string responseText = "🤖 *Robot Orange Bot*\n";
                    string jsonCommand = "";
                    string mediaUrl = "";

                    bool isConnected = manager.IsRobotConnected(robotId);

                    Log($"📩 Simulated Command: {body} (Target: {robotId}, Connected: {isConnected})");
                    OnWhatsAppLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] 📩 {body}");

                    // Nudge Logic Preparations
                    float[] targetJoints = manager.GetCurrentJoints();
                    float nudgeStep = 0.2f; // ~11 degrees

                    // Command Logic
                    switch (body)
                    {
                        // === MOVEMENT (Absolute) ===
                        case "home":
                            responseText += "Moving to HOME position... 🏠";
                            jsonCommand = GetTrajectoryJson(new float[] { 0, 0, 0, 0, 0, 0 });
                            break;
                        case "park":
                            responseText += "Parking robot... 🅿️";
                            jsonCommand = GetTrajectoryJson(new float[] { 0, 0.5f, -1.2f, 0, 0, 0 });
                            break;
                        case "wave":
                            responseText += "Waving! 👋";
                            jsonCommand = GetTrajectoryJson(new float[] { 0, 0, 0, 0, -0.5f, 0 });
                            break;

                        // === NUDGE (Incremental) ===
                        case "left":
                            responseText += "Turning Left ⬅️";
                            targetJoints[0] += nudgeStep;
                            jsonCommand = GetTrajectoryJson(targetJoints);
                            break;
                        case "right":
                            responseText += "Turning Right ➡️";
                            targetJoints[0] -= nudgeStep;
                            jsonCommand = GetTrajectoryJson(targetJoints);
                            break;
                        case "up":
                            responseText += "Moving Up ⬆️";
                            targetJoints[1] -= nudgeStep; // J2: Negative usually goes up
                            jsonCommand = GetTrajectoryJson(targetJoints);
                            break;
                        case "down":
                            responseText += "Moving Down ⬇️";
                            targetJoints[1] += nudgeStep;
                            jsonCommand = GetTrajectoryJson(targetJoints);
                            break;
                        case "forward":
                        case "reach":
                            responseText += "Reaching Forward ⏭️";
                            targetJoints[2] -= nudgeStep; // J3: Usually negative extends
                            jsonCommand = GetTrajectoryJson(targetJoints);
                            break;
                        case "back":
                            responseText += "Pulling Back ⏮️";
                            targetJoints[2] += nudgeStep;
                            jsonCommand = GetTrajectoryJson(targetJoints);
                            break;

                        // === MODES & CALIBRATION ===
                        case "free":
                        case "learning":
                            responseText += "Enabling Learning Mode (Motors OFF)... 🔓";
                            // Service: /niryo_robot/learning_mode/activate (SetBool: true)
                            jsonCommand = GetServiceJson("/niryo_robot/learning_mode/activate", "niryo_robot_msgs/SetBool", new { value = true });
                            break;
                        case "lock":
                        case "work":
                            responseText += "Disabling Learning Mode (Motors ON)... 🔒";
                            // Service: /niryo_robot/learning_mode/activate (SetBool: false)
                            jsonCommand = GetServiceJson("/niryo_robot/learning_mode/activate", "niryo_robot_msgs/SetBool", new { value = false });
                            break;
                        case "calibrate":
                            responseText += "Requesting Calibration... ⚙️";
                            // Service: /niryo_robot/joints_interface/calibrate_motors (SetInt: 0=AUTO)
                            jsonCommand = GetServiceJson("/niryo_robot/joints_interface/calibrate_motors", "niryo_robot_msgs/SetInt", new { value = 0 });
                            break;

                        // === GRIPPER ===
                        case "open":
                        case "release":
                            responseText += "Opening Gripper... 👐";
                            jsonCommand = GetGripperJson(true);
                            break;
                        case "close":
                        case "grab":
                            responseText += "Closing Gripper (Max Power)... ✊";
                            jsonCommand = GetGripperJson(false);
                            break;

                        // === UTILS ===
                        case "photo":
                        case "pic":
                        case "see":
                        case "image":
                            var img = manager.GetLatestImage();
                            if (img != null)
                            {
                                responseText += $"Here is what I see! 📸";
                                // Public URL to this server's image endpoint
                                // Use configured PublicUrl if available, otherwise fallback to known default or empty
                                string baseUrl = !string.IsNullOrEmpty(PublicUrl) ? PublicUrl : "https://teleop.dmzs-lab.com";
                                mediaUrl = $"{baseUrl.TrimEnd('/')}/image";
                            }
                            else
                            {
                                responseText += "Camera not active or no image received yet. 🚫\nMake sure Robot Console is running.";
                            }
                            break;
                        case "status":
                            responseText += isConnected ? "System Online. 🟢" : "System Offline. 🔴 (Server running, Robot disconnected)";
                            break;

                        // === HELP ===
                        case "help":
                        case "menu":
                        case "commands":
                        case "features":
                            responseText += "📋 *Robot Features*:\n\n" +
                                            "📸 *Photo* - Get camera snapshot\n" +
                                            "👋 *Wave* - Say hello\n" +
                                            "⬅️ *Left/Right* - Rotate Base\n" +
                                            "⬆️ *Up/Down* - Lift Arm\n" +
                                            "⏭️ *Forward/Back* - Reach\n" +
                                            "🏠 *Home* - Reset position\n" +
                                            "🔓 *Free* - Learning Mode ON\n" +
                                            "🔒 *Lock* - Learning Mode OFF\n" +
                                            "⚙️ *Calibrate* - Auto-Calibrate\n" +
                                            "🅿️ *Park* - Fold robot safely\n" +
                                            "✊ *Grab* - Close gripper\n" +
                                            "👐 *Release* - Open gripper\n" +
                                            "🟢 *Status* - Check connectivity";
                            break;

                        default:
                            if (!isConnected)
                                responseText += "⚠️ Warning: Robot is OFFLINE. This command might not execute.";
                            else
                                responseText += "Unknown command. Send *Help* for features.";
                            break;
                    }

                    // Send Command ONLY if connected
                    if (!string.IsNullOrEmpty(jsonCommand))
                    {
                        if (isConnected)
                        {
                            Log($"🚀 Forwarding '{body}' command to {robotId}");
                            await manager.SendToRobotClient(robotId, jsonCommand);
                        }
                        else
                        {
                            // OVERRIDE the previous success message with an error
                            responseText = "🤖 *Robot Orange Bot*\n⚠️ Command Failed: Robot is not connected to the relay server. Unable to execute.";
                            mediaUrl = ""; // Clear any media since we failed
                        }
                    }

                    // Log the response for the UI
                    OnWhatsAppLog?.Invoke($"[{DateTime.Now:HH:mm:ss}] 🤖 {responseText}");

                    // Return TwiML XML (Standard Twilio Response)
                    string xmlResponse = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Response><Message>";
                    xmlResponse += $"<Body>{responseText}</Body>";

                    if (!string.IsNullOrEmpty(mediaUrl))
                    {
                        xmlResponse += $"<Media>{mediaUrl}</Media>";
                    }

                    xmlResponse += "</Message></Response>";

                    return Results.Content(xmlResponse, "application/xml");
                });

                app.MapGet("/", () => "Robot Orange Relay Server - WebSocket endpoints: /robot?robotId=X, /unity?robotId=X");
                app.MapGet("/status", () => "OK");

                Log($"Relay Server active on Port {Port}");
                await app.RunAsync(token);
            }
            catch (Exception ex)
            {
                Log($"Critical Error: {ex.Message}");
            }
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
        }

        // --- HANDLERS ---

        async Task HandleRobotConnection(WebSocket ws, string robotId, ConnectionManager manager, CancellationToken token)
        {
            var buffer = new byte[1024 * 1024]; // 1MB buffer
            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    var message = Encoding.UTF8.GetString(ms.ToArray());

                    // --- LATENCY PING (Interception) ---
                    if (message.Contains("\"op\":\"ping\""))
                    {
                        var pong = "{\"op\":\"pong\"}";
                        var pongBytes = Encoding.UTF8.GetBytes(pong);
                        await ws.SendAsync(new ArraySegment<byte>(pongBytes), WebSocketMessageType.Text, true, token);
                        continue; // Don't forward heartbeat to Unity/ROS
                    }

                    // --- MESSAGE INTERCEPTION ---

                    // 1. Joint States (Update Position)
                    if (message.Contains("joint_states"))
                    {
                        try
                        {
                            using (JsonDocument doc = JsonDocument.Parse(message))
                            {
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
                                    OnJointsReceived?.Invoke(positions);
                                }
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
                                int colonIndex = message.IndexOf(":", dataPropIndex);
                                if (colonIndex != -1)
                                {
                                    int startQuote = message.IndexOf("\"", colonIndex + 1);
                                    if (startQuote != -1)
                                    {
                                        int start = startQuote + 1;
                                        int end = message.IndexOf("\"", start);
                                        if (end != -1)
                                        {
                                            string base64 = message.Substring(start, end - start);
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
                    }

                    // Relay to Unity client
                    await manager.SendToUnityClient(robotId, message);
                }
            }
            catch (WebSocketException)
            {
                // Normal disconnect (abrupt)
            }
            catch (Exception ex)
            {
                Log($"[Robot Error] {ex.Message}");
            }
        }

        async Task HandleUnityConnection(WebSocket ws, string robotId, ConnectionManager manager, CancellationToken token)
        {
            var buffer = new byte[1024 * 1024]; // 1MB buffer
            try
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", token);
                        break;
                    }

                    var message = Encoding.UTF8.GetString(ms.ToArray());
                    OnUnityMessageReceived?.Invoke(message);

                    // Relay to Robot client
                    await manager.SendToRobotClient(robotId, message);
                }
            }
            catch (WebSocketException)
            {
                // Normal disconnect (abrupt)
            }
            catch (Exception ex)
            {
                Log($"[Unity Error] {ex.Message}");
            }
        }

        // --- HELPERS ---

        string GetTrajectoryJson(float[] joints)
        {
            var msg = new
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
                            positions = joints,
                            velocities = new float[0],
                            accelerations = new float[0],
                            effort = new float[0],
                            time_from_start = new { secs = 2, nsecs = 0 }
                        }
                    }
                }
            };
            return JsonSerializer.Serialize(msg);
        }

        string GetGripperJson(bool open)
        {
            var args = new
            {
                id = 11,
                position = open ? 100 : 0,
                speed = 100,
                hold_torque = 1000, // Updated to 1000 for full holding strength
                max_torque = 1000   // Updated to 1000 for full holding strength
            };

            var msg = new
            {
                op = "call_service",
                service = open ? "/niryo_robot/tools/open_gripper" : "/niryo_robot/tools/close_gripper",
                type = "tools_interface/ToolCommand",
                args = args
            };
            string json = JsonSerializer.Serialize(msg);
            Log($"[Gripper] Generated Command: {json}");
            return json;
        }

        string GetServiceJson(string serviceName, string serviceType, object args)
        {
            var msg = new
            {
                op = "call_service",
                service = serviceName,
                type = serviceType,
                args = args
            };
            return JsonSerializer.Serialize(msg);
        }
    }
}
