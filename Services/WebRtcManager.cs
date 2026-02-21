using SIPSorcery.Net;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RobotControllerApp.Services
{
    public class WebRtcManager
    {
        private RTCPeerConnection? _pc;
        private RTCDataChannel? _dataChannel;

        public event Action<string>? OnLocalIceCandidate;
        public event Action<string>? OnLocalOfferReady;
        public event Action<string>? OnMessageReceived;
        public event Action<bool>? OnConnectionStateChanged;

        public async Task InitializeAndOffer()
        {
            RTCConfiguration config = new RTCConfiguration
            {
                iceServers = new System.Collections.Generic.List<RTCIceServer> { new RTCIceServer { urls = "stun:stun.l.google.com:19302" } }
            };

            _pc = new RTCPeerConnection(config);

            _pc.onicecandidate += (candidate) =>
            {
                OnLocalIceCandidate?.Invoke(candidate.toJSON());
            };

            _pc.onconnectionstatechange += (state) =>
            {
                OnConnectionStateChanged?.Invoke(state == RTCPeerConnectionState.connected);
                Console.WriteLine($"[WebRTC] PC State is now {state}");
            };

            _dataChannel = await _pc.createDataChannel("robot_data_channel");
            _dataChannel.onmessage += (dc, proto, data) =>
            {
                string msg = Encoding.UTF8.GetString(data);
                OnMessageReceived?.Invoke(msg);
            };

            _dataChannel.onopen += () =>
            {
                Console.WriteLine("[WebRTC] DataChannel Open!");
            };

            var offer = _pc.createOffer(null);
            await _pc.setLocalDescription(offer);

            OnLocalOfferReady?.Invoke(offer.sdp);
        }

        public void SetRemoteAnswer(string sdp)
        {
            var init = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp };
            _pc?.setRemoteDescription(init);
        }

        public void AddIceCandidate(string jsonCandidate)
        {
            try
            {
                if (RTCIceCandidateInit.TryParse(jsonCandidate, out var init))
                {
                    _pc?.addIceCandidate(init);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] ICE candidate error: {ex.Message}");
            }
        }

        public void SendData(string msg)
        {
            if (_dataChannel != null && _dataChannel.readyState == RTCDataChannelState.open)
            {
                _dataChannel.send(msg);
            }
        }

        public void Close()
        {
            _dataChannel?.close();
            _pc?.close();
        }
    }
}
