using FileLocker.Core.Io;
using Xunit;

namespace FileLocker.Core.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerAtomicFileTests_");
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void WriteAllText_CreatesFileWithCorrectContent()
    {
        var path = Path.Combine(_tempDir.FullName, "test.json");

        AtomicFile.WriteAllText(path, "內容 A");

        Assert.True(File.Exists(path));
        Assert.Equal("內容 A", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir.FullName, "test.json");
        File.WriteAllText(path, "舊內容");

        AtomicFile.WriteAllText(path, "新內容");

        Assert.Equal("新內容", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_DoesNotLeaveTempFileBehind()
    {
        var path = Path.Combine(_tempDir.FullName, "test.json");

        AtomicFile.WriteAllText(path, "內容");

        // 目的檔案之外，資料夾裡不該留下任何 .tmp- 開頭的殘留暫存檔。
        var remainingFiles = Directory.GetFiles(_tempDir.FullName);
        Assert.Single(remainingFiles);
        Assert.Equal(path, remainingFiles[0]);
    }
}