using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using FileLocker.Core;
using FileLocker.Core.History;
using FileLocker.Core.Vault;

namespace FileLocker.App;

public partial class MainWindow : Window
{
    private readonly VaultManager _vaultManager;
    private readonly HistoryLogger _historyLogger;
    private readonly LockService _lockService;

    /// <summary>
    /// VaultManager／LockService 現在由 App.xaml.cs 統一建立、傳進來——這樣主視窗跟密碼小視窗
    /// 用的是同一份 Vault／History 設定，不會各自重複建立、路徑卻可能不小心兜不起來。
    /// </summary>
    public MainWindow(VaultManager vaultManager, HistoryLogger historyLogger, LockService lockService)
    {
        InitializeComponent();

        _vaultManager = vaultManager;
        _historyLogger = historyLogger;
        _lockService = lockService;

        Loaded += async (_, _) =>
        {
            await MainWebView.EnsureCoreWebView2Async();
            MainWebView.CoreWebView2.Navigate("http://localhost:5173/");
            MainWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        };
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

                case "saveRecoveryKeyToFile":
                    HandleSaveRecoveryKeyToFileRequest(root);
                    break;

                case "inspectLockedFile":
                    HandleInspectLockedFileRequest(root);
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
        var path = request.GetProperty("path").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";
        var hint = request.TryGetProperty("hint", out var hintProp) ? hintProp.GetString() : null;
        var enablePasskey = request.TryGetProperty("enablePasskey", out var passkeyProp) && passkeyProp.GetBoolean();
        var enableRecoveryKey = request.TryGetProperty("enableRecoveryKey", out var recoveryProp) && recoveryProp.GetBoolean();

        var ownerWindowHandle = enablePasskey ? new WindowInteropHelper(this).Handle : IntPtr.Zero;

        var result = await _lockService.EncryptAsync(
            path, password, string.IsNullOrWhiteSpace(hint) ? null : hint,
            enablePasskey, ownerWindowHandle, enableRecoveryKey);

        // 使用者勾了「開啟 Passkey」不代表一定成功啟用（裝置不支援、驗證中途取消都會導致沒真的開成功），
        // 回頭查一次實際的 metadata，讓前端能準確告知使用者「有沒有真的多了這層保護」，不能只看使用者當初的意圖。
        var actuallyPasskeyEnabled = false;
        if (result.Success)
        {
            actuallyPasskeyEnabled = _vaultManager.LoadMetadata(result.Uuid)?.PasskeyEnabled ?? false;
        }

        SendToFrontend(new
        {
            type = "encryptResult",
            success = result.Success,
            uuid = result.Uuid,
            lockedMarkerPath = result.LockedMarkerPath,
            errorMessage = result.ErrorMessage,
            passkeyRequested = enablePasskey,
            passkeyEnabled = actuallyPasskeyEnabled,
            recoveryKey = result.RecoveryKey
        });
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

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = purpose == "decryptPath" ? "選擇要解密的 .locked 檔案" : "選擇要加密的檔案",
            CheckFileExists = true,
            Multiselect = false,
            Filter = purpose == "decryptPath"
                ? "FileLocker 鎖定檔 (*.locked)|*.locked|所有檔案 (*.*)|*.*"
                : "所有檔案 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SendToFrontend(new { type = "pathPicked", purpose, path = dialog.FileName });
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