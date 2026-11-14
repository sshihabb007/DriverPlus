using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DriverPlus.Services
{
    public sealed class DriverPlusSettings
    {
        public bool WhqlOnly { get; set; }
        public bool CreateRestorePointBeforeInstall { get; set; }
        public bool AutoBackupDriverZip { get; set; }
        public bool QueryLiveMicrosoftCatalog { get; set; }
        public bool SilentFocusGamingGuard { get; set; }
        public List<string> IgnoredInstanceIds { get; set; }
        public List<string> UpdateHistory { get; set; }

        public DriverPlusSettings()
        {
            WhqlOnly = true;
            CreateRestorePointBeforeInstall = true;
            AutoBackupDriverZip = true;
            QueryLiveMicrosoftCatalog = true;
            SilentFocusGamingGuard = true;
            IgnoredInstanceIds = new List<string>();
            UpdateHistory = new List<string>();
        }
    }

    public static class SettingsService
    {
        private static readonly object SyncRoot = new object();

        public static string GetAppDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DriverPlus");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static string GetSettingsFilePath()
        {
            return Path.Combine(GetAppDataDir(), "settings.json");
        }

        public static DriverPlusSettings Load()
        {
            lock (SyncRoot)
            {
                try
                {
                    string path = GetSettingsFilePath();
                    if (File.Exists(path))
                    {
                        var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                        DriverPlusSettings loaded = serializer.Deserialize<DriverPlusSettings>(File.ReadAllText(path, Encoding.UTF8));
                        if (loaded != null)
                        {
                            if (loaded.IgnoredInstanceIds == null) loaded.IgnoredInstanceIds = new List<string>();
                            if (loaded.UpdateHistory == null) loaded.UpdateHistory = new List<string>();
                            return loaded;
                        }
                    }
                }
                catch
                {
                }
                return new DriverPlusSettings();
            }
        }

        public static void Save(DriverPlusSettings settings)
        {
            if (settings == null) return;
            lock (SyncRoot)
            {
                try
                {
                    var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                    File.WriteAllText(GetSettingsFilePath(), serializer.Serialize(settings), Encoding.UTF8);
                }
                catch
                {
                }
            }
        }

        public static void AppendHistoryEntry(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            DriverPlusSettings s = Load();
            s.UpdateHistory.Insert(0, line);
            if (s.UpdateHistory.Count > 200)
            {
                s.UpdateHistory.RemoveRange(200, s.UpdateHistory.Count - 200);
            }
            Save(s);
        }
    }
}
