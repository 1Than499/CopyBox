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
            // 开启 WAL 模式：高并发读写、零锁库卡顿
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to init SQLite: {ex.Message}");
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
