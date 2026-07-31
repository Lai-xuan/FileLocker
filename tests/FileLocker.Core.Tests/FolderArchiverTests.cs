using FileLocker.Core.FolderPackaging;
using Xunit;

namespace FileLocker.Core.Tests;

public class FolderArchiverTests : IDisposable
{
    private readonly DirectoryInfo _sourceDir;
    private readonly DirectoryInfo _extractDir;
    private string? _createdZipPath;

    public FolderArchiverTests()
    {
        _sourceDir = Directory.CreateTempSubdirectory("FileLockerSourceTests_");
        _extractDir = Directory.CreateTempSubdirectory("FileLockerExtractTests_");
    }

    public void Dispose()
    {
        if (_sourceDir.Exists) _sourceDir.Delete(recursive: true);
        if (_extractDir.Exists) _extractDir.Delete(recursive: true);
        if (_createdZipPath is not null && File.Exists(_createdZipPath)) File.Delete(_createdZipPath);
    }

    [Fact]
    public void CompressToTempZip_ThenExtract_RoundTripsFolderContents()
    {
        // 建立一個含子資料夾的來源結構，確保壓縮/解壓縮連巢狀結構都能正確還原。
        File.WriteAllText(Path.Combine(_sourceDir.FullName, "root.txt"), "root 檔案內容");
        var subDir = Directory.CreateDirectory(Path.Combine(_sourceDir.FullName, "subfolder"));
        File.WriteAllText(Path.Combine(subDir.FullName, "nested.txt"), "巢狀檔案內容");

        _createdZipPath = FolderArchiver.CompressToTempZip(_sourceDir.FullName);
        FolderArchiver.ExtractZipToFolder(_createdZipPath, _extractDir.FullName);

        Assert.True(File.Exists(Path.Combine(_extractDir.FullName, "root.txt")));
        Assert.Equal("root 檔案內容", File.ReadAllText(Path.Combine(_extractDir.FullName, "root.txt")));
        Assert.True(File.Exists(Path.Combine(_extractDir.FullName, "subfolder", "nested.txt")));
        Assert.Equal("巢狀檔案內容", File.ReadAllText(Path.Combine(_extractDir.FullName, "subfolder", "nested.txt")));
    }

    [Fact]
    public void CompressToTempZip_NonexistentFolder_ThrowsDirectoryNotFoundException()
    {
        var nonexistentPath = Path.Combine(_sourceDir.FullName, "不存在的資料夾");

        Assert.Throws<DirectoryNotFoundException>(() => FolderArchiver.CompressToTempZip(nonexistentPath));
    }

    [Fact]
    public void FindNestedLockedFiles_FindsLockedFilesRecursively()
    {
        // 對應規格文件 3.2 節巢狀鎖定情境：資料夾內、以及更深一層子資料夾內都各放一個 .locked 檔案。
        File.WriteAllText(Path.Combine(_sourceDir.FullName, "已鎖定項目.locked"), "{}");
        var subDir = Directory.CreateDirectory(Path.Combine(_sourceDir.FullName, "subfolder"));
        File.WriteAllText(Path.Combine(subDir.FullName, "深層鎖定項目.locked"), "{}");
        File.WriteAllText(Path.Combine(_sourceDir.FullName, "普通檔案.txt"), "not locked");

        var found = FolderArchiver.FindNestedLockedFiles(_sourceDir.FullName);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.EndsWith("已鎖定項目.locked"));
        Assert.Contains(found, p => p.EndsWith("深層鎖定項目.locked"));
    }

    [Fact]
    public void FindNestedLockedFiles_NoLockedFiles_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_sourceDir.FullName, "普通檔案.txt"), "not locked");

        var found = FolderArchiver.FindNestedLockedFiles(_sourceDir.FullName);

        Assert.Empty(found);
    }

    [Fact]
    public void FindNestedGuardedFolders_GuardedPathInsideTree_IsFound()
    {
        var subDir = Directory.CreateDirectory(Path.Combine(_sourceDir.FullName, "subfolder"));
        var guardedPaths = new[] { subDir.FullName, @"C:\完全不相關的路徑" };

        var found = FolderArchiver.FindNestedGuardedFolders(_sourceDir.FullName, guardedPaths);

        Assert.Single(found);
        Assert.Equal(subDir.FullName, found[0]);
    }

    [Fact]
    public void FindNestedGuardedFolders_TheFolderItselfIsGuarded_IsNotIncluded()
    {
        // 要加密的資料夾本身如果就是防護中的資料夾，那是另一個情境（加密流程自己就會因為讀不到
        // 內容而失敗），不算是「巢狀」——這個方法只回報「裡面」的防護資料夾。
        var found = FolderArchiver.FindNestedGuardedFolders(_sourceDir.FullName, new[] { _sourceDir.FullName });

        Assert.Empty(found);
    }

    [Fact]
    public void FindNestedGuardedFolders_NoGuardedPaths_ReturnsEmpty()
    {
        var found = FolderArchiver.FindNestedGuardedFolders(_sourceDir.FullName, Array.Empty<string>());

        Assert.Empty(found);
    }
}