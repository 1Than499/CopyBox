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

        public DatabaseService(string storageDir)
        {
            SwitchStorageDirectory(storageDir);
        }

        public void SwitchStorageDirectory(string newStorageDir)
        {
            lock (_lock)
            {
                _storageDir = newStorageDir;
                _dbPath = Path.Combine(_storageDir, "clipboard.db");
                InitializeDatabase();
            }
        }

        private SqliteConnection CreateConnection()
        {
            var conn = new SqliteConnection($"Data Source={_dbPath};");
            conn.Open();
            // 开启 WAL 模式与 3秒 busy_timeout：高并发读写、零锁库卡顿
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 3000;";
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
                        Sha256 TEXT
                    );

                    CREATE INDEX IF NOT EXISTS idx_items_created ON ClipboardItems(CreatedAt DESC);
                    CREATE INDEX IF NOT EXISTS idx_items_sha256 ON ClipboardItems(Sha256);
                    CREATE INDEX IF NOT EXISTS idx_items_pinned ON ClipboardItems(IsPinned);
                ";
                cmd.ExecuteNonQuery();

                // 若数据库为空，自动播种 1:1 概念图示例数据与缩略图
                SeedConceptDataIfNeeded(conn);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to init SQLite: {ex.Message}");
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
                        INSERT INTO ClipboardItems (ItemType, Content, Summary, SourceApp, SourceProcess, CreatedAt, IsPinned, CharCount, Sha256)
                        VALUES ($type, $content, $summary, $app, $process, $created, $pinned, $chars, $hash);
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
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    using var cmd = conn.CreateCommand();

                    var conditions = new List<string>();
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        conditions.Add("(Content LIKE $search OR Summary LIKE $search OR SourceApp LIKE $search)");
                        cmd.Parameters.AddWithValue("$search", $"%{search}%");
                    }

                    if (!string.IsNullOrWhiteSpace(category) && category != "all")
                    {
                        if (category == "pinned")
                        {
                            conditions.Add("IsPinned = 1");
                        }
                        else
                        {
                            conditions.Add("ItemType = $cat");
                            cmd.Parameters.AddWithValue("$cat", category);
                        }
                    }

                    string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

                    cmd.CommandText = $@"
                        SELECT Id, ItemType, Content, Summary, SourceApp, SourceProcess, CreatedAt, IsPinned, CharCount, Sha256
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
                            Sha256 = reader.IsDBNull(9) ? "" : reader.GetString(9)
                        };

                        if (it.IsImage)
                        {
                            it.FullImagePath = Path.Combine(_storageDir, it.Content);
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
                    cmd.CommandText = "DELETE FROM ClipboardItems WHERE IsPinned = 0;";
                    cmd.ExecuteNonQuery();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to clear: {ex.Message}");
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
