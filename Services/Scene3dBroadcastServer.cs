using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    /// <summary>
    /// Lightweight Kestrel server that:
    ///   • Serves all Assets (GLB models, images) over HTTP for the Unity Windows app and LAN devices.
    ///   • Exposes a thread-safe WebSocket endpoint at /scene3d-ws (consumed by the Unity viewer).
    ///   • Acts as a zero-allocation proxy for the Whisper API.
    /// </summary>
    public class Scene3dBroadcastServer
    {
        // ── Shared HTTP Client (Prevents TCP Port Exhaustion) ───────────────────
        private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(2) };

        // ── Public API ──────────────────────────────────────────────────────────
        public const int DefaultPort = 8181;
        public const int DefaultHttpsPort = 8182;

        public int Port { get; set; } = DefaultPort;
        public int HttpsPort { get; set; } = DefaultHttpsPort;

        public string AssetsPath { get; set; } = string.Empty;
        public string LibraryPath { get; set; } = string.Empty;

        public string WhisperApiUrl { get; set; } = string.Empty;
        public string WhisperApiKey { get; set; } = string.Empty;

        // ── Events / Logging ────────────────────────────────────────────────────
        public static event Action<string>? OnLog;
        private static void Log(string msg) => OnLog?.Invoke($"[Scene3D] {msg}");

        public event Action<string>? OnBrowserMessage;
        public event Func<Task>? OnClientConnected;
        public event Action? OnClientDisconnected;

        // ── Thread-Safe WebSocket Wrapper ─────────────────────────────────────────
        // Uses a "latest-wins" slot per message type for high-frequency streams
        // (camera feed, robot joints, camera pose).  If a send is still in-flight
        // when the next update arrives, the old update is DROPPED — never queued —
        // so slow Wi-Fi clients (Quest, remote PC) stay real-time instead of
        // replaying a growing backlog of stale frames.
        private class ConnectedClient : IAsyncDisposable
        {
            public string Id { get; }
            private readonly WebSocket _socket;

            // Reliable ordered channel for one-shot messages (scan results, model URLs …)
            private readonly System.Threading.Channels.Channel<byte[]> _reliableChannel =
                System.Threading.Channels.Channel.CreateUnbounded<byte[]>(
                    new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

            // One slot per droppable stream type — always holds the *latest* payload only
            private readonly ConcurrentDictionary<string, byte[]> _latestSlot = new();
            private volatile int _latestPending = 0; // 1 = a droppable flush is already scheduled

            // Message types safe to drop when the client is too slow to keep up.
            // "pong" is droppable: a stale pong from the previous ping cycle must not
            // arrive after the client has already sent a newer ping.
            private static readonly HashSet<string> _droppableTypes =
                new() { "updateCameraFeed", "updateCameraFeed2", "updateArFeed", "updateArFeed2", "setCameraPose", "setRobotJoints", "setCameraRobot", "pong" };

            private readonly CancellationTokenSource _cts = new();
            private readonly SemaphoreSlim _socketLock = new(1, 1);

            public ConnectedClient(string id, WebSocket socket)
            {
                Id = id;
                _socket = socket;
                _ = Task.Run(ReliableSendLoopAsync); // dedicated loop for reliable messages
            }

            // ── Public enqueue API ────────────────────────────────────────────────

            /// <summary>
            /// Enqueue a message. Droppable types keep only the latest value;
            /// non-droppable types go into a reliable ordered channel.
            /// </summary>
            public void Enqueue(string type, byte[] bytes)
            {
                if (_socket.State != WebSocketState.Open) return;

                if (_droppableTypes.Contains(type))
                {
                    // Overwrite whatever was waiting — older value is intentionally discarded
                    _latestSlot[type] = bytes;

                    // Schedule exactly one flush task if none is already running
                    if (Interlocked.Exchange(ref _latestPending, 1) == 0)
                        _ = Task.Run(FlushDroppableAsync);
                }
                else
                {
                    _reliableChannel.Writer.TryWrite(bytes);
                }
            }

            // ── Legacy API used by BroadcastRawAsync ─────────────────────────────
            public Task SendAsync(ArraySegment<byte> segment)
            {
                var bytes = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array!, segment.Offset, bytes, 0, segment.Count);
                // Treat raw sends as reliable (called for one-shot messages)
                _reliableChannel.Writer.TryWrite(bytes);
                return Task.CompletedTask;
            }

            // ── Private send loops ────────────────────────────────────────────────

            private async Task ReliableSendLoopAsync()
            {
                try
                {
                    await foreach (var bytes in _reliableChannel.Reader.ReadAllAsync(_cts.Token))
                        await SendBytesDirectAsync(bytes);
                }
                catch (OperationCanceledException) { }
            }

            private async Task FlushDroppableAsync()
            {
                // Drain all pending latest-slots in one pass
                foreach (var key in _latestSlot.Keys.ToArray())
                {
                    if (_latestSlot.TryRemove(key, out var bytes))
                        await SendBytesDirectAsync(bytes);
                }

                // Allow next arrival to schedule another flush
                Interlocked.Exchange(ref _latestPending, 0);

                // If new items arrived while we were flushing, go again
                if (!_latestSlot.IsEmpty && Interlocked.Exchange(ref _latestPending, 1) == 0)
                    await FlushDroppableAsync();
            }

            private async Task SendBytesDirectAsync(byte[] bytes)
            {
                if (_socket.State != WebSocketState.Open) return;
                await _socketLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                try
                {
                    if (_socket.State == WebSocketState.Open)
                        await _socket.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true, _cts.Token);
                }
                catch { }
                finally { _socketLock.Release(); }
            }

            public WebSocketState State => _socket.State;

            public async ValueTask DisposeAsync()
            {
                _cts.Cancel();
                _reliableChannel.Writer.TryComplete();
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    try
                    {
                        await _socketLock.WaitAsync(TimeSpan.FromSeconds(1));
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                            "Server shutting down", CancellationToken.None);
                    }
                    catch { }
                    finally { _socketLock.Release(); }
                }
                _socketLock.Dispose();
                _cts.Dispose();
                _socket.Dispose();
            }
        }

        private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();

        private WebApplication? _app;
        private CancellationTokenSource? _cts;
        private string? _cachedServerGeo; // cached response from ipapi.co for /server-geo endpoint

        public bool IsRunning => _app != null;

        // ── Lifecycle ───────────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            if (IsRunning) return;

            if (string.IsNullOrWhiteSpace(AssetsPath))
            {
                Log("Assets path not set — server will not start.");
                return;
            }

            // Fix 404 issue: Must ensure directories exist BEFORE injecting PhysicalFileProvider
            Directory.CreateDirectory(AssetsPath);
            if (!string.IsNullOrWhiteSpace(LibraryPath))
                Directory.CreateDirectory(LibraryPath);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                var builder = WebApplication.CreateBuilder();

                // Silence native ASP.NET verbose logs from the WinUI Output window
                builder.Logging.ClearProviders();

                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Limits.MaxRequestBodySize = 50 * 1024 * 1024; // 50 MB
                    options.ListenAnyIP(Port);
                    try
                    {
                        options.ListenAnyIP(HttpsPort, lo => lo.UseHttps(BuildSelfSignedCert()));
                    }
                    catch (Exception ex)
                    {
                        Log($"HTTPS bind failed (Cert error?): {ex.Message}");
                    }
                });

                // TCP_NODELAY — disables Nagle's algorithm so small WS frames (ping/pong,
                // pose updates) are flushed immediately instead of being batched for up to 40 ms.
                builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions>(opts =>
                {
                    opts.NoDelay = true;
                });

                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                        policy.AllowAnyOrigin()
                              .AllowAnyHeader()
                              .AllowAnyMethod()
                              .WithExposedHeaders("Upgrade", "Connection"));
                });

                builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(opt =>
                {
                    opt.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50 MB limits
                    opt.ValueLengthLimit = int.MaxValue;
                });

                var app = builder.Build();
                _app = app;

                app.UseCors();
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(60) });

                // ── Static file serving ───────────────────────────────────────────
                var mimeProvider = new FileExtensionContentTypeProvider();
                mimeProvider.Mappings[".glb"] = "model/gltf-binary";
                mimeProvider.Mappings[".gltf"] = "model/gltf+json";

                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(AssetsPath),
                    RequestPath = "",
                    ContentTypeProvider = mimeProvider
                });

                if (!string.IsNullOrWhiteSpace(LibraryPath))
                {
                    // Use an explicit endpoint instead of UseStaticFiles so we can set
                    // proper CORS + Content-Length headers that Cloudflare Tunnel needs
                    // to correctly proxy large binary GLB files (avoids 502 errors).
                    app.MapGet("/library/{**filename}", async context =>
                    {
                        var filename = context.Request.RouteValues["filename"]?.ToString() ?? "";
                        // Decode percent-encoded filename (spaces, special chars)
                        filename = Uri.UnescapeDataString(filename);

                        // Security: prevent directory traversal
                        var fullPath = Path.GetFullPath(Path.Combine(LibraryPath, filename));
                        if (!fullPath.StartsWith(Path.GetFullPath(LibraryPath), StringComparison.OrdinalIgnoreCase))
                        {
                            context.Response.StatusCode = 403;
                            return;
                        }

                        if (!File.Exists(fullPath))
                        {
                            context.Response.StatusCode = 404;
                            await context.Response.WriteAsync($"Not found: {filename}");
                            return;
                        }

                        // Determine MIME type
                        string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                        string mime = ext switch
                        {
                            ".glb"  => "model/gltf-binary",
                            ".gltf" => "model/gltf+json",
                            ".png"  => "image/png",
                            ".jpg" or ".jpeg" => "image/jpeg",
                            ".webp" => "image/webp",
                            _       => "application/octet-stream"
                        };

                        var fi = new FileInfo(fullPath);
                        context.Response.ContentType   = mime;
                        context.Response.ContentLength = fi.Length;
                        context.Response.Headers["Access-Control-Allow-Origin"]  = "*";
                        context.Response.Headers["Cache-Control"] = "public, max-age=3600";
                        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

                        try
                        {
                            await context.Response.SendFileAsync(fullPath, context.RequestAborted);
                        }
                        catch (OperationCanceledException) { }
                    });
                }

                // ── WebSocket Endpoint ────────────────────────────────────────────
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

                    var client = new ConnectedClient(clientId, ws);
                    _clients.TryAdd(clientId, client);
                    Log($"Browser connected — id={clientId} ip={context.Connection.RemoteIpAddress} total={_clients.Count}");

                    if (OnClientConnected != null)
                    {
                        // Fire-and-forget to avoid blocking the receive loop
                        _ = Task.Run(async () => { try { await OnClientConnected.Invoke(); } catch { } }, token);
                    }

                    // Buffer pooling prevents LOH fragmentation on frequent 64KB allocations
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                    using var messageBuffer = new MemoryStream();

                    try
                    {
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            WebSocketReceiveResult result;
                            messageBuffer.SetLength(0);
                            do
                            {
                                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                                if (result.MessageType == WebSocketMessageType.Close) break;
                                if (result.Count > 0) messageBuffer.Write(buffer, 0, result.Count);

                            } while (!result.EndOfMessage);

                            if (result.MessageType == WebSocketMessageType.Close) break;

                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                if (messageBuffer.TryGetBuffer(out ArraySegment<byte> segment))
                                {
                                    var msg = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);

                                    // ── Fast-path: ping → pong without going through the MainWindow event chain.
                                    // Cuts ~1-3 ms of dispatch + channel roundtrip for the RTT-critical message.
                                    if (msg.Contains("\"ping\"", StringComparison.Ordinal))
                                    {
                                        try
                                        {
                                            using var pingDoc = JsonDocument.Parse(msg);
                                            var root = pingDoc.RootElement;
                                            if (root.TryGetProperty("type", out var t) && t.GetString() == "ping"
                                                && root.TryGetProperty("payload", out var pl))
                                            {
                                                string tsRaw = pl.ValueKind == JsonValueKind.Number ? pl.GetRawText() : "0";
                                                string pongJson = $"{{\"type\":\"pong\",\"payload\":{tsRaw}}}";
                                                client.Enqueue("pong", Encoding.UTF8.GetBytes(pongJson));
                                                continue; // skip MainWindow dispatch
                                            }
                                        }
                                        catch { /* fall through to normal dispatch */ }
                                    }

                                    OnBrowserMessage?.Invoke(msg);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) { }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        _clients.TryRemove(clientId, out _);
                        await client.DisposeAsync();
                        Log($"Browser disconnected — id={clientId} remaining={_clients.Count}");
                        OnClientDisconnected?.Invoke();
                    }
                });

                app.MapGet("/", async context =>
                {
                    context.Response.ContentType = "application/json";
                    context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                    await context.Response.WriteAsync($"{{\"ok\":true,\"service\":\"Robot Orange Hub\",\"clients\":{_clients.Count},\"ws\":\"/scene3d-ws\"}}");
                });

                app.MapGet("/ping", async context =>
                {
                    context.Response.Headers.Append("X-Hub-Version", "1.0");
                    context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                    await context.Response.WriteAsync($"{{\"ok\":true,\"clients\":{_clients.Count}}}");
                });

                // ── Server geo-location (called once by the browser to populate the route tooltip) ──
                // Fetches ipapi.co from the server side so the browser learns the server's real city.
                app.MapGet("/server-geo", async context =>
                {
                    context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
                    context.Response.ContentType = "application/json";
                    try
                    {
                        // Cache result so we only outbound-call ipapi.co once per server lifetime
                        if (_cachedServerGeo is null)
                        {
                            using var req = new HttpRequestMessage(HttpMethod.Get, "https://ipapi.co/json/");
                            req.Headers.Add("User-Agent", "RobotControllerApp/1.0");
                            using var resp = await SharedHttpClient.SendAsync(req, context.RequestAborted);
                            _cachedServerGeo = await resp.Content.ReadAsStringAsync(context.RequestAborted);
                        }
                        await context.Response.WriteAsync(_cachedServerGeo);
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = 502;
                        await context.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}");
                    }
                });

                // ── Zero-Allocation Transcribe Proxy ──────────────────────────────
                app.MapPost("/transcribe", async context =>
                {
                    if (!context.Request.HasFormContentType)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("Multipart form required");
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(WhisperApiUrl) || string.IsNullOrWhiteSpace(WhisperApiKey))
                    {
                        context.Response.StatusCode = 503;
                        await context.Response.WriteAsync("Whisper API not configured.");
                        return;
                    }

                    IFormFile? audioFile;
                    try
                    {
                        var form = await context.Request.ReadFormAsync(context.RequestAborted);
                        audioFile = form.Files.GetFile("file");
                    }
                    catch (Exception ex)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync($"Form parse error: {ex.Message}");
                        return;
                    }

                    if (audioFile == null || audioFile.Length == 0)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("No audio file received");
                        return;
                    }

                    try
                    {
                        using var requestMsg = new HttpRequestMessage(HttpMethod.Post, WhisperApiUrl.TrimEnd('/') + "/v1/audio/transcriptions");
                        requestMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WhisperApiKey);

                        using var multipart = new MultipartFormDataContent();

                        // ZERO-COPY Streaming: Pipe stream directly to HTTP instead of copying to MemoryStream
                        await using var audioStream = audioFile.OpenReadStream();
                        using var audioContent = new StreamContent(audioStream);

                        string cType = audioFile.ContentType ?? "audio/webm";
                        int semiIndex = cType.IndexOf(';');
                        if (semiIndex > 0) cType = cType[..semiIndex];

                        audioContent.Headers.ContentType = new MediaTypeHeaderValue(cType);
                        multipart.Add(audioContent, "file", audioFile.FileName ?? "audio.webm");
                        multipart.Add(new StringContent("openai/whisper-1"), "model");

                        requestMsg.Content = multipart;

                        // Use ResponseHeadersRead to immediately stream the API response back
                        using var response = await SharedHttpClient.SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

                        context.Response.StatusCode = (int)response.StatusCode;
                        context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";

                        // Stream response directly to output
                        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        Log($"Transcribe error: {ex.Message}");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Transcription proxy error: {ex.Message}");
                    }
                });

                Log($"3D Preview server started on http://*:{Port}  →  open http://YOUR_PC_IP:{Port}/ on the Quest");
                await app.StartAsync(token); // Run in background without blocking
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"Server error: {ex.Message}");
            }
        }

        private static X509Certificate2 BuildSelfSignedCert()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=OrangeRobotHub", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            try
            {
                foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) san.AddIpAddress(ip);
            }
            catch { }

            req.CertificateExtensions.Add(san.Build());
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

            // EphemeralKeySet prevents Windows from cluttering the OS cert store with temp keys on every run
            return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.EphemeralKeySet);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();

            var disposeTasks = _clients.Values.Select(c => c.DisposeAsync().AsTask());
            await Task.WhenAll(disposeTasks);
            _clients.Clear();

            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
        }

        // ── Broadcast helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Broadcasts a typed message to all connected clients.
        /// Droppable types (camera feed, pose, joints) use the latest-wins slot;
        /// all others are sent reliably in order.
        /// </summary>
        public Task BroadcastAsync(string type, string payloadJson)
        {
            if (_clients.IsEmpty) return Task.CompletedTask;
            string envelope = $"{{\"type\":\"{type}\",\"payload\":{payloadJson}}}";
            byte[] bytes = Encoding.UTF8.GetBytes(envelope);

            foreach (var kvp in _clients)
            {
                try { kvp.Value.Enqueue(type, bytes); }
                catch { _clients.TryRemove(kvp.Key, out _); }
            }

            return Task.CompletedTask;
        }

        // Kept for any internal callers that pass raw envelopes (treated as reliable)
        private Task BroadcastRawAsync(string message)
        {
            if (_clients.IsEmpty) return Task.CompletedTask;
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            foreach (var kvp in _clients)
            {
                try { kvp.Value.Enqueue("_raw", bytes); }
                catch { _clients.TryRemove(kvp.Key, out _); }
            }
            return Task.CompletedTask;
        }

        public int ConnectedClients => _clients.Count;
    }
}