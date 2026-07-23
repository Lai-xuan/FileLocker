using System.IO;
using System.Windows;
using FileLocker.Core;
using FileLocker.Core.History;
using FileLocker.Core.Security;
using FileLocker.Core.Settings;
using FileLocker.Core.Vault;

namespace FileLocker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // appDataDir 是固定的（不可搬）：App 本身的設定、使用紀錄、鎖定狀態都放這裡，
        // 跟 Vault 內容（可以搬到別的位置）分開處理，見規格文件第 6 節。
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FileLocker");
        Directory.CreateDirectory(appDataDir);

        var settingsManager = new AppSettingsManager(Path.Combine(appDataDir, "settings.json"));
        var settings = settingsManager.Load();

        // 第一次啟動、還沒設定過 Vault 位置的話，用預設路徑並存回設定檔，之後都以設定檔為準。
        if (string.IsNullOrWhiteSpace(settings.VaultPath))
        {
            settings.VaultPath = Path.Combine(appDataDir, "Vault");
            settingsManager.Save(settings);
        }

        Directory.CreateDirectory(settings.VaultPath);

        var vaultManager = new VaultManager(settings.VaultPath);
        var historyLogger = new HistoryLogger(Path.Combine(appDataDir, "history.jsonl"));
        var lockoutTracker = new LockoutTracker(Path.Combine(appDataDir, "lockout.json"));
        var lockService = new LockService(vaultManager, historyLogger, lockoutTracker);

        if (e.Args.Length > 0 && LooksLikeLockedFileArgument(e.Args[0]))
        {
            var promptWindow = new PasswordPromptWindow(e.Args[0], vaultManager, lockService);
            MainWindow = promptWindow;
            promptWindow.Show();
        }
        else
        {
            var mainWindow = new MainWindow(vaultManager, historyLogger, lockService, settingsManager, settings, appDataDir);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }

    private static bool LooksLikeLockedFileArgument(string arg)
        => File.Exists(arg) && string.Equals(Path.GetExtension(arg), ".locked", StringComparison.OrdinalIgnoreCase);
}