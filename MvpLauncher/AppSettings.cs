using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MvpLauncher
{
    public class Profile
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "Offline";
        public string Uuid { get; set; } = "";
    }

    public class AppSettings
    {
        public List<Profile> Profiles { get; set; } = new();
        public string SelectedProfile { get; set; } = "";
        public int RamMb { get; set; } = 4096;
        public string Theme { get; set; } = "System";
        public string LauncherBehavior { get; set; } = "Hide";
        public string Language { get; set; } = "hu";
        public string JavaPreset { get; set; } = "Default";
        public string JavaArgs { get; set; } = "";

        public static string SettingsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".mvplauncher",
                "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null)
                    {
                        if (s.Profiles == null || s.Profiles.Count == 0)
                        {
                            s.Profiles = new List<Profile> { new Profile { Name = "Player" } };
                            s.SelectedProfile = "Player";
                        }
                        if (string.IsNullOrEmpty(s.Language)) s.Language = "hu";
                        if (string.IsNullOrEmpty(s.JavaPreset)) s.JavaPreset = "Default";
                        return s;
                    }
                }
            }
            catch { }

            return new AppSettings
            {
                Profiles = new List<Profile> { new Profile { Name = "Player" } },
                SelectedProfile = "Player",
                RamMb = 4096,
                Theme = "System",
                LauncherBehavior = "Hide",
                Language = "hu",
                JavaPreset = "Default",
                JavaArgs = ""
            };
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}