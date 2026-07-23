using System.Security.Cryptography;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;
using Xunit;

namespace FileLocker.Core.Tests;

public class LockServiceTests : IDisposable
{
    private readonly DirectoryInfo _vaultDir;
    private readonly DirectoryInfo _workDir; // 模擬使用者的「文件」資料夾
    private readonly DirectoryInfo _historyDir;
    private readonly HistoryLogger _history;
    private readonly LockService _service;

    public LockServiceTests()
    {
        _vaultDir = Directory.CreateTempSubdirectory("FileLockerVault_");
        _workDir = Directory.CreateTempSubdirectory("FileLockerWork_");
        _historyDir = Directory.CreateTempSubdirectory("FileLockerHistory_");
        _history = new HistoryLogger(Path.Combine(_historyDir.FullName, "history.jsonl"));
        _service = new LockService(new VaultManager(_vaultDir.FullName), _history);
    }

    public void Dispose()
    {
        if (_vaultDir.Exists) _vaultDir.Delete(recursive: true);
        if (_workDir.Exists) _workDir.Delete(recursive: true);
        if (_historyDir.Exists) _historyDir.Delete(recursive: true);
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

    [Fact]
    public async Task CheckMarkerStatus_ForItemStillAtOriginalLocation_ReturnsFound()
    {
        var filePath = Path.Combine(_workDir.FullName, "沒被搬動的檔案.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);
        var metadata = new VaultManager(_vaultDir.FullName).LoadMetadata(lockResult.Uuid)!;

        var status = _service.CheckMarkerStatus(metadata);

        Assert.True(status.Found);
        Assert.Equal(lockResult.LockedMarkerPath, status.MarkerPath);
    }

    [Fact]
    public async Task CheckMarkerStatus_WhenMarkerFileHasBeenMoved_ReturnsNotFound()
    {
        var filePath = Path.Combine(_workDir.FullName, "會被搬走的檔案.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);
        var metadata = new VaultManager(_vaultDir.FullName).LoadMetadata(lockResult.Uuid)!;

        // 模擬使用者事後把 .locked 檔案搬到別的地方。
        var elsewhereDir = Directory.CreateTempSubdirectory("FileLockerElsewhere_");
        try
        {
            File.Move(lockResult.LockedMarkerPath, Path.Combine(elsewhereDir.FullName, "會被搬走的檔案.locked"));

            var status = _service.CheckMarkerStatus(metadata);

            Assert.False(status.Found);
        }
        finally
        {
            elsewhereDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DecryptByUuidAsync_WithCorrectPassword_RestoresContentAndRemovesExistingMarker()
    {
        var filePath = Path.Combine(_workDir.FullName, "清單解密測試.txt");
        File.WriteAllText(filePath, "透過清單直接解密");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);
        Assert.True(File.Exists(lockResult.LockedMarkerPath));

        var unlockResult = await _service.DecryptByUuidAsync(lockResult.Uuid, "password");

        Assert.True(unlockResult.Success);
        Assert.Equal(filePath, unlockResult.RestoredPath);
        Assert.Equal("透過清單直接解密", File.ReadAllText(filePath));
        Assert.False(File.Exists(lockResult.LockedMarkerPath)); // marker 應該被一併清掉
    }

    [Fact]
    public async Task DecryptByUuidAsync_WhenMarkerAlreadyMovedAway_StillSucceedsAndLeavesMovedMarkerAlone()
    {
        var filePath = Path.Combine(_workDir.FullName, "指標檔被搬走.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);

        var elsewhere = Directory.CreateTempSubdirectory("FileLockerElsewhere2_");
        try
        {
            var movedMarkerPath = Path.Combine(elsewhere.FullName, "指標檔被搬走.locked");
            File.Move(lockResult.LockedMarkerPath, movedMarkerPath);

            var unlockResult = await _service.DecryptByUuidAsync(lockResult.Uuid, "password");

            Assert.True(unlockResult.Success);
            Assert.True(File.Exists(movedMarkerPath)); // 別的地方那份不屬於檢查範圍，不會被動到
        }
        finally
        {
            elsewhere.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EncryptAsync_ThenDecryptAsync_AppendsHistoryEntries()
    {
        var filePath = Path.Combine(_workDir.FullName, "歷史紀錄測試.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", "提示文字");
        await _service.DecryptAsync(lockResult.LockedMarkerPath, "password");

        var entries = _history.ReadAll();

        Assert.Contains(entries, entry => entry.Uuid == lockResult.Uuid && entry.Action == HistoryAction.Encrypted);
        Assert.Contains(entries, entry => entry.Uuid == lockResult.Uuid && entry.Action == HistoryAction.Decrypted);
    }

    [Fact]
    public async Task DecryptByUuidAsync_WithCustomDestination_RestoresThereInsteadOfOriginalLocation()
    {
        var filePath = Path.Combine(_workDir.FullName, "自訂位置解密測試.txt");
        File.WriteAllText(filePath, "自訂還原位置");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);

        var customDestDir = Directory.CreateTempSubdirectory("FileLockerCustomDest_");
        try
        {
            var unlockResult = await _service.DecryptByUuidAsync(lockResult.Uuid, "password", customDestDir.FullName);

            Assert.True(unlockResult.Success);
            var expectedRestoredPath = Path.Combine(customDestDir.FullName, "自訂位置解密測試.txt");
            Assert.Equal(expectedRestoredPath, unlockResult.RestoredPath);
            Assert.True(File.Exists(expectedRestoredPath));
            Assert.Equal("自訂還原位置", File.ReadAllText(expectedRestoredPath));
            Assert.False(File.Exists(filePath)); // 原始位置不會出現還原的檔案
            Assert.False(File.Exists(lockResult.LockedMarkerPath)); // 原始位置的指標檔還是會被正確清掉
        }
        finally
        {
            customDestDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task EncryptAsync_WhenOriginalFileCannotBeDeleted_StillReportsSuccessWithWarning()
    {
        // 對應修掉的 bug：加密內容已經安全寫進 Vault 之後，只是清除原始檔案這個收尾動作失敗，
        // 不應該讓整個結果被回報成「加密失敗」。用另一個檔案控制代碼鎖住檔案，模擬刪除失敗的情境。
        var filePath = Path.Combine(_workDir.FullName, "被鎖住的檔案.txt");
        File.WriteAllText(filePath, "內容");

        using (new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await _service.EncryptAsync(filePath, "password", null);

            Assert.True(result.Success);
            Assert.NotNull(result.ErrorMessage);
            Assert.True(File.Exists(result.LockedMarkerPath)); // marker 有正常產生，代表加密內容確實寫入成功
            Assert.NotNull(new VaultManager(_vaultDir.FullName).LoadMetadata(result.Uuid)); // Vault 裡的紀錄也在
        }
    }

    [Fact]
    public async Task EncryptAsync_WithRecoveryKeyEnabled_ReturnsDisplayableRecoveryKey()
    {
        var filePath = Path.Combine(_workDir.FullName, "恢復金鑰測試.txt");
        File.WriteAllText(filePath, "內容");

        var result = await _service.EncryptAsync(filePath, "password", null, enableRecoveryKey: true);

        Assert.True(result.Success);
        Assert.NotNull(result.RecoveryKey);
        Assert.Contains("-", result.RecoveryKey); // 應該是分組格式，不是一長串沒有分隔的字元

        var metadata = new VaultManager(_vaultDir.FullName).LoadMetadata(result.Uuid);
        Assert.True(metadata!.RecoveryKeyEnabled);
    }

    [Fact]
    public async Task EncryptAsync_WithoutRecoveryKey_ReturnsNullRecoveryKey()
    {
        var filePath = Path.Combine(_workDir.FullName, "沒開恢復金鑰.txt");
        File.WriteAllText(filePath, "內容");

        var result = await _service.EncryptAsync(filePath, "password", null);

        Assert.Null(result.RecoveryKey);
    }

    [Fact]
    public async Task DecryptByRecoveryKeyAsync_WithCorrectKey_RestoresContentWithoutPassword()
    {
        var filePath = Path.Combine(_workDir.FullName, "用恢復金鑰解密.txt");
        File.WriteAllText(filePath, "只有恢復金鑰知道的內容");

        var encryptResult = await _service.EncryptAsync(filePath, "password", null, enableRecoveryKey: true);
        Assert.NotNull(encryptResult.RecoveryKey);

        var unlockResult = await _service.DecryptByRecoveryKeyAsync(encryptResult.Uuid, encryptResult.RecoveryKey!);

        Assert.True(unlockResult.Success);
        Assert.Equal("只有恢復金鑰知道的內容", File.ReadAllText(filePath));
    }

    [Fact]
    public async Task DecryptByRecoveryKeyAsync_WithWrongKey_Fails()
    {
        var filePath = Path.Combine(_workDir.FullName, "恢復金鑰錯誤測試.txt");
        File.WriteAllText(filePath, "內容");

        var encryptResult = await _service.EncryptAsync(filePath, "password", null, enableRecoveryKey: true);
        var wrongKey = FileLocker.Core.Crypto.RecoveryKeyProtector.FormatForDisplay(
            FileLocker.Core.Crypto.RecoveryKeyProtector.GenerateRecoveryKeyBytes());

        var unlockResult = await _service.DecryptByRecoveryKeyAsync(encryptResult.Uuid, wrongKey);

        Assert.False(unlockResult.Success);
        Assert.False(File.Exists(filePath)); // 沒有還原
    }

    [Fact]
    public async Task DecryptByRecoveryKeyAsync_WhenNotEnabled_ReturnsClearError()
    {
        var filePath = Path.Combine(_workDir.FullName, "沒開恢復金鑰_解密測試.txt");
        File.WriteAllText(filePath, "內容");

        var encryptResult = await _service.EncryptAsync(filePath, "password", null); // 沒開恢復金鑰

        var unlockResult = await _service.DecryptByRecoveryKeyAsync(encryptResult.Uuid, "ABCDE-FGHIJ-KLMNO-PQRST-UVWXY-ZABCD-EFGHI-JKLMN-OPQRS-TUVWX-YZABC");

        Assert.False(unlockResult.Success);
        Assert.Contains("沒有啟用恢復金鑰", unlockResult.ErrorMessage);
    }

    [Fact]
    public async Task RestoreFromKey_WithTamperedOriginalNameContainingPathTraversal_RejectsRestore()
    {
        // 模擬 .meta.json 被竄改：把 OriginalName 換成帶路徑穿越片段的惡意值。
        var filePath = Path.Combine(_workDir.FullName, "正常檔案.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null, enableRecoveryKey: true);

        var vault = new VaultManager(_vaultDir.FullName);
        var metadata = vault.LoadMetadata(lockResult.Uuid)!;
        metadata.OriginalName = "..\\..\\惡意檔案.txt";
        vault.SaveMetadata(metadata);

        var unlockResult = await _service.DecryptByRecoveryKeyAsync(lockResult.Uuid, lockResult.RecoveryKey!);

        Assert.False(unlockResult.Success);
        Assert.Contains("檔名", unlockResult.ErrorMessage);

        var maliciousTarget = Path.Combine(_workDir.Parent!.FullName, "惡意檔案.txt");
        Assert.False(File.Exists(maliciousTarget));
    }

    [Fact]
    public async Task RestoreFromKey_WithTamperedOriginalNameAsAbsolutePath_RejectsRestore()
    {
        // 模擬更嚴重的情況：OriginalName 被直接換成一個絕對路徑，
        // 如果沒有防護，Path.Combine 會直接忽略目的地資料夾，寫到這個絕對路徑去。
        var filePath = Path.Combine(_workDir.FullName, "正常檔案2.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null, enableRecoveryKey: true);

        var maliciousAbsolutePath = Path.Combine(_workDir.Parent!.FullName, "FileLockerAttackTarget.txt");

        var vault = new VaultManager(_vaultDir.FullName);
        var metadata = vault.LoadMetadata(lockResult.Uuid)!;
        metadata.OriginalName = maliciousAbsolutePath;
        vault.SaveMetadata(metadata);

        var unlockResult = await _service.DecryptByRecoveryKeyAsync(lockResult.Uuid, lockResult.RecoveryKey!);

        Assert.False(unlockResult.Success);
        Assert.False(File.Exists(maliciousAbsolutePath));
    }

    [Fact]
    public async Task DecryptByRecoveryKeyAsync_WhenMarkerAtOriginalLocationHasForgedSignature_DoesNotDeleteIt()
    {
        // 對應修掉的 bug：CleanupMarkerIfMatches 現在除了比對 UUID，還要驗證簽章才會刪除，
        // 偽造一個 UUID 對得上、但簽章是亂數（不是用 Vault 簽章金鑰簽出來的）的假指標檔應該不會被清掉。
        var filePath = Path.Combine(_workDir.FullName, "測試簽章防護.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null, enableRecoveryKey: true);

        var forgedMarker = new LockedMarkerFile
        {
            Uuid = lockResult.Uuid,
            SignatureBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
        forgedMarker.WriteTo(lockResult.LockedMarkerPath);

        var customDestDir = Directory.CreateTempSubdirectory("FileLockerForgedMarkerTest_");
        try
        {
            var unlockResult = await _service.DecryptByRecoveryKeyAsync(lockResult.Uuid, lockResult.RecoveryKey!, customDestDir.FullName);

            Assert.True(unlockResult.Success);
            Assert.True(File.Exists(lockResult.LockedMarkerPath)); // 假指標檔簽章驗證不過，不應該被清掉
        }
        finally
        {
            customDestDir.Delete(recursive: true);
        }
    }
}