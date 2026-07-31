using FileLocker.Core.FolderGuard;
using Xunit;

namespace FileLocker.Core.Tests;

public class FolderGuardAclTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;

    public FolderGuardAclTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerFolderGuardAclTests_");
    }

    public void Dispose()
    {
        // Deny 規則會擋掉目前使用者自己的刪除權限，清理前一定要先移除，否則暫存資料夾刪不掉。
        if (_tempDir.Exists && FolderGuardAcl.IsDenyRuleActive(_tempDir.FullName))
        {
            FolderGuardAcl.RemoveDeny(_tempDir.FullName);
        }
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void IsDenyRuleActive_BeforeApply_IsFalse()
    {
        Assert.False(FolderGuardAcl.IsDenyRuleActive(_tempDir.FullName));
    }

    [Fact]
    public void ApplyDeny_ThenIsDenyRuleActive_IsTrue()
    {
        FolderGuardAcl.ApplyDeny(_tempDir.FullName);

        Assert.True(FolderGuardAcl.IsDenyRuleActive(_tempDir.FullName));
    }

    [Fact]
    public void ApplyDeny_ThenRemoveDeny_IsDenyRuleActive_IsFalse()
    {
        FolderGuardAcl.ApplyDeny(_tempDir.FullName);
        FolderGuardAcl.RemoveDeny(_tempDir.FullName);

        Assert.False(FolderGuardAcl.IsDenyRuleActive(_tempDir.FullName));
    }

    [Fact]
    public void IsDenyRuleActive_NonexistentFolder_ReturnsFalse()
    {
        var nonexistentPath = Path.Combine(_tempDir.FullName, "不存在的資料夾");

        Assert.False(FolderGuardAcl.IsDenyRuleActive(nonexistentPath));
    }
}
