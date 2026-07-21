using FileLocker.Core.SecureDelete;
using Xunit;

namespace FileLocker.Core.Tests;

public class SecureFileEraserTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;

    public SecureFileEraserTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerEraserTests_");
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void OverwriteAndDelete_RemovesFileFromDisk()
    {
        var filePath = Path.Combine(_tempDir.FullName, "secret.txt");
        File.WriteAllText(filePath, "這是需要被安全刪除的明文內容");

        SecureFileEraser.OverwriteAndDelete(filePath);

        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void OverwriteAndDelete_NonexistentFile_DoesNotThrow()
    {
        var nonexistentPath = Path.Combine(_tempDir.FullName, "不存在.txt");

        var exception = Record.Exception(() => SecureFileEraser.OverwriteAndDelete(nonexistentPath));

        Assert.Null(exception);
    }

    [Fact]
    public void OverwriteAndDelete_EmptyFile_DoesNotThrowAndRemovesFile()
    {
        var filePath = Path.Combine(_tempDir.FullName, "empty.txt");
        File.WriteAllBytes(filePath, Array.Empty<byte>());

        var exception = Record.Exception(() => SecureFileEraser.OverwriteAndDelete(filePath));

        Assert.Null(exception);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public void OverwriteAndDeleteFolder_RemovesFolderAndAllNestedContents()
    {
        var subDir = Directory.CreateDirectory(Path.Combine(_tempDir.FullName, "資料夾"));
        File.WriteAllText(Path.Combine(subDir.FullName, "a.txt"), "內容 A");
        var nestedDir = Directory.CreateDirectory(Path.Combine(subDir.FullName, "nested"));
        File.WriteAllText(Path.Combine(nestedDir.FullName, "b.txt"), "內容 B");

        SecureFileEraser.OverwriteAndDeleteFolder(subDir.FullName);

        Assert.False(Directory.Exists(subDir.FullName));
    }

    [Fact]
    public void OverwriteAndDeleteFolder_NonexistentFolder_DoesNotThrow()
    {
        var nonexistentPath = Path.Combine(_tempDir.FullName, "不存在的資料夾");

        var exception = Record.Exception(() => SecureFileEraser.OverwriteAndDeleteFolder(nonexistentPath));

        Assert.Null(exception);
    }
}