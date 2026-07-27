using System.Security.Cryptography;
using System.Text.Json;
using FileLocker.Core.Crypto;
using FileLocker.Core.FolderPackaging;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.Settings;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Protocol;

/// <summary>
/// 對應「MainWindow 分派層」架構審查（2026-07-26）：把「解析前端請求 → 呼叫 Core 業務邏輯 →
/// 組裝回應」這一層從 MainWindow.xaml.cs 抽出來，讓它不依賴任何 WPF／WebView2 具體型別，可以
/// 直接用單元測試驗證（見 VaultProtocolHandlersTests），不需要真的開一個視窗。
///
/// 平台相關的東西（開檔案/資料夾對話框、視窗控制代碼取得、拖放的 CoreWebView2File、視窗最大化/
/// 最小化/關閉）留在 MainWindow——這裡只吃已經拿到的資料（例如 ownerWindowHandle）當參數，
/// 不自己去問視窗要。加密/批次解密這種要邊做邊回報進度的，回傳 IAsyncEnumerable，讓呼叫端
/// 自己決定要不要每完成一筆就送一次 WebView2 訊息，這裡完全不知道「WebView2 訊息」這件事的存在。
/// </summary>
public sealed class VaultProtocolHandlers
{
    private readonly VaultManager _vaultManager;
    private readonly LockService _lockService;
    private readonly VaultIndexCache _vaultIndexCache;
    private readonly HistoryLogger _historyLogger;
    private readonly AppSettingsManager _settingsManager;
    private readonly AppSettings _settings;

    public VaultProtocolHandlers(
        VaultManager vaultManager, LockService lockService, VaultIndexCache vaultIndexCache,
        HistoryLogger historyLogger, AppSettingsManager settingsManager, AppSettings settings)
    {
        _vaultManager = vaultManager;
        _lockService = lockService;
        _vaultIndexCache = vaultIndexCache;
        _historyLogger = historyLogger;
        _settingsManager = settingsManager;
        _settings = settings;
    }

    public async IAsyncEnumerable<EncryptItemResponse> EncryptBatchAsync(
        IReadOnlyList<string> paths, string password, string? hint,
        bool enablePasskey, bool enableRecoveryKey, IntPtr ownerWindowHandle)
    {
        // 選了不只一個項目才需要分組——單一項目沒有「摺疊」的意義，維持 batchId = null。
        var batchId = paths.Count > 1 ? Guid.NewGuid().ToString() : null;

        foreach (var path in paths)
        {
            var result = await _lockService.EncryptAsync(
                path, password, string.IsNullOrWhiteSpace(hint) ? null : hint,
                enablePasskey, ownerWindowHandle, enableRecoveryKey, batchId);

            var actuallyPasskeyEnabled = false;
            if (result.Success)
            {
                actuallyPasskeyEnabled = _vaultManager.LoadMetadata(result.Uuid)?.PasskeyEnabled ?? false;
            }

            yield return new EncryptItemResponse(path, result, enablePasskey, actuallyPasskeyEnabled);
        }
    }

    public Task<UnlockResult> DecryptAsync(string lockedMarkerPath, string password)
        => _lockService.DecryptAsync(lockedMarkerPath, password);

    public Task<UnlockResult> DecryptByUuidAsync(string uuid, string password, string? destinationDir)
        => _lockService.DecryptByUuidAsync(uuid, password, destinationDir);

    public Task<UnlockResult> DecryptByPasskeyAsync(string uuid, IntPtr ownerWindowHandle, string? destinationDir)
        => _lockService.DecryptByPasskeyAsync(uuid, ownerWindowHandle, destinationDir);

    public Task<UnlockResult> DecryptByRecoveryKeyAsync(string uuid, string recoveryKeyInput, string? destinationDir)
        => _lockService.DecryptByRecoveryKeyAsync(uuid, recoveryKeyInput, destinationDir);

    /// <summary>對應「已加密清單」頁摺疊群組的「全部解鎖」按鈕，只支援密碼、逐一解密。</summary>
    public async IAsyncEnumerable<DecryptBatchItemResponse> DecryptBatchAsync(IReadOnlyList<string> uuids, string password)
    {
        foreach (var uuid in uuids)
        {
            var result = await _lockService.DecryptByUuidAsync(uuid, password);
            yield return new DecryptBatchItemResponse(uuid, result);
        }
    }

    /// <summary>
    /// decryptByPasskey／decryptByRecoveryKey 共用：優先用前端明確指定的 destinationDir；
    /// 沒有的話，若前端傳了 markerPath（例如「解密」頁籤選了 .locked 檔案的情境），用該檔案
    /// 目前所在的資料夾當還原位置，維持跟密碼路徑一致的行為。
    /// </summary>
    public static string? ResolveDestinationDirFromRequest(JsonElement request)
    {
        if (request.TryGetProperty("destinationDir", out var destProp) && destProp.ValueKind == JsonValueKind.String)
        {
            return destProp.GetString();
        }

        if (request.TryGetProperty("markerPath", out var markerProp) && markerProp.ValueKind == JsonValueKind.String)
        {
            var markerPath = markerProp.GetString();
            if (!string.IsNullOrEmpty(markerPath))
            {
                return Path.GetDirectoryName(Path.GetFullPath(markerPath));
            }
        }

        return null;
    }

    /// <summary>
    /// 對應「解密」頁籤：使用者選好 .locked 檔案後，查一下這個項目除了密碼之外，還有沒有開
    /// Passkey／恢復金鑰，讓前端可以動態顯示對應的按鈕。這裡只讀 marker 拿 UUID、查 metadata，
    /// 不驗證簽章——純粹是為了顯示資訊，真正的安全驗證在使用者實際選擇某條解鎖路徑時才會發生。
    /// </summary>
    public InspectLockedFileResponse InspectLockedFile(string path)
    {
        var marker = LockedMarkerFile.ReadFrom(path);
        if (marker is null)
        {
            return new InspectLockedFileResponse(false, null, null, null, false, false);
        }

        var metadata = _vaultManager.LoadMetadata(marker.Uuid);
        return new InspectLockedFileResponse(
            metadata is not null, marker.Uuid, metadata?.OriginalName, metadata?.Hint,
            metadata?.PasskeyEnabled ?? false, metadata?.RecoveryKeyEnabled ?? false);
    }

    /// <summary>
    /// 純粹給前端「假的進度條」估算時間用——不是真正的加解密進度回報，只是先問一次每個項目的
    /// 大小跟型別。抓不到大小（例如檔案剛好被移走、資料夾存取被拒）就當作 0，這只是體驗用的
    /// 估算功能，不該讓錯誤影響到後面真正的加密流程能不能跑。
    /// </summary>
    public async Task<IReadOnlyList<PathSizeInfo>> GetPathSizesAsync(IReadOnlyList<string> paths)
        => await Task.Run(() => paths.Select(GetPathSizeInfoSafe).ToList());

    private static PathSizeInfo GetPathSizeInfoSafe(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new PathSizeInfo(new FileInfo(path).Length, false);
            }

            if (Directory.Exists(path))
            {
                var totalBytes = new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                return new PathSizeInfo(totalBytes, true);
            }
        }
        catch (Exception)
        {
            // 存取不到（權限、路徑被移走之類）就當作 0，這是估算用的輔助功能，不值得為此中斷。
        }

        return new PathSizeInfo(0, false);
    }

    /// <summary>
    /// 加密前的巢狀鎖定掃描——純粹讓前端知道「這批選取項目裡有沒有巢狀 .locked 檔案」，
    /// 好顯示一個資訊性提示（不阻擋加密，巢狀項目本身的 vault 紀錄不受外層加密/刪除影響，
    /// 見 LockService.DecryptByUuidCore 不依賴指標檔就能解密）。純掃描，不寫入任何東西。
    /// </summary>
    public async Task<int> CheckNestedLockCountAsync(IReadOnlyList<string> paths)
        => await Task.Run(() => paths
            .Where(Directory.Exists)
            .Sum(path => FolderArchiver.FindNestedLockedFiles(path).Count));

    public SettingsResponse GetSettings() => new(_settings.VaultPath, _settings.Language, _settings.Theme, IsCriticalActionConfigured);

    /// <summary>「關鍵操作」目前是否已經設定過 Windows Hello 驗證——不綁定任何特定加密項目，
    /// 目前唯一的呼叫端是「使用紀錄」頁的清除功能，之後如果有其他破壞性動作要加同樣的門檻，
    /// 可以直接重用這裡跟下面兩個方法，不用重新設計一遍。</summary>
    public bool IsCriticalActionConfigured => !string.IsNullOrEmpty(_settings.CriticalActionCredentialName);

    /// <summary>設定（或重新設定，ReplaceExisting 讓重複呼叫可以直接覆蓋舊憑證）「關鍵操作」驗證用的
    /// Windows Hello 憑證。</summary>
    public async Task<bool> SetupCriticalActionAsync(IntPtr ownerWindowHandle)
    {
        var credentialName = PasskeyProtector.GenerateCredentialName();
        var created = await PasskeyProtector.CreateCredentialAsync(credentialName, ownerWindowHandle);
        if (!created)
        {
            return false;
        }

        _settings.CriticalActionCredentialName = credentialName;
        _settingsManager.Save(_settings);
        return true;
    }

    /// <summary>驗證使用者能通過 Windows Hello 挑戰簽章——回傳 true/false，呼叫端不需要知道細節
    /// （沒設定過／使用者取消／驗證失敗，統一當作「這次沒通過」），跟 PasskeyProtector 既有的
    /// 個別項目解鎖走同一套「不區分失敗原因」慣例。</summary>
    public async Task<bool> VerifyCriticalActionAsync(IntPtr ownerWindowHandle)
    {
        if (_settings.CriticalActionCredentialName is not { } credentialName)
        {
            return false;
        }

        var challenge = PasskeyProtector.GenerateChallenge();
        var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);
        if (signature is null)
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(signature);
        return true;
    }

    public void ClearHistory() => _historyLogger.ClearAll();

    /// <summary>停用「關鍵操作」驗證：呼叫端（HandleDisableCriticalActionRequestAsync）必須先呼叫過
    /// VerifyCriticalActionAsync 並確認成功，這裡本身不重複驗證。清掉設定值之外，也把底層 Windows Hello
    /// 憑證一併刪除，避免留下孤兒憑證。</summary>
    public async Task DisableCriticalActionAsync()
    {
        if (_settings.CriticalActionCredentialName is { } credentialName)
        {
            await PasskeyProtector.DeleteCredentialAsync(credentialName);
        }

        _settings.CriticalActionCredentialName = null;
        _settingsManager.Save(_settings);
    }

    public UpdateSettingResponse UpdateSetting(string key, string value)
    {
        switch (key)
        {
            case "language":
                _settings.Language = value;
                break;
            case "theme":
                _settings.Theme = value;
                break;
            default:
                return new UpdateSettingResponse(false, key, value);
        }

        _settingsManager.Save(_settings);
        return new UpdateSettingResponse(true, key, value);
    }

    /// <summary>
    /// 搬移 Vault：把目前 Vault 資料夾底下所有檔案搬到新位置、更新設定檔。刻意不嘗試在同一個
    /// 執行中的 App 裡「熱替換」正在使用的 VaultManager（怕跟正在進行中的加密/解密操作互相
    /// 干擾），搬完之後請使用者自己重新啟動 App 讓變更生效，比較單純可靠。
    /// </summary>
    public async Task<ChangeVaultPathResponse> ChangeVaultPathAsync(string newPath)
    {
        var currentPath = _settings.VaultPath!;

        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
        {
            return new ChangeVaultPathResponse(false, null, "新位置跟目前位置相同，不需要搬移。", ErrorCodes.VaultMoveSamePath);
        }

        if (Directory.Exists(newPath) && Directory.EnumerateFileSystemEntries(newPath).Any())
        {
            return new ChangeVaultPathResponse(false, null, "新位置的資料夾不是空的，請選一個空資料夾，避免跟裡面既有的檔案混在一起。", ErrorCodes.VaultMoveDestinationNotEmpty);
        }

        try
        {
            await Task.Run(() => MoveVaultContents(currentPath, newPath));

            _settings.VaultPath = newPath;
            _settingsManager.Save(_settings);

            return new ChangeVaultPathResponse(true, newPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ChangeVaultPathResponse(false, null, $"搬移失敗：{ex.Message}", ErrorCodes.VaultMoveIoError, ex.Message);
        }
    }

    /// <summary>優先用 Directory.Move（同一個磁碟區內幾乎瞬間完成）；跨磁碟區的話 Directory.Move 會失敗，退而求其次逐一複製再刪除來源。</summary>
    private static void MoveVaultContents(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(destinationPath) && !Directory.EnumerateFileSystemEntries(destinationPath).Any())
        {
            Directory.Delete(destinationPath);
        }

        try
        {
            Directory.Move(sourcePath, destinationPath);
            return;
        }
        catch (IOException)
        {
            // 通常是跨磁碟區導致 Directory.Move 不支援，改用複製再刪除。
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var filePath in Directory.EnumerateFiles(sourcePath))
        {
            var targetPath = Path.Combine(destinationPath, Path.GetFileName(filePath));
            File.Copy(filePath, targetPath, overwrite: false);
        }
        Directory.Delete(sourcePath, recursive: true);
    }

    /// <summary>
    /// 清單「有哪些項目」讀 VaultIndexCache（本機 SQLite 快取，不用每次都全量重掃 Vault
    /// 資料夾）；但每一筆的 CheckMarkerStatus 仍然是即時查詢——那是原始位置的 .locked 指標檔
    /// 還在不在，跟 Vault 資料夾內容無關，本來就該每次刷新都重新問一次磁碟，不應該被快取。
    /// 用 AsParallel 讓多筆的檔案 I/O 可以同時進行，項目一多（幾百筆）刷新清單會明顯變快。
    /// </summary>
    public async Task<IReadOnlyList<VaultListItemResponse>> ListVaultAsync()
    {
        return await Task.Run(() =>
        {
            var entries = _vaultIndexCache.GetItems();

            // 健檢：快取列背後的 metadata 檔案理論上一定存在（正常情況下 FileSystemWatcher 會
            // 即時同步），但漏接事件的情況下會留下孤兒列——每次刷新清單時用一次便宜的 File.Exists
            // 檢查（不需要整個 Rebuild() 重新掃描），找到孤兒列就順手清掉，不用等使用者自己發現
            // 「這筆怎麼點什麼都說找不到紀錄」才想辦法處理。
            var validEntries = new List<VaultIndexEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (File.Exists(_vaultManager.GetMetaFilePath(entry.Uuid)))
                {
                    validEntries.Add(entry);
                }
                else
                {
                    _vaultIndexCache.RemoveEntry(entry.Uuid);
                }
            }

            // 巢狀 uuid → 包住它的外層項目名稱對照表。資料夾加密時，內層既有的 .locked 指標檔
            // 會整包被壓縮進外層的 zip，內層原始位置的資料夾本身也會被刪除——所以內層項目的
            // 指標檔「找不到」不一定是使用者自己搬移/刪除，很常見是被外層資料夾加密整個收進去了。
            // 只掃有巢狀鎖定的候選項目（通常很少），一次建好對照表，避免底下每一筆缺指標檔的
            // 項目都要重新掃一次全部候選、重複讀取同一份外層 metadata。
            // 同一個迴圈順便建一份反過來的對照表：外層 uuid → 巢狀項目名稱清單，給清單頁的
            // 🔒 ×N 圖示 tooltip 用（顯示裡面實際包含哪些檔案，不是只顯示數量）。複用同一次
            // LoadMetadata(candidate.Uuid) 呼叫，不需要為了這件事再重新讀一次檔。
            var nestedContainerNames = new Dictionary<string, string>();
            var containerNestedNames = new Dictionary<string, List<string>>();
            foreach (var candidate in validEntries.Where(e => e.NestedLockCount > 0))
            {
                var containerMetadata = _vaultManager.LoadMetadata(candidate.Uuid);
                if (containerMetadata is null)
                {
                    continue;
                }

                var nestedNames = new List<string>();
                foreach (var nestedUuid in containerMetadata.ContainsNestedLocks)
                {
                    nestedContainerNames[nestedUuid] = containerMetadata.OriginalName;

                    // 查不到名稱（該巢狀項目後來也被刪除了）就跳過，不硬塞一個佔位字串進清單。
                    var nestedName = _vaultManager.LoadMetadata(nestedUuid)?.OriginalName;
                    if (nestedName is not null)
                    {
                        nestedNames.Add(nestedName);
                    }
                }
                containerNestedNames[candidate.Uuid] = nestedNames;
            }

            return validEntries
                .AsParallel()
                .Select(entry =>
                {
                    var markerStatus = MarkerStatusChecker.CheckMarkerStatus(entry.Uuid, entry.OriginalPath, entry.Type);
                    if (!markerStatus.Found && markerStatus.ConflictingUuid is { } conflictingUuid)
                    {
                        // 查得到佔用者的名稱就直接講清楚是誰取代的，查不到（例如那個項目後來也被
                        // 刪除了）就維持原本的通用訊息——這條路徑本來就很罕見，不需要第二層 fallback 文案。
                        var conflictingName = _vaultManager.LoadMetadata(conflictingUuid)?.OriginalName;
                        if (conflictingName is not null)
                        {
                            markerStatus = markerStatus with
                            {
                                Code = ErrorCodes.MarkerReplacedByOtherNamed,
                                Detail = conflictingName,
                                Message = $"指標檔已被「{conflictingName}」鎖定"
                            };
                        }
                    }
                    else if (!markerStatus.Found && nestedContainerNames.TryGetValue(entry.Uuid, out var containerName))
                    {
                        markerStatus = markerStatus with
                        {
                            Code = ErrorCodes.MarkerPackedIntoContainer,
                            Detail = containerName,
                            Message = $"該檔案的指標檔已經收進「{containerName}」這個資料夾一起加密了"
                        };
                    }
                    return new VaultListItemResponse(entry, markerStatus, containerNestedNames.GetValueOrDefault(entry.Uuid, []));
                })
                .ToList()
                .OrderByDescending(item => item.CreatedAtUtc)
                .ToList();
        });
    }

    /// <summary>對應「使用紀錄」子頁籤：跟 Vault 目前狀態無關，單純把本機累積的操作日誌全部讀出來。</summary>
    public IReadOnlyList<HistoryListItemResponse> ListHistory()
        => _historyLogger.ReadAll()
            .OrderByDescending(entry => entry.TimestampUtc)
            .Select(entry => new HistoryListItemResponse(entry))
            .ToList();

    /// <summary>
    /// RecordNotFound 代表這筆快取列背後的 metadata 已經不存在（孤兒列，見 ListVaultAsync 的健檢
    /// 說明）——沒有內容需要保護，直接清掉快取、當成刪除成功處理，不要讓使用者卡在一個永遠
    /// 刪不掉的殭屍紀錄上。這裡是唯一同時握有 _lockService 跟 _vaultIndexCache 的地方，
    /// 兩者的協調只適合放在這一層，不該讓 LockService 反過來依賴 VaultIndexCache。
    /// </summary>
    public async Task<DeleteRecordResult> DeleteRecordAsync(string uuid)
    {
        var result = await _lockService.TryDeleteRecordAsync(uuid);
        if (!result.Success && result.ErrorCode == ErrorCodes.RecordNotFound)
        {
            _vaultIndexCache.RemoveEntry(uuid);
            return new DeleteRecordResult(true, false);
        }

        return result;
    }

    public Task<VerifyPasswordResult> VerifyPasswordAsync(string uuid, string password) => _lockService.VerifyPasswordAsync(uuid, password);
}
