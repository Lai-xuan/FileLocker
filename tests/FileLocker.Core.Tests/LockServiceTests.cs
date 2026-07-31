using System.Security.Cryptography;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.Security;
using FileLocker.Core.Vault;
using Xunit;

namespace FileLocker.Core.Tests;

public class LockServiceTests : IDisposable
{
    private readonly DirectoryInfo _vaultDir;
    private readonly DirectoryInfo _workDir; // 模擬使用者的「文件」資料夾
    private readonly DirectoryInfo _historyDir;
    private readonly HistoryLogger _history;
    private readonly LockoutTracker _lockout;
    private readonly LockService _service;

    public LockServiceTests()
    {
        _vaultDir = Directory.CreateTempSubdirectory("FileLockerVault_");
        _workDir = Directory.CreateTempSubdirectory("FileLockerWork_");
        _historyDir = Directory.CreateTempSubdirectory("FileLockerHistory_");
        _history = new HistoryLogger(Path.Combine(_historyDir.FullName, "history.jsonl"));
        _lockout = new LockoutTracker(Path.Combine(_historyDir.FullName, "lockout.json"));
        _service = new LockService(new VaultManager(_vaultDir.FullName), _history, _lockout);
    }

    public void Dispose()
    {
        if (_vaultDir.Exists) _vaultDir.Delete(recursive: true);
        if (_workDir.Exists) _workDir.Delete(recursive: true);
        if (_historyDir.Exists) _historyDir.Delete(recursive: true);
    }

    [Fact]
    public async Task EncryptAsync_FolderContainingGuardedSubfolder_ReturnsNestedGuardedError()
    {
        var folderPath = Path.Combine(_workDir.FullName, "要加密的資料夾");
        Directory.CreateDirectory(folderPath);
        var guardedSubPath = Path.Combine(folderPath, "防護中的子資料夾");
        Directory.CreateDirectory(guardedSubPath);
        File.WriteAllText(Path.Combine(folderPath, "普通檔案.txt"), "內容");

        // 對應規劃文件第 8 節：不需要真的套用 ACL 就能測到這條路徑——getGuardedFolderPaths
        // 只是告訴 LockService「目前有哪些路徑正在防護中」，真正的 ACL 阻擋行為屬於
        // FolderGuardAcl／FolderGuardService 自己的職責，這裡只驗證 LockService 收到
        // 巢狀防護清單後會正確中止並回報，不會讓 UnauthorizedAccessException 裸奔出去。
        var serviceWithGuard = new LockService(
            new VaultManager(_vaultDir.FullName), _history, _lockout,
            getGuardedFolderPaths: () => new[] { guardedSubPath });

        var result = await serviceWithGuard.EncryptAsync(folderPath, "correct-password", null);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FolderGuardContainsNestedGuarded, result.ErrorCode);
        Assert.Contains("防護中的子資料夾", result.ErrorMessage);
        Assert.True(Directory.Exists(folderPath)); // 中止在壓縮之前，原始資料夾要維持完整不動
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

    // CheckMarkerStatus 的測試搬到 MarkerStatusCheckerTests.cs 了——這個查詢邏輯已經從
    // LockService 分離成獨立的 MarkerStatusChecker（見架構審查 2026-07-26），不再需要透過
    // LockService 的完整依賴（HistoryLogger／LockoutTracker／VaultManager）才能測試。

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

    [Fact]
    public async Task DecryptAsync_AfterFiveFailedAttempts_LocksOutEvenWithCorrectPassword()
    {
        var filePath = Path.Combine(_workDir.FullName, "鎖定測試.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "correct-password", null);

        for (var i = 0; i < 5; i++)
        {
            await _service.DecryptAsync(lockResult.LockedMarkerPath, "wrong-password");
        }

        var result = await _service.DecryptAsync(lockResult.LockedMarkerPath, "correct-password");

        Assert.False(result.Success);
        Assert.Contains("錯誤次數過多", result.ErrorMessage);
    }

    [Fact]
    public async Task DecryptAsync_SuccessfulUnlock_ResetsFailedAttemptCounter()
    {
        var filePath = Path.Combine(_workDir.FullName, "重置測試.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "correct-password", null);

        await _service.DecryptAsync(lockResult.LockedMarkerPath, "wrong-password");
        await _service.DecryptAsync(lockResult.LockedMarkerPath, "wrong-password");

        var successResult = await _service.DecryptAsync(lockResult.LockedMarkerPath, "correct-password");

        Assert.True(successResult.Success);
    }

    [Fact]
    public async Task DecryptAsync_FewerThanThresholdFailures_StillAllowsCorrectPassword()
    {
        var filePath = Path.Combine(_workDir.FullName, "未達門檻測試.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "correct-password", null);

        for (var i = 0; i < 3; i++)
        {
            await _service.DecryptAsync(lockResult.LockedMarkerPath, "wrong-password");
        }

        var result = await _service.DecryptAsync(lockResult.LockedMarkerPath, "correct-password");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task EncryptAsync_WithBatchId_StoresItInMetadata()
    {
        var filePath = Path.Combine(_workDir.FullName, "批次測試.txt");
        File.WriteAllText(filePath, "內容");
        var batchId = Guid.NewGuid().ToString();

        var result = await _service.EncryptAsync(filePath, "password", null, batchId: batchId);

        var metadata = new VaultManager(_vaultDir.FullName).LoadMetadata(result.Uuid);
        Assert.Equal(batchId, metadata!.BatchId);
    }

    [Fact]
    public async Task EncryptAsync_WithoutBatchId_LeavesItNull()
    {
        var filePath = Path.Combine(_workDir.FullName, "非批次測試.txt");
        File.WriteAllText(filePath, "內容");

        var result = await _service.EncryptAsync(filePath, "password", null);

        var metadata = new VaultManager(_vaultDir.FullName).LoadMetadata(result.Uuid);
        Assert.Null(metadata!.BatchId);
    }

    [Fact]
    public async Task TryDeleteRecordAsync_OnSuccess_AlsoRemovesLockedMarkerAtOriginalLocation()
    {
        var filePath = Path.Combine(_workDir.FullName, "刪除紀錄測試.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null);
        Assert.True(File.Exists(lockResult.LockedMarkerPath));

        var deleteResult = await _service.TryDeleteRecordAsync(lockResult.Uuid);

        Assert.True(deleteResult.Success);
        Assert.False(File.Exists(lockResult.LockedMarkerPath)); // 失效的指標檔應該一併被清掉
        Assert.Null(new VaultManager(_vaultDir.FullName).LoadMetadata(lockResult.Uuid));
    }

    // ---- 對應雲端同步情境測試（2026-07-24）----
    // 這幾個測試不牽涉真的雲端帳號，而是模擬 FileLocker 自己能控制、也真正該負責的部分：
    // 「Vault 被某種外部機制（同步用戶端）不受控地搬移/同時存取時，程式不能崩潰或算錯」。
    // 真的跨裝置同步（上傳下載本身）是 OneDrive/Dropbox 的事，不是這裡要驗證的範圍。

    [Fact]
    public async Task Vault_CopiedToNewLocation_CanStillDecryptWithNewVaultManagerInstance()
    {
        // 模擬「同步到另一台裝置」最貼近的本機替代測試法：把整個 Vault 資料夾原封不動搬到
        // 別的路徑，用全新的 VaultManager／LockService 開啟，確認密碼還是能正常解密——
        // 這驗證了 Vault「輕便可攜」這個核心設計目標，不依賴任何機器綁定的狀態。
        var filePath = Path.Combine(_workDir.FullName, "可攜測試.txt");
        File.WriteAllText(filePath, "這份內容要能在另一個 Vault 位置正常解密");
        await _service.EncryptAsync(filePath, "correct-password", null);

        var copiedVaultDir = Directory.CreateTempSubdirectory("FileLockerCopiedVault_");
        try
        {
            CopyDirectory(_vaultDir.FullName, copiedVaultDir.FullName);

            var newVaultManager = new VaultManager(copiedVaultDir.FullName);
            var newService = new LockService(newVaultManager);

            var items = newVaultManager.ScanAll().ToList();
            Assert.Single(items);

            var restoreDir = Directory.CreateTempSubdirectory("FileLockerCopiedVaultRestore_");
            try
            {
                var result = await newService.DecryptByUuidAsync(items[0].Uuid, "correct-password", restoreDir.FullName);

                Assert.True(result.Success);
                Assert.Equal("這份內容要能在另一個 Vault 位置正常解密", File.ReadAllText(Path.Combine(restoreDir.FullName, "可攜測試.txt")));
            }
            finally
            {
                restoreDir.Delete(recursive: true);
            }
        }
        finally
        {
            copiedVaultDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TwoInstancesPointingAtSameVault_ConcurrentDecryptOfSameItem_DoesNotThrowOrCorruptState()
    {
        // 模擬兩台裝置幾乎同時對同一個 UUID 做操作（例如兩台電腦都在使用者離開電腦時自動同步、
        // 剛好都嘗試解密同一筆）。不保證兩邊都成功（畢竟只有一份內容可以被解密+刪除一次），
        // 但至少不能讓其中一邊丟出沒接住的例外、或讓 Vault 留下損毀的中間狀態。
        var filePath = Path.Combine(_workDir.FullName, "併發測試.txt");
        File.WriteAllText(filePath, "併發測試內容");
        var lockResult = await _service.EncryptAsync(filePath, "correct-password", null);

        var vaultManagerA = new VaultManager(_vaultDir.FullName);
        var vaultManagerB = new VaultManager(_vaultDir.FullName);
        var serviceA = new LockService(vaultManagerA);
        var serviceB = new LockService(vaultManagerB);

        var restoreDirA = Directory.CreateTempSubdirectory("FileLockerConcurrentA_");
        var restoreDirB = Directory.CreateTempSubdirectory("FileLockerConcurrentB_");
        try
        {
            var exception = await Record.ExceptionAsync(async () =>
            {
                var taskA = serviceA.DecryptByUuidAsync(lockResult.Uuid, "correct-password", restoreDirA.FullName);
                var taskB = serviceB.DecryptByUuidAsync(lockResult.Uuid, "correct-password", restoreDirB.FullName);
                await Task.WhenAll(taskA, taskB);
            });

            Assert.Null(exception); // 重點：不管誰贏，都不該有沒接住的例外跑出來

            // Vault 裡的項目最終應該已經被清掉（不管是哪一邊贏的），不會卡在半殘狀態。
            var remainingItems = new VaultManager(_vaultDir.FullName).ScanAll().ToList();
            Assert.Empty(remainingItems);
        }
        finally
        {
            restoreDirA.Delete(recursive: true);
            restoreDirB.Delete(recursive: true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var filePath in Directory.GetFiles(sourceDir))
        {
            File.Copy(filePath, Path.Combine(destinationDir, Path.GetFileName(filePath)));
        }
    }

    // ---- 對應多語言錯誤代碼系統（2026-07-24）：確認常見錯誤情境都有帶上 ErrorCode，
    // 前端才有辦法查表翻譯，不是只能顯示固定的繁體中文。 ----

    [Fact]
    public async Task DecryptAsync_WithWrongPassword_ReturnsPasswordIncorrectErrorCode()
    {
        var filePath = Path.Combine(_workDir.FullName, "錯誤代碼測試1.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "correct-password", null);

        var result = await _service.DecryptAsync(lockResult.LockedMarkerPath, "wrong-password");

        Assert.False(result.Success);
        Assert.Equal("PASSWORD_INCORRECT", result.ErrorCode);
    }

    [Fact]
    public async Task DecryptByUuidAsync_WithNonexistentUuid_ReturnsRecordNotFoundErrorCode()
    {
        var result = await _service.DecryptByUuidAsync(Guid.NewGuid().ToString(), "any-password");

        Assert.False(result.Success);
        Assert.Equal("RECORD_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task DecryptByRecoveryKeyAsync_WithInvalidFormat_ReturnsRecoveryKeyInvalidFormatErrorCode()
    {
        var filePath = Path.Combine(_workDir.FullName, "錯誤代碼測試2.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "password", null, enableRecoveryKey: true);

        var result = await _service.DecryptByRecoveryKeyAsync(lockResult.Uuid, "這不是合法的恢復金鑰格式");

        Assert.False(result.Success);
        Assert.Equal("RECOVERY_KEY_INVALID_FORMAT", result.ErrorCode);
    }

    [Fact]
    public async Task DecryptAsync_WhenLockedOut_ReturnsLockedOutErrorCodeWithRemainingSecondsAsDetail()
    {
        var filePath = Path.Combine(_workDir.FullName, "錯誤代碼測試3.txt");
        File.WriteAllText(filePath, "內容");
        var lockResult = await _service.EncryptAsync(filePath, "correct-password", null);

        for (var i = 0; i < 5; i++)
        {
            await _service.DecryptAsync(lockResult.LockedMarkerPath, "wrong-password");
        }

        var result = await _service.DecryptAsync(lockResult.LockedMarkerPath, "correct-password");

        Assert.False(result.Success);
        Assert.Equal("LOCKED_OUT", result.ErrorCode);
        Assert.True(int.TryParse(result.ErrorDetail, out var seconds) && seconds > 0);
    }
}