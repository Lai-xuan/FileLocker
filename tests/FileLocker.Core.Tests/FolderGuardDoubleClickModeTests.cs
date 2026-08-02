using FileLocker.Core.FolderGuard;
using FileLocker.Core.Models;
using FileLocker.Core.Security;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 「雙擊已上鎖資料夾直接解鎖」啟用時，資料夾防護在同一層多放一個 `.lockfolder` 標記檔
/// （見 FolderGuardUnlockMarkerFile.cs）——ACL 保護強度不受影響，兩種模式都一樣強，差別只在
/// 要不要多這個標記檔。
/// </summary>
public class FolderGuardDoubleClickModeTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly FolderGuardService _service;
    private readonly List<DirectoryInfo> _guardedDirs = new();

    public FolderGuardDoubleClickModeTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerFolderGuardDoubleClickTests_");
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
            var markerPath = FolderGuardUnlockMarkerFile.GetMarkerPath(dir.FullName);
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
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
    public async Task LockFolderAsync_DoubleClickUnlockEnabled_AppliesAclAndMarker()
    {
        var dir = NewGuardableDir();
        await _service.SetDoubleClickUnlockEnabledAsync(true);

        var result = await _service.LockFolderAsync(dir.FullName);

        Assert.True(result.Success);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        Assert.True(FolderGuardUnlockMarkerFile.IsMarked(dir.FullName));
    }

    [Fact]
    public async Task LockFolderAsync_DoubleClickUnlockDisabled_AppliesAclOnlyNoMarker()
    {
        var dir = NewGuardableDir();
        await _service.SetDoubleClickUnlockEnabledAsync(false);

        var result = await _service.LockFolderAsync(dir.FullName);

        Assert.True(result.Success);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        Assert.False(FolderGuardUnlockMarkerFile.IsMarked(dir.FullName));
    }

    [Fact]
    public async Task UnlockFolderAsync_MarkerModeFolder_RemovesAclAndMarker()
    {
        var dir = NewGuardableDir();
        await _service.SetupCredentialAsync("correct-password");
        await _service.SetDoubleClickUnlockEnabledAsync(true);
        await _service.LockFolderAsync(dir.FullName);

        var result = await _service.UnlockFolderAsync(dir.FullName, "correct-password", IntPtr.Zero, keepInListAsUnlocked: true);

        Assert.True(result.Success);
        Assert.False(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        Assert.False(FolderGuardUnlockMarkerFile.IsMarked(dir.FullName));
    }

    [Fact]
    public async Task SetDoubleClickUnlockEnabledAsync_TogglingOn_AddsMarkerToAlreadyLockedFolderWithoutTouchingAcl()
    {
        var dir = NewGuardableDir();
        await _service.SetDoubleClickUnlockEnabledAsync(false);
        await _service.LockFolderAsync(dir.FullName);
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));

        await _service.SetDoubleClickUnlockEnabledAsync(true);

        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
        Assert.True(FolderGuardUnlockMarkerFile.IsMarked(dir.FullName));
    }

    [Fact]
    public async Task SetDoubleClickUnlockEnabledAsync_TogglingOff_RemovesMarkerFromAlreadyLockedFolderWithoutTouchingAcl()
    {
        var dir = NewGuardableDir();
        await _service.SetDoubleClickUnlockEnabledAsync(true);
        await _service.LockFolderAsync(dir.FullName);
        Assert.True(FolderGuardUnlockMarkerFile.IsMarked(dir.FullName));

        await _service.SetDoubleClickUnlockEnabledAsync(false);

        Assert.False(FolderGuardUnlockMarkerFile.IsMarked(dir.FullName));
        Assert.True(FolderGuardAcl.IsDenyRuleActive(dir.FullName));
    }

    [Fact]
    public async Task ListWithSelfHeal_MarkerModeLockedEntry_IsNotDropped()
    {
        var dir = NewGuardableDir();
        await _service.SetDoubleClickUnlockEnabledAsync(true);
        await _service.LockFolderAsync(dir.FullName);

        var list = await _service.ListAsync();

        Assert.Contains(list, e => e.Path == dir.FullName && e.Status == FolderGuardStatus.Locked);
    }

    [Fact]
    public void FolderGuardUnlockMarkerFile_ReadTargetFolderPath_ReturnsPathWrittenByApply()
    {
        var dir = NewGuardableDir();
        FolderGuardUnlockMarkerFile.Apply(dir.FullName);

        var target = FolderGuardUnlockMarkerFile.ReadTargetFolderPath(FolderGuardUnlockMarkerFile.GetMarkerPath(dir.FullName));

        Assert.Equal(dir.FullName, target);
    }

    [Fact]
    public void FolderGuardUnlockMarkerFile_ReadTargetFolderPath_MissingFile_ReturnsNull()
    {
        var dir = NewGuardableDir();

        var target = FolderGuardUnlockMarkerFile.ReadTargetFolderPath(FolderGuardUnlockMarkerFile.GetMarkerPath(dir.FullName));

        Assert.Null(target);
    }
}
