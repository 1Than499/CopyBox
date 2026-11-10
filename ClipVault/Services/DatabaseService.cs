using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using ClipVault.Models;

namespace ClipVault.Services
{
    public class DatabaseService : IDisposable
    {
        private string _dbPath = string.Empty;
        private string _storageDir = string.Empty;
        private readonly object _lock = new object();

        public event Action? CustomTagsChanged;

        public DatabaseService(string storageDir)
        {
            SwitchStorageDirectory(storageDir);
        }

        public void SwitchStorageDirectory(string newStorageDir)
        {
            lock (_lock)
            {
                _storageDir = newStorageDir;
                _dbPath = ResolveDatabasePath(_storageDir);
                InitializeDatabase();
            }
        }

        private static string ResolveDatabasePath(string storageDir)
        {
            if (string.IsNullOrWhiteSpace(storageDir)) return "clipboard.db";

            string defaultDb = Path.Combine(storageDir, "clipboard.db");
            string aliasDb = Path.Combine(storageDir, "clipvault.db");

            // 若用户目录中已存在 clipvault.db 且不存在 clipboard.db，则无缝兼容 clipvault.db
            if (!File.Exists(defaultDb) && File.Exists(aliasDb))
            {
                return aliasDb;
            }
            return defaultDb;
        }

        private SqliteConnection CreateConnection()
        {
            if (!string.IsNullOrEmpty(_storageDir) && !Directory.Exists(_storageDir))
            {
                try { Directory.CreateDirectory(_storageDir); } catch { }
            }
            var conn = new SqliteConnection($"Data Source={_dbPath};");
            conn.Open();
            // 开启 WAL 模式与 3秒 busy_timeout：高并发读写、零锁库卡顿
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 3000; PRAGMA cache_size = -2000;";
            cmd.ExecuteNonQuery();
            return conn;
        }

        private void InitializeDatabase()
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ClipboardItems (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        ItemType TEXT NOT NULL,
                        Content TEXT NOT NULL,
                        Summary TEXT NOT NULL,
                        SourceApp TEXT,
                        SourceProcess TEXT,
                        CreatedAt TEXT NOT NULL,
                        IsPinned INTEGER DEFAULT 0,
                        CharCount INTEGER DEFAULT 0,
                        Sha256 TEXT,
                        CustomTag TEXT DEFAULT '',
                        OcrText TEXT DEFAULT ''
                    );

                    CREATE TABLE IF NOT EXISTS CustomTags (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        TagName TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        TagIcon TEXT DEFAULT '🏷️',
                        SortOrder INTEGER DEFAULT 0,
                        CreatedAt TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_items_created ON ClipboardItems(CreatedAt DESC);
                    CREATE INDEX IF NOT EXISTS idx_items_sha256 ON ClipboardItems(Sha256);
                    CREATE INDEX IF NOT EXISTS idx_items_pinned ON ClipboardItems(IsPinned);
                ";
                cmd.ExecuteNonQuery();

                // v3.0 幂等无损迁移：检查旧版数据库是否缺少 CustomTag 与 OcrText 列，按需自动扩展
                EnsureV3SchemaColumns(conn);

                // 自动清理存量因历史复制 bug 误产生的内部图片路径纯文本垃圾记录 (例如 images\xxxx.png)
                using var cleanCmd = conn.CreateCommand();
                cleanCmd.CommandText = "DELETE FROM ClipboardItems WHERE ItemType = 'Text' AND (Content LIKE 'images\\%.png' OR Content LIKE 'images/%.png');";
                cleanCmd.ExecuteNonQuery();

                // 若数据库为空，自动播种 1:1 概念图示例数据与缩略图
                SeedConceptDataIfNeeded(conn);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to init SQLite: {ex.Message}");
                App.LogError("SQLite Init Error", ex);
            }
        }

        private void EnsureV3SchemaColumns(SqliteConnection conn)
        {
            try
            {
                bool hasCustomTag = false;
                bool hasOcrText = false;

                using (var infoCmd = conn.CreateCommand())
                {
                    infoCmd.CommandText = "PRAGMA table_info(ClipboardItems);";
                    using var reader = infoCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string colName = reader.GetString(1);
                        if (string.Equals(colName, "CustomTag", StringComparison.OrdinalIgnoreCase)) hasCustomTag = true;
                        else if (string.Equals(colName, "OcrText", StringComparison.OrdinalIgnoreCase)) hasOcrText = true;
                    }
                }

                if (!hasCustomTag)
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE ClipboardItems ADD COLUMN CustomTag TEXT DEFAULT '';";
                    alterCmd.ExecuteNonQuery();
                }

                if (!hasOcrText)
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE ClipboardItems ADD COLUMN OcrText TEXT DEFAULT '';";
                    alterCmd.ExecuteNonQuery();
                }

                using (var idxCmd = conn.CreateCommand())
                {
                    idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_items_custom_tag ON ClipboardItems(CustomTag);";
                    idxCmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EnsureV3SchemaColumns error: {ex.Message}");
            }
        }

        private void SeedConceptDataIfNeeded(SqliteConnection conn)
        {
            try
            {
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT COUNT(*) FROM ClipboardItems;";
                var count = Convert.ToInt64(checkCmd.ExecuteScalar());
                if (count > 0) return;

                // 1. 生成概念图右上角同款精致深色桌面窗口截图缩略图
                string imgRelPath = EnsureSeedWallpaperImage();

                // 2. 依次插入概念图中的 4 条典型记录 (时间倒序，最新在最上方)
                // 序号4: 快捷键文本卡片
                InsertSeedItem(conn, "Text", 
                    "Quick-paste keyboard badge shortcuts for the same tumg.", 
                    "Quick-paste shortcuts", "Notepad", "notepad.exe", 
                    DateTime.Now.AddMinutes(-15));

                // 序号3: 引语文本卡片
                InsertSeedItem(conn, "Text", 
                    "\"I Lorem ipsum dolor sit amet, consectetur adipiscing elit. We will lits on the fostrer to poissant your sendenticomrewch and can entrue much more.\"", 
                    "Typography Quote", "Google Chrome", "chrome.exe", 
                    DateTime.Now.AddMinutes(-10));

                // 序号2: 图片卡片
                InsertSeedItem(conn, "Image", 
                    imgRelPath, 
                    "Desktop Window Preview", "Snipping Tool", "SnippingTool.exe", 
                    DateTime.Now.AddMinutes(-5));

                // 序号1 (首项): JSON 代码卡片 (概念图左上角选中项)
                string jsonCode = "{\n  \"name\": \"context\",\n  \"poie\": \"rg\",\n  \"syntats\": {\n    \"name\": \"Json\",\n    \"message\": \"javascript\"\n  }\n}";
                InsertSeedItem(conn, "Code", 
                    jsonCode, 
                    "JSON Config Schema", "VS Code", "Code.exe", 
                    DateTime.Now.AddMinutes(-1));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Seed concept data error: {ex.Message}");
            }
        }

        private void InsertSeedItem(SqliteConnection conn, string type, string content, string summary, string app, string proc, DateTime dt)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ClipboardItems (ItemType, Content, Summary, SourceApp, SourceProcess, CreatedAt, IsPinned, CharCount, Sha256)
                VALUES ($type, $content, $summary, $app, $process, $created, 0, $chars, $hash);
            ";
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$summary", summary);
            cmd.Parameters.AddWithValue("$app", app);
            cmd.Parameters.AddWithValue("$process", proc);
            cmd.Parameters.AddWithValue("$created", dt.ToString("o"));
            cmd.Parameters.AddWithValue("$chars", content.Length);
            cmd.Parameters.AddWithValue("$hash", Guid.NewGuid().ToString("N"));
            cmd.ExecuteNonQuery();
        }

        private string EnsureSeedWallpaperImage()
        {
            try
            {
                string imgDir = Path.Combine(_storageDir, "images");
                if (!Directory.Exists(imgDir)) Directory.CreateDirectory(imgDir);
                string imgPath = Path.Combine(imgDir, "seed_concept_preview.png");
                if (!File.Exists(imgPath))
                {
                    using var bmp = new System.Drawing.Bitmap(320, 180, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        using var bgBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                            new System.Drawing.Rectangle(0, 0, 320, 180),
                            System.Drawing.Color.FromArgb(22, 27, 38),
                            System.Drawing.Color.FromArgb(10, 13, 19),
                            45f);
                        g.FillRectangle(bgBrush, 0, 0, 320, 180);

                        // 绘制居中深色浮动小窗口模拟截图 (1:1 概念图右上角图片)
                        using var winBg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(235, 24, 30, 42));
                        g.FillRectangle(winBg, 45, 22, 230, 136);

                        using var winBorder = new System.Drawing.Pen(System.Drawing.Color.FromArgb(80, 120, 180), 1.5f);
                        g.DrawRectangle(winBorder, 45, 22, 230, 136);

                        // 窗口内两张深蓝卡片
                        using var innerCard = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(210, 16, 21, 29));
                        g.FillRectangle(innerCard, 58, 42, 98, 96);
                        g.FillRectangle(innerCard, 164, 42, 98, 96);

                        // 装饰微线条
                        using var linePen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(80, 140, 220), 1.2f);
                        g.DrawLine(linePen, 68, 60, 130, 60);
                        g.DrawLine(linePen, 68, 76, 145, 76);
                        g.DrawLine(linePen, 174, 60, 236, 60);
                        g.DrawLine(linePen, 174, 76, 250, 76);
                    }
                    bmp.Save(imgPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                return "images/seed_concept_preview.png";
            }
            catch
            {
                return string.Empty;
            }
        }

        public int InsertItem(ClipboardItem item)
        {
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO ClipboardItems (ItemType, Content, Summary, SourceApp, SourceProcess, CreatedAt, IsPinned, CharCount, Sha256, CustomTag, OcrText)
                        VALUES ($type, $content, $summary, $app, $process, $created, $pinned, $chars, $hash, $tag, $ocr);
                        SELECT last_insert_rowid();
                    ";
                    cmd.Parameters.AddWithValue("$type", item.ItemType);
                    cmd.Parameters.AddWithValue("$content", item.Content);
                    cmd.Parameters.AddWithValue("$summary", item.Summary);
                    cmd.Parameters.AddWithValue("$app", item.SourceApp ?? string.Empty);
                    cmd.Parameters.AddWithValue("$process", item.SourceProcess ?? string.Empty);
                    cmd.Parameters.AddWithValue("$created", item.CreatedAt.ToString("o"));
                    cmd.Parameters.AddWithValue("$pinned", item.IsPinned ? 1 : 0);
                    cmd.Parameters.AddWithValue("$chars", item.CharCount);
                    cmd.Parameters.AddWithValue("$hash", item.Sha256 ?? string.Empty);
                    cmd.Parameters.AddWithValue("$tag", item.CustomTag ?? string.Empty);
                    cmd.Parameters.AddWithValue("$ocr", item.OcrText ?? string.Empty);

                    var idObj = cmd.ExecuteScalar();
                    if (idObj != null && int.TryParse(idObj.ToString(), out int id))
                    {
                        item.Id = id;
                        return id;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to insert item: {ex.Message}");
                }
                return 0;
            }
        }

        public List<ClipboardItem> GetRecentItems(string? search = null, string? category = null, int limit = 100)
        {
            var list = new List<ClipboardItem>();
            string trimmedSearch = search?.Trim() ?? string.Empty;
            bool hasSearch = !string.IsNullOrWhiteSpace(trimmedSearch);

            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();

                    var conditions = new List<string>();
                    if (hasSearch)
                    {
                        conditions.Add("(Content LIKE $search OR Summary LIKE $search OR SourceApp LIKE $search OR CustomTag LIKE $search OR OcrText LIKE $search)");
                        cmd.Parameters.AddWithValue("$search", $"%{trimmedSearch}%");
                    }

                    if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        if (category.Equals("pinned", StringComparison.OrdinalIgnoreCase))
                        {
                            conditions.Add("IsPinned = 1");
                        }
                        else if (category.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                        {
                            string tagName = category.Substring(4).Trim();
                            conditions.Add("LOWER(CustomTag) = LOWER($customTag)");
                            cmd.Parameters.AddWithValue("$customTag", tagName);
                        }
                        else
                        {
                            conditions.Add("LOWER(ItemType) = LOWER($cat)");
                            cmd.Parameters.AddWithValue("$cat", category.Trim());
                        }
                    }

                    string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

                    cmd.CommandText = $@"
                        SELECT Id, ItemType, Content, Summary, SourceApp, SourceProcess, CreatedAt, IsPinned, CharCount, Sha256, CustomTag, OcrText
                        FROM ClipboardItems
                        {whereClause}
                        ORDER BY IsPinned DESC, CreatedAt DESC
                        LIMIT {limit};
                    ";

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var it = new ClipboardItem
                        {
                            Id = reader.GetInt32(0),
                            ItemType = reader.GetString(1),
                            Content = reader.GetString(2),
                            Summary = reader.GetString(3),
                            SourceApp = reader.IsDBNull(4) ? "" : reader.GetString(4),
                            SourceProcess = reader.IsDBNull(5) ? "" : reader.GetString(5),
                            CreatedAt = DateTime.TryParse(reader.GetString(6), out var dt) ? dt : DateTime.Now,
                            IsPinned = reader.GetInt32(7) == 1,
                            CharCount = reader.GetInt32(8),
                            Sha256 = reader.IsDBNull(9) ? "" : reader.GetString(9),
                            CustomTag = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            OcrText = reader.IsDBNull(11) ? "" : reader.GetString(11)
                        };

                        if (it.IsImage)
                        {
                            it.FullImagePath = Path.Combine(_storageDir, it.Content);
                            if (hasSearch && !string.IsNullOrWhiteSpace(it.OcrText) &&
                                it.OcrText.Contains(trimmedSearch, StringComparison.OrdinalIgnoreCase))
                            {
                                it.IsOcrKeywordMatched = true;
                            }
                        }

                        list.Add(it);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read items: {ex.Message}");
                }
            }
            return list;
        }

        public string? GetLatestItemHash()
        {
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Sha256 FROM ClipboardItems ORDER BY Id DESC LIMIT 1;";
                    var obj = cmd.ExecuteScalar();
                    return obj?.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }

        public void TogglePin(int id)
        {
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE ClipboardItems SET IsPinned = CASE WHEN IsPinned = 1 THEN 0 ELSE 1 END WHERE Id = $id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to toggle pin: {ex.Message}");
                }
            }
        }

        public void DeleteItem(int id)
        {
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM ClipboardItems WHERE Id = $id;";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to delete item: {ex.Message}");
                }
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    // v3.0 保护机制：置顶项目或已归入自定义标签分组的项目免于一键清空
                    cmd.CommandText = "DELETE FROM ClipboardItems WHERE IsPinned = 0 AND (CustomTag IS NULL OR CustomTag = '');";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to clear: {ex.Message}");
                }
            }
        }

        // ==================== v3.0 自定义标签分组与离线 OCR 数据库接口 ====================

        public List<CustomTagItem> GetCustomTags()
        {
            var tags = new List<CustomTagItem>();
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT Id, TagName, TagIcon, SortOrder, CreatedAt FROM CustomTags ORDER BY SortOrder ASC, Id ASC;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        tags.Add(new CustomTagItem
                        {
                            Id = reader.GetInt32(0),
                            TagName = reader.GetString(1),
                            TagIcon = reader.IsDBNull(2) ? "🏷️" : reader.GetString(2),
                            SortOrder = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                            CreatedAt = (!reader.IsDBNull(4) && DateTime.TryParse(reader.GetString(4), out var dt)) ? dt : DateTime.Now
                        });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to read custom tags: {ex.Message}");
                }
            }
            return tags;
        }

        public bool AddCustomTag(string tagName, string tagIcon = "🏷️")
        {
            string cleanName = tagName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanName)) return false;

            string cleanIcon = string.IsNullOrWhiteSpace(tagIcon) ? "🏷️" : tagIcon.Trim();
            bool added = false;

            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var checkCmd = conn.CreateCommand();
                    checkCmd.CommandText = "SELECT COUNT(*) FROM CustomTags WHERE LOWER(TagName) = LOWER($name);";
                    checkCmd.Parameters.AddWithValue("$name", cleanName);
                    long existing = Convert.ToInt64(checkCmd.ExecuteScalar());
                    if (existing > 0) return false;

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO CustomTags (TagName, TagIcon, SortOrder, CreatedAt)
                        VALUES ($name, $icon, (SELECT COALESCE(MAX(SortOrder), 0) + 1 FROM CustomTags), $created);
                    ";
                    cmd.Parameters.AddWithValue("$name", cleanName);
                    cmd.Parameters.AddWithValue("$icon", cleanIcon);
                    cmd.Parameters.AddWithValue("$created", DateTime.Now.ToString("o"));
                    cmd.ExecuteNonQuery();
                    added = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to add custom tag: {ex.Message}");
                }
            }

            if (added)
            {
                CustomTagsChanged?.Invoke();
            }
            return added;
        }

        public void DeleteCustomTag(string tagName)
        {
            string cleanName = tagName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanName)) return;

            bool deleted = false;
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var tx = conn.BeginTransaction();

                    using (var delCmd = conn.CreateCommand())
                    {
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM CustomTags WHERE LOWER(TagName) = LOWER($name);";
                        delCmd.Parameters.AddWithValue("$name", cleanName);
                        delCmd.ExecuteNonQuery();
                    }

                    using (var clrCmd = conn.CreateCommand())
                    {
                        clrCmd.Transaction = tx;
                        clrCmd.CommandText = "UPDATE ClipboardItems SET CustomTag = '' WHERE LOWER(CustomTag) = LOWER($name);";
                        clrCmd.Parameters.AddWithValue("$name", cleanName);
                        clrCmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                    deleted = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to delete custom tag: {ex.Message}");
                }
            }

            if (deleted)
            {
                CustomTagsChanged?.Invoke();
            }
        }

        public void SetItemCustomTag(int itemId, string tagName)
        {
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE ClipboardItems SET CustomTag = $tag WHERE Id = $id;";
                    cmd.Parameters.AddWithValue("$tag", tagName?.Trim() ?? string.Empty);
                    cmd.Parameters.AddWithValue("$id", itemId);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to set item custom tag: {ex.Message}");
                }
            }
        }

        public void UpdateItemOcrText(int itemId, string ocrText)
        {
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE ClipboardItems SET OcrText = $ocr WHERE Id = $id;";
                    cmd.Parameters.AddWithValue("$ocr", ocrText?.Trim() ?? string.Empty);
                    cmd.Parameters.AddWithValue("$id", itemId);
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to update item OCR text: {ex.Message}");
                }
            }
        }

        public (long dbSizeBytes, int totalCount, int pinnedCount, int imageCount, long imagesSizeBytes) GetStats()
        {
            lock (_lock)
            {
                long dbSize = 0;
                if (File.Exists(_dbPath))
                {
                    dbSize = new FileInfo(_dbPath).Length;
                }

                int totalCount = 0;
                int pinnedCount = 0;

                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*), SUM(CASE WHEN IsPinned = 1 THEN 1 ELSE 0 END) FROM ClipboardItems;";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        totalCount = reader.GetInt32(0);
                        pinnedCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    }
                }
                catch { }

                int imgCount = 0;
                long imgSize = 0;
                string imagesDir = Path.Combine(_storageDir, "images");
                if (Directory.Exists(imagesDir))
                {
                    var files = Directory.GetFiles(imagesDir);
                    imgCount = files.Length;
                    foreach (var f in files)
                    {
                        imgSize += new FileInfo(f).Length;
                    }
                }

                return (dbSize, totalCount, pinnedCount, imgCount, imgSize);
            }
        }

        public void Dispose()
        {
        }
    }
}
