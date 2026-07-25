using System.Text.Json;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileLocker.Core.Tests;

public class VaultIndexCacheTests : IDisposable
{
    private readonly DirectoryInfo _tempVaultDir;
    private readonly DirectoryInfo _tempCacheDir;
    private readonly VaultManager _vault;

    public VaultIndexCacheTests()
    {
        _tempVaultDir = Directory.CreateTempSubdirectory("FileLockerVaultTests_");
        _tempCacheDir = Directory.CreateTempSubdirectory("FileLockerCacheTests_");
        _vault = new VaultManager(_tempVaultDir.FullName);
    }

    public void Dispose()
    {
        if (_tempVaultDir.Exists)
        {
            _tempVaultDir.Delete(recursive: true);
        }

        if (_tempCacheDir.Exists)
        {
            _tempCacheDir.Delete(recursive: true);
        }
    }

    private static LockedItemMetadata CreateSampleMetadata(string uuid) => new()
    {
        Uuid = uuid,
        OriginalName = "測試檔案.txt",
        OriginalPath = @"C:\Users\test\Documents\測試檔案.txt",
        PasswordVerificationHash = "dummyHashBase64==",
        Salt = "dummySaltBase64==",
        Argon2TimeCost = 3,
        Argon2MemoryCostKb = 65536,
        Argon2Parallelism = 2,
        Hint = "測試提示",
        Type = ItemType.File,
        OriginalSizeBytes = 1024,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public void GetItems_MatchesVaultManagerScanAll_ForFreshCache()
    {
        var uuidA = Guid.NewGuid().ToString();
        var uuidB = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuidA));
        _vault.SaveMetadata(CreateSampleMetadata(uuidB));

        using var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);

        var expected = _vault.ScanAll().Select(m => m.Uuid).OrderBy(u => u).ToList();
        var actual = cache.GetItems().Select(e => e.Uuid).OrderBy(u => u).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Rebuild_SkipsCorruptedMetaFile_ButReturnsValidOnes_SameAsScanAll()
    {
        var validUuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(validUuid));

        // 模擬雲端同步中途讀到不完整內容、或檔案損毀的情境（跟 VaultManagerTests 的既有測試對應）。
        var corruptedPath = Path.Combine(_tempVaultDir.FullName, $"{Guid.NewGuid()}.meta.json");
        File.WriteAllText(corruptedPath, "{ 這不是合法的 JSON");

        using var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);

        var items = cache.GetItems();

        Assert.Single(items);
        Assert.Equal(validUuid, items[0].Uuid);
    }

    [Fact]
    public void Rebuild_DeduplicatesSyncConflictCopies_KeepsNewestWrite()
    {
        // 對應真正的分歧情境：兩台裝置各自對同一個項目做了不同修改，快取應該跟
        // VaultManager.ScanAll() 一樣，偏好保留寫入時間比較新的那份。
        var uuid = Guid.NewGuid().ToString();

        var olderVersion = CreateSampleMetadata(uuid);
        olderVersion.Hint = "舊裝置的提示";
        var olderPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");
        File.WriteAllText(olderPath, JsonSerializer.Serialize(olderVersion));
        File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddMinutes(-10));

        var newerVersion = CreateSampleMetadata(uuid);
        newerVersion.Hint = "新裝置的提示，應該是這份被留下來";
        var newerPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}-另一台裝置.meta.json");
        File.WriteAllText(newerPath, JsonSerializer.Serialize(newerVersion));
        File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);

        using var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        var items = cache.GetItems().Where(e => e.Uuid == uuid).ToList();

        Assert.Single(items);
        Assert.Equal("新裝置的提示，應該是這份被留下來", items[0].Hint);
    }

    [Fact]
    public void GetItems_WhenCacheFileIsCorrupted_FallsBackToRebuild()
    {
        var uuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        string dbPath;
        using (var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName))
        {
            Assert.Single(cache.GetItems());
            dbPath = Directory.EnumerateFiles(_tempCacheDir.FullName, "*.db").Single();
        }

        // 直接把底層 .db 檔案內容用垃圾位元組覆寫掉，模擬快取檔損毀。
        File.WriteAllBytes(dbPath, new byte[] { 1, 2, 3, 4, 5 });

        using var reopened = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        var items = reopened.GetItems();

        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }

    [Fact]
    public void GetItems_WhenSchemaVersionMismatch_TriggersFullRebuild()
    {
        var uuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        string dbPath;
        using (var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName))
        {
            dbPath = Directory.EnumerateFiles(_tempCacheDir.FullName, "*.db").Single();
        }

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE CacheMeta SET Value = '999' WHERE Key = 'SchemaVersion'";
            command.ExecuteNonQuery();
        }

        using var reopened = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        var items = reopened.GetItems();

        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }

    [Fact]
    public void OnMetaFileChanged_WithCanonicalFileName_UpsertsSingleRow()
    {
        using var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        Assert.Empty(cache.GetItems());

        var uuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuid));
        var metaPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");

        cache.OnMetaFileChanged(metaPath);

        var items = cache.GetItems();
        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }

    [Fact]
    public void OnMetaFileChanged_OlderWriteTime_DoesNotOverwriteNewerRow()
    {
        using var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);

        var uuid = Guid.NewGuid().ToString();
        var metadata = CreateSampleMetadata(uuid);
        metadata.Hint = "比較新的提示";
        _vault.SaveMetadata(metadata);
        var metaPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");
        cache.OnMetaFileChanged(metaPath);

        // 模擬一個時間上比較舊、內容不同的事件晚到——不應該覆蓋已經在快取裡的較新內容，
        // 呼應 VaultManager.ScanAll() 既有的「保留最新寫入」規則要在快取層一併維持。
        var staleMetadata = CreateSampleMetadata(uuid);
        staleMetadata.Hint = "比較舊、不該生效的提示";
        File.WriteAllText(metaPath, JsonSerializer.Serialize(staleMetadata));
        File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow.AddMinutes(-10));

        cache.OnMetaFileChanged(metaPath);

        var items = cache.GetItems();
        Assert.Single(items);
        Assert.Equal("比較新的提示", items[0].Hint);
    }

    [Fact]
    public void OnMetaFileRemoved_WithCanonicalFileName_DeletesRow()
    {
        var uuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        using var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        Assert.Single(cache.GetItems());

        var metaPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");
        File.Delete(metaPath);
        cache.OnMetaFileRemoved(metaPath);

        Assert.Empty(cache.GetItems());
    }

    [Fact]
    public void OnMetaFileChanged_WithNonCanonicalFileName_TriggersFullRebuildInstead()
    {
        using var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);

        var uuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        var nonCanonicalPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}-我的電腦.meta.json");
        File.WriteAllText(nonCanonicalPath, JsonSerializer.Serialize(CreateSampleMetadata(uuid)));

        cache.OnMetaFileChanged(nonCanonicalPath);

        // 非正規檔名觸發的是全量重建，結果應該跟 VaultManager.ScanAll() 一致（去重成一筆）。
        var items = cache.GetItems();
        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }

    [Fact]
    public void Dispose_ReleasesUnderlyingSqliteConnection()
    {
        var cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        cache.Dispose();

        var dbPath = Directory.EnumerateFiles(_tempCacheDir.FullName, "*.db").Single();

        // 連線沒釋放的話這裡會因為檔案還被鎖住而拋例外。
        var exception = Record.Exception(() => File.Delete(dbPath));
        Assert.Null(exception);
    }
}
