using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using FileLocker.Core;
using FileLocker.Core.History;
using FileLocker.Core.Security;
using FileLocker.Core.Settings;
using FileLocker.Core.Vault;

namespace FileLocker.App;

public partial class App : Application
{
    // 純本機、單一使用者範圍內的名稱即可，不加 Global\ 前綴——不同使用者各自能跑自己的一份，
    // 只擋同一個使用者底下重複開啟多個實體。
    private const string MutexName = "FileLocker-SingleInstance-Mutex";
    private const string PipeName = "FileLocker-SingleInstance-Pipe";

    private Mutex? _singleInstanceMutex;

    // 這些欄位是給 HandleLaunchArgs 用的——不管是這次啟動本身要處理的參數，
    // 還是之後透過 Named Pipe 收到、從其他行程轉送過來的參數，都走同一套邏輯，
    // 所以需要把建立好的這幾個共用元件存起來，而不是侷限在 OnStartup 的區域變數裡。
    private VaultManager? _vaultManager;
    private HistoryLogger? _historyLogger;
    private LockService? _lockService;
    private AppSettingsManager? _settingsManager;
    private AppSettings? _settings;
    private string? _appDataDir;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 改成手動控制何時真正結束整個 App，而不是「第一個視窗一關就結束」的預設行為——
        // 之後可能會同時開著 MainWindow 跟好幾個 PasswordPromptWindow，任何一個先關掉
        // 都不該讓整個 App 跟著結束，只有全部視窗都關了才真的結束。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstanceMutex = new Mutex(true, MutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            // 已經有一個實體在跑了：把這次的命令列參數轉送過去，自己不開任何視窗，直接結束。
            TryForwardArgsToRunningInstance(e.Args);
            Shutdown();
            return;
        }

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

        _vaultManager = new VaultManager(settings.VaultPath);
        _historyLogger = new HistoryLogger(Path.Combine(appDataDir, "history.jsonl"));
        var lockoutTracker = new LockoutTracker(Path.Combine(appDataDir, "lockout.json"));
        _lockService = new LockService(_vaultManager, _historyLogger, lockoutTracker);
        _settingsManager = settingsManager;
        _settings = settings;
        _appDataDir = appDataDir;

        StartPipeServerListener();

        // 檢查／需要的話自動註冊 Shell Extension（見 ShellExtensionRegistrar 說明）。
        // 全新安裝、或應用程式資料夾被搬移過之後，這裡會真的執行註冊動作並回傳 true，
        // 這種情況要提示使用者重啟 Explorer，右鍵選單才會出現新登錄的項目
        // （Explorer 對 Shell Extension 有自己的快取，不會即時反映登錄檔變化）。
        var justRegisteredShellExtension = ShellExtensionRegistrar.EnsureRegistered();

        HandleLaunchArgs(e.Args);

        if (justRegisteredShellExtension)
        {
            MessageBox.Show(
                "已完成右鍵選單設定。需要重新啟動 Windows 檔案總管，右鍵選單裡才會出現「使用 FileLocker 加密」的選項——" +
                "可以到工作管理員裡找到「Windows 檔案總管」，按右鍵選「重新啟動」，或登出再登入也可以。",
                "FileLocker",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 不管是這次啟動本身要處理的參數，還是之後從其他（被擋下來的）行程轉送過來的參數，
    /// 都走這個方法，行為完全一致——這是「單一執行個體」機制的核心：外部看起來像是
    /// 開了一支新的 FileLocker，實際上都是同一支行程在處理。
    /// </summary>
    private void HandleLaunchArgs(string[] args)
    {
        // 雙擊 .locked 檔案：允許同時存在多個 PasswordPromptWindow（使用者可能想同時解鎖
        // 好幾個不同的項目），每次都開一個新的，不嘗試去找「有沒有已經開著的」。
        if (args.Length == 1 && LooksLikeLockedFileArgument(args[0]))
        {
            var promptWindow = new PasswordPromptWindow(args[0], _vaultManager!, _lockService!);
            promptWindow.Closed += (_, _) => ShutdownIfNoWindowsRemain();
            promptWindow.Show();
            return;
        }

        var initialPaths = args.Length > 0 ? ResolveInitialPaths(args) : new List<string>();

        // 加密用的路徑（右鍵選單多選、或其他情境）：如果已經有一個 MainWindow 開著，
        // 就把新的路徑送進那一個既有的視窗、順便搶回前景焦點，不要再開一個新視窗——
        // 這正是這個機制原本要解決的問題：右鍵選單觸發好幾次，畫面上不該同時冒出好幾個 FileLocker。
        var existingMainWindow = Windows.OfType<MainWindow>().FirstOrDefault();
        if (existingMainWindow is not null)
        {
            existingMainWindow.ApplyIncomingPaths(initialPaths);
            return;
        }

        var mainWindow = new MainWindow(
            _vaultManager!, _historyLogger!, _lockService!, _settingsManager!, _settings!, _appDataDir!,
            initialPaths.Count > 0 ? initialPaths : null);
        mainWindow.Closed += (_, _) => ShutdownIfNoWindowsRemain();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private void ShutdownIfNoWindowsRemain()
    {
        if (Windows.Count == 0)
        {
            Shutdown();
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

    /// <summary>
    /// 第一個實體背景監聽：等待之後可能被 Mutex 擋下來的行程透過 Named Pipe 把參數轉送過來。
    /// 收到之後要切回 UI 執行緒才能操作 WPF 視窗，所以用 Dispatcher.Invoke 包起來。
    /// 這個迴圈本身沒有停止條件——App 結束時整個行程連背景執行緒一起終止，不需要額外收尾。
    /// </summary>
    private void StartPipeServerListener()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    // PipeOptions.CurrentUserOnly：限制只有目前這個 Windows 使用者能連進這個管道，
                    // 避免同一台機器上其他登入的使用者（例如透過快速切換使用者、遠端桌面）能連進來
                    // 塞任意路徑給這個正在跑的 FileLocker 實體。
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync();

                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var json = await reader.ReadToEndAsync();

                    var forwardedArgs = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();

                    Dispatcher.Invoke(() => HandleLaunchArgs(forwardedArgs));
                }
                catch (Exception)
                {
                    // 這個背景監聽迴圈本身不能因為單次連線失敗就整個停掉（沒有 GUI 可以顯示錯誤），
                    // 吞掉繼續等下一次連線，最壞情況只是那一次轉送沒有成功。
                }
            }
        });
    }

    /// <summary>
    /// 被 Mutex 擋下來的（第二個以後啟動的）行程呼叫這個方法，把自己的命令列參數
    /// 透過 Named Pipe 傳給第一個實體，然後這個行程本身就結束了，不開任何視窗。
    /// </summary>
    private static void TryForwardArgsToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000); // 2 秒逾時，避免真的連不上時整個行程卡住不結束

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(JsonSerializer.Serialize(args));
            writer.Flush();
        }
        catch (Exception)
        {
            // 轉送失敗（例如剛好在那個瞬間第一個實體正在重啟監聽迴圈）就放棄，
            // 這次操作沒反應，比意外開出第二個視窗互相打架更容易處理／不會造成資料風險。
        }
    }
}