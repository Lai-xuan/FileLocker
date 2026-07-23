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

        if (e.Args.Length == 1 && LooksLikeLockedFileArgument(e.Args[0]))
        {
            var promptWindow = new PasswordPromptWindow(e.Args[0], vaultManager, lockService);
            MainWindow = promptWindow;
            promptWindow.Show();
        }
        else
        {
            var initialPaths = e.Args.Length > 0 ? ResolveInitialPaths(e.Args) : null;
            var mainWindow = new MainWindow(vaultManager, historyLogger, lockService, settingsManager, settings, appDataDir, initialPaths);
            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }

    private static bool LooksLikeLockedFileArgument(string arg)
        => File.Exists(arg) && string.Equals(Path.GetExtension(arg), ".locked", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 對應規格文件第 5.2 節：Shell Extension 選取數量/長度超過門檻時，不會把每個路徑各自當一個命令列參數，
    /// 而是寫進一個暫存清單檔，只傳「@檔案路徑」這一個參數過來，這裡要反過來把清單讀出來。
    /// </summary>
    private static List<string> ResolveInitialPaths(string[] args)
    {
        if (args.Length == 1 && args[0].StartsWith('@'))
        {
            var listFilePath = args[0][1..];
            try
            {
                var paths = File.ReadAllLines(listFilePath)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();

                // 讀完就刪掉，內容是使用者選了哪些檔案路徑，沒必要一直留在 %TEMP% 裡。
                try { File.Delete(listFilePath); } catch (IOException) { /* 盡力而為，刪不掉不影響主要流程 */ }

                return paths;
            }
            catch (IOException)
            {
                return new List<string>();
            }
        }

        return args.ToList();
    }
}