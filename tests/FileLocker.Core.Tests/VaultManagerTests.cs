using FileLocker.Core.Models;
using FileLocker.Core.Vault;
using Xunit;

namespace FileLocker.Core.Tests;

public class VaultManagerTests : IDisposable
{
    private readonly DirectoryInfo _tempVaultDir;
    private readonly VaultManager _vault;

    public VaultManagerTests()
    {
        _tempVaultDir = Directory.CreateTempSubdirectory("FileLockerVaultTests_");
        _vault = new VaultManager(_tempVaultDir.FullName);
    }

    public void Dispose()
    {
        if (_tempVaultDir.Exists)
        {
            _tempVaultDir.Delete(recursive: true);
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
    public void LoadOrCreateConfig_WhenNotExists_CreatesConfigWithNonEmptyKey()
    {
        var config = _vault.LoadOrCreateConfig();

        Assert.False(string.IsNullOrWhiteSpace(config.SigningKeyBase64));
        Assert.True(File.Exists(Path.Combine(_tempVaultDir.FullName, "vault.config.json")));
    }

    [Fact]
    public void LoadOrCreateConfig_CalledTwice_ReturnsSameKeyBothTimes()
    {
        var first = _vault.LoadOrCreateConfig();
        var second = _vault.LoadOrCreateConfig();

        Assert.Equal(first.SigningKeyBase64, second.SigningKeyBase64);
    }

    [Fact]
    public void SaveMetadata_ThenLoadMetadata_RoundTripsContent()
    {
        var uuid = Guid.NewGuid().ToString();
        var original = CreateSampleMetadata(uuid);

        _vault.SaveMetadata(original);
        var loaded = _vault.LoadMetadata(uuid);

        Assert.NotNull(loaded);
        Assert.Equal(original.OriginalName, loaded!.OriginalName);
        Assert.Equal(original.Salt, loaded.Salt);
        Assert.Equal(original.Type, loaded.Type);
    }

    [Fact]
    public void LoadMetadata_NonexistentUuid_ReturnsNull()
    {
        var result = _vault.LoadMetadata(Guid.NewGuid().ToString());

        Assert.Null(result);
    }

    [Fact]
    public void ScanAll_ReturnsAllSavedMetadataItems()
    {
        var uuidA = Guid.NewGuid().ToString();
        var uuidB = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuidA));
        _vault.SaveMetadata(CreateSampleMetadata(uuidB));

        var results = _vault.ScanAll().Select(m => m.Uuid).ToList();

        Assert.Contains(uuidA, results);
        Assert.Contains(uuidB, results);
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ScanAll_SkipsCorruptedMetaFile_ButReturnsValidOnes()
    {
        var validUuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(validUuid));

        // 模擬雲端同步中途讀到不完整內容、或檔案損毀的情境。
        var corruptedPath = Path.Combine(_tempVaultDir.FullName, $"{Guid.NewGuid()}.meta.json");
        File.WriteAllText(corruptedPath, "{ 這不是合法的 JSON");

        var results = _vault.ScanAll().ToList();

        Assert.Single(results);
        Assert.Equal(validUuid, results[0].Uuid);
    }

    [Fact]
    public void ScanAll_WithSyncConflictCopy_ReturnsOnlyOneEntryPerUuid()
    {
        // 對應雲端同步情境測試：模擬 OneDrive/Dropbox 之類的同步用戶端偵測到衝突時，
        // 另外存一份帶著裝置名稱的「衝突副本」——檔名不同，內容其實是同一筆資料的重複。
        var uuid = Guid.NewGuid().ToString();
        var metadata = CreateSampleMetadata(uuid);
        _vault.SaveMetadata(metadata); // 存成 {uuid}.meta.json

        var conflictCopyPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}-我的電腦.meta.json");
        var json = System.Text.Json.JsonSerializer.Serialize(metadata);
        File.WriteAllText(conflictCopyPath, json);

        var results = _vault.ScanAll().Where(m => m.Uuid == uuid).ToList();

        // 就算實際上有兩個檔案對應同一個 UUID，清單也只應該看到一筆，不能讓使用者看到重複列。
        Assert.Single(results);
    }

    [Fact]
    public void DeleteItem_RemovesEncAndMetaFiles()
    {
        var uuid = Guid.NewGuid().ToString();
        _vault.SaveMetadata(CreateSampleMetadata(uuid));
        using (var stream = _vault.OpenEncryptedContentWrite(uuid))
        {
            stream.Write(new byte[] { 1, 2, 3 });
        }

        _vault.DeleteItem(uuid);

        Assert.Null(_vault.LoadMetadata(uuid));
        Assert.False(File.Exists(Path.Combine(_tempVaultDir.FullName, $"{uuid}.enc")));
    }

    [Fact]
    public void DeleteItem_WhenFilesDontExist_DoesNotThrow()
    {
        var exception = Record.Exception(() => _vault.DeleteItem(Guid.NewGuid().ToString()));

        Assert.Null(exception);
    }
}