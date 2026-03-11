using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    /// <summary>
    /// Thread-safe active connection registry managing telemetry caching and concurrent WebSocket transmission paths.
    /// </summary>
    public class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, WebSocket> _robotClients = new();
        private readonly ConcurrentDictionary<string, WebSocket> _unityClients = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _sendLocks = new();
        private byte[]? _latestImage;
        private byte[]? _latestOperatorImage;
        private float[] _currentJoints = new float[6];

        private SemaphoreSlim SendLock(string key) =>
            _sendLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        /// <summary>Registers an active hardware robot bridge connection.</summary>
        public void AddRobotClient(string robotId, WebSocket ws) => _robotClients[robotId] = ws;

        /// <summary>Removes a disconnected hardware robot bridge.</summary>
        public void RemoveRobotClient(string robotId) => _robotClients.TryRemove(robotId, out _);

        /// <summary>Registers an active remote expert (Quest) interface stream.</summary>
        public void AddUnityClient(string robotId, WebSocket ws) => _unityClients[robotId] = ws;

        /// <summary>Removes a disconnected remote expert stream.</summary>
        public void RemoveUnityClient(string robotId) => _unityClients.TryRemove(robotId, out _);

        /// <summary>Updates the latest cached image frame from the primary robot camera.</summary>
        public void UpdateLatestImage(byte[] image) => _latestImage = image;

        /// <summary>Retrieves the latest cached image frame from the primary robot camera.</summary>
        public byte[]? GetLatestImage() => _latestImage;

        /// <summary>Updates the latest cached image frame from the expert's local webcam.</summary>
        public void UpdateLatestOperatorImage(byte[] image) => _latestOperatorImage = image;

        /// <summary>Retrieves the latest cached image frame from the expert's local webcam.</summary>
        public byte[]? GetLatestOperatorImage() => _latestOperatorImage;

        /// <summary>Caches the latest joint configurations for internal nudge tracking.</summary>
        public void UpdateJoints(float[] newJoints)
        {
            if (newJoints != null && newJoints.Length >= 6)
            {
                Array.Copy(newJoints, _currentJoints, 6);
            }
        }

        /// <summary>Returns a clone of the most recently received joint state payload.</summary>
        public float[] GetCurrentJoints() => (float[])_currentJoints.Clone();

        /// <summary>Checks if a specific robot client maintains an open WebSocket state.</summary>
        public bool IsRobotConnected(string robotId) =>
            _robotClients.TryGetValue(robotId, out var ws) && ws.State == WebSocketState.Open;

        /// <summary>Asynchronously dispatches a UTF-8 encoded text message to a designated robot client.</summary>
        public Task SendToRobotClient(string robotId, string message) =>
            SendThrottledMessageAsync(_robotClients, $"robot_{robotId}", robotId, message);

        /// <summary>Asynchronously dispatches a UTF-8 encoded text message to the remote expert interface.</summary>
        public Task SendToUnityClient(string robotId, string message) =>
            SendThrottledMessageAsync(_unityClients, $"unity_{robotId}", robotId, message);

        private async Task SendThrottledMessageAsync(ConcurrentDictionary<string, WebSocket> collection, string lockKey, string id, string message)
        {
            if (!collection.TryGetValue(id, out var ws) || ws.State != WebSocketState.Open) return;

            var sem = SendLock(lockKey);
            await sem.WaitAsync();
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    var bytes = Encoding.UTF8.GetBytes(message);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConnectionManager] Send error on {lockKey}: {ex.Message}");
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>Generates an anonymous status payload mapping current active paired connections.</summary>
        public object GetStatus() => new
        {
            Timestamp = DateTime.UtcNow,
            RobotClients = _robotClients.Keys.ToList(),
            ActivePairs = _robotClients.Keys.Intersect(_unityClients.Keys).ToList()
        };

        /// <summary>Returns the string identifier of the first valid robot connection dict key.</summary>
        public string? GetFirstConnectedRobotId() => _robotClients.Keys.FirstOrDefault();

        /// <summary>
        /// Broadcasts a message to ALL currently connected Unity/Quest clients simultaneously.
        /// Used for control-plane notifications like camera_robot_changed.
        /// </summary>
        public async Task BroadcastToAllUnityClients(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            var tasks = _unityClients.Values
                .Where(ws => ws.State == WebSocketState.Open)
                .Select(async ws =>
                {
                    try
                    {
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch { }
                });
            await Task.WhenAll(tasks);
        }
    }
}