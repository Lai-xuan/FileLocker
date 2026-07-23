using FileLocker.Core.Settings;
using Xunit;

namespace FileLocker.Core.Tests;

public class AppSettingsManagerTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly string _filePath;

    public AppSettingsManagerTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerAppSettingsTests_");
        _filePath = Path.Combine(_tempDir.FullName, "settings.json");
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var manager = new AppSettingsManager(_filePath);

        var settings = manager.Load();

        Assert.Null(settings.VaultPath);
        Assert.Equal("zh-TW", settings.Language);
        Assert.Equal("light", settings.Theme);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsValues()
    {
        var manager = new AppSettingsManager(_filePath);
        var original = new AppSettings { VaultPath = @"D:\我的Vault", Language = "zh-TW", Theme = "dark" };

        manager.Save(original);
        var loaded = manager.Load();

        Assert.Equal(original.VaultPath, loaded.VaultPath);
        Assert.Equal(original.Theme, loaded.Theme);
    }

    [Fact]
    public void Load_WithCorruptedFile_ReturnsDefaultsInsteadOfThrowing()
    {
        File.WriteAllText(_filePath, "這不是合法的 JSON {{{");
        var manager = new AppSettingsManager(_filePath);

        var settings = manager.Load();

        Assert.Equal("zh-TW", settings.Language);
    }
}