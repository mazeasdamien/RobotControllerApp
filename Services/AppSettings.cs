using System;
using System.IO;
using System.Text.Json;

namespace RobotControllerApp.Services
{
    /// <summary>
    /// Manages persistent application configuration parameters for network connectivity.
    /// </summary>
    public class AppSettings
    {
        /// <summary>The port on which the local WebSocket relay server listens.</summary>
        public int RelayPort { get; set; } = 5000;

        /// <summary>The designated public tunnel URL for remote expert connection.</summary>
        public string PublicUrl { get; set; } = "https://niryo.dmzs-lab.com";

        /// <summary>The IP address of the primary Niryo robot.</summary>
        public string RobotIp { get; set; } = "169.254.200.200";

        /// <summary>The IP address of the secondary Niryo robot.</summary>
        public string Robot2Ip { get; set; } = "169.254.200.201";

        /// <summary>The IP address of the remote expert client.</summary>
        public string ExpertIp { get; set; } = "";

        /// <summary>The Orange API Key.</summary>
        public string OrangeApiKey { get; set; } = "";

        /// <summary>The Orange / Whisper API base URL.</summary>
        public string OrangeApiUrl { get; set; } = "https://llmproxy.ai.orange";

        /// <summary>The Google Gemini API Key.</summary>
        public string GeminiApiKey { get; set; } = "";

        /// <summary>The Banana image generation model.</summary>
        public string BananaModel { get; set; } = "gemini-2.5-flash-image";

        /// <summary>Hugging Face API token — used to authenticate against private/personal HF Spaces (e.g. TRELLIS).</summary>
        public string HfToken { get; set; } = "";

        /// <summary>Base URL of the personal TRELLIS HF Space.</summary>
        public string TrellisSpaceUrl { get; set; } = "https://mazeasdamien-trellis-2.hf.space";

        /// <summary>If true, use cloud 3D generation (TRELLIS) instead of local inference.</summary>
        public bool Use3DApiMode { get; set; } = false;

        /// <summary>Scale of the extracted object (0.0 to 1.0) in the generation prompt.</summary>
        public double BananaFramingScale { get; set; } = 0.6;

        /// <summary>Custom prompt string for extracting object.</summary>
        public string BananaPromptTemplate { get; set; } = "";


        /// <summary>The scale factor of the 3D Projection.</summary>
        public double CameraFovScale { get; set; } = 0.40;

        /// <summary>
        /// The opacity applied to the glowing AR Holograms in the telepresence Hub view.
        /// </summary>
        public double ArOpacity { get; set; } = 0.15;

        /// <summary>
        /// Which physical robot carries the camera (1 = Robot 1, 2 = Robot 2).
        /// The hub subscribes to the camera topic only on this robot's bridge.
        /// </summary>
        public int CameraRobot { get; set; } = 1;


        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobotOrange", "settings.json");

        /// <summary>
        /// Loads settings from local application data. Returns defaults if file does not exist.
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        /// <summary>
        /// Serializes and saves current configuration state to persistent storage.
        /// </summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this);
                var dir = Path.GetDirectoryName(SettingsPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
