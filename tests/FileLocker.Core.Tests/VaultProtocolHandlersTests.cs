using FileLocker.Core.History;
using FileLocker.Core.Protocol;
using FileLocker.Core.Security;
using FileLocker.Core.Settings;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應架構審查（2026-07-26）：這些測試就是「拆開 MainWindow 的協定分派層」這項深化的驗證——
/// VaultProtocolHandlers 不依賴任何 WPF／WebView2 具體型別，這裡完全不用開真的視窗就能測試
/// 「解析請求 → 呼叫 Core 業務邏輯 → 組裝回應」這一整層，這在拆分之前是做不到的。
/// </summary>
public class VaultProtocolHandlersTests : IDisposable
{
    private readonly DirectoryInfo _vaultDir;
    private readonly DirectoryInfo _cacheDir;
    private readonly DirectoryInfo _workDir;
    private readonly DirectoryInfo _historyDir;
    private readonly VaultManager _vaultManager;
    private readonly VaultIndexCache _vaultIndexCache;
    private readonly VaultProtocolHandlers _handlers;

    public VaultProtocolHandlersTests()
    {
        _vaultDir = Directory.CreateTempSubdirectory("FileLockerVault_");
        _cacheDir = Directory.CreateTempSubdirectory("FileLockerCache_");
        _workDir = Directory.CreateTempSubdirectory("FileLockerWork_");
        _historyDir = Directory.CreateTempSubdirectory("FileLockerHistory_");

        _vaultManager = new VaultManager(_vaultDir.FullName);
        _vaultIndexCache = new VaultIndexCache(_vaultManager, _cacheDir.FullName);

        var history = new HistoryLogger(Path.Combine(_historyDir.FullName, "history.jsonl"));
        var lockout = new LockoutTracker(Path.Combine(_historyDir.FullName, "lockout.json"));
        var lockService = new LockService(_vaultManager, history, lockout);
        var settingsManager = new AppSettingsManager(Path.Combine(_historyDir.FullName, "settings.json"));
        var settings = new AppSettings { VaultPath = _vaultDir.FullName };

        _handlers = new VaultProtocolHandlers(_vaultManager, lockService, _vaultIndexCache, history, settingsManager, settings);
    }

    public void Dispose()
    {
        _vaultIndexCache.Dispose();

        if (_vaultDir.Exists) _vaultDir.Delete(recursive: true);
        if (_cacheDir.Exists) _cacheDir.Delete(recursive: true);
        if (_workDir.Exists) _workDir.Delete(recursive: true);
        if (_historyDir.Exists) _historyDir.Delete(recursive: true);
    }

    private string CreateWorkFile(string name, string content)
    {
        var path = Path.Combine(_workDir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task EncryptBatchAsync_YieldsOneResultPerPath()
    {
        var pathA = CreateWorkFile("甲.txt", "內容甲");
        var pathB = CreateWorkFile("乙.txt", "內容乙");

        var results = new List<EncryptItemResponse>();
        await foreach (var item in _handlers.EncryptBatchAsync([pathA, pathB], "correct-password", null, false, false, IntPtr.Zero))
        {
            results.Add(item);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Contains(results, r => r.Path == pathA);
        Assert.Contains(results, r => r.Path == pathB);
    }

    [Fact]
    public async Task ListVaultAsync_AfterEncrypt_ReturnsItemWithMarkerFound()
    {
        var path = CreateWorkFile("清單測試.txt", "測試內容");
        await foreach (var _ in _handlers.EncryptBatchAsync([path], "correct-password", "提示", false, false, IntPtr.Zero)) { }

        // 這個測試沒有接 VaultChangeWatcher（那是即時監控 Vault 變化用的，見另一份測試），
        // 快取不會自動發現剛才新寫入的 .meta.json，手動 Rebuild 一次模擬 watcher 本來會做的事。
        _vaultIndexCache.Rebuild();
        var items = await _handlers.ListVaultAsync();

        Assert.Single(items);
        Assert.Equal("清單測試.txt", items[0].OriginalName);
        Assert.True(items[0].MarkerFound);
        Assert.Equal("提示", items[0].Hint);
    }

    [Fact]
    public async Task ListHistory_AfterEncrypt_RecordsEncryptedEntry()
    {
        var path = CreateWorkFile("紀錄測試.txt", "測試內容");
        await foreach (var _ in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero)) { }

        var entries = _handlers.ListHistory();

        Assert.Contains(entries, e => e.OriginalName == "紀錄測試.txt" && e.Action == "Encrypted");
    }

    [Fact]
    public async Task DeleteRecordAsync_RemovesItemFromSubsequentListing()
    {
        var path = CreateWorkFile("刪除測試.txt", "測試內容");
        EncryptItemResponse? encrypted = null;
        await foreach (var item in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero))
        {
            encrypted = item;
        }

        var deleteResult = await _handlers.DeleteRecordAsync(encrypted!.Uuid);
        Assert.True(deleteResult.Success);

        var items = await _handlers.ListVaultAsync();
        Assert.Empty(items);
    }

    [Fact]
    public async Task InspectLockedFile_ForValidMarker_ReturnsMetadataInfo()
    {
        var path = CreateWorkFile("檢視測試.txt", "測試內容");
        EncryptItemResponse? encrypted = null;
        await foreach (var item in _handlers.EncryptBatchAsync([path], "correct-password", "我的提示", false, false, IntPtr.Zero))
        {
            encrypted = item;
        }

        var result = _handlers.InspectLockedFile(encrypted!.LockedMarkerPath);

        Assert.True(result.Success);
        Assert.Equal(encrypted.Uuid, result.Uuid);
        Assert.Equal("檢視測試.txt", result.OriginalName);
        Assert.Equal("我的提示", result.Hint);
    }

    [Fact]
    public void InspectLockedFile_ForNonexistentPath_ReturnsFailure()
    {
        var result = _handlers.InspectLockedFile(Path.Combine(_workDir.FullName, "不存在.locked"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GetPathSizesAsync_ForExistingFile_ReturnsActualByteCount()
    {
        var path = CreateWorkFile("大小測試.txt", "1234567890");

        var results = await _handlers.GetPathSizesAsync([path]);

        Assert.Single(results);
        Assert.Equal(10, results[0].Bytes);
        Assert.False(results[0].IsFolder);
    }

    [Fact]
    public async Task GetPathSizesAsync_ForMissingPath_ReturnsZeroInsteadOfThrowing()
    {
        var results = await _handlers.GetPathSizesAsync([Path.Combine(_workDir.FullName, "不存在的檔案.txt")]);

        Assert.Single(results);
        Assert.Equal(0, results[0].Bytes);
    }

    [Fact]
    public void GetSettings_ReturnsConfiguredValues()
    {
        var result = _handlers.GetSettings();

        Assert.Equal(_vaultDir.FullName, result.VaultPath);
        Assert.Equal("zh-TW", result.Language);
    }

    [Fact]
    public void UpdateSetting_Language_PersistsChange()
    {
        var result = _handlers.UpdateSetting("language", "en");

        Assert.True(result.Success);
        Assert.Equal("en", _handlers.GetSettings().Language);
    }

    [Fact]
    public void UpdateSetting_UnknownKey_ReturnsFailureAndDoesNotChangeSettings()
    {
        var before = _handlers.GetSettings();

        var result = _handlers.UpdateSetting("unknownKey", "whatever");

        Assert.False(result.Success);
        Assert.Equal(before.Language, _handlers.GetSettings().Language);
    }

    [Fact]
    public async Task ChangeVaultPathAsync_MovesExistingItemsToNewLocation()
    {
        var path = CreateWorkFile("搬移測試.txt", "測試內容");
        await foreach (var _ in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero)) { }

        var newVaultDir = Directory.CreateTempSubdirectory("FileLockerVaultMoved_");
        try
        {
            Directory.Delete(newVaultDir.FullName); // ChangeVaultPathAsync 只接受不存在或空的目的地

            var result = await _handlers.ChangeVaultPathAsync(newVaultDir.FullName);

            Assert.True(result.Success);
            Assert.True(Directory.Exists(newVaultDir.FullName));
            Assert.True(new VaultManager(newVaultDir.FullName).ScanAll().Any());
        }
        finally
        {
            if (newVaultDir.Exists) newVaultDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ChangeVaultPathAsync_SamePath_ReturnsFailureWithoutMoving()
    {
        var result = await _handlers.ChangeVaultPathAsync(_vaultDir.FullName);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
