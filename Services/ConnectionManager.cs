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
    public class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, WebSocket> _robotClients = new();
        private readonly ConcurrentDictionary<string, WebSocket> _unityClients = new();
        private byte[]? _latestImage; // Cache
        private float[] _currentJoints = new float[6]; // Cache for Nudge commands

        public void AddRobotClient(string robotId, WebSocket ws)
        {
            _robotClients[robotId] = ws;
        }

        public void RemoveRobotClient(string robotId)
        {
            _robotClients.TryRemove(robotId, out _);
        }

        public void AddUnityClient(string robotId, WebSocket ws)
        {
            _unityClients[robotId] = ws;
        }

        public void RemoveUnityClient(string robotId)
        {
            _unityClients.TryRemove(robotId, out _);
        }

        private readonly ConcurrentDictionary<string, WebRtcManager> _webRtcManagers = new();

        public void SetWebRtcManager(string robotId, WebRtcManager webRtc)
        {
            _webRtcManagers[robotId] = webRtc;
        }

        public void RemoveWebRtcManager(string robotId)
        {
            _webRtcManagers.TryRemove(robotId, out _);
        }

        public void UpdateLatestImage(byte[] image)
        {
            _latestImage = image;
        }

        public byte[]? GetLatestImage()
        {
            return _latestImage;
        }

        public void UpdateJoints(float[] newJoints)
        {
            if (newJoints != null && newJoints.Length >= 6)
            {
                // Niryo sends 6 joints usually. Copy safely.
                Array.Copy(newJoints, _currentJoints, 6);
            }
        }

        public float[] GetCurrentJoints()
        {
            // Return clone to avoid race conditions
            return (float[])_currentJoints.Clone();
        }

        public bool IsRobotConnected(string robotId)
        {
            return _robotClients.TryGetValue(robotId, out var ws) && ws.State == WebSocketState.Open;
        }

        public async Task SendToRobotClient(string robotId, string message)
        {
            if (_robotClients.TryGetValue(robotId, out var ws) && ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public async Task SendToUnityClient(string robotId, string message)
        {
            if (_webRtcManagers.TryGetValue(robotId, out var webRtc))
            {
                webRtc.SendData(message);
            }
            else if (_unityClients.TryGetValue(robotId, out var ws) && ws.State == WebSocketState.Open)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        public object GetStatus()
        {
            return new
            {
                Timestamp = DateTime.UtcNow,
                RobotClients = _robotClients.Keys.ToList(),
                ActivePairs = _robotClients.Keys.Intersect(_unityClients.Keys).ToList()
            };
        }

        public string? GetFirstConnectedRobotId()
        {
            return _robotClients.Keys.FirstOrDefault();
        }
    }
}
