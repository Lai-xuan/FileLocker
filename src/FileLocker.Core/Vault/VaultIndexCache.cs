using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FileLocker.Core.Models;
using Microsoft.Data.Sqlite;

namespace FileLocker.Core.Vault;

/// <summary>
/// 對應規格文件第 4 節「本機唯讀快取索引」：包住一份本機專屬的 SQLite 資料庫，
/// 讓清單頁不用每次刷新都對 Vault 資料夾做一次 VaultManager.ScanAll() 全量重掃。
///
/// 這一層刻意不取代 VaultManager——.meta.json 永遠是唯一真實來源，這裡的資料庫純粹是
/// 「讀起來比較快」的加速層，隨時可以整個刪掉、從 VaultManager.ScanAll() 重建，不影響正確性。
/// 也因此絕對不能放在 Vault 資料夾內（會被雲端同步用戶端誤傳到其他裝置，多裝置各自寫入
/// 同一個 SQLite 檔案容易造成同步衝突甚至資料庫損毀），呼叫端必須傳一個 Vault 之外、
/// 本機專屬的目錄（例如 %LOCALAPPDATA%\FileLocker\VaultIndexCache）。
/// </summary>
public sealed class VaultIndexCache : IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private readonly VaultManager _vaultManager;
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;

    // VaultChangeWatcher 的 debounce 是「每個檔案各自一個 Timer」，好幾個檔案同時安靜下來時，
    // 各自的 Timer 回呼會在執行緒集區的不同執行緒上並行觸發，全部共用同一個 SqliteConnection——
    // Microsoft.Data.Sqlite 的 SqliteConnection 不是執行緒安全的，沒有這道鎖時，兩個執行緒同時
    // 建立/釋放 SqliteCommand 會弄亂連線內部的命令清單，實測會直接讓行程當掉（不是丟例外，是
    // crash）。這裡用一個簡單的鎖序列化所有存取，用量規模（本機、單一使用者）用不到更複雜的
    // 做法（例如每執行緒各自開一條連線）。lock 是可重入的，Rebuild() 從其他已經持有鎖的方法
    // 內部被呼叫（例如 OnMetaFileChanged 偵測到非正規檔名時）不會死鎖。
    private readonly object _connectionLock = new();

    /// <summary>
    /// cacheDirectory 底下的實際檔名是 Vault 路徑正規化後的雜湊值——同一個 Vault 路徑每次
    /// 啟動都會對到同一份快取檔（不用每次重建），換了 Vault 路徑（設定頁搬移）自然對到一份
    /// 全新的檔名，不會誤用舊資料；建構時如果偵測到現有快取檔不存在、版本不符、或內容已損毀，
    /// 會直接重新建立 schema 並呼叫 Rebuild() 全量重建，建構完成後保證資料立即可用。
    /// </summary>
    public VaultIndexCache(VaultManager vaultManager, string cacheDirectory)
    {
        _vaultManager = vaultManager;
        Directory.CreateDirectory(cacheDirectory);

        var normalizedVaultPath = NormalizeVaultPath(vaultManager.VaultPath);
        var hash = ComputeShortHash(normalizedVaultPath);
        _dbPath = Path.Combine(cacheDirectory, $"{hash}.db");

        _connection = OpenOrRebuildConnection(normalizedVaultPath, cacheDirectory, hash);
    }

    /// <summary>直接讀 SQLite，不碰 Vault 資料夾的檔案系統——這是這個類別存在的意義。</summary>
    public IReadOnlyList<VaultIndexEntry> GetItems()
    {
        lock (_connectionLock)
        {
            var results = new List<VaultIndexEntry>();

            using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT Uuid, OriginalName, OriginalPath, Type, PasskeyEnabled, RecoveryKeyEnabled, " +
                "BatchId, OriginalSizeBytes, Hint, CreatedAtUtc, NestedLockCount FROM VaultItems";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new VaultIndexEntry(
                    Uuid: reader.GetString(0),
                    OriginalName: reader.GetString(1),
                    OriginalPath: reader.GetString(2),
                    Type: Enum.Parse<ItemType>(reader.GetString(3)),
                    PasskeyEnabled: reader.GetInt64(4) != 0,
                    RecoveryKeyEnabled: reader.GetInt64(5) != 0,
                    BatchId: reader.IsDBNull(6) ? null : reader.GetString(6),
                    OriginalSizeBytes: reader.GetInt64(7),
                    Hint: reader.IsDBNull(8) ? null : reader.GetString(8),
                    CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(9), null, DateTimeStyles.RoundtripKind),
                    NestedLockCount: (int)reader.GetInt64(10)));
            }

            return results;
        }
    }

    /// <summary>
    /// 全量重建：清空 VaultItems 表，呼叫 VaultManager.ScanAll() 重新寫入。
    /// 用於：快取檔不存在／版本不符／內容損毀／FileSystemWatcher 偵測到非正規檔名變化
    /// 或事件緩衝區溢位時的保底復原——這些情況都不值得花力氣猜測該怎麼增量處理，
    /// 全部重掃一次最簡單也最不容易出錯。
    /// </summary>
    public void Rebuild()
    {
        lock (_connectionLock)
        {
            RebuildInternal(_connection);
        }
    }

    /// <summary>FileSystemWatcher 偵測到 {uuid}.meta.json 新增或內容變更時呼叫。</summary>
    public void OnMetaFileChanged(string metaFilePath)
    {
        var fileName = Path.GetFileName(metaFilePath);
        if (!TryExtractUuidFromCanonicalFileName(fileName, out var uuid))
        {
            // 非正規檔名（例如雲端同步的衝突副本，檔名帶裝置代號），不嘗試猜測要怎麼處理，
            // 這種情況本來就罕見，猜錯弄髒快取的風險遠大於偶爾全量重掃一次的成本。
            Rebuild();
            return;
        }

        LockedItemMetadata? metadata;
        DateTime lastWriteUtc;
        try
        {
            metadata = _vaultManager.LoadMetadata(uuid);
            lastWriteUtc = File.GetLastWriteTimeUtc(metaFilePath);
        }
        catch (IOException)
        {
            // 檔案可能正被雲端同步用戶端鎖定寫入中，略過這次更新，下次事件再處理一次即可。
            return;
        }

        if (metadata is null)
        {
            // 內容暫時解析失敗（例如同步中途讀到不完整內容），不動既有快取列，下次事件再試。
            return;
        }

        lock (_connectionLock)
        {
            UpsertIfNewer(uuid, metadata, lastWriteUtc);
        }
    }

    /// <summary>FileSystemWatcher 偵測到 {uuid}.meta.json 已經不存在時呼叫。</summary>
    public void OnMetaFileRemoved(string metaFilePath)
    {
        var fileName = Path.GetFileName(metaFilePath);
        if (!TryExtractUuidFromCanonicalFileName(fileName, out var uuid))
        {
            Rebuild();
            return;
        }

        lock (_connectionLock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM VaultItems WHERE Uuid = $uuid";
            command.Parameters.AddWithValue("$uuid", uuid);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();

        // Microsoft.Data.Sqlite 預設會連線池化，光 Dispose() 並不會讓底層原生檔案控制代碼
        // 立刻真正關閉——呼叫端（例如測試收尾時要刪除整個暫存資料夾）緊接著操作同一個
        // .db 檔案會撞到「檔案被另一個處理程序使用中」。ClearPool 強制釋放，確保 Dispose
        // 回來之後檔案控制代碼保證已經真正關閉。
        SqliteConnection.ClearPool(_connection);
    }

    private SqliteConnection OpenOrRebuildConnection(string normalizedVaultPath, string cacheDirectory, string hash)
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        try
        {
            connection.Open();
            ConfigurePragmas(connection);
            if (IsExistingCacheValid(connection, normalizedVaultPath))
            {
                return connection;
            }
        }
        catch (SqliteException)
        {
            // 快取檔案損毀（例如打不開、內容不是合法的 SQLite 檔案），視為不可信，往下走全部重建。
        }

        connection.Dispose();
        SqliteConnection.ClearPool(connection);
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        ConfigurePragmas(connection);
        CreateSchema(connection, normalizedVaultPath);
        RebuildInternal(connection);
        CleanupOrphanedCacheFiles(cacheDirectory, hash);
        return connection;
    }

    /// <summary>
    /// WAL 模式：這個快取會被 FileSystemWatcher 頻繁做小筆增量寫入（每個 .meta.json 變化各一次
    /// upsert），預設的 rollback journal 模式每次寫入都要多一次 fsync，量一多會累積成明顯延遲。
    /// WAL 模式把大部分寫入延後合併，單筆寫入快很多；journal_mode 是資料庫檔案本身的設定，
    /// 一旦設定就會保留，之後每次開啟不需要重設，但 synchronous 是連線層級設定，每次開啟都要設。
    /// </summary>
    private static void ConfigurePragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
    }

    private static bool IsExistingCacheValid(SqliteConnection connection, string normalizedVaultPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM CacheMeta";

        string? schemaVersion = null;
        string? sourceVaultPath = null;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var value = reader.GetString(1);
            if (key == "SchemaVersion")
            {
                schemaVersion = value;
            }
            else if (key == "SourceVaultPath")
            {
                sourceVaultPath = value;
            }
        }

        return schemaVersion == CurrentSchemaVersion.ToString()
            && sourceVaultPath == normalizedVaultPath;
    }

    private static void CreateSchema(SqliteConnection connection, string normalizedVaultPath)
    {
        using var createCommand = connection.CreateCommand();
        createCommand.CommandText =
            """
            CREATE TABLE CacheMeta (
                Key   TEXT PRIMARY KEY NOT NULL,
                Value TEXT NOT NULL
            );

            CREATE TABLE VaultItems (
                Uuid                 TEXT PRIMARY KEY NOT NULL,
                OriginalName         TEXT NOT NULL,
                OriginalPath         TEXT NOT NULL,
                Type                 TEXT NOT NULL,
                PasskeyEnabled       INTEGER NOT NULL,
                RecoveryKeyEnabled   INTEGER NOT NULL,
                BatchId              TEXT NULL,
                OriginalSizeBytes    INTEGER NOT NULL,
                Hint                 TEXT NULL,
                CreatedAtUtc         TEXT NOT NULL,
                NestedLockCount      INTEGER NOT NULL,
                MetaFileLastWriteUtc TEXT NOT NULL
            );
            """;
        createCommand.ExecuteNonQuery();

        using var metaCommand = connection.CreateCommand();
        metaCommand.CommandText =
            """
            INSERT INTO CacheMeta (Key, Value) VALUES
                ('SchemaVersion', $schemaVersion),
                ('SourceVaultPath', $sourceVaultPath),
                ('LastRebuildUtc', $lastRebuildUtc);
            """;
        metaCommand.Parameters.AddWithValue("$schemaVersion", CurrentSchemaVersion.ToString());
        metaCommand.Parameters.AddWithValue("$sourceVaultPath", normalizedVaultPath);
        metaCommand.Parameters.AddWithValue("$lastRebuildUtc", DateTimeOffset.UtcNow.ToString("o"));
        metaCommand.ExecuteNonQuery();
    }

    private void RebuildInternal(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM VaultItems";
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var metadata in _vaultManager.ScanAll())
        {
            UpsertRow(connection, transaction, metadata, GetMetaFileLastWriteUtc(metadata.Uuid));
        }

        using (var metaCommand = connection.CreateCommand())
        {
            metaCommand.Transaction = transaction;
            metaCommand.CommandText = "INSERT OR REPLACE INTO CacheMeta (Key, Value) VALUES ('LastRebuildUtc', $value)";
            metaCommand.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("o"));
            metaCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private DateTime GetMetaFileLastWriteUtc(string uuid)
    {
        var canonicalPath = _vaultManager.GetMetaFilePath(uuid);

        // 正規檔名理論上一定存在（剛從 ScanAll() 掃出來的項目）；如果剛好是靠衝突副本檔名
        // 贏得去重判斷的極罕見情況，退回 DateTime.MinValue——之後任何觸碰到那個衝突副本
        // 檔案的 watcher 事件都會走「非正規檔名一律全量重建」的路徑自我修正，不需要在
        // 這裡特別處理。
        return File.Exists(canonicalPath) ? File.GetLastWriteTimeUtc(canonicalPath) : DateTime.MinValue;
    }

    private void UpsertIfNewer(string uuid, LockedItemMetadata metadata, DateTime lastWriteUtc)
    {
        using var selectCommand = _connection.CreateCommand();
        selectCommand.CommandText = "SELECT MetaFileLastWriteUtc FROM VaultItems WHERE Uuid = $uuid";
        selectCommand.Parameters.AddWithValue("$uuid", uuid);

        if (selectCommand.ExecuteScalar() is string existingRaw)
        {
            var existingLastWriteUtc = DateTime.Parse(existingRaw, null, DateTimeStyles.RoundtripKind);
            if (lastWriteUtc < existingLastWriteUtc)
            {
                // 快取裡已經有更新的版本，這次事件帶來的是比較舊的內容（事件時序錯亂或重複
                // 事件），不覆蓋——呼應 VaultManager.ScanAll() 既有的「保留最新寫入」規則。
                return;
            }
        }

        UpsertRow(_connection, transaction: null, metadata, lastWriteUtc);
    }

    private static void UpsertRow(
        SqliteConnection connection, SqliteTransaction? transaction, LockedItemMetadata metadata, DateTime metaFileLastWriteUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO VaultItems
                (Uuid, OriginalName, OriginalPath, Type, PasskeyEnabled, RecoveryKeyEnabled, BatchId, OriginalSizeBytes, Hint, CreatedAtUtc, NestedLockCount, MetaFileLastWriteUtc)
            VALUES
                ($uuid, $originalName, $originalPath, $type, $passkeyEnabled, $recoveryKeyEnabled, $batchId, $originalSizeBytes, $hint, $createdAtUtc, $nestedLockCount, $metaFileLastWriteUtc)
            ON CONFLICT(Uuid) DO UPDATE SET
                OriginalName = excluded.OriginalName,
                OriginalPath = excluded.OriginalPath,
                Type = excluded.Type,
                PasskeyEnabled = excluded.PasskeyEnabled,
                RecoveryKeyEnabled = excluded.RecoveryKeyEnabled,
                BatchId = excluded.BatchId,
                OriginalSizeBytes = excluded.OriginalSizeBytes,
                Hint = excluded.Hint,
                CreatedAtUtc = excluded.CreatedAtUtc,
                NestedLockCount = excluded.NestedLockCount,
                MetaFileLastWriteUtc = excluded.MetaFileLastWriteUtc
            """;
        command.Parameters.AddWithValue("$uuid", metadata.Uuid);
        command.Parameters.AddWithValue("$originalName", metadata.OriginalName);
        command.Parameters.AddWithValue("$originalPath", metadata.OriginalPath);
        command.Parameters.AddWithValue("$type", metadata.Type.ToString());
        command.Parameters.AddWithValue("$passkeyEnabled", metadata.PasskeyEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$recoveryKeyEnabled", metadata.RecoveryKeyEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$batchId", (object?)metadata.BatchId ?? DBNull.Value);
        command.Parameters.AddWithValue("$originalSizeBytes", metadata.OriginalSizeBytes);
        command.Parameters.AddWithValue("$hint", (object?)metadata.Hint ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", metadata.CreatedAtUtc.ToString("o"));
        command.Parameters.AddWithValue("$nestedLockCount", metadata.ContainsNestedLocks.Count);
        command.Parameters.AddWithValue("$metaFileLastWriteUtc", metaFileLastWriteUtc.ToString("o"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 只在「這次是全新建立快取檔」的情況下執行一次——代表 Vault 路徑換了（或第一次啟動），
    /// 之前其他雜湊命名留下的快取檔已經沒有用了，順手清掉避免使用者多次搬移 Vault 後
    /// 留下一堆孤兒快取檔。
    /// </summary>
    private static void CleanupOrphanedCacheFiles(string cacheDirectory, string currentHash)
    {
        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.db"))
        {
            if (!Path.GetFileNameWithoutExtension(file).Equals(currentHash, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // 舊快取檔可能還被其他行程或延遲的檔案控制代碼鎖住，略過，不影響這次啟動。
                }
            }
        }
    }

    private static bool TryExtractUuidFromCanonicalFileName(string fileName, out string uuid)
    {
        const string suffix = ".meta.json";
        uuid = "";

        if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^suffix.Length];
        if (!Guid.TryParse(stem, out var parsed))
        {
            return false;
        }

        uuid = parsed.ToString();
        return true;
    }

    private static string NormalizeVaultPath(string vaultPath)
        => Path.GetFullPath(vaultPath).ToLowerInvariant().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ComputeShortHash(string normalizedVaultPath)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedVaultPath));
        return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
    }
}
