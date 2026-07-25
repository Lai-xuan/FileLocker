using System.Collections.Concurrent;

namespace FileLocker.Core.Vault;

/// <summary>
/// 監控 Vault 資料夾內 *.meta.json 的新增/變更/刪除，把變化即時同步進 VaultIndexCache，
/// 並在一輪變化處理完後觸發 Changed 事件，讓呼叫端（App 層）可以推送通知給前端清單頁。
///
/// 兩層 debounce，關注點分離：
/// 1. 單檔 debounce——同一個路徑短時間內收到再多次事件，都只在「安靜下來」後處理一次。
/// 2. 全域通知 debounce——任何一次單檔處理完成都會重置這個計時器，批次加密/解密幾十個
///    檔案時只對外觸發一次 Changed，不會連環轟炸前端。
/// </summary>
public sealed class VaultChangeWatcher : IDisposable
{
    private readonly VaultIndexCache _indexCache;
    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _perFileDebounce;
    private readonly TimeSpan _notifyDebounce;
    private readonly ConcurrentDictionary<string, Timer> _perFileTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _notifyTimerLock = new();
    private Timer? _notifyTimer;

    /// <summary>快取已經處理完一輪變化。從背景計時器執行緒觸發，呼叫端要自己切回 UI 執行緒。</summary>
    public event EventHandler? Changed;

    public VaultChangeWatcher(
        string vaultPath,
        VaultIndexCache indexCache,
        TimeSpan? perFileDebounce = null,
        TimeSpan? notifyDebounce = null)
    {
        _indexCache = indexCache;
        _perFileDebounce = perFileDebounce ?? TimeSpan.FromMilliseconds(300);
        _notifyDebounce = notifyDebounce ?? TimeSpan.FromMilliseconds(750);

        _watcher = new FileSystemWatcher(vaultPath, "*.meta.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            // 預設 8KB 太容易在批次加密/解密大量檔案時溢位（溢位會漏事件，見 OnError）。
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnRenamedEvent;
        _watcher.Error += OnError;
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleProcessing(e.FullPath);

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        // 舊名字現在不存在了、新名字現在存在——分別排一次，自然對應到 Removed/Changed，
        // 不需要為 Renamed 額外寫一套邏輯。
        ScheduleProcessing(e.OldFullPath);
        ScheduleProcessing(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 已經確定漏事件了（通常是 InternalBufferOverflowException），唯一能保證正確的方式
        // 就是全量重掃，不值得為這個罕見情況做更複雜的處理。
        _indexCache.Rebuild();
        ScheduleNotify();
    }

    private void ScheduleProcessing(string fullPath)
    {
        // 用 Timer.Change 覆寫既有計時器來達成 debounce——每次事件都把倒數重設，
        // 而不是疊加多個計時器，安靜下來後才會真的處理一次。
        _perFileTimers.AddOrUpdate(
            fullPath,
            _ => new Timer(ProcessFile, fullPath, _perFileDebounce, Timeout.InfiniteTimeSpan),
            (_, existingTimer) =>
            {
                existingTimer.Change(_perFileDebounce, Timeout.InfiniteTimeSpan);
                return existingTimer;
            });
    }

    private void ProcessFile(object? state)
    {
        var fullPath = (string)state!;
        if (_perFileTimers.TryRemove(fullPath, out var timer))
        {
            timer.Dispose();
        }

        // 處理時重新問一次磁碟現況，而不是完全相信事件當時標記的型別——雲端同步用戶端
        // 常見「先寫暫存檔、再改名蓋掉」或「短暫建立又立刻刪除」的模式，debounce 真正
        // 觸發的當下，磁碟狀態可能已經跟事件剛發生時不一樣了。
        try
        {
            if (File.Exists(fullPath))
            {
                _indexCache.OnMetaFileChanged(fullPath);
            }
            else
            {
                _indexCache.OnMetaFileRemoved(fullPath);
            }
        }
        catch (IOException)
        {
            // 檔案可能還在被寫入/鎖定中，略過這次，下次事件到來再處理一次。
        }

        ScheduleNotify();
    }

    private void ScheduleNotify()
    {
        lock (_notifyTimerLock)
        {
            _notifyTimer ??= new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _notifyTimer.Change(_notifyDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();

        foreach (var timer in _perFileTimers.Values)
        {
            timer.Dispose();
        }
        _perFileTimers.Clear();

        lock (_notifyTimerLock)
        {
            _notifyTimer?.Dispose();
        }
    }
}
