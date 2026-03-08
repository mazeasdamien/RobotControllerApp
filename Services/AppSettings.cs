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
        
        /// <summary>The Google Gemini API Key.</summary>
        public string GeminiApiKey { get; set; } = "";

        /// <summary>The Tripo3D API Key for cloud-based 3D model generation.</summary>
        public string TripoApiKey { get; set; } = "";

        /// <summary>If true, use the Tripo3D cloud API instead of the local TripoSR server.</summary>
        public bool Use3DApiMode { get; set; } = false;

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
