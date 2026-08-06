using FileLocker.Core.FolderGuard;
using FileLocker.Core.Security;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 「解鎖後閒置自動重新上鎖」——閒置定義是解鎖後經過的牆鐘時間（FolderGuardEntry.UnlockedAtUtc
/// 起算），不是真正的系統輸入閒置偵測。RelockExpiredEntriesAsync 是計時器（App.xaml.cs）跟
/// 啟動補跑共用的同一個核心判斷方法，這裡直接測這個方法，不牽涉 WPF 的 DispatcherTimer。
/// </summary>
public class FolderGuardAutoRelockTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly FolderGuardStore _store;
    private readonly FolderGuardService _service;
    private readonly List<DirectoryInfo> _guardedDirs = new();

    public FolderGuardAutoRelockTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerFolderGuardAutoRelockTests_");
        _store = new FolderGuardStore(Path.Combine(_tempDir.FullName, "guarded-folders.json"));
        var lockoutTracker = new LockoutTracker(Path.Combine(_tempDir.FullName, "lockout.json"));
        _service = new FolderGuardService(_store, lockoutTracker);
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

    /// <summary>鎖定→解鎖走正常流程產生一筆 Unlocked 紀錄，再直接改寫 store 裡的 UnlockedAtUtc
    /// 往前推，模擬「已經解鎖超過 N 分鐘」，不用真的等待時間流逝。</summary>
    private async Task<DirectoryInfo> LockThenUnlockWithBackdatedTimestampAsync(TimeSpan elapsedSinceUnlock)
    {
        var dir = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.LockFolderAsync(dir.FullName);
        await _service.UnlockFolderAsync(dir.FullName, "correct-password", IntPtr.Zero, keepInListAsUnlocked: true);

        var data = _store.Load();
        var entry = data.Entries.Single(e => e.Path == dir.FullName);
        entry.UnlockedAtUtc = DateTime.UtcNow - elapsedSinceUnlock;
        _store.Save(data);

        return dir;
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_EntryUnlockedPastThreshold_IsRelocked()
    {
        await _service.SetAutoRelockAsync(true, 15);
        var dir = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(20));

        var relocked = await _service.RelockExpiredEntriesAsync();

        Assert.Contains(dir.FullName, relocked);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        var entry = (await _service.ListAsync()).Single(e => e.Path == dir.FullName);
        Assert.Equal(FolderGuardStatus.Locked, entry.Status);
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_EntryUnlockedWithinThreshold_IsUntouched()
    {
        await _service.SetAutoRelockAsync(true, 15);
        var dir = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(5));

        var relocked = await _service.RelockExpiredEntriesAsync();

        Assert.Empty(relocked);
        Assert.False(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        var entry = (await _service.ListAsync()).Single(e => e.Path == dir.FullName);
        Assert.Equal(FolderGuardStatus.Unlocked, entry.Status);
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_AutoRelockDisabled_NothingHappens()
    {
        await _service.SetAutoRelockAsync(false, 15);
        var dir = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(999));

        var relocked = await _service.RelockExpiredEntriesAsync();

        Assert.Empty(relocked);
        Assert.False(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_MultipleEntries_HandledIndependently()
    {
        await _service.SetAutoRelockAsync(true, 15);
        var expiredDir = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(30));
        var freshDir = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(1));
        var stillLockedDir = NewGuardableDir();
        await _service.LockFolderAsync(stillLockedDir.FullName);

        var relocked = await _service.RelockExpiredEntriesAsync();

        Assert.Equal([expiredDir.FullName], relocked);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(expiredDir.FullName));
        Assert.False(FolderGuardAcl.IsDenyRuleActive(freshDir.FullName));
        Assert.True(FolderGuardAcl.IsDenyRuleActive(stillLockedDir.FullName));
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_MethodIsIdempotent_CalledTwiceInARow_SecondCallIsNoOp()
    {
        await _service.SetAutoRelockAsync(true, 15);
        var dir = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(20));

        var first = await _service.RelockExpiredEntriesAsync();
        var second = await _service.RelockExpiredEntriesAsync();

        Assert.Single(first);
        Assert.Empty(second);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_ReturnsRelockedPaths_ForNotificationCaller()
    {
        await _service.SetAutoRelockAsync(true, 15);
        var dirA = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(30));
        var dirB = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(45));

        var relocked = await _service.RelockExpiredEntriesAsync();

        Assert.Equal(2, relocked.Count);
        Assert.Contains(dirA.FullName, relocked);
        Assert.Contains(dirB.FullName, relocked);
    }

    [Fact]
    public async Task SetAutoRelockAsync_PersistsEnabledAndMinutes_RoundTripsThroughNewStoreInstance()
    {
        await _service.SetAutoRelockAsync(false, 42);

        var reloadedStore = new FolderGuardStore(Path.Combine(_tempDir.FullName, "guarded-folders.json"));
        var data = reloadedStore.Load();

        Assert.False(data.AutoRelockEnabled);
        Assert.Equal(42, data.AutoRelockMinutes);
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_RaisesEntriesAutoRelockedEvent_WithRelockedPaths()
    {
        await _service.SetAutoRelockAsync(true, 15);
        var dir = await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(20));

        IReadOnlyList<string>? raisedPaths = null;
        _service.EntriesAutoRelocked += (_, paths) => raisedPaths = paths;

        await _service.RelockExpiredEntriesAsync();

        Assert.NotNull(raisedPaths);
        Assert.Equal([dir.FullName], raisedPaths);
    }

    [Fact]
    public async Task RelockExpiredEntriesAsync_NoExpiredEntries_DoesNotRaiseEvent()
    {
        await _service.SetAutoRelockAsync(true, 15);
        await LockThenUnlockWithBackdatedTimestampAsync(TimeSpan.FromMinutes(1));

        var raised = false;
        _service.EntriesAutoRelocked += (_, _) => raised = true;

        await _service.RelockExpiredEntriesAsync();

        Assert.False(raised);
    }
}
