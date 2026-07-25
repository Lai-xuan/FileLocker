using System.Text.Json;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Tests;

public class VaultChangeWatcherTests : IDisposable
{
    // 覆寫成很短的 debounce 值，搭配逾時輪詢斷言而非固定 sleep，盡量降低機器負載造成的不穩定
    // ——這類涉及計時的測試先天比純邏輯測試容易偶爾變慢，是明確接受的取捨。
    private static readonly TimeSpan PerFileDebounce = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan NotifyDebounce = TimeSpan.FromMilliseconds(80);

    private readonly DirectoryInfo _tempVaultDir;
    private readonly DirectoryInfo _tempCacheDir;
    private readonly VaultManager _vault;
    private readonly VaultIndexCache _cache;
    private readonly VaultChangeWatcher _watcher;

    public VaultChangeWatcherTests()
    {
        _tempVaultDir = Directory.CreateTempSubdirectory("FileLockerVaultTests_");
        _tempCacheDir = Directory.CreateTempSubdirectory("FileLockerCacheTests_");
        _vault = new VaultManager(_tempVaultDir.FullName);
        _cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        _watcher = new VaultChangeWatcher(_tempVaultDir.FullName, _cache, PerFileDebounce, NotifyDebounce);
        _watcher.Start();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _cache.Dispose();

        if (_tempVaultDir.Exists)
        {
            _tempVaultDir.Delete(recursive: true);
        }

        if (_tempCacheDir.Exists)
        {
            _tempCacheDir.Delete(recursive: true);
        }
    }

    private static LockedItemMetadata CreateSampleMetadata(string uuid) => new()
    {
        Uuid = uuid,
        OriginalName = "測試檔案.txt",
        OriginalPath = @"C:\Users\test\Documents\測試檔案.txt",
        PasswordVerificationHash = "dummyHashBase64==",
        Salt = "dummySaltBase64==",
        Argon2TimeCost = 3,
        Argon2MemoryCostKb = 65536,
        Argon2Parallelism = 2,
        Hint = "測試提示",
        Type = ItemType.File,
        OriginalSizeBytes = 1024,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    /// <summary>等到 Changed 事件觸發、或逾時；用輪詢等待而非固定 sleep 後單次斷言。</summary>
    private async Task<bool> WaitForChangedAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>();
        void Handler(object? sender, EventArgs e) => tcs.TrySetResult(true);

        _watcher.Changed += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return completed == tcs.Task;
        }
        finally
        {
            _watcher.Changed -= Handler;
        }
    }

    [Fact]
    public async Task RapidSuccessiveWritesToSameFile_OnlyProcessedOnce()
    {
        var uuid = Guid.NewGuid().ToString();
        var metaPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");

        for (var i = 0; i < 5; i++)
        {
            var metadata = CreateSampleMetadata(uuid);
            metadata.Hint = $"第 {i} 次寫入";
            File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata));
            await Task.Delay(5); // 遠小於 PerFileDebounce，確保這些事件會被視為同一輪安靜下來後才處理
        }

        var raised = await WaitForChangedAsync(TimeSpan.FromSeconds(2));

        Assert.True(raised);
        var items = _cache.GetItems();
        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }

    [Fact]
    public async Task BurstOfManyFileChanges_RaisesChangedEventExactlyOnce()
    {
        var raisedCount = 0;
        void CountHandler(object? sender, EventArgs e) => Interlocked.Increment(ref raisedCount);
        _watcher.Changed += CountHandler;

        try
        {
            for (var i = 0; i < 15; i++)
            {
                _vault.SaveMetadata(CreateSampleMetadata(Guid.NewGuid().ToString()));
            }

            // 等到「安靜下來」的通知 debounce 視窗過去，再多留一點緩衝時間。
            await Task.Delay(NotifyDebounce + TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            _watcher.Changed -= CountHandler;
        }

        Assert.Equal(1, raisedCount);
        Assert.Equal(15, _cache.GetItems().Count);
    }

    [Fact]
    public async Task DeleteThenRecreateWithinDebounceWindow_EndsUpConsistentWithFinalDiskState()
    {
        var uuid = Guid.NewGuid().ToString();
        var metaPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        await WaitForChangedAsync(TimeSpan.FromSeconds(2));
        Assert.Single(_cache.GetItems());

        // debounce 視窗內刪除又重建：處理當下重新問磁碟現況的設計，應該讓最終結果收斂到
        // 「磁碟上現在真的存在」這個狀態，而不是被中間某個瞬間的事件型別誤導。
        File.Delete(metaPath);
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        var raised = await WaitForChangedAsync(TimeSpan.FromSeconds(2));

        Assert.True(raised);
        var items = _cache.GetItems();
        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }
}
