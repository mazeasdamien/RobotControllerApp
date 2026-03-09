using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    /// <summary>
    /// Lightweight Kestrel server that:
    ///   • Serves scene3d.html and all Assets over HTTP so any device on the LAN
    ///     (e.g. a Meta Quest) can open the 3D preview in its browser.
    ///   • Exposes a WebSocket endpoint at /scene3d-ws that pushes live scene
    ///     updates (camera pose, detected objects, camera feed) to every
    ///     connected browser client in real-time.
    /// </summary>
    public class Scene3dBroadcastServer
    {
        // ── Public API ───────────────────────────────────────────────────────────

        public const int DefaultPort = 8181;

        /// <summary>Port the server will listen on. Must be set before StartAsync().</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>Full path to the Assets folder that contains scene3d.html.</summary>
        public string AssetsPath { get; set; } = string.Empty;

        /// <summary>Full path to the Library folder that contains .glb model files.</summary>
        public string LibraryPath { get; set; } = string.Empty;

        // ── Events / Logging ─────────────────────────────────────────────────────

        public static event Action<string>? OnLog;
        private static void Log(string msg) => OnLog?.Invoke($"[Scene3D] {msg}");

        /// <summary>Fired when a new remote browser connects. Subscriber should push a full state snapshot.</summary>
        public event Func<Task>? OnClientConnected;

        // ── Internal state ────────────────────────────────────────────────────────

        // All currently-connected Quest (or any) browser WebSocket clients.
        private readonly ConcurrentDictionary<string, WebSocket> _clients = new();

        private WebApplication? _app;
        private CancellationTokenSource? _cts;

        /// <summary>True once StartAsync has been called and the server is listening.</summary>
        public bool IsRunning => _app != null;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            if (string.IsNullOrWhiteSpace(AssetsPath) || !Directory.Exists(AssetsPath))
            {
                Log($"Assets path not set or missing: '{AssetsPath}' — server will not start.");
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                var builder = WebApplication.CreateBuilder();

                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.ListenAnyIP(Port);
                });

                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
                });

                var app = builder.Build();
                _app = app;

                app.UseCors();
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

                // ── Static file serving ───────────────────────────────────────────
                // Custom MIME types: .glb / .gltf are not in the default provider
                var mimeProvider = new FileExtensionContentTypeProvider();
                mimeProvider.Mappings[".glb"] = "model/gltf-binary";
                mimeProvider.Mappings[".gltf"] = "model/gltf+json";

                // Assets directory — serves scene3d.html, ned.glb, SVGs, …
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(AssetsPath),
                    RequestPath = "",
                    ContentTypeProvider = mimeProvider
                });

                // Library directory — serves generated .glb models
                if (!string.IsNullOrWhiteSpace(LibraryPath) && Directory.Exists(LibraryPath))
                {
                    app.UseStaticFiles(new StaticFileOptions
                    {
                        FileProvider = new PhysicalFileProvider(LibraryPath),
                        RequestPath = "/library",
                        ContentTypeProvider = mimeProvider
                    });
                }

                // ── WebSocket endpoint for browser clients ────────────────────────
                app.Map("/scene3d-ws", async context =>
                {
                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("WebSocket only");
                        return;
                    }

                    using var ws = await context.WebSockets.AcceptWebSocketAsync();
                    string clientId = Guid.NewGuid().ToString("N")[..8];
                    _clients[clientId] = ws;
                    Log($"Quest browser connected — id={clientId}  ip={context.Connection.RemoteIpAddress}  total={_clients.Count}");

                    // Push full state snapshot to the newly connected client
                    if (OnClientConnected != null)
                    {
                        try { await OnClientConnected.Invoke(); }
                        catch { }
                    }

                    // Keep the socket open — drain any incoming messages (ping/pong or controls)
                    var buffer = new byte[4096];
                    try
                    {
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            if (result.MessageType == WebSocketMessageType.Close)
                                break;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) { }
                    finally
                    {
                        _clients.TryRemove(clientId, out _);
                        Log($"Quest browser disconnected — id={clientId}  remaining={_clients.Count}");
                    }
                });

                // ── Root redirect → scene3d.html ──────────────────────────────────
                app.MapGet("/", context =>
                {
                    context.Response.Redirect("/scene3d.html");
                    return Task.CompletedTask;
                });

                Log($"3D Preview server started on http://*:{Port}  →  open http://YOUR_PC_IP:{Port}/ on the Quest");
                await app.RunAsync(token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Server error: {ex.Message}");
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

        // ── Broadcast helpers (called from MainWindow) ────────────────────────────

        /// <summary>
        /// Broadcasts a typed JSON message to every connected browser.
        /// The envelope: { "type": "...", "payload": ... }
        /// scene3d.html's WebSocket client dispatches on "type".
        /// </summary>
        public Task BroadcastAsync(string type, string payloadJson)
        {
            if (_clients.IsEmpty) return Task.CompletedTask;

            // Build { "type":"setCameraPose", "payload": <raw-json> }
            string envelope = $"{{\"type\":\"{type}\",\"payload\":{payloadJson}}}";
            return BroadcastRawAsync(envelope);
        }

        private async Task BroadcastRawAsync(string message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            var segment = new ArraySegment<byte>(bytes);

            foreach (var (id, ws) in _clients)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    _clients.TryRemove(id, out _);
                }
            }
        }

        /// <summary>Number of currently connected browser clients.</summary>
        public int ConnectedClients => _clients.Count;
    }
}
