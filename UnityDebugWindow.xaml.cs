using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;

namespace RobotControllerApp
{
    public class MessageState : INotifyPropertyChanged
    {
        private string _type = "";
        private string _lastUpdated = "";
        private int _count = 0;
        private string _formattedPayload = "";
        private string _direction = "";
        private string _directionColor = "Gray";

        public string Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(nameof(Type)); }
        }

        public string LastUpdated
        {
            get => _lastUpdated;
            set { _lastUpdated = value; OnPropertyChanged(nameof(LastUpdated)); }
        }

        public int Count
        {
            get => _count;
            set { _count = value; OnPropertyChanged(nameof(Count)); }
        }

        public string FormattedPayload
        {
            get => _formattedPayload;
            set { _formattedPayload = value; OnPropertyChanged(nameof(FormattedPayload)); }
        }

        public string Direction
        {
            get => _direction;
            set { _direction = value; OnPropertyChanged(nameof(Direction)); }
        }

        public string DirectionColor
        {
            get => _directionColor;
            set { _directionColor = value; OnPropertyChanged(nameof(DirectionColor)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed partial class UnityDebugWindow : Window
    {
        private ObservableCollection<MessageState> _messageStates = new();

        public UnityDebugWindow()
        {
            this.InitializeComponent();

            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(TitleBarDragArea);

            MessageTypesList.ItemsSource = _messageStates;

            Services.Scene3dBroadcastServer.OnMessageBroadcast += Scene3dBroadcastServer_OnMessageBroadcast;
            this.Closed += UnityDebugWindow_Closed;
        }

        private void UnityDebugWindow_Closed(object sender, WindowEventArgs args)
        {
            Services.Scene3dBroadcastServer.OnMessageBroadcast -= Scene3dBroadcastServer_OnMessageBroadcast;
        }

        private void Scene3dBroadcastServer_OnMessageBroadcast(string type, string payload, string direction)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                string displayType = type;
                
                // Visually differentiate messages per robot id or ROS topic
                if (type == "setRobotJoints" || type == "robot_state" || type == "publish" || type == "setCameraPose")
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(payload);
                        
                        // For 'publish' operations sent by rosbridge, topic is the true identifier
                        if (type == "publish" && doc.RootElement.TryGetProperty("topic", out var tProp))
                        {
                            string t = tProp.GetString() ?? "";
                            // Clean up verbose topic names for cleaner UI
                            if (t.Contains('/')) t = t.Substring(t.LastIndexOf('/') + 1);
                            displayType = $"{type}_{t}";
                        }
                        else
                        {
                            // Check standard root properties for IDs
                            if (doc.RootElement.TryGetProperty("robotIdx", out var rIdx))
                                displayType = $"{type}_{rIdx}";
                            else if (doc.RootElement.TryGetProperty("id", out var idProp))
                                displayType = $"{type}_{idProp}";
                            else if (doc.RootElement.TryGetProperty("robotId", out var ridProp))
                                displayType = $"{type}_{ridProp}";
                                
                            // Check inside payload object (if nested)
                            else if (doc.RootElement.TryGetProperty("payload", out var p))
                            {
                                if (p.TryGetProperty("robotIdx", out var pIdx))
                                    displayType = $"{type}_{pIdx}";
                                else if (p.TryGetProperty("id", out var pId))
                                    displayType = $"{type}_{pId}";
                                else if (p.TryGetProperty("robotId", out var pRid))
                                    displayType = $"{type}_{pRid}";
                            }
                        }
                    }
                    catch { }
                }

                var state = _messageStates.FirstOrDefault(m => m.Type == displayType);
                if (state == null)
                {
                    state = new MessageState { Type = displayType };
                    _messageStates.Add(state);
                }

                state.Count++;
                state.LastUpdated = DateTime.Now.ToString("HH:mm:ss.fff");
                state.Direction = direction;
                state.DirectionColor = direction == "ToUnity" ? "#4CAF50" : "#FF9800"; // Green for ToUnity, Orange for FromUnity

                // Truncate Base64 images to prevent UI freezes
                string cleanPayload = payload;
                if (type.Contains("Feed", StringComparison.OrdinalIgnoreCase) && cleanPayload.Length > 200)
                {
                    int snippetEnd = Math.Min(200, cleanPayload.Length);
                    cleanPayload = cleanPayload.Substring(0, snippetEnd) + "\"... [TRUNCATED HUGE BASE64 IMAGE DATA]\"";
                }
                
                // Truncate compressed_video_stream
                if (cleanPayload.Contains("compressed_video_stream") && cleanPayload.Length > 300)
                {
                     int pIdx = cleanPayload.IndexOf("\"data\":");
                     if (pIdx > 0) 
                     {
                         cleanPayload = cleanPayload.Substring(0, pIdx + 7) + " \"[TRUNCATED RAW JPEG BYTES]\" }}}}";
                     }
                }

                // Format JSON nicely if possible
                try
                {
                    using var doc = JsonDocument.Parse(cleanPayload);
                    state.FormattedPayload = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
                catch
                {
                    state.FormattedPayload = cleanPayload; // Fallback to raw if not valid JSON
                }

                // If this is the currently selected item, update the detail view live
                if (MessageTypesList.SelectedItem == state)
                {
                    PayloadDetailText.Text = state.FormattedPayload;
                }
            });
        }

        private void MessageTypesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MessageTypesList.SelectedItem is MessageState state)
            {
                PayloadDetailText.Text = state.FormattedPayload;
            }
            else
            {
                PayloadDetailText.Text = "";
            }
        }
    }
}
