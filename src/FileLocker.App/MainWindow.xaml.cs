using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using FileLocker.Core;
using FileLocker.Core.History;
using FileLocker.Core.Settings;
using FileLocker.Core.Vault;
using Microsoft.Web.WebView2.Core;

namespace FileLocker.App;

public partial class MainWindow : Window
{
    // Release 建置時 SetVirtualHostNameToFolderMapping 用的虛擬主機名稱，純粹是本機識別用，
    // 不是真的網域，不需要真的擁有或註冊這個名稱。
    private const string AppOrigin = "filelocker.local";

    private readonly VaultManager _vaultManager;
    private readonly HistoryLogger _historyLogger;
    private readonly LockService _lockService;
    private readonly AppSettingsManager _settingsManager;
    private readonly AppSettings _settings;
    private readonly string _appDataDir;
    private readonly List<string>? _initialPaths;

    /// <summary>
    /// VaultManager／LockService 現在由 App.xaml.cs 統一建立、傳進來——這樣主視窗跟密碼小視窗
    /// 用的是同一份 Vault／History 設定，不會各自重複建立、路徑卻可能不小心兜不起來。
    /// initialPaths 是從 Shell Extension 右鍵選單過來的（可能是空的、一個，或多個路徑），
    /// 等 WebView2 頁面真的載入完成才送給前端，避免前端還沒掛上訊息監聽器就漏接。
    /// </summary>
    public MainWindow(
        VaultManager vaultManager, HistoryLogger historyLogger, LockService lockService,
        AppSettingsManager settingsManager, AppSettings settings, string appDataDir,
        List<string>? initialPaths = null)
    {
        InitializeComponent();

        _vaultManager = vaultManager;
        _historyLogger = historyLogger;
        _lockService = lockService;
        _settingsManager = settingsManager;
        _settings = settings;
        _appDataDir = appDataDir;
        _initialPaths = initialPaths;

        Loaded += async (_, _) =>
        {
            await MainWebView.EnsureCoreWebView2Async();

            // WebView2 安全性硬化：
            // 1. 關掉密碼自動儲存/自動填入——不關的話，使用者在加密/解密表單輸入的密碼可能被
            //    Chromium 內建的密碼管理員另外存一份，離開我們自己的掌控範圍，也弱化了「密碼不會被
            //    存在任何地方」的安全宣稱。這個不管 Debug/Release 都要關。
            // 2. DevTools 只有 Release 建置才關掉——Debug 建置留著方便自己開發時除錯前端問題。
            MainWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            MainWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
#if DEBUG
            MainWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            MainWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

            // 右鍵選單：只有點在可編輯欄位（密碼／恢復金鑰輸入框）上才保留瀏覽器預設的剪下/複製/貼上選單，
            // 其餘一律不顯示——原本 Chromium 內建的右鍵選單會有「上一頁」「重新整理」「檢視原始碼」
            // 這類跟一般瀏覽器一樣的雜訊項目，在一個不是瀏覽器的桌面工具上沒有意義，關掉比較乾淨。
            MainWebView.CoreWebView2.ContextMenuRequested += (_, ctxArgs) =>
            {
                if (!ctxArgs.ContextMenuTarget.IsEditable)
                {
                    ctxArgs.Handled = true;
                }
            };

            // 導覽限制：只允許導覽到我們預期的網址，其餘一律擋下——避免 Debug 模式下本機
            // localhost 埠被其他程式搶先佔用時載入到惡意頁面；Release 模式下也是防禦性寫法，
            // 就算未來哪個環節不小心觸發了非預期的導覽，也不會真的跑到別的地方去。
            MainWebView.CoreWebView2.NavigationStarting += (_, navArgs) =>
            {
#if DEBUG
                var isAllowed = navArgs.Uri.StartsWith("http://localhost:5173/", StringComparison.Ordinal);
#else
                var isAllowed = navArgs.Uri.StartsWith($"https://{AppOrigin}/", StringComparison.Ordinal);
#endif
                if (!isAllowed)
                {
                    navArgs.Cancel = true;
                }
            };

#if DEBUG
            // Debug 建置：連到 Vite 開發伺服器，需要另外開一個終端機跑 npm run dev。
            MainWebView.CoreWebView2.Navigate("http://localhost:5173/");
#else
            // Release 建置：直接從封裝好的靜態檔案載入，不透過任何本機網路埠——
            // 這是規格文件 8.3 節記錄的硬性阻擋項目的正式修法。webapp 資料夾由
            // FileLocker.App.csproj 的 Release 建置流程自動產生（npm run build + 複製檔案）。
            // CoreWebView2HostResourceAccessKind.Deny：不允許其他來源透過網路請求存取這個
            // 虛擬主機底下的資源，我們自己只會直接導覽過去，不需要開放跨來源存取。
            var webAppFolder = Path.Combine(AppContext.BaseDirectory, "webapp");
            MainWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppOrigin, webAppFolder, CoreWebView2HostResourceAccessKind.Deny);
            MainWebView.CoreWebView2.Navigate($"https://{AppOrigin}/index.html");
#endif
            MainWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            MainWebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess && _initialPaths is { Count: > 0 })
                {
                    SendToFrontend(new { type = "initialPaths", paths = _initialPaths });
                }
            };
        };
    }

    /// <summary>
    /// 對應單一執行個體機制（見 App.xaml.cs）：已經有這個視窗開著時，之後被 Mutex 擋下來、
    /// 轉送過來的加密路徑清單就送進這裡，而不是另外開一個新的 MainWindow。
    /// 順便把視窗搶回前景（可能被壓在其他視窗底下，或被縮到最小），讓使用者知道有新的東西進來了。
    /// </summary>
    public void ApplyIncomingPaths(List<string> paths)
    {
        Activate();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (paths.Count > 0)
        {
            SendToFrontend(new { type = "initialPaths", paths });
        }
    }

    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            switch (type)
            {
                case "encrypt":
                    await HandleEncryptRequestAsync(root);
                    break;

                case "decrypt":
                    await HandleDecryptRequestAsync(root);
                    break;

                case "decryptByUuid":
                    await HandleDecryptByUuidRequestAsync(root);
                    break;

                case "decryptByPasskey":
                    await HandleDecryptByPasskeyRequestAsync(root);
                    break;

                case "decryptByRecoveryKey":
                    await HandleDecryptByRecoveryKeyRequestAsync(root);
                    break;

                case "decryptBatch":
                    await HandleDecryptBatchRequestAsync(root);
                    break;

                case "saveRecoveryKeyToFile":
                    HandleSaveRecoveryKeyToFileRequest(root);
                    break;

                case "inspectLockedFile":
                    HandleInspectLockedFileRequest(root);
                    break;

                case "getSettings":
                    HandleGetSettingsRequest();
                    break;

                case "pickVaultFolder":
                    HandlePickVaultFolder();
                    break;

                case "changeVaultPath":
                    await HandleChangeVaultPathRequestAsync(root);
                    break;

                case "updateSetting":
                    HandleUpdateSettingRequest(root);
                    break;

                case "pickFile":
                    HandlePickFile(root);
                    break;

                case "pickFolder":
                    HandlePickFolder(root);
                    break;

                case "listVault":
                    await HandleListVaultRequestAsync();
                    break;

                case "listHistory":
                    HandleListHistoryRequest();
                    break;

                case "deleteRecord":
                    await HandleDeleteRecordRequestAsync(root);
                    break;

                default:
                    Console.WriteLine($"未知的訊息類型：{type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendToFrontend(new { type = "error", message = $"處理訊息時發生未預期的錯誤：{ex.Message}" });
        }
    }

    private async Task HandleEncryptRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var password = request.GetProperty("password").GetString() ?? "";
        var hint = request.TryGetProperty("hint", out var hintProp) ? hintProp.GetString() : null;
        var enablePasskey = request.TryGetProperty("enablePasskey", out var passkeyProp) && passkeyProp.GetBoolean();
        var enableRecoveryKey = request.TryGetProperty("enableRecoveryKey", out var recoveryProp) && recoveryProp.GetBoolean();

        var ownerWindowHandle = enablePasskey ? new WindowInteropHelper(this).Handle : IntPtr.Zero;

        // 選了不只一個項目才需要分組——單一項目沒有「摺疊」的意義，維持 batchId = null。
        var batchId = paths.Count > 1 ? Guid.NewGuid().ToString() : null;

        SendToFrontend(new { type = "encryptBatchStarted", totalCount = paths.Count });

        var successCount = 0;

        foreach (var path in paths)
        {
            var result = await _lockService.EncryptAsync(
                path, password, string.IsNullOrWhiteSpace(hint) ? null : hint,
                enablePasskey, ownerWindowHandle, enableRecoveryKey, batchId);

            var actuallyPasskeyEnabled = false;
            if (result.Success)
            {
                successCount++;
                actuallyPasskeyEnabled = _vaultManager.LoadMetadata(result.Uuid)?.PasskeyEnabled ?? false;
            }

            // 每完成一個項目就馬上回報，前端可以即時更新清單，不用等全部跑完才看到結果。
            SendToFrontend(new
            {
                type = "encryptItemResult",
                path,
                success = result.Success,
                uuid = result.Uuid,
                lockedMarkerPath = result.LockedMarkerPath,
                errorMessage = result.ErrorMessage,
                passkeyRequested = enablePasskey,
                passkeyEnabled = actuallyPasskeyEnabled,
                recoveryKey = result.RecoveryKey
            });
        }

        SendToFrontend(new { type = "encryptBatchDone", totalCount = paths.Count, successCount });
    }

    private async Task HandleDecryptRequestAsync(JsonElement request)
    {
        var lockedMarkerPath = request.GetProperty("path").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";

        var result = await _lockService.DecryptAsync(lockedMarkerPath, password);

        SendToFrontend(new
        {
            type = "decryptResult",
            success = result.Success,
            restoredPath = result.RestoredPath,
            errorMessage = result.ErrorMessage
        });
    }

    /// <summary>
    /// 對應「已加密清單」頁直接選項目解密，不需要使用者先手動找到 .locked 檔案。
    /// </summary>
    private async Task HandleDecryptByUuidRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";
        var destinationDir = request.TryGetProperty("destinationDir", out var destProp) && destProp.ValueKind == JsonValueKind.String
            ? destProp.GetString()
            : null;

        var result = await _lockService.DecryptByUuidAsync(uuid, password, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByUuidResult",
            uuid,
            success = result.Success,
            restoredPath = result.RestoredPath,
            errorMessage = result.ErrorMessage
        });
    }

    /// <summary>對應「已加密清單」頁的 Passkey 解鎖按鈕：不需要密碼，走 Windows Hello 驗證。</summary>
    private async Task HandleDecryptByPasskeyRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var destinationDir = ResolveDestinationDirFromRequest(request);

        var hwnd = new WindowInteropHelper(this).Handle;
        var result = await _lockService.DecryptByPasskeyAsync(uuid, hwnd, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByPasskeyResult",
            uuid,
            success = result.Success,
            restoredPath = result.RestoredPath,
            errorMessage = result.ErrorMessage
        });
    }

    /// <summary>對應「已加密清單」頁的恢復金鑰解鎖按鈕：不需要密碼、不需要 Windows Hello。</summary>
    private async Task HandleDecryptByRecoveryKeyRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var recoveryKeyInput = request.GetProperty("recoveryKey").GetString() ?? "";
        var destinationDir = ResolveDestinationDirFromRequest(request);

        var result = await _lockService.DecryptByRecoveryKeyAsync(uuid, recoveryKeyInput, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByRecoveryKeyResult",
            uuid,
            success = result.Success,
            restoredPath = result.RestoredPath,
            errorMessage = result.ErrorMessage
        });
    }

    /// <summary>
    /// 對應「已加密清單」頁摺疊群組的「全部解鎖」按鈕：跟批次加密一樣只支援密碼，
    /// 逐一解密、每完成一個就馬上回報，不用等全部跑完才看到結果。還原位置固定用各自的原始位置，
    /// 不像單獨解鎖那樣可以問「原始位置還是自訂位置」——批次情境下每個項目分別問一次太打擾人。
    /// </summary>
    private async Task HandleDecryptBatchRequestAsync(JsonElement request)
    {
        var uuids = request.GetProperty("uuids").EnumerateArray()
            .Select(u => u.GetString() ?? "")
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();
        var password = request.GetProperty("password").GetString() ?? "";

        SendToFrontend(new { type = "decryptBatchStarted", totalCount = uuids.Count });

        var successCount = 0;

        foreach (var uuid in uuids)
        {
            var result = await _lockService.DecryptByUuidAsync(uuid, password);
            if (result.Success)
            {
                successCount++;
            }

            SendToFrontend(new
            {
                type = "decryptBatchItemResult",
                uuid,
                success = result.Success,
                restoredPath = result.RestoredPath,
                errorMessage = result.ErrorMessage
            });
        }

        SendToFrontend(new { type = "decryptBatchDone", totalCount = uuids.Count, successCount });
    }

    /// <summary>
    /// decryptByPasskey／decryptByRecoveryKey 共用：優先用前端明確指定的 destinationDir；
    /// 沒有的話，若前端傳了 markerPath（例如「解密」頁籤選了 .locked 檔案的情境），
    /// 用該檔案目前所在的資料夾當還原位置，維持跟密碼路徑一致的行為。
    /// </summary>
    private static string? ResolveDestinationDirFromRequest(JsonElement request)
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
    /// 對應恢復金鑰顯示畫面的「存成檔案」選項：跳原生存檔對話框，把恢復金鑰文字寫進使用者選的檔案。
    /// </summary>
    private void HandleSaveRecoveryKeyToFileRequest(JsonElement request)
    {
        var content = request.GetProperty("content").GetString() ?? "";
        var suggestedFileName = request.TryGetProperty("suggestedFileName", out var nameProp)
            ? nameProp.GetString() ?? "FileLocker-恢復金鑰.txt"
            : "FileLocker-恢復金鑰.txt";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "儲存恢復金鑰",
            FileName = suggestedFileName,
            Filter = "文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            DefaultExt = ".txt"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, content);
                SendToFrontend(new { type = "saveRecoveryKeyToFileResult", success = true, path = dialog.FileName });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SendToFrontend(new { type = "saveRecoveryKeyToFileResult", success = false, errorMessage = ex.Message });
            }
        }
        else
        {
            SendToFrontend(new { type = "saveRecoveryKeyToFileResult", success = false, cancelled = true });
        }
    }

    /// <summary>
    /// 對應「解密」頁籤：使用者選好 .locked 檔案後，查一下這個項目除了密碼之外，
    /// 還有沒有開 Passkey／恢復金鑰，讓前端可以動態顯示對應的按鈕，不用每次都固定只能輸密碼。
    /// 這裡只讀 marker 拿 UUID、查 metadata，不驗證簽章——純粹是為了顯示資訊，
    /// 真正的安全驗證在使用者實際選擇某條解鎖路徑時才會發生。
    /// </summary>
    private void HandleInspectLockedFileRequest(JsonElement request)
    {
        var path = request.GetProperty("path").GetString() ?? "";
        var marker = LockedMarkerFile.ReadFrom(path);

        if (marker is null)
        {
            SendToFrontend(new { type = "inspectLockedFileResult", success = false });
            return;
        }

        var metadata = _vaultManager.LoadMetadata(marker.Uuid);

        SendToFrontend(new
        {
            type = "inspectLockedFileResult",
            success = metadata is not null,
            uuid = marker.Uuid,
            originalName = metadata?.OriginalName,
            hint = metadata?.Hint,
            passkeyEnabled = metadata?.PasskeyEnabled ?? false,
            recoveryKeyEnabled = metadata?.RecoveryKeyEnabled ?? false
        });
    }

    private void HandleGetSettingsRequest()
    {
        SendToFrontend(new
        {
            type = "settingsResult",
            vaultPath = _settings.VaultPath,
            language = _settings.Language,
            theme = _settings.Theme
        });
    }

    private void HandlePickVaultFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇要搬移到的新 Vault 位置（建議選一個空資料夾）"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SendToFrontend(new { type = "pathPicked", purpose = "vaultFolder", path = dialog.FolderName });
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled", purpose = "vaultFolder" });
        }
    }

    /// <summary>
    /// 搬移 Vault：把目前 Vault 資料夾底下所有檔案搬到新位置、更新設定檔。
    /// 刻意不嘗試在同一個執行中的 App 裡「熱替換」正在使用的 VaultManager（怕跟正在進行中的
    /// 加密/解密操作互相干擾），搬完之後請使用者自己重新啟動 App 讓變更生效，比較單純可靠。
    /// </summary>
    private async Task HandleChangeVaultPathRequestAsync(JsonElement request)
    {
        var newPath = request.GetProperty("newPath").GetString() ?? "";
        var currentPath = _settings.VaultPath!;

        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(currentPath), StringComparison.OrdinalIgnoreCase))
        {
            SendToFrontend(new { type = "changeVaultPathResult", success = false, errorMessage = "新位置跟目前位置相同，不需要搬移。" });
            return;
        }

        if (Directory.Exists(newPath) && Directory.EnumerateFileSystemEntries(newPath).Any())
        {
            SendToFrontend(new { type = "changeVaultPathResult", success = false, errorMessage = "新位置的資料夾不是空的，請選一個空資料夾，避免跟裡面既有的檔案混在一起。" });
            return;
        }

        try
        {
            await Task.Run(() => MoveVaultContents(currentPath, newPath));

            _settings.VaultPath = newPath;
            _settingsManager.Save(_settings);

            SendToFrontend(new { type = "changeVaultPathResult", success = true, newPath, requiresRestart = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SendToFrontend(new { type = "changeVaultPathResult", success = false, errorMessage = $"搬移失敗：{ex.Message}" });
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

    private void HandleUpdateSettingRequest(JsonElement request)
    {
        var key = request.GetProperty("key").GetString() ?? "";
        var value = request.GetProperty("value").GetString() ?? "";

        switch (key)
        {
            case "language":
                _settings.Language = value;
                break;
            case "theme":
                _settings.Theme = value;
                break;
            default:
                return;
        }

        _settingsManager.Save(_settings);
        SendToFrontend(new { type = "updateSettingResult", success = true, key, value });
    }

    private async Task HandleListVaultRequestAsync()
    {
        var items = await Task.Run(() =>
        {
            // 每一筆的 CheckMarkerStatus 都是各自獨立的檔案讀取，彼此不共用狀態，
            // 用 AsParallel 讓多筆的檔案 I/O 可以同時進行，而不是一筆一筆排隊等——
            // 項目數量少的時候感覺不出差異，項目一多（幾百筆）刷新清單會明顯變快。
            var metadataList = _vaultManager.ScanAll().ToList();

            return metadataList
                .AsParallel()
                .Select(m =>
                {
                    var markerStatus = _lockService.CheckMarkerStatus(m);
                    return new
                    {
                        uuid = m.Uuid,
                        originalName = m.OriginalName,
                        originalPath = m.OriginalPath,
                        type = m.Type.ToString(),
                        passkeyEnabled = m.PasskeyEnabled,
                        recoveryKeyEnabled = m.RecoveryKeyEnabled,
                        batchId = m.BatchId,
                        originalSizeBytes = m.OriginalSizeBytes,
                        hint = m.Hint,
                        createdAtUtc = m.CreatedAtUtc,
                        hasNestedLocks = m.ContainsNestedLocks.Count > 0,
                        nestedLockCount = m.ContainsNestedLocks.Count,
                        markerFound = markerStatus.Found,
                        markerStatusMessage = markerStatus.Message
                    };
                })
                .ToList()
                .OrderByDescending(m => m.createdAtUtc)
                .ToList();
        });

        SendToFrontend(new { type = "vaultList", items });
    }

    /// <summary>對應「使用紀錄」子頁籤：跟 Vault 目前狀態無關，單純把本機累積的操作日誌全部讀出來。</summary>
    private void HandleListHistoryRequest()
    {
        var entries = _historyLogger.ReadAll()
            .OrderByDescending(entry => entry.TimestampUtc)
            .Select(entry => new
            {
                uuid = entry.Uuid,
                originalName = entry.OriginalName,
                action = entry.Action.ToString(),
                timestampUtc = entry.TimestampUtc,
                detail = entry.Detail,
                sourcePath = entry.SourcePath,
                passkeyEnabled = entry.PasskeyEnabled,
                recoveryKeyEnabled = entry.RecoveryKeyEnabled,
                unlockMethod = entry.UnlockMethod,
                restoredPath = entry.RestoredPath
            });

        SendToFrontend(new { type = "historyList", items = entries });
    }

    private async Task HandleDeleteRecordRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";

        var result = await _lockService.TryDeleteRecordAsync(uuid);

        SendToFrontend(new
        {
            type = "deleteRecordResult",
            uuid,
            success = result.Success,
            blockedByNestedLocks = result.BlockedByNestedLocks,
            nestedUuids = result.NestedUuids,
            errorMessage = result.ErrorMessage
        });
    }

    private void HandlePickFile(JsonElement request)
    {
        var purpose = request.TryGetProperty("purpose", out var purposeProp) ? purposeProp.GetString() : null;
        var allowMultiselect = purpose == "encryptPath";

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = purpose == "decryptPath" ? "選擇要解密的 .locked 檔案" : "選擇要加密的檔案",
            CheckFileExists = true,
            Multiselect = allowMultiselect,
            Filter = purpose == "decryptPath"
                ? "FileLocker 鎖定檔 (*.locked)|*.locked|所有檔案 (*.*)|*.*"
                : "所有檔案 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            if (allowMultiselect)
            {
                SendToFrontend(new { type = "pathsPicked", purpose, paths = dialog.FileNames });
            }
            else
            {
                SendToFrontend(new { type = "pathPicked", purpose, path = dialog.FileName });
            }
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled", purpose });
        }
    }

    private void HandlePickFolder(JsonElement request)
    {
        var purpose = request.TryGetProperty("purpose", out var purposeProp) ? purposeProp.GetString() : "encryptPath";

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = purpose == "decryptDestination" ? "選擇要還原到哪個資料夾" : "選擇要加密的資料夾"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SendToFrontend(new { type = "pathPicked", purpose, path = dialog.FolderName });
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled", purpose });
        }
    }

    private void SendToFrontend(object message)
    {
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }
}