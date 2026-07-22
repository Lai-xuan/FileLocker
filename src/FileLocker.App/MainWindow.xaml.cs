using System.IO;
using System.Text.Json;
using System.Windows;
using FileLocker.Core;
using FileLocker.Core.Vault;

namespace FileLocker.App;

public partial class MainWindow : Window
{
    private readonly VaultManager _vaultManager;
    private readonly LockService _lockService;

    public MainWindow()
    {
        InitializeComponent();

        var vaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker", "Vault");
        Directory.CreateDirectory(vaultPath);
        _vaultManager = new VaultManager(vaultPath);
        _lockService = new LockService(_vaultManager);

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

                case "pickFile":
                    HandlePickFile(root);
                    break;

                case "pickFolder":
                    HandlePickFolder();
                    break;

                case "listVault":
                    await HandleListVaultRequestAsync();
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
    /// 對應規格文件第 4 節：掃描 Vault 內所有 .meta.json 組成清單。ScanAll 本身是同步的檔案 I/O，
    /// 包進 Task.Run 避免項目數量多時卡住 UI 執行緒。
    /// </summary>
    private async Task HandleListVaultRequestAsync()
    {
        var items = await Task.Run(() => _vaultManager.ScanAll()
            .Select(m =>
            {
                // 對應第 4 節「盡力而為」的檢查：只確認原本位置現在還在不在，不做全磁碟掃描。
                var markerStatus = _lockService.CheckMarkerStatus(m);
                return new
                {
                    uuid = m.Uuid,
                    originalName = m.OriginalName,
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

    /// <summary>
    /// 對應規格文件 3.2 節「刪除紀錄時，改成預設直接擋下來」：呼叫 LockService.TryDeleteRecordAsync，
    /// 有巢狀鎖定就回傳擋下的結果讓前端顯示白話提示，不在這一層額外做任何強制刪除的旁路。
    /// </summary>
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

    private void HandlePickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇要加密的資料夾"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SendToFrontend(new { type = "pathPicked", purpose = "encryptPath", path = dialog.FolderName });
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled", purpose = "encryptPath" });
        }
    }

    private void SendToFrontend(object message)
    {
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }
}