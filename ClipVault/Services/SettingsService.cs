using System;
using System.IO;
using System.Text.Json;
using ClipVault.Models;
using Microsoft.Data.Sqlite;

namespace ClipVault.Services
{
    public class SettingsService
    {
        private static string AppRootDirectory => AppSettings.GetAppRootDirectory();
        private static string PortableConfigFilePath => Path.Combine(AppRootDirectory, "settings.json");
        private static readonly string LegacyAppDataConfigFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyBox",
            "settings.json"
        );
        private static readonly string LegacyDefaultDataDir1 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CopyBox",
            "Data"
        );
        private static readonly string LegacyDefaultDataDir2 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipVault",
            "Data"
        );

        public AppSettings CurrentSettings { get; private set; }

        public event Action<string>? StorageDirectoryChanged;
        public event Action? SettingsChanged;

        public SettingsService()
        {
            CurrentSettings = LoadSettings();
            EnsureStorageDirectoryReady();
        }

        private AppSettings LoadSettings()
        {
            try
            {
                string localConfig = PortableConfigFilePath;
                string targetConfigFile = localConfig;
                bool loadedFromLegacyAppData = false;

                if (!File.Exists(targetConfigFile))
                {
                    if (File.Exists(LegacyAppDataConfigFilePath))
                    {
                        targetConfigFile = LegacyAppDataConfigFilePath;
                        loadedFromLegacyAppData = true;
                    }
                    else
                    {
                        string oldClipVaultConfig = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "ClipVault",
                            "settings.json"
                        );
                        if (File.Exists(oldClipVaultConfig))
                        {
                            targetConfigFile = oldClipVaultConfig;
                            loadedFromLegacyAppData = true;
                        }
                    }
                }

                if (File.Exists(targetConfigFile))
                {
                    string json = File.ReadAllText(targetConfigFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        settings.PortableMode = true;
                        string defaultRootPath = AppSettings.GetDefaultStoragePath();

                        // 如果存储目录为空，或者是从旧版 AppData 默认路径升级而来，自动将默认存储目录对齐到程序根目录并平滑迁移旧数据
                        bool isLegacyDefaultPath = !string.IsNullOrWhiteSpace(settings.StorageDirectory) &&
                            (settings.StorageDirectory.TrimEnd('\\', '/').Equals(LegacyDefaultDataDir1.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                             settings.StorageDirectory.TrimEnd('\\', '/').Equals(LegacyDefaultDataDir2.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));

                        if (string.IsNullOrWhiteSpace(settings.StorageDirectory))
                        {
                            settings.StorageDirectory = defaultRootPath;
                        }
                        else if (loadedFromLegacyAppData && isLegacyDefaultPath)
                        {
                            string oldDir = settings.StorageDirectory;
                            settings.StorageDirectory = defaultRootPath;
                            TryMigrateDirectoryContents(oldDir, defaultRootPath);
                        }

                        // 确保配置文件同步写入程序根目录
                        if (loadedFromLegacyAppData)
                        {
                            try
                            {
                                string migratedJson = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                                File.WriteAllText(localConfig, migratedJson);
                            }
                            catch { }
                        }

                        return settings;
                    }
                }
            }
            catch (Exception)
            {
                // 读取失败则回退默认配置
            }

            var defaultSettings = new AppSettings
            {
                PortableMode = true,
                StorageDirectory = AppSettings.GetDefaultStoragePath()
            };

            // 若旧版默认目录存在历史数据，首次启动自动迁移至程序根目录
            if (Directory.Exists(LegacyDefaultDataDir1))
            {
                TryMigrateDirectoryContents(LegacyDefaultDataDir1, defaultSettings.StorageDirectory);
            }

            try
            {
                string initJson = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(PortableConfigFilePath, initJson);
            }
            catch { }

            return defaultSettings;
        }

        private static void TryMigrateDirectoryContents(string oldPath, string newPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return;
                if (!Directory.Exists(oldPath)) return;
                if (oldPath.TrimEnd('\\', '/').Equals(newPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;

                Directory.CreateDirectory(newPath);
                SqliteConnection.ClearAllPools();

                string oldDb = Path.Combine(oldPath, "clipboard.db");
                string newDb = Path.Combine(newPath, "clipboard.db");
                string targetAliasDb = Path.Combine(newPath, "clipvault.db");
                if (File.Exists(oldDb) && !File.Exists(newDb) && !File.Exists(targetAliasDb))
                {
                    File.Copy(oldDb, newDb, true);
                }

                string oldDb2 = Path.Combine(oldPath, "clipvault.db");
                if (File.Exists(oldDb2) && !File.Exists(newDb) && !File.Exists(Path.Combine(newPath, "clipvault.db")))
                {
                    File.Copy(oldDb2, Path.Combine(newPath, "clipvault.db"), true);
                }

                string oldImages = Path.Combine(oldPath, "images");
                string newImages = Path.Combine(newPath, "images");
                if (Directory.Exists(oldImages))
                {
                    Directory.CreateDirectory(newImages);
                    foreach (var file in Directory.GetFiles(oldImages))
                    {
                        string destFile = Path.Combine(newImages, Path.GetFileName(file));
                        if (!File.Exists(destFile))
                        {
                            File.Copy(file, destFile, true);
                        }
                    }
                }
            }
            catch { }
        }

        public void SaveSettings()
        {
            try
            {
                string targetConfigFile = PortableConfigFilePath;
                try
                {
                    string? dir = Path.GetDirectoryName(targetConfigFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(targetConfigFile, json);
                }
                catch
                {
                    // 若程序根目录无写入权限则回退至 LocalApplicationData
                    string? fallbackDir = Path.GetDirectoryName(LegacyAppDataConfigFilePath);
                    if (!string.IsNullOrEmpty(fallbackDir) && !Directory.Exists(fallbackDir))
                    {
                        Directory.CreateDirectory(fallbackDir);
                    }
                    string json = JsonSerializer.Serialize(CurrentSettings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(LegacyAppDataConfigFilePath, json);
                }

                SettingsChanged?.Invoke();
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

            try
            {
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
            catch { }
        }

        /// <summary>
        /// 更改本地存储根路径，并支持平滑迁移数据与通知双端同步
        /// </summary>
        public bool UpdateStorageDirectory(string newPath, bool migrateExistingData)
        {
            if (string.IsNullOrWhiteSpace(newPath))
            {
                return false;
            }

            string normalizedNew = newPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedOld = (CurrentSettings.StorageDirectory ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (normalizedNew.Equals(normalizedOld, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(normalizedNew))
                {
                    Directory.CreateDirectory(normalizedNew);
                }

                string newImagesDir = Path.Combine(normalizedNew, "images");
                if (!Directory.Exists(newImagesDir))
                {
                    Directory.CreateDirectory(newImagesDir);
                }

                if (migrateExistingData && !string.IsNullOrEmpty(normalizedOld) && Directory.Exists(normalizedOld))
                {
                    TryMigrateDirectoryContents(normalizedOld, normalizedNew);
                }

                CurrentSettings.StorageDirectory = normalizedNew;
                SaveSettings();
                StorageDirectoryChanged?.Invoke(normalizedNew);
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
