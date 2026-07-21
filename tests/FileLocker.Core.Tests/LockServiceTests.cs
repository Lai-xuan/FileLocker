using FileLocker.Core.Models;
using FileLocker.Core.Vault;
using Xunit;

namespace FileLocker.Core.Tests;

public class LockServiceTests : IDisposable
{
    private readonly DirectoryInfo _vaultDir;
    private readonly DirectoryInfo _workDir; // 模擬使用者的「文件」資料夾
    private readonly LockService _service;

    public LockServiceTests()
    {
        _vaultDir = Directory.CreateTempSubdirectory("FileLockerVault_");
        _workDir = Directory.CreateTempSubdirectory("FileLockerWork_");
        _service = new LockService(new VaultManager(_vaultDir.FullName));
    }

    public void Dispose()
    {
        if (_vaultDir.Exists) _vaultDir.Delete(recursive: true);
        if (_workDir.Exists) _workDir.Delete(recursive: true);
    }

    [Fact]
    public async Task EncryptAsync_SingleFile_RemovesOriginalAndCreatesMarker()
    {
        var filePath = Path.Combine(_workDir.FullName, "秘密文件.txt");
        File.WriteAllText(filePath, "這是不能被看到的內容");

        var result = await _service.EncryptAsync(filePath, "correct-password", "測試提示");

        Assert.True(result.Success);
        Assert.False(File.Exists(filePath)); // 原始明文已被清除
        Assert.True(File.Exists(result.LockedMarkerPath));
        Assert.Equal(Path.Combine(_workDir.FullName, "秘密文件.locked"), result.LockedMarkerPath);
    }

    [Fact]
    public async Task EncryptAsync_ThenDecryptAsync_WithCorrectPassword_RestoresOriginalContent()
    {
        var filePath = Path.Combine(_workDir.FullName, "報告.txt");
        const string originalContent = "第一季營收成長 15%";
        File.WriteAllText(filePath, originalContent);

        var lockResult = await _service.EncryptAsync(filePath, "my-strong-password", null);
        var unlockResult = await _service.DecryptAsync(lockResult.LockedMarkerPath, "my-strong-password");

        Assert.True(unlockResult.Success);
        Assert.Equal(filePath, unlockResult.RestoredPath);
        Assert.True(File.Exists(filePath));
        Assert.Equal(originalContent, File.ReadAllText(filePath));
        Assert.False(File.Exists(lockResult.LockedMarkerPath)); // marker 應該在解密後被移除
    }

    [Fact]
    public async Task DecryptAsync_WithWrongPassword_FailsAndLeavesEverythingIntact()
    {
        var filePath = Path.Combine(_workDir.FullName, "機密.txt");
        File.WriteAllText(filePath, "top secret");
        var lockResult = await _service.EncryptAsync(filePath, "correct-password", null);

        var unlockResult = await _service.DecryptAsync(lockResult.LockedMarkerPath, "wrong-password");

        Assert.False(unlockResult.Success);
        Assert.False(File.Exists(filePath)); // 還原不會發生
        Assert.True(File.Exists(lockResult.LockedMarkerPath)); // marker 還在，可以再試一次
    }

    [Fact]
    public async Task EncryptAsync_ThenDecryptAsync_Folder_RestoresStructureAndContents()
    {
        var folderPath = Path.Combine(_workDir.FullName, "專案資料夾");
        Directory.CreateDirectory(folderPath);
        File.WriteAllText(Path.Combine(folderPath, "readme.txt"), "說明文件");
        var subDir = Directory.CreateDirectory(Path.Combine(folderPath, "images"));
        File.WriteAllText(Path.Combine(subDir.FullName, "note.txt"), "圖片說明");

        var lockResult = await _service.EncryptAsync(folderPath, "folder-password", null);
        Assert.True(lockResult.Success);
        Assert.False(Directory.Exists(folderPath));

        var unlockResult = await _service.DecryptAsync(lockResult.LockedMarkerPath, "folder-password");

        Assert.True(unlockResult.Success);
        Assert.True(Directory.Exists(folderPath));
        Assert.Equal("說明文件", File.ReadAllText(Path.Combine(folderPath, "readme.txt")));
        Assert.Equal("圖片說明", File.ReadAllText(Path.Combine(folderPath, "images", "note.txt")));
    }

    [Fact]
    public async Task EncryptAsync_FolderContainingNestedLockedFile_RecordsNestedUuid()
    {
        // 先加密一個單獨的檔案，製造出一個巢狀 .locked 項目。
        var nestedFilePath = Path.Combine(_workDir.FullName, "inner.txt");
        File.WriteAllText(nestedFilePath, "被包在外層資料夾裡的檔案");
        var nestedResult = await _service.EncryptAsync(nestedFilePath, "inner-password", null);
        Assert.True(nestedResult.Success);

        // 把整個工作資料夾（現在裡面有 inner.locked）搬進一個要被加密的外層資料夾。
        var outerFolder = Path.Combine(Path.GetTempPath(), $"FileLockerOuter_{Guid.NewGuid()}");
        Directory.CreateDirectory(outerFolder);
        var innerLockedDestination = Path.Combine(outerFolder, "inner.locked");
        File.Move(nestedResult.LockedMarkerPath, innerLockedDestination);

        try
        {
            var outerResult = await _service.EncryptAsync(outerFolder, "outer-password", null);
            Assert.True(outerResult.Success);

            var metadata = new VaultManager(_vaultDir.FullName).LoadMetadata(outerResult.Uuid);
            Assert.NotNull(metadata);
            Assert.Single(metadata!.ContainsNestedLocks);
            Assert.Equal(nestedResult.Uuid, metadata.ContainsNestedLocks[0]);
        }
        finally
        {
            if (Directory.Exists(outerFolder)) Directory.Delete(outerFolder, recursive: true);
        }
    }

    [Fact]
    public async Task TryDeleteRecordAsync_WithNestedLocks_IsBlockedByDefault()
    {
        var vault = new VaultManager(_vaultDir.FullName);
        vault.SaveMetadata(new LockedItemMetadata
        {
            Uuid = "outer-uuid",
            OriginalName = "外層資料夾",
            OriginalPath = @"C:\fake\path",
            PasswordVerificationHash = "dummy==",
            Salt = "dummy==",
            Argon2TimeCost = 1,
            Argon2MemoryCostKb = 8192,
            Argon2Parallelism = 1,
            Type = ItemType.Folder,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ContainsNestedLocks = new List<string> { "inner-uuid-1", "inner-uuid-2" }
        });

        var result = await _service.TryDeleteRecordAsync("outer-uuid");

        Assert.False(result.Success);
        Assert.True(result.BlockedByNestedLocks);
        Assert.Equal(2, result.NestedUuids!.Count);
    }

    [Fact]
    public async Task TryDeleteRecordAsync_WithoutNestedLocks_Succeeds()
    {
        var filePath = Path.Combine(_workDir.FullName, "普通檔案.txt");
        File.WriteAllText(filePath, "沒有巢狀鎖定");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);

        var result = await _service.TryDeleteRecordAsync(lockResult.Uuid);

        Assert.True(result.Success);
        Assert.False(result.BlockedByNestedLocks);
        Assert.Null(new VaultManager(_vaultDir.FullName).LoadMetadata(lockResult.Uuid));
    }

    [Fact]
    public async Task DecryptAsync_WithTamperedMarker_FailsSignatureVerification()
    {
        var filePath = Path.Combine(_workDir.FullName, "檔案.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);

        // 模擬 .locked 檔案被竄改成指向另一個（不存在的）UUID。
        var tampered = LockedMarkerFile.ReadFrom(lockResult.LockedMarkerPath)!;
        tampered.Uuid = Guid.NewGuid().ToString();
        tampered.WriteTo(lockResult.LockedMarkerPath);

        var unlockResult = await _service.DecryptAsync(lockResult.LockedMarkerPath, "password");

        Assert.False(unlockResult.Success);
        Assert.Contains("竄改", unlockResult.ErrorMessage);
    }
}