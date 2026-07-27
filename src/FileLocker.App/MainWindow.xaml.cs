using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using FileLocker.Core;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.Protocol;
using FileLocker.Core.Settings;
using FileLocker.Core.Vault;
using Microsoft.Web.WebView2.Core;

namespace FileLocker.App;

public partial class MainWindow : Window
{
    // Release 建置時 SetVirtualHostNameToFolderMapping 用的虛擬主機名稱，純粹是本機識別用，
    // 不是真的網域，不需要真的擁有或註冊這個名稱。
    private const string AppOrigin = "filelocker.local";

    // 無邊框視窗的 Win32/DWM 互操作跟視窗外框相關邏輯搬到 MainWindow.Chrome.cs 了（見該檔案
    // 開頭說明）——這裡只留 WebView2 初始化／IPC 派送／Vault 協定呼叫。

    // VaultManager／HistoryLogger／LockService／AppSettingsManager／VaultIndexCache 不再各自存成
    // 欄位——除了組出 _protocolHandlers 跟訂閱 _vaultChangeWatcher.Changed，MainWindow 本身不直接
    // 呼叫任何一個，全部透過協定分派層存取（見架構審查 2026-07-26：MainWindow 不該同時身兼視窗
    // 平台細節、WebView2 初始化、跟業務邏輯呼叫這三種不相關的職責）。
    private readonly AppSettings _settings;
    private readonly VaultChangeWatcher _vaultChangeWatcher;
    private readonly VaultProtocolHandlers _protocolHandlers;
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
        VaultIndexCache vaultIndexCache, VaultChangeWatcher vaultChangeWatcher,
        List<string>? initialPaths = null)
    {
        InitializeComponent();

        _settings = settings;
        _vaultChangeWatcher = vaultChangeWatcher;
        _initialPaths = initialPaths;

        // 協定分派層（見架構審查 2026-07-26）：純 C#、不依賴 WPF/WebView2，直接組裝既有的
        // Core 依賴即可，不需要 App.xaml.cs 額外建立、也不改動它呼叫 MainWindow 建構子的簽章。
        _protocolHandlers = new VaultProtocolHandlers(
            vaultManager, lockService, vaultIndexCache, historyLogger, settingsManager, settings);

        // Watcher 偵測到 Vault 變化（背景執行緒觸發）時，推播一則通知給前端清單頁——
        // 沿用既有的「背景推送資料進已開啟視窗」模式（見 ApplyIncomingPaths）。
        // 必須用 Dispatcher 切回 UI 執行緒，SendToFrontend 底層是 WebView2 COM 物件，
        // 不能從背景執行緒直接呼叫。
        _vaultChangeWatcher.Changed += (_, _) =>
            Dispatcher.BeginInvoke(() => SendToFrontend(new { type = "vaultChanged" }));

        // 啟動時就先套用一次已儲存的主題背景色，不要等使用者到設定頁重新選一次才生效——
        // 不然重開 App 之後，即使上次選的是深色模式，WebView2 邊緣那圈窄邊還是會先閃一下
        // 白色，等頁面裡的 JS 執行完才切過去。
        ApplyWindowBackgroundForTheme(_settings.Theme);

        SourceInitialized += OnSourceInitialized;

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
            // 桌面應用程式不應該讓使用者用 Ctrl+滾輪或觸控手勢意外縮放畫面（那是瀏覽器的行為，
            // 這裡不是瀏覽器）。畫面本身在不同 Windows 顯示器縮放比例（100%/125%/150%...）下
            // 已經會由 WebView2 自動依照系統 DPI 正確縮放，不需要額外的網頁縮放疊加上去。
            MainWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // 開啟 app-region CSS 支援：HTML 裡標記 app-region: drag 的區域會被當成視窗標題列，
            // 拖曳、右鍵系統選單、雙擊最大化全部交給作業系統的視窗管理員原生處理。
            // 這比「用 JavaScript 追游標位置再回頭叫視窗移動」可靠得多——後者每次移動都要跨進程
            // 來回一次，延遲累積起來視窗就會抖動，而且拿不到 Aero Snap 那些原生行為。
            // 注意：這個設定必須在導覽之前設定好，下一次導覽才會生效。
            MainWebView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
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

            // 擋掉 window.open()／target="_blank" 開新視窗：WebView2 預設會直接跳出一個完全不受
            // 上面 NavigationStarting 限制的獨立 Chromium 彈出視窗。這個應用不需要彈出視窗功能，
            // 全部擋掉——就算前端未來哪個相依套件出問題被注入惡意腳本，也沒辦法藉此跳出一個
            // 能導覽到任意網址的視窗。
            MainWebView.CoreWebView2.NewWindowRequested += (_, newWindowArgs) =>
            {
                newWindowArgs.Handled = true;
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
            if (!File.Exists(Path.Combine(webAppFolder, "index.html")))
            {
                // 找不到打包好的前端靜態檔案——通常代表建置流程沒跑過 npm run build，
                // 或是輸出目錄結構跟預期不一樣。與其讓畫面就這樣一片空白讓使用者一頭霧水，
                // 直接跳出明確的錯誤訊息，方便排查。
                MessageBox.Show(
                    $"找不到前端畫面檔案（預期位置：{webAppFolder}）。\n\n" +
                    "如果是開發階段自己編譯的 Release 版本，請確認 FileLocker.App.csproj 的建置流程有" +
                    "成功執行 npm run build，並把輸出複製到這個資料夾。",
                    "FileLocker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            MainWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppOrigin, webAppFolder, CoreWebView2HostResourceAccessKind.Deny);
            MainWebView.CoreWebView2.Navigate($"https://{AppOrigin}/index.html");
#endif
            MainWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            MainWebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    return;
                }

                // 頁面載入完成先同步一次目前的視窗狀態，前端的最大化按鈕才知道該顯示哪個圖示
                // （例如上次關閉時是最大化的，這次啟動就要直接顯示「還原」而不是「最大化」）。
                SendWindowStateToFrontend();

                if (_initialPaths is { Count: > 0 })
                {
                    SendToFrontend(new { type = "initialPaths", paths = _initialPaths });
                }
            };

            StateChanged += (_, _) => SendWindowStateToFrontend();
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

                case "windowMinimize":
                    WindowState = WindowState.Minimized;
                    break;

                case "windowMaximizeToggle":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;

                case "windowClose":
                    Close();
                    break;

                case "filesDroppedFromWebView":
                    HandleFilesDroppedFromWebView(e);
                    break;

                case "getPathSizes":
                    await HandleGetPathSizesRequestAsync(root);
                    break;

                case "checkNestedLocks":
                    await HandleCheckNestedLocksRequestAsync(root);
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

                case "setupCriticalAction":
                    await HandleSetupCriticalActionRequestAsync();
                    break;

                case "verifyCriticalAction":
                    await HandleVerifyCriticalActionRequestAsync();
                    break;

                case "clearHistory":
                    HandleClearHistoryRequest();
                    break;

                case "disableCriticalAction":
                    await HandleDisableCriticalActionRequestAsync();
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

                case "verifyPasswordForDelete":
                    await HandleVerifyPasswordForDeleteRequestAsync(root);
                    break;

                default:
                    Console.WriteLine($"未知的訊息類型：{type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendToFrontend(new { type = "error", message = ex.Message });
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

        // 視窗控制代碼是 WPF 平台細節，只有這裡（呼叫端）拿得到，往下傳給協定分派層當一般參數，
        // 那一層不需要知道「視窗」這個概念存在。
        var ownerWindowHandle = enablePasskey ? new WindowInteropHelper(this).Handle : IntPtr.Zero;

        SendToFrontend(new { type = "encryptBatchStarted", totalCount = paths.Count });

        var successCount = 0;

        // 每完成一個項目就馬上回報，前端可以即時更新清單，不用等全部跑完才看到結果——
        // 這裡只負責「收到一筆就送一次 WebView2 訊息」，逐項的業務邏輯在 EncryptBatchAsync 裡。
        await foreach (var item in _protocolHandlers.EncryptBatchAsync(
            paths, password, hint, enablePasskey, enableRecoveryKey, ownerWindowHandle,
            verifying => SendToFrontend(new { type = "encryptPasskeyVerifying", verifying })))
        {
            if (item.Success)
            {
                successCount++;
            }

            // 前端讀的是攤平的欄位（data.path／data.success／...），不是巢狀的 data.item.xxx，
            // 這裡要攤平回去，維持既有的線上協定格式不變。
            SendToFrontend(new
            {
                type = "encryptItemResult",
                item.Path,
                item.Success,
                item.Uuid,
                item.LockedMarkerPath,
                item.ErrorMessage,
                item.ErrorCode,
                item.ErrorDetail,
                item.PasskeyRequested,
                item.PasskeyEnabled,
                item.RecoveryKey
            });
        }

        SendToFrontend(new { type = "encryptBatchDone", totalCount = paths.Count, successCount });
    }

    private async Task HandleDecryptRequestAsync(JsonElement request)
    {
        var lockedMarkerPath = request.GetProperty("path").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";

        var result = await _protocolHandlers.DecryptAsync(lockedMarkerPath, password);

        SendToFrontend(new
        {
            type = "decryptResult",
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
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

        var result = await _protocolHandlers.DecryptByUuidAsync(uuid, password, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByUuidResult",
            uuid,
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    /// <summary>對應「已加密清單」頁的 Passkey 解鎖按鈕：不需要密碼，走 Windows Hello 驗證。</summary>
    private async Task HandleDecryptByPasskeyRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var destinationDir = VaultProtocolHandlers.ResolveDestinationDirFromRequest(request);

        var hwnd = new WindowInteropHelper(this).Handle;
        var result = await _protocolHandlers.DecryptByPasskeyAsync(uuid, hwnd, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByPasskeyResult",
            uuid,
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    /// <summary>對應「已加密清單」頁的恢復金鑰解鎖按鈕：不需要密碼、不需要 Windows Hello。</summary>
    private async Task HandleDecryptByRecoveryKeyRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var recoveryKeyInput = request.GetProperty("recoveryKey").GetString() ?? "";
        var destinationDir = VaultProtocolHandlers.ResolveDestinationDirFromRequest(request);

        var result = await _protocolHandlers.DecryptByRecoveryKeyAsync(uuid, recoveryKeyInput, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByRecoveryKeyResult",
            uuid,
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
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

        await foreach (var item in _protocolHandlers.DecryptBatchAsync(uuids, password))
        {
            if (item.Success)
            {
                successCount++;
            }

            SendToFrontend(new
            {
                type = "decryptBatchItemResult",
                item.Uuid,
                item.Success,
                item.RestoredPath,
                item.ErrorMessage,
                item.ErrorCode,
                item.ErrorDetail
            });
        }

        SendToFrontend(new { type = "decryptBatchDone", totalCount = uuids.Count, successCount });
    }

    /// <summary>
    /// 對應恢復金鑰顯示畫面的「存成檔案」選項：跳原生存檔對話框，把恢復金鑰文字寫進使用者選的檔案。
    /// </summary>
    /// <summary>
    /// 拖放檔案支援：JS 端用 postMessageWithAdditionalObjects 把拖進來的 File 物件連同這則
    /// 訊息一起送過來，這裡收到的每個物件會是 CoreWebView2File——這是 WebView2 官方專門為了
    /// 「從拖放進來的網頁 File 物件反查真正磁碟路徑」設計的機制，讀 .Path 屬性就是真正路徑，
    /// 不是瀏覽器沙盒化、拿不到路徑的一般 File 物件。
    /// </summary>
    private void HandleFilesDroppedFromWebView(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.AdditionalObjects is null)
        {
            return;
        }

        var paths = new List<string>();
        foreach (var obj in e.AdditionalObjects)
        {
            if (obj is CoreWebView2File file && !string.IsNullOrWhiteSpace(file.Path))
            {
                paths.Add(file.Path);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        Activate();
        // 拖放是在已經開著的視窗裡追加檔案，使用者可能已經選了一些東西，前端會把這個訊息
        // 合併進現有清單，不是整份取代（見 App.vue 的 filesDropped 處理）。
        SendToFrontend(new { type = "filesDropped", paths });
    }

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
                SendToFrontend(new { type = "saveRecoveryKeyToFileResult", success = false, errorMessage = ex.Message, errorCode = ErrorCodes.RecoveryKeySaveError, errorDetail = ex.Message });
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
        var result = _protocolHandlers.InspectLockedFile(path);

        SendToFrontend(new
        {
            type = "inspectLockedFileResult",
            result.Success,
            result.Uuid,
            result.OriginalName,
            result.Hint,
            result.PasskeyEnabled,
            result.RecoveryKeyEnabled
        });
    }

    /// <summary>
    /// 純粹給前端「假的進度條」估算時間用——不是真正的加解密進度回報，只是先問一次每個項目
    /// 的大小跟型別（檔案/資料夾），讓前端可以依大小/數量/型別分類決定進度動畫要跑多久、
    /// 資料夾項目要不要多顯示一段「壓縮中」的階段。抓不到大小（例如檔案剛好被移走、資料夾
    /// 存取被拒）就當作 0，這只是體驗用的估算功能，不該讓錯誤影響到後面真正的加密流程能不能跑。
    /// 資料夾大小用遞迴列舉加總，可能要花一點時間，所以丟到背景執行緒。
    /// </summary>
    private async Task HandleGetPathSizesRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .ToList();

        var items = await _protocolHandlers.GetPathSizesAsync(paths);

        SendToFrontend(new { type = "pathSizesResult", items });
    }

    /// <summary>
    /// 加密前的巢狀鎖定掃描——純資訊性用途，前端拿到數量後只會顯示一個不擋流程的提示，
    /// 不是像 getPathSizes 那樣影響進度條估算，也不需要因為抓不到資料而特別處理錯誤情境。
    /// </summary>
    private async Task HandleCheckNestedLocksRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .ToList();

        var count = await _protocolHandlers.CheckNestedLockCountAsync(paths);

        SendToFrontend(new { type = "nestedLockCheckResult", count });
    }

    private void HandleGetSettingsRequest()
    {
        var settings = _protocolHandlers.GetSettings();
        SendToFrontend(new { type = "settingsResult", settings.VaultPath, settings.Language, settings.Theme, settings.CriticalActionConfigured });
    }

    private async Task HandleSetupCriticalActionRequestAsync()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var success = await _protocolHandlers.SetupCriticalActionAsync(hwnd);
        SendToFrontend(new { type = "setupCriticalActionResult", success });
    }

    private async Task HandleVerifyCriticalActionRequestAsync()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var success = await _protocolHandlers.VerifyCriticalActionAsync(hwnd);
        SendToFrontend(new { type = "verifyCriticalActionResult", success });
    }

    private void HandleClearHistoryRequest()
    {
        _protocolHandlers.ClearHistory();
        SendToFrontend(new { type = "clearHistoryResult", success = true });
    }

    private async Task HandleDisableCriticalActionRequestAsync()
    {
        await _protocolHandlers.DisableCriticalActionAsync();
        SendToFrontend(new { type = "disableCriticalActionResult", success = true });
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

        var result = await _protocolHandlers.ChangeVaultPathAsync(newPath);

        SendToFrontend(new
        {
            type = "changeVaultPathResult",
            result.Success,
            newPath = result.NewPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail,
            result.RequiresRestart
        });
    }

    private void HandleUpdateSettingRequest(JsonElement request)
    {
        var key = request.GetProperty("key").GetString() ?? "";
        var value = request.GetProperty("value").GetString() ?? "";

        var result = _protocolHandlers.UpdateSetting(key, value);
        if (!result.Success)
        {
            return;
        }

        // 套用到這個視窗本身的背景色是 WPF 平台細節，設定值有沒有存成功是業務邏輯——
        // 兩件事分開做，UpdateSetting 只管持久化，視覺副作用留在這裡。
        if (key == "theme")
        {
            ApplyWindowBackgroundForTheme(value);
        }

        SendToFrontend(new { type = "updateSettingResult", result.Success, result.Key, result.Value });
    }

    private async Task HandleListVaultRequestAsync()
    {
        var items = await _protocolHandlers.ListVaultAsync();
        SendToFrontend(new { type = "vaultList", items });
    }

    /// <summary>對應「使用紀錄」子頁籤：跟 Vault 目前狀態無關，單純把本機累積的操作日誌全部讀出來。</summary>
    private void HandleListHistoryRequest()
    {
        var items = _protocolHandlers.ListHistory();
        SendToFrontend(new { type = "historyList", items });
    }

    private async Task HandleDeleteRecordRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";

        var result = await _protocolHandlers.DeleteRecordAsync(uuid);

        SendToFrontend(new
        {
            type = "deleteRecordResult",
            uuid,
            result.Success,
            result.BlockedByNestedLocks,
            result.NestedUuids,
            result.ErrorMessage,
            result.ErrorCode
        });
    }

    /// <summary>對應「已加密清單」頁永久刪除前的密碼再驗證，驗證通過前端才會真的送出 deleteRecord。</summary>
    private async Task HandleVerifyPasswordForDeleteRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";

        var result = await _protocolHandlers.VerifyPasswordAsync(uuid, password);

        SendToFrontend(new
        {
            type = "verifyPasswordForDeleteResult",
            uuid,
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
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

    // 既有的匿名型別訊息（type = "..." 這種寫法）本來就直接把 C# 屬性名稱寫成 camelCase，
    // 這個命名策略對它們是不動作（"type" 已經是小寫開頭）；VaultProtocolHandlers 回傳的
    // response record 用慣例的 PascalCase 屬性名稱，靠這個策略轉成前端預期的 camelCase JSON，
    // 兩種寫法可以在同一個 SendToFrontend 底下並存，不用回頭把舊的匿名型別全部改名。
    private static readonly JsonSerializerOptions SendToFrontendJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private void SendToFrontend(object message)
    {
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, SendToFrontendJsonOptions));
    }
}