using System;
using System.IO;
using System.Text.Json;
using ClipVault.Models;

namespace ClipVault.Services
{
    public class SettingsService
    {
        private static readonly string AppDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string ConfigFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyBox",
            "settings.json"
        );

        public AppSettings CurrentSettings { get; private set; }

        public event Action<string>? StorageDirectoryChanged;

        public SettingsService()
        {
            CurrentSettings = LoadSettings();
            EnsureStorageDirectoryReady();
        }

        private AppSettings LoadSettings()
        {
            try
            {
                // 便携模式检测：程序同级目录下若有 portable.flag，优先读程序目录配置
                string localConfig = Path.Combine(AppDirectory, "settings.json");
                string targetConfigFile = File.Exists(Path.Combine(AppDirectory, "portable.flag")) ? localConfig : ConfigFilePath;

                if (!File.Exists(targetConfigFile))
                {
                    string oldConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipVault", "settings.json");
                    if (File.Exists(oldConfig)) targetConfigFile = oldConfig;
                }

                if (File.Exists(targetConfigFile))
                {
                    string json = File.ReadAllText(targetConfigFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        if (string.IsNullOrWhiteSpace(settings.StorageDirectory))
                        {
                            settings.StorageDirectory = AppSettings.GetDefaultStoragePath();
                        }
                        return settings;
                    }
                }
            }
            catch (Exception)
            {
                // 读取失败则回退默认配置
            }

            return new AppSettings
            {
                StorageDirectory = AppSettings.GetDefaultStoragePath()
            };
        }

        public void SaveSettings()
        {
            try
            {
                string targetConfigFile = CurrentSettings.PortableMode 
                    ? Path.Combine(AppDirectory, "settings.json") 
                    : ConfigFilePath;

                string? dir = Path.GetDirectoryName(targetConfigFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(targetConfigFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        public void EnsureStorageDirectoryReady()
        {
            if (string.IsNullOrWhiteSpace(CurrentSettings.StorageDirectory))
            {
                CurrentSettings.StorageDirectory = AppSettings.GetDefaultStoragePath();
            }

            if (!Directory.Exists(CurrentSettings.StorageDirectory))
            {
                Directory.CreateDirectory(CurrentSettings.StorageDirectory);
            }

            string imagesDir = Path.Combine(CurrentSettings.StorageDirectory, "images");
            if (!Directory.Exists(imagesDir))
            {
                Directory.CreateDirectory(imagesDir);
            }
        }

        /// <summary>
        /// 更改本地存储根路径，并支持平滑迁移数据
        /// </summary>
        public bool UpdateStorageDirectory(string newPath, bool migrateExistingData)
        {
            if (string.IsNullOrWhiteSpace(newPath) || newPath.Equals(CurrentSettings.StorageDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(newPath))
                {
                    Directory.CreateDirectory(newPath);
                }

                string newImagesDir = Path.Combine(newPath, "images");
                if (!Directory.Exists(newImagesDir))
                {
                    Directory.CreateDirectory(newImagesDir);
                }

                string oldPath = CurrentSettings.StorageDirectory;

                if (migrateExistingData && Directory.Exists(oldPath))
                {
                    // 1. 迁移数据库文件
                    string oldDb = Path.Combine(oldPath, "clipboard.db");
                    string newDb = Path.Combine(newPath, "clipboard.db");
                    if (File.Exists(oldDb) && !File.Exists(newDb))
                    {
                        File.Copy(oldDb, newDb, true);
                    }

                    // 2. 迁移图片目录
                    string oldImages = Path.Combine(oldPath, "images");
                    if (Directory.Exists(oldImages))
                    {
                        foreach (var file in Directory.GetFiles(oldImages))
                        {
                            string destFile = Path.Combine(newImagesDir, Path.GetFileName(file));
                            if (!File.Exists(destFile))
                            {
                                File.Copy(file, destFile, true);
                            }
                        }
                    }
                }

                CurrentSettings.StorageDirectory = newPath;
                SaveSettings();
                StorageDirectoryChanged?.Invoke(newPath);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to migrate storage: {ex.Message}");
                return false;
            }
        }
    }
}
