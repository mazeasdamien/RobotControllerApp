using System;
using System.IO;
using System.Text.Json;

namespace RobotHub.Services
{
    /// <summary>
    /// Manages persistent hub configuration. Only fields actively used by
    /// the Worker Service are kept — all Unity-side and AI API settings removed.
    /// </summary>
    public class AppSettings
    {
        /// <summary>Port on which the WebSocket relay server (RelayServerHost) listens.</summary>
        public int RelayPort { get; set; } = 5000;

        /// <summary>Public Cloudflare tunnel URL shown in logs and dashboard.</summary>
        public string PublicUrl { get; set; } = "https://niryo.dmzs-lab.com";

        /// <summary>IP address of the primary Niryo robot (R1).</summary>
        public string RobotIp { get; set; } = "169.254.200.200";

        /// <summary>IP address of the secondary Niryo robot (R2).</summary>
        public string Robot2Ip { get; set; } = "169.254.200.201";

        /// <summary>Which robot carries the camera (1 = R1, 2 = R2). Controls camera topic subscription.</summary>
        public int CameraRobot { get; set; } = 1;

        /// <summary>Path where GLB model files are stored and served via /library/.</summary>
        public string LibraryPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobotOrange", "Library");

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobotOrange", "settings.json");

        /// <summary>Loads settings from local app data. Returns defaults if file does not exist.</summary>
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

        /// <summary>Serializes and saves current configuration to persistent storage.</summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                var dir = Path.GetDirectoryName(SettingsPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
