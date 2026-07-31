using FileLocker.Core.FolderGuard;
using FileLocker.Core.Models;
using FileLocker.Core.Security;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 只測密碼路徑——Passkey 相關方法牽涉真的 Windows Hello 硬體互動，跟 PasskeyProtectorTests
/// 同樣的限制，沒辦法自動化測試（見該檔案說明），這裡的 VerifyCredentialAsync 呼叫全部維持
/// tryPasskeyFirst 預設值搭配「從未呼叫過 SetupPasskeyAsync」，PasskeyEnabled 永遠是 false，
/// 不會實際觸發任何 Windows Hello 呼叫。
/// </summary>
public class FolderGuardServiceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly FolderGuardService _service;
    private readonly List<DirectoryInfo> _guardedDirs = new();

    public FolderGuardServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerFolderGuardServiceTests_");
        var store = new FolderGuardStore(Path.Combine(_tempDir.FullName, "guarded-folders.json"));
        var lockoutTracker = new LockoutTracker(Path.Combine(_tempDir.FullName, "lockout.json"));
        _service = new FolderGuardService(store, lockoutTracker);
    }

    public void Dispose()
    {
        foreach (var dir in _guardedDirs)
        {
            if (dir.Exists && FolderGuardAcl.IsDenyRuleActive(dir.FullName))
            {
                FolderGuardAcl.RemoveDeny(dir.FullName);
            }
        }
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    private DirectoryInfo NewGuardableDir()
    {
        var dir = Directory.CreateTempSubdirectory("FileLockerGuardable_");
        _guardedDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void IsConfigured_BeforeSetup_IsFalse()
    {
        Assert.False(_service.IsConfigured);
    }

    [Fact]
    public async Task SetupCredentialAsync_ThenIsConfigured_IsTrue()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        Assert.True(_service.IsConfigured);
    }

    [Fact]
    public async Task LockFolderAsync_NonexistentPath_ReturnsPathNotFolderError()
    {
        var result = await _service.LockFolderAsync(Path.Combine(_tempDir.FullName, "不存在的資料夾"));

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FolderGuardPathNotFolder, result.ErrorCode);
    }

    [Fact]
    public async Task LockFolderAsync_ValidFolder_AppliesAclAndAddsToList()
    {
        var dir = NewGuardableDir();

        var result = await _service.LockFolderAsync(dir.FullName);

        Assert.True(result.Success);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        var list = await _service.ListAsync();
        Assert.Contains(list, e => e.Path == dir.FullName && e.Status == FolderGuardStatus.Locked);
    }

    [Fact]
    public async Task LockFolderAsync_AlreadyLocked_ReturnsAlreadyLockedError()
    {
        var dir = NewGuardableDir();
        await _service.LockFolderAsync(dir.FullName);

        var result = await _service.LockFolderAsync(dir.FullName);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FolderGuardAlreadyLocked, result.ErrorCode);
    }

    [Fact]
    public async Task UnlockFolderAsync_WithoutConfiguredCredential_ReturnsNotConfiguredError()
    {
        var dir = NewGuardableDir();
        await _service.LockFolderAsync(dir.FullName);

        var result = await _service.UnlockFolderAsync(dir.FullName, "任意密碼", IntPtr.Zero, keepInListAsUnlocked: true);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FolderGuardNotConfigured, result.ErrorCode);
    }

    [Fact]
    public async Task UnlockFolderAsync_WrongPassword_DoesNotRemoveAcl()
    {
        var dir = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dir.FullName);

        var result = await _service.UnlockFolderAsync(dir.FullName, "wrong-password", IntPtr.Zero, keepInListAsUnlocked: true);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FolderGuardPasswordIncorrect, result.ErrorCode);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
    }

    [Fact]
    public async Task UnlockFolderAsync_CorrectPassword_KeepInListTrue_MarksUnlockedInList()
    {
        var dir = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dir.FullName);

        var result = await _service.UnlockFolderAsync(dir.FullName, "correct-password", IntPtr.Zero, keepInListAsUnlocked: true);

        Assert.True(result.Success);
        Assert.False(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        var list = await _service.ListAsync();
        Assert.Contains(list, e => e.Path == dir.FullName && e.Status == FolderGuardStatus.Unlocked);
    }

    [Fact]
    public async Task UnlockFolderAsync_CorrectPassword_KeepInListFalse_RemovesFromList()
    {
        var dir = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dir.FullName);

        await _service.UnlockFolderAsync(dir.FullName, "correct-password", IntPtr.Zero, keepInListAsUnlocked: false);

        var list = await _service.ListAsync();
        Assert.DoesNotContain(list, e => e.Path == dir.FullName);
    }

    [Fact]
    public async Task UnlockAllAsync_UnlocksEveryLockedEntry()
    {
        var dirA = NewGuardableDir();
        var dirB = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dirA.FullName);
        await _service.LockFolderAsync(dirB.FullName);

        var result = await _service.UnlockAllAsync("correct-password", IntPtr.Zero);

        Assert.True(result.Success);
        Assert.False(FolderGuardAcl.IsDenyRuleActive(dirA.FullName));
        Assert.False(FolderGuardAcl.IsDenyRuleActive(dirB.FullName));
    }

    [Fact]
    public async Task RemoveFromListAsync_LockedEntry_IsNotRemoved()
    {
        var dir = NewGuardableDir();
        await _service.LockFolderAsync(dir.FullName);

        await _service.RemoveFromListAsync(dir.FullName);

        var list = await _service.ListAsync();
        Assert.Contains(list, e => e.Path == dir.FullName);
    }

    [Fact]
    public async Task RemoveFromListAsync_UnlockedEntry_IsRemoved()
    {
        var dir = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dir.FullName);
        await _service.UnlockFolderAsync(dir.FullName, "correct-password", IntPtr.Zero, keepInListAsUnlocked: true);

        await _service.RemoveFromListAsync(dir.FullName);

        var list = await _service.ListAsync();
        Assert.DoesNotContain(list, e => e.Path == dir.FullName);
    }

    [Fact]
    public async Task VerifyCredentialAsync_FiveWrongPasswords_LocksOut()
    {
        var dir = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dir.FullName);

        for (var i = 0; i < 5; i++)
        {
            await _service.UnlockFolderAsync(dir.FullName, "wrong-password", IntPtr.Zero, keepInListAsUnlocked: true);
        }

        var result = await _service.UnlockFolderAsync(dir.FullName, "correct-password", IntPtr.Zero, keepInListAsUnlocked: true);

        // 鎖定機制的鍵值是整個功能共用一把（"folder-guard-unlock"），連正確密碼都會被暫時擋下——
        // 這是刻意接受的取捨（見規劃文件第 3 節），不是 bug。
        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.FolderGuardLockedOut, result.ErrorCode);
    }

    [Fact]
    public async Task DisableAsync_ClearsConfiguration()
    {
        await _service.SetupCredentialAsync("correct-password");

        var result = await _service.DisableAsync("correct-password", IntPtr.Zero);

        Assert.True(result.Success);
        Assert.False(_service.IsConfigured);
    }

    [Fact]
    public async Task DisableAsync_WrongPassword_DoesNotDisable()
    {
        await _service.SetupCredentialAsync("correct-password");

        var result = await _service.DisableAsync("wrong-password", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.True(_service.IsConfigured);
    }

    [Fact]
    public async Task DisablePasskeyAsync_CorrectPassword_Succeeds()
    {
        await _service.SetupCredentialAsync("correct-password");

        var result = await _service.DisablePasskeyAsync("correct-password", IntPtr.Zero);

        Assert.True(result.Success);
        Assert.False(_service.IsPasskeyEnabled);
    }

    [Fact]
    public async Task DisablePasskeyAsync_WrongPassword_Fails()
    {
        await _service.SetupCredentialAsync("correct-password");

        var result = await _service.DisablePasskeyAsync("wrong-password", IntPtr.Zero);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GetLockedPathsAsync_OnlyReturnsLockedEntries()
    {
        var dirLocked = NewGuardableDir();
        var dirUnlocked = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dirLocked.FullName);
        await _service.LockFolderAsync(dirUnlocked.FullName);
        await _service.UnlockFolderAsync(dirUnlocked.FullName, "correct-password", IntPtr.Zero, keepInListAsUnlocked: true);

        var lockedPaths = await _service.GetLockedPathsAsync();

        Assert.Contains(dirLocked.FullName, lockedPaths);
        Assert.DoesNotContain(dirUnlocked.FullName, lockedPaths);
    }
}
