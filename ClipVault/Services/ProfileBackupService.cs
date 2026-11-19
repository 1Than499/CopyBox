using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ClipVault.Models;

namespace ClipVault.Services
{
    public sealed class ProfileBackupPackage
    {
        public string Version { get; set; } = "3.1.5";
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
        public List<CustomTagItem> CustomTags { get; set; } = new();
        public List<ClipboardItem> PinnedAndTaggedSnippets { get; set; } = new();
        public List<CustomActionItem> CustomActions { get; set; } = new();
        public bool DeduplicateOnCopy { get; set; } = true;
        public int MaxHistoryCount { get; set; } = 1000;
        public string CustomPrivacyBlacklist { get; set; } = string.Empty;
        public string ThemeMode { get; set; } = "Dark";
        public string BackdropMaterial { get; set; } = "Default";
        public bool EnableSoundEffects { get; set; } = false;
        public bool AutoMaskSensitiveData { get; set; } = true;
    }

    /// <summary>
    /// v3.1.5 轻量配置、自定义标签分组与常用模板精选包导出/导入服务
    /// （纯本地 .copybox.json 文件，跨机迁移时无需搬运海量截图与完整历史数据库）
    /// </summary>
    public static class ProfileBackupService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static (int tagCount, int snippetCount, int actionCount) ExportPackage(
            string filePath,
            SettingsService settingsService,
            DatabaseService dbService)
        {
            var s = settingsService.CurrentSettings;
            var tags = dbService.GetCustomTags();
            var snippets = dbService.GetPinnedAndTaggedTextItems();

            var pkg = new ProfileBackupPackage
            {
                Version = "3.1.5",
                ExportedAt = DateTime.UtcNow,
                CustomTags = tags,
                PinnedAndTaggedSnippets = snippets,
                CustomActions = s.CustomActions ?? new List<CustomActionItem>(),
                DeduplicateOnCopy = s.DeduplicateOnCopy,
                MaxHistoryCount = s.MaxHistoryCount,
                CustomPrivacyBlacklist = s.CustomPrivacyBlacklist,
                ThemeMode = s.ThemeMode,
                BackdropMaterial = s.BackdropMaterial,
                EnableSoundEffects = s.EnableSoundEffects,
                AutoMaskSensitiveData = s.AutoMaskSensitiveData
            };

            string json = JsonSerializer.Serialize(pkg, JsonOptions);
            File.WriteAllText(filePath, json);
            return (tags.Count, snippets.Count, pkg.CustomActions.Count);
        }

        public static (int tagCount, int addedSnippets, int mergedActions) ImportPackage(
            string filePath,
            SettingsService settingsService,
            DatabaseService dbService)
        {
            string json = File.ReadAllText(filePath);
            var pkg = JsonSerializer.Deserialize<ProfileBackupPackage>(json, JsonOptions)
                ?? throw new InvalidDataException("Invalid CopyBox backup package format.");

            int addedSnippets = dbService.MergeBackupTagsAndItems(
                pkg.CustomTags ?? new List<CustomTagItem>(),
                pkg.PinnedAndTaggedSnippets ?? new List<ClipboardItem>());

            int mergedActions = 0;
            var cur = settingsService.CurrentSettings;
            if (pkg.CustomActions != null && pkg.CustomActions.Count > 0)
            {
                var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in cur.CustomActions)
                {
                    existingKeys.Add($"{a.Name}|{a.CommandOrPath}");
                }
                foreach (var incoming in pkg.CustomActions)
                {
                    if (string.IsNullOrWhiteSpace(incoming.Name) || string.IsNullOrWhiteSpace(incoming.CommandOrPath)) continue;
                    string key = $"{incoming.Name}|{incoming.CommandOrPath}";
                    if (existingKeys.Add(key))
                    {
                        cur.CustomActions.Add(new CustomActionItem
                        {
                            Id = string.IsNullOrWhiteSpace(incoming.Id) ? Guid.NewGuid().ToString("N") : incoming.Id,
                            Name = incoming.Name,
                            Icon = incoming.Icon ?? string.Empty,
                            CommandOrPath = incoming.CommandOrPath,
                            ActivateExistingInstance = incoming.ActivateExistingInstance,
                            Hotkey = incoming.Hotkey ?? string.Empty
                        });
                        mergedActions++;
                    }
                }
            }

            cur.DeduplicateOnCopy = pkg.DeduplicateOnCopy;
            if (pkg.MaxHistoryCount >= 0) cur.MaxHistoryCount = pkg.MaxHistoryCount;
            if (!string.IsNullOrWhiteSpace(pkg.CustomPrivacyBlacklist))
            {
                cur.CustomPrivacyBlacklist = pkg.CustomPrivacyBlacklist;
            }
            settingsService.SaveSettings();

            return (pkg.CustomTags?.Count ?? 0, addedSnippets, mergedActions);
        }
    }
}