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
using System.Net.Http;
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
    ///   • Serves preview.html and all Assets over HTTP so any device on the LAN
    ///     (e.g. a Meta Quest) can open the 3D preview in its browser.
    ///   • Exposes a WebSocket endpoint at /scene3d-ws that pushes live scene
    ///     updates (camera pose, detected objects, camera feed) to every
    ///     connected browser client in real-time.
    /// </summary>
    public class Scene3dBroadcastServer
    {
        // ── Public API ───────────────────────────────────────────────────────────

        public const int DefaultPort     = 8181;
        public const int DefaultHttpsPort = 8182;

        /// <summary>Port the server will listen on. Must be set before StartAsync().</summary>
        public int Port      { get; set; } = DefaultPort;
        public int HttpsPort { get; set; } = DefaultHttpsPort;

        /// <summary>Full path to the Assets folder that contains preview.html.</summary>
        public string AssetsPath { get; set; } = string.Empty;

        /// <summary>Full path to the Library folder that contains .glb model files.</summary>
        public string LibraryPath { get; set; } = string.Empty;

        /// <summary>Base URL of the Orange Whisper API (e.g. https://api.orange.com/ai).</summary>
        public string WhisperApiUrl { get; set; } = string.Empty;

        /// <summary>Bearer token / API key for the Orange Whisper API.</summary>
        public string WhisperApiKey { get; set; } = string.Empty;

        // ── Events / Logging ─────────────────────────────────────────────────────

        public static event Action<string>? OnLog;
        private static void Log(string msg) => OnLog?.Invoke($"[Scene3D] {msg}");

        /// <summary>Fired when a connected browser client sends a message (e.g. FK slider moved).</summary>
        public event Action<string>? OnBrowserMessage;

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
                    options.ListenAnyIP(HttpsPort, lo => lo.UseHttps(BuildSelfSignedCert()));
                });

                builder.Services.AddCors(options =>
                {
                    options.AddDefaultPolicy(policy =>
                        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
                });

                // Allow up to 50 MB audio uploads for voice transcription
                builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(opt =>
                {
                    opt.MultipartBodyLengthLimit = 50 * 1024 * 1024; // 50 MB
                    opt.ValueLengthLimit = int.MaxValue;
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

                // Assets directory — serves preview.html, ned.glb, SVGs, …
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

                    // Keep the socket open -- decode and forward any incoming messages (controls, FK, etc.)
                    var buffer = new byte[4096];
                    try
                    {
                        while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                            if (result.MessageType == WebSocketMessageType.Close)
                                break;
                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                OnBrowserMessage?.Invoke(msg);
                            }
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

                // ── Root redirect → preview.html ──────────────────────────────────
                app.MapGet("/", context =>
                {
                    context.Response.Redirect("/preview.html");
                    return Task.CompletedTask;
                });

                // ── /transcribe — proxy audio to Orange Whisper API ───────────────
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
                        await context.Response.WriteAsync("Whisper API not configured (set OrangeApiKey and WhisperApiUrl)");
                        return;
                    }

                    IFormFile? audioFile = null;
                    try
                    {
                        var form = await context.Request.ReadFormAsync();
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
                        using var httpClient = new HttpClient();
                        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {WhisperApiKey}");

                        using var multipart = new MultipartFormDataContent();

                        // Copy the audio stream into a MemoryStream so it can be sent
                        using var ms = new MemoryStream();
                        await audioFile.CopyToAsync(ms);
                        ms.Position = 0;

                        var audioContent = new ByteArrayContent(ms.ToArray());
                        audioContent.Headers.ContentType =
                            new System.Net.Http.Headers.MediaTypeHeaderValue(
                                audioFile.ContentType ?? "audio/webm");
                        multipart.Add(audioContent, "file", audioFile.FileName ?? "audio.webm");
                        multipart.Add(new StringContent("openai/whisper-1"), "model");

                        var apiEndpoint = WhisperApiUrl.TrimEnd('/') + "/v1/audio/transcriptions";
                        var response = await httpClient.PostAsync(apiEndpoint, multipart);

                        string responseBody = await response.Content.ReadAsStringAsync();

                        context.Response.StatusCode = (int)response.StatusCode;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(responseBody);
                    }
                    catch (Exception ex)
                    {
                        Log($"Transcribe error: {ex.Message}");
                        context.Response.StatusCode = 500;
                        await context.Response.WriteAsync($"Transcription proxy error: {ex.Message}");
                    }
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


        private static X509Certificate2 BuildSelfSignedCert()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=OrangeRobotHub", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            try { foreach (var ip in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())) if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) san.AddIpAddress(ip); } catch { }
            req.CertificateExtensions.Add(san.Build());
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
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
        /// preview.html's WebSocket client dispatches on "type".
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
