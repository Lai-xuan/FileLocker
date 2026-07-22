using System.IO;
using System.Text.Json;
using System.Windows;
using FileLocker.Core;
using FileLocker.Core.Vault;

namespace FileLocker.App;

public partial class MainWindow : Window
{
    private readonly LockService _lockService;

    public MainWindow()
    {
        InitializeComponent();

        var vaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker", "Vault");
        Directory.CreateDirectory(vaultPath);
        _lockService = new LockService(new VaultManager(vaultPath));

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

                case "pickFile":
                    HandlePickFile();
                    break;

                case "pickFolder":
                    HandlePickFolder();
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

    /// <summary>
    /// WebMessageReceived 在 WPF 這裡是在 UI 執行緒上被觸發的，所以可以直接同步呼叫 ShowDialog()，
    /// 不需要額外用 Dispatcher.Invoke 包一層。
    /// </summary>
    private void HandlePickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選擇要加密的檔案",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            SendToFrontend(new { type = "pathPicked", path = dialog.FileName });
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled" });
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
            SendToFrontend(new { type = "pathPicked", path = dialog.FolderName });
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled" });
        }
    }

    private void SendToFrontend(object message)
    {
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }
}