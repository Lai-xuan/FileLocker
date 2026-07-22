using FileLocker.Core.History;
using Xunit;

namespace FileLocker.Core.Tests;

public class HistoryLoggerTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly string _historyFilePath;
    private readonly HistoryLogger _logger;

    public HistoryLoggerTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerHistoryTests_");
        _historyFilePath = Path.Combine(_tempDir.FullName, "history.jsonl");
        _logger = new HistoryLogger(_historyFilePath);
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void ReadAll_WhenFileDoesNotExist_ReturnsEmpty()
    {
        Assert.Empty(_logger.ReadAll());
    }

    [Fact]
    public void Append_ThenReadAll_ReturnsEntryInOrder()
    {
        var uuid = Guid.NewGuid().ToString();
        _logger.Append(new HistoryEntry(uuid, "測試檔案.txt", HistoryAction.Encrypted, DateTimeOffset.UtcNow, "提示：無"));
        _logger.Append(new HistoryEntry(uuid, "測試檔案.txt", HistoryAction.Decrypted, DateTimeOffset.UtcNow, null));

        var entries = _logger.ReadAll();

        Assert.Equal(2, entries.Count);
        Assert.Equal(HistoryAction.Encrypted, entries[0].Action);
        Assert.Equal(HistoryAction.Decrypted, entries[1].Action);
    }

    [Fact]
    public void ReadAll_SkipsCorruptedLine_ButReturnsValidOnes()
    {
        var uuid = Guid.NewGuid().ToString();
        _logger.Append(new HistoryEntry(uuid, "檔案.txt", HistoryAction.Encrypted, DateTimeOffset.UtcNow, null));
        File.AppendAllText(_historyFilePath, "這不是合法的 JSON 行\n");
        _logger.Append(new HistoryEntry(uuid, "檔案.txt", HistoryAction.Decrypted, DateTimeOffset.UtcNow, null));

        var entries = _logger.ReadAll();

        Assert.Equal(2, entries.Count);
    }
}