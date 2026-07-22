using System.IO;
using System.Text.Json;
using System.Windows;
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

        var result = await _lockService.EncryptAsync(path, password, string.IsNullOrWhiteSpace(hint) ? null : hint);

        SendToFrontend(new
        {
            type = "encryptResult",
            success = result.Success,
            uuid = result.Uuid,
            lockedMarkerPath = result.LockedMarkerPath,
            errorMessage = result.ErrorMessage
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

    private async Task HandleListVaultRequestAsync()
    {
        var items = await Task.Run(() => _vaultManager.ScanAll()
            .Select(m =>
            {
                var markerStatus = _lockService.CheckMarkerStatus(m);
                return new
                {
                    uuid = m.Uuid,
                    originalName = m.OriginalName,
                    originalPath = m.OriginalPath,
                    type = m.Type.ToString(),
                    originalSizeBytes = m.OriginalSizeBytes,
                    hint = m.Hint,
                    createdAtUtc = m.CreatedAtUtc,
                    hasNestedLocks = m.ContainsNestedLocks.Count > 0,
                    nestedLockCount = m.ContainsNestedLocks.Count,
                    markerFound = markerStatus.Found,
                    markerStatusMessage = markerStatus.Message
                };
            })
            .OrderByDescending(m => m.createdAtUtc)
            .ToList());

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
                detail = entry.Detail
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