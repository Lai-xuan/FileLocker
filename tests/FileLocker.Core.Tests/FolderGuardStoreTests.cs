using FileLocker.Core.FolderGuard;
using Xunit;

namespace FileLocker.Core.Tests;

public class FolderGuardStoreTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly FolderGuardStore _store;
    private DirectoryInfo? _guardedDir;

    public FolderGuardStoreTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerFolderGuardStoreTests_");
        _store = new FolderGuardStore(Path.Combine(_tempDir.FullName, "guarded-folders.json"));
    }

    public void Dispose()
    {
        if (_guardedDir is { Exists: true } && FolderGuardAcl.IsDenyRuleActive(_guardedDir.FullName))
        {
            FolderGuardAcl.RemoveDeny(_guardedDir.FullName);
        }
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void ListWithSelfHeal_EntryWithMatchingAcl_IsKept()
    {
        _guardedDir = Directory.CreateTempSubdirectory("FileLockerGuardedTests_");
        FolderGuardAcl.ApplyDeny(_guardedDir.FullName);

        var data = _store.Load();
        data.Entries.Add(new FolderGuardEntry { Path = _guardedDir.FullName, Status = FolderGuardStatus.Locked, LockedAtUtc = DateTime.UtcNow });
        _store.Save(data);

        var result = _store.ListWithSelfHeal();

        Assert.Single(result);
        Assert.Equal(_guardedDir.FullName, result[0].Path);
    }

    [Fact]
    public void ListWithSelfHeal_LockedEntryWithoutActualAcl_IsRemovedAndSavedBack()
    {
        // 模擬使用者自己在檔案總管把權限改回來的情境：索引裡有記錄，但實際上 ACL 早就不在了。
        var externallyModifiedDir = Directory.CreateTempSubdirectory("FileLockerExternallyModified_");
        try
        {
            var data = _store.Load();
            data.Entries.Add(new FolderGuardEntry { Path = externallyModifiedDir.FullName, Status = FolderGuardStatus.Locked, LockedAtUtc = DateTime.UtcNow });
            _store.Save(data);

            var result = _store.ListWithSelfHeal();

            Assert.Empty(result);
            Assert.Empty(_store.Load().Entries); // 自我修復要同步寫回，不只是回傳值濾掉而已
        }
        finally
        {
            externallyModifiedDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ListWithSelfHeal_LockedEntryForDeletedFolder_IsRemoved()
    {
        var deletedPath = Path.Combine(_tempDir.FullName, "已經不存在的資料夾");

        var data = _store.Load();
        data.Entries.Add(new FolderGuardEntry { Path = deletedPath, Status = FolderGuardStatus.Locked, LockedAtUtc = DateTime.UtcNow });
        _store.Save(data);

        var result = _store.ListWithSelfHeal();

        Assert.Empty(result);
    }

    [Fact]
    public void ListWithSelfHeal_UnlockedEntry_IsKeptRegardlessOfAcl()
    {
        var data = _store.Load();
        data.Entries.Add(new FolderGuardEntry
        {
            Path = Path.Combine(_tempDir.FullName, "任意路徑"),
            Status = FolderGuardStatus.Unlocked,
            LockedAtUtc = DateTime.UtcNow,
            UnlockedAtUtc = DateTime.UtcNow
        });
        _store.Save(data);

        var result = _store.ListWithSelfHeal();

        Assert.Single(result);
    }

    [Fact]
    public void FolderGuardData_DefaultValues_AutoRelockEnabledTrueAndMinutes15()
    {
        var data = new FolderGuardData();

        Assert.True(data.AutoRelockEnabled);
        Assert.Equal(15, data.AutoRelockMinutes);
    }

    [Fact]
    public void Load_OldFormatJsonWithoutAutoRelockFields_FallsBackToDefaults()
    {
        // 模擬升級前就存在的 guarded-folders.json：完全沒有 AutoRelockEnabled/AutoRelockMinutes
        // 這兩個鍵，驗證舊使用者升級後不會因為缺欄位而讀出奇怪的值（例如 bool 預設 false、
        // int 預設 0），而是拿到跟全新安裝一樣的預設值。
        var filePath = Path.Combine(_tempDir.FullName, "guarded-folders.json");
        File.WriteAllText(filePath, """
            {
              "PasswordSaltBase64": null,
              "PasswordVerificationHashBase64": null,
              "PasskeyEnabled": false,
              "PasskeyCredentialName": null,
              "DoubleClickUnlockEnabled": false,
              "Entries": []
            }
            """);
        var oldFormatStore = new FolderGuardStore(filePath);

        var data = oldFormatStore.Load();

        Assert.True(data.AutoRelockEnabled);
        Assert.Equal(15, data.AutoRelockMinutes);
    }
}
