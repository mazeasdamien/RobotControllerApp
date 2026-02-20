using System;
using System.Text.Json;
using Windows.Storage;

namespace RobotControllerApp.Services
{
    public class AppSettings
    {
        public int RelayPort { get; set; } = 5000;
        public string PublicUrl { get; set; } = "https://teleop.dmzs-lab.com";
        public string RobotIp { get; set; } = "169.254.200.200";
        public string Robot2Ip { get; set; } = "169.254.200.201";


        private static string SettingsPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RobotOrange", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (System.IO.File.Exists(SettingsPath))
                {
                    var json = System.IO.File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(this);
                var dir = System.IO.Path.GetDirectoryName(SettingsPath);
                if (dir != null) System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
