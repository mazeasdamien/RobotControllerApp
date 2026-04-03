using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RobotHub.Services
{
    /// <summary>
    /// Manages the Scene3D WebSocket channel to Unity and the GLB asset library endpoint.
    ///
    /// This class no longer creates its own Kestrel instance — routes are registered
    /// into the host's shared WebApplication via MapRoutes().
    /// </summary>
    public class UnityPushServer
    {
        // ── Logging ────────────────────────────────────────────────────────────
        public static event Action<string>? OnLog;
        private static void Log(string msg) => OnLog?.Invoke($"[Scene3D] {msg}");

        // ── Events ─────────────────────────────────────────────────────────────
        public event Func<Task>? OnClientConnected;

        // Static singleton — gives workers access to BroadcastAsync without DI
        public static UnityPushServer? Instance { get; private set; }

        /// <summary>Path to GLB/image files served under /library/. Leave empty to disable.</summary>
        public string LibraryPath { get; set; } = string.Empty;

        // ── Thread-Safe WebSocket Client Wrapper ──────────────────────────────
        // Uses a "latest-wins" slot for high-frequency streams (camera, joints, pose)
        // so slow Wi-Fi clients never replay a growing backlog of stale frames.
        private class ConnectedClient : IAsyncDisposable
        {
            public string Id { get; }
            private readonly WebSocket _socket;

            private readonly System.Threading.Channels.Channel<byte[]> _reliableChannel =
                System.Threading.Channels.Channel.CreateUnbounded<byte[]>(
                    new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

            private readonly ConcurrentDictionary<string, byte[]> _latestSlot = new();
            private volatile int _latestPending;

            private static readonly HashSet<string> _droppableTypes = new()
            {
                "updateCameraFeed", "updateCameraFeed2", "updateArFeed", "updateArFeed2",
                "setCameraPose", "setRobotJoints", "setCameraRobot", "pong"
            };

            private readonly CancellationTokenSource _cts = new();
            private readonly SemaphoreSlim _socketLock = new(1, 1);

            public ConnectedClient(string id, WebSocket socket)
            {
                Id = id;
                _socket = socket;
                _ = Task.Run(ReliableSendLoopAsync);
            }

            public void Enqueue(string type, byte[] bytes)
            {
                if (_socket.State != WebSocketState.Open) return;
                if (_droppableTypes.Contains(type))
                {
                    _latestSlot[type] = bytes;
                    if (Interlocked.Exchange(ref _latestPending, 1) == 0)
                        _ = Task.Run(FlushDroppableAsync);
                }
                else
                {
                    _reliableChannel.Writer.TryWrite(bytes);
                }
            }

            public Task SendAsync(ArraySegment<byte> segment)
            {
                var bytes = new byte[segment.Count];
                Buffer.BlockCopy(segment.Array!, segment.Offset, bytes, 0, segment.Count);
                _reliableChannel.Writer.TryWrite(bytes);
                return Task.CompletedTask;
            }

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
                foreach (var key in _latestSlot.Keys.ToArray())
                {
                    if (_latestSlot.TryRemove(key, out var bytes))
                        await SendBytesDirectAsync(bytes);
                }
                Interlocked.Exchange(ref _latestPending, 0);
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

        /// <summary>
        /// Registers all Scene3D routes into the shared host WebApplication.
        /// Must be called before app.Run().
        /// </summary>
        public void MapRoutes(WebApplication app)
        {
            Instance = this;

            if (!string.IsNullOrWhiteSpace(LibraryPath))
                Directory.CreateDirectory(LibraryPath);

            // ── GLB library endpoint ──────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(LibraryPath))
            {
                app.MapGet("/library/{**filename}", async context =>
                {
                    var filename = context.Request.RouteValues["filename"]?.ToString() ?? "";
                    filename = Uri.UnescapeDataString(filename);

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
                    context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                    context.Response.Headers["Cache-Control"] = "public, max-age=3600";

                    try { await context.Response.SendFileAsync(fullPath, context.RequestAborted); }
                    catch (OperationCanceledException) { }
                });
            }

            // ── WebSocket endpoint: Unity Scene3D client ──────────────────────
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
                Log($"Unity connected — id={clientId} ip={context.Connection.RemoteIpAddress} total={_clients.Count}");

                if (OnClientConnected != null)
                    _ = Task.Run(async () => { try { await OnClientConnected.Invoke(); } catch { } });

                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                using var messageBuffer = new MemoryStream();

                try
                {
                    while (ws.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
                    {
                        WebSocketReceiveResult result;
                        messageBuffer.SetLength(0);
                        do
                        {
                            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                            if (result.MessageType == WebSocketMessageType.Close) break;
                            if (result.Count > 0) messageBuffer.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (result.MessageType == WebSocketMessageType.Close) break;

                        if (result.MessageType == WebSocketMessageType.Text &&
                            messageBuffer.TryGetBuffer(out ArraySegment<byte> segment))
                        {
                            var msg = Encoding.UTF8.GetString(segment.Array!, segment.Offset, segment.Count);

                            // Fast-path: ping → pong (saves 1-3 ms of dispatch overhead)
                            if (msg.Contains("\"ping\"", StringComparison.Ordinal))
                            {
                                try
                                {
                                    using var pingDoc = JsonDocument.Parse(msg);
                                    var root = pingDoc.RootElement;
                                    if (root.TryGetProperty("type", out var t) && t.GetString() == "ping" &&
                                        root.TryGetProperty("payload", out var pl))
                                    {
                                        string tsRaw = pl.ValueKind == JsonValueKind.Number ? pl.GetRawText() : "0";
                                        client.Enqueue("pong", Encoding.UTF8.GetBytes($"{{\"type\":\"pong\",\"payload\":{tsRaw}}}"));
                                        continue;
                                    }
                                }
                                catch { }
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
                    Log($"Unity disconnected — id={clientId} remaining={_clients.Count}");
                }
            });

            Log("Scene3D routes registered on shared host.");
        }

        /// <summary>
        /// Disposes all active client connections. Called by Scene3dWorker on shutdown.
        /// </summary>
        public async Task StopAsync()
        {
            var tasks = _clients.Values.Select(c => c.DisposeAsync().AsTask());
            await Task.WhenAll(tasks);
            _clients.Clear();
        }

        /// <summary>
        /// Broadcasts a typed message to all connected Unity clients.
        /// Droppable types (camera, pose, joints) use the latest-wins slot.
        /// All others are delivered reliably in order.
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

        public int ConnectedClients => _clients.Count;
    }
}
