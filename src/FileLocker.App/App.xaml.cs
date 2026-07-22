using System.IO;
using System.Windows;
using FileLocker.Core;
using FileLocker.Core.History;
using FileLocker.Core.Vault;

namespace FileLocker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker");
        var vaultPath = Path.Combine(appDataDir, "Vault");
        Directory.CreateDirectory(vaultPath);

        var vaultManager = new VaultManager(vaultPath);
        var historyLogger = new HistoryLogger(Path.Combine(appDataDir, "history.jsonl"));
        var lockService = new LockService(vaultManager, historyLogger);

        // Windows 雙擊 .locked 檔案時，會用「檔案路徑當作命令列參數」啟動這支程式。
        // 判斷到這種情況就只開密碼小視窗，完全不建立/載入主視窗跟 WebView2，
        // 這樣反應速度才能真正做到「幾乎瞬間跳出來」。
        if (e.Args.Length > 0 && LooksLikeLockedFileArgument(e.Args[0]))
        {
            var promptWindow = new PasswordPromptWindow(e.Args[0], vaultManager, lockService);
            MainWindow = promptWindow;
            promptWindow.Show();
        }
        else
        {
            var mainWindow = new MainWindow(vaultManager, historyLogger, lockService);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }

    private static bool LooksLikeLockedFileArgument(string arg)
        => File.Exists(arg) && string.Equals(Path.GetExtension(arg), ".locked", StringComparison.OrdinalIgnoreCase);
}