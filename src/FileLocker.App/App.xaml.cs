using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FileLocker.Core;
using FileLocker.Core.FolderGuard;
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

    // OnExit 判斷要不要釋放 Mutex 用——只有真正拿到所有權的（第一個）行程可以釋放，見 OnExit 的說明。
    private bool _ownsSingleInstanceMutex;

    // 這些欄位是給 HandleLaunchArgs 用的——不管是這次啟動本身要處理的參數，
    // 還是之後透過 Named Pipe 收到、從其他行程轉送過來的參數，都走同一套邏輯，
    // 所以需要把建立好的這幾個共用元件存起來，而不是侷限在 OnStartup 的區域變數裡。
    private VaultManager? _vaultManager;
    private HistoryLogger? _historyLogger;
    private LockService? _lockService;
    private AppSettingsManager? _settingsManager;
    private AppSettings? _settings;
    private string? _appDataDir;
    private VaultIndexCache? _vaultIndexCache;
    private VaultChangeWatcher? _vaultChangeWatcher;
    private FolderGuardService? _folderGuardService;
    private DispatcherTimer? _folderGuardAutoRelockTimer;
    private TrayIconManager? _trayIconManager;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 改成手動控制何時真正結束整個 App，而不是「第一個視窗一關就結束」的預設行為——
        // 之後可能會同時開著 MainWindow 跟好幾個 PasswordPromptWindow，任何一個先關掉
        // 都不該讓整個 App 跟著結束，只有全部視窗都關了才真的結束。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _singleInstanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            // 已經有一個實體在跑了：把這次的命令列參數轉送過去，自己不開任何視窗，直接結束。
            // 注意：這個行程從來沒有真正拿到 Mutex 的所有權（Mutex(true, ...) 的 initiallyOwned
            // 只有在「真的建立了新的 Mutex」時才會生效，這裡 isFirstInstance 是 false，代表
            // Mutex 早就存在、所有權在另一個行程手上）——OnExit 之後一定不能對這個 Mutex
            // 呼叫 ReleaseMutex，否則會因為「釋放一個自己沒有持有的鎖」丟出未處理例外，
            // 讓這個原本只是負責轉送參數、馬上要結束的行程整個當掉（曾經是右鍵「上鎖」在背景已
            // 開啟時完全沒反應的真正原因：每次右鍵動作都會讓這個轉送行程立刻崩潰）。
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

        // 資料夾防護（Folder Guard）：獨立於 Vault 之外的本機儲存，見規劃文件第 11 節。
        // 憑證與清單存在自己的資料夾，鎖定狀態也用自己獨立的檔案（folder-guard-unlock 這個
        // 鍵值代表整個共用密碼，不是像加密那樣每個項目各自一把，見規劃文件第 3 節）。
        var folderGuardDir = Path.Combine(appDataDir, "FolderGuard");
        Directory.CreateDirectory(folderGuardDir);
        var folderGuardStore = new FolderGuardStore(Path.Combine(folderGuardDir, "guarded-folders.json"));
        var folderGuardLockout = new LockoutTracker(Path.Combine(folderGuardDir, "lockout.json"));
        _folderGuardService = new FolderGuardService(folderGuardStore, folderGuardLockout);

        // LockService 透過這個委派得知目前有哪些資料夾正在防護中，用來在加密流程一開始就擋下
        // 內含巢狀防護資料夾的情況（見 LockService.EncryptAsync、規劃文件第 8 節）——LockService
        // 本身不需要知道 FolderGuardService／FolderGuardStore 型別的存在，只吃這個委派。
        _lockService = new LockService(_vaultManager, _historyLogger, lockoutTracker,
            getGuardedFolderPaths: () => folderGuardStore.ListWithSelfHeal()
                .Where(entry => entry.Status == FolderGuardStatus.Locked)
                .Select(entry => entry.Path)
                .ToList());
        _settingsManager = settingsManager;
        _settings = settings;
        _appDataDir = appDataDir;

        // 清單頁快取索引：跟 appDataDir 一樣是固定、不可搬的本機路徑（不能放 Vault 資料夾內，
        // 見 VaultIndexCache 上的說明），VaultIndexCache 建構時就會確保快取跟目前 Vault 路徑
        // 一致（不一致就整個重建），建構完成後 GetItems() 保證可用。
        _vaultIndexCache = new VaultIndexCache(_vaultManager, Path.Combine(appDataDir, "VaultIndexCache"));
        _vaultChangeWatcher = new VaultChangeWatcher(settings.VaultPath, _vaultIndexCache);
        _vaultChangeWatcher.Start();

        StartPipeServerListener();
        StartFolderGuardAutoRelockTimer();

        // 系統匣常駐、跟隨 Windows 啟動：兩個獨立設定，各自只在開啟時才生效，關閉的人完全
        // 不會看到系統匣圖示／不會被登記到 Run 機碼，行為跟這兩個功能出現之前一模一樣。
        StartupRegistrar.EnsureConsistent(settings.LaunchAtStartupEnabled, GetAppExePath());
        if (settings.MinimizeToTrayEnabled)
        {
            CreateTrayIcon();
        }

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

    /// <summary>解鎖後閒置自動重新上鎖：啟動時補跑一次（涵蓋「上次關閉前忘記重新上鎖、這次
    /// 重開機才發現已經過期」的情境），之後每 60 秒輪詢一次——門檻是分鐘級精度（預設 15 分鐘），
    /// 60 秒輪詢造成的最大延遲在這個時間尺度下可忽略，不需要更頻繁。兩條路徑呼叫的是
    /// FolderGuardService 裡同一個冪等方法，這裡只負責排程，不重複判斷邏輯。只在第一個實體、
    /// _folderGuardService 已經建構完成後才會呼叫到，不牽涉 Mutex 相關的啟動路徑（CLAUDE.md
    /// 已知的坑那則提醒的是背景轉送行程，跟這裡的計時器排程無關）。</summary>
    private void StartFolderGuardAutoRelockTimer()
    {
        _ = RunFolderGuardAutoRelockCheckAsync();

        _folderGuardAutoRelockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _folderGuardAutoRelockTimer.Tick += async (_, _) => await RunFolderGuardAutoRelockCheckAsync();
        _folderGuardAutoRelockTimer.Start();
    }

    /// <summary>包一層 try/catch——DispatcherTimer.Tick 的 async void 事件處理常式如果丟出未處理
    /// 例外會直接讓整個行程當掉，這個計時器會一直跑到 App 結束，不能讓單次失敗（例如某個資料夾
    /// 剛好在檢查瞬間被外部刪除）拖垮後續所有 tick。</summary>
    private async Task RunFolderGuardAutoRelockCheckAsync()
    {
        try
        {
            await _folderGuardService!.RelockExpiredEntriesAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _folderGuardAutoRelockTimer?.Stop();
        _trayIconManager?.Dispose();
        _vaultChangeWatcher?.Dispose();
        _vaultIndexCache?.Dispose();

        // 只有真正拿到所有權的第一個行程才能釋放——被 Mutex 擋下來、只負責轉送參數就結束的
        // 行程從來沒有持有過它，呼叫 ReleaseMutex 會丟出 ApplicationException（釋放一個自己
        // 沒有持有的鎖），見 OnStartup 的說明。
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 不管是這次啟動本身要處理的參數，還是之後從其他（被擋下來的）行程轉送過來的參數，
    /// 都走這個方法，行為完全一致——這是「單一執行個體」機制的核心：外部看起來像是
    /// 開了一支新的 FileLocker，實際上都是同一支行程在處理。
    /// </summary>
    // Shell Extension 右鍵「上鎖」／「解鎖」命令列旗標（見 dllmain.cpp InvokeCommand），跟現有的
    // 「直接傳路徑＝加密」預設行為區隔開——資料夾防護是完全不同的操作，不能讓 Shell Extension
    // 傳來的路徑預設被當成要加密的東西。
    private const string FolderGuardLockArgFlag = "--folder-guard-lock";
    private const string FolderGuardUnlockArgFlag = "--folder-guard-unlock";

    // 雙擊 `.lockfolder` 標記檔時 ShellExtensionRegistrar 註冊的檔案關聯用這個旗標啟動——
    // 帶進來的是標記檔自己的路徑，不是真正的資料夾路徑，要先讀出標記檔內容轉換過，
    // 才能沿用既有的 HandleFolderGuardUnlockLaunch（見 FolderGuardUnlockMarkerFile 上的說明）。
    private const string FolderGuardUnlockMarkerArgFlag = "--folder-guard-unlock-marker";

    // 跟隨 Windows 啟動時，StartupRegistrar 登記的 Run 值帶這個旗標啟動——不開任何視窗，
    // 只留系統匣圖示（背景服務在呼叫到這裡之前，OnStartup 已經全部啟動完成）。
    private const string StartupArgFlag = "--startup";

    /// <summary>
    /// 「旗標 → 該開哪個資料夾防護進入點」的對應表：之後新增 Folder Guard 命令列旗標，
    /// 只需要在這裡加一列，不需要去改 HandleLaunchArgs 本身的控制流程。
    /// </summary>
    private Dictionary<string, Action<List<string>>>? _folderGuardLaunchHandlers;
    private Dictionary<string, Action<List<string>>> FolderGuardLaunchHandlers => _folderGuardLaunchHandlers ??= new()
    {
        // 右鍵「上鎖」（見規劃文件第 6 節）：已經設定過共用密碼就走瞬間確認的原生小視窗，
        // 不開主視窗；還沒設定過就退回開主視窗、導引使用者先完成首次設定。
        [FolderGuardLockArgFlag] = HandleFolderGuardLockLaunch,

        // 右鍵「解鎖」：解鎖一定要驗證身份，不會有「還沒設定過」要導去首次設定的分支——
        // 右鍵會顯示「解鎖」代表這些資料夾已經是鎖定狀態，資料夾防護一定已經設定過。
        [FolderGuardUnlockArgFlag] = paths => HandleFolderGuardUnlockLaunch(paths),

        // 雙擊 `.lockfolder` 標記檔：帶進來的是標記檔路徑，先轉成標記檔裡記錄的真正資料夾路徑，
        // 再沿用同一套解鎖流程。
        [FolderGuardUnlockMarkerArgFlag] = HandleFolderGuardUnlockMarkerLaunch,
    };

    private void HandleLaunchArgs(string[] args)
    {
        if (args.Length == 1 && args[0] == StartupArgFlag)
        {
            if (_trayIconManager is null)
            {
                // 背景模式已經被使用者關閉，但這次登入還是被舊的 Run 登錄值觸發（下次登入就
                // 不會了，OnStartup 已經呼叫過 StartupRegistrar.EnsureConsistent 把它刪掉）——
                // 沒有視窗也沒有系統匣圖示可以留著，直接結束，不要變成看不見、殺不掉的殭屍行程。
                Shutdown();
            }
            return;
        }

        // 雙擊 .locked 檔案：允許同時存在多個 PasswordPromptWindow（使用者可能想同時解鎖
        // 好幾個不同的項目），每次都開一個新的，不嘗試去找「有沒有已經開著的」。
        if (args.Length == 1 && LooksLikeLockedFileArgument(args[0]))
        {
            var promptWindow = new PasswordPromptWindow(args[0], _vaultManager!, _lockService!, _settings!.Theme);
            promptWindow.Closed += (_, _) => ShutdownIfNoWindowsRemain();
            // 這裡也可能是已經在系統匣裡的實體透過 Named Pipe 收到轉送過來的（見 WindowActivation
            // 上的說明），不能只靠 Show()。
            WindowActivation.ForceToForeground(promptWindow);
            return;
        }

        if (args.Length >= 1 && FolderGuardLaunchHandlers.TryGetValue(args[0], out var folderGuardHandler))
        {
            folderGuardHandler(ResolveInitialPaths(args[1..]));
            return;
        }

        var initialPaths = args.Length > 0 ? ResolveInitialPaths(args) : new List<string>();

        // 加密用的路徑（右鍵選單多選、或其他情境）：如果已經有一個 MainWindow 開著，
        // 就把新的路徑送進那一個既有的視窗、順便搶回前景焦點，不要再開一個新視窗——
        // 這正是這個機制原本要解決的問題：右鍵選單觸發好幾次，畫面上不該同時冒出好幾個 FileLocker。
        var existingMainWindow = Windows.OfType<MainWindow>().FirstOrDefault();
        if (existingMainWindow is not null)
        {
            existingMainWindow.ApplyIncomingPaths(initialPaths, "encrypt");
            return;
        }

        OpenMainWindow(initialPaths.Count > 0 ? initialPaths : null, "encrypt");
    }

    private void HandleFolderGuardLockLaunch(List<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (!_folderGuardService!.IsConfigured)
        {
            OpenMainWindow(paths, "folderGuardSetup");
            return;
        }

        var confirmWindow = new FolderGuardConfirmLockWindow(
            paths, _folderGuardService, _settings!.Theme,
            openEncryptTab: encryptPaths =>
            {
                var existingMainWindow = Windows.OfType<MainWindow>().FirstOrDefault();
                if (existingMainWindow is not null)
                {
                    existingMainWindow.ApplyIncomingPaths(encryptPaths.ToList(), "encrypt");
                }
                else
                {
                    OpenMainWindow(encryptPaths.ToList(), "encrypt");
                }
            });
        confirmWindow.Closed += (_, _) => ShutdownIfNoWindowsRemain();
        // 這次動作很可能是背景執行個體透過 Named Pipe 收到轉送過來的（見 StartPipeServerListener），
        // 單純 Show()＋Activate() 不保證能把視窗搶到前景，見 WindowActivation 上的說明。
        WindowActivation.ForceToForeground(confirmWindow);
    }

    private void HandleFolderGuardUnlockLaunch(List<string> paths, bool openFoldersAfterUnlock = false)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var unlockWindow = new FolderGuardUnlockPromptWindow(paths, _folderGuardService!, _settings!.Theme, openFoldersAfterUnlock);
        unlockWindow.Closed += (_, _) => ShutdownIfNoWindowsRemain();
        WindowActivation.ForceToForeground(unlockWindow);
    }

    /// <summary>雙擊 `.lockfolder` 標記檔的入口：帶進來的每個路徑都是標記檔本身，不是真正的
    /// 資料夾——先讀出標記檔內容轉成資料夾路徑，讀不到（檔案被搬移/刪除/內容損毀）的直接
    /// 忽略那一筆，不中止其他還讀得到的項目，再沿用既有的 <see cref="HandleFolderGuardUnlockLaunch"/>。
    /// 這個入口點的使用者意圖是「打開這個資料夾」，解鎖成功後要接著開啟資料夾本身
    /// （見 FolderGuardUnlockPromptWindow 建構子上的說明），跟右鍵選單「解鎖」不同。</summary>
    private void HandleFolderGuardUnlockMarkerLaunch(List<string> markerPaths)
    {
        var folderPaths = markerPaths
            .Select(FolderGuardUnlockMarkerFile.ReadTargetFolderPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();

        HandleFolderGuardUnlockLaunch(folderPaths, openFoldersAfterUnlock: true);
    }

    /// <summary>HandleLaunchArgs 裡兩個「需要開一個全新 MainWindow」的分支共用：一般加密路徑、
    /// 跟資料夾防護首次設定導引都走這裡，只差 initialAction 要帶什麼值。</summary>
    private void OpenMainWindow(List<string>? initialPaths, string? initialAction)
    {
        var mainWindow = new MainWindow(
            _vaultManager!, _historyLogger!, _lockService!, _settingsManager!, _settings!, _appDataDir!,
            _vaultIndexCache!, _vaultChangeWatcher!, _folderGuardService!,
            initialPaths, initialAction);
        mainWindow.Closed += (_, _) => ShutdownIfNoWindowsRemain();
        MainWindow = mainWindow;
        // 這個方法本來只有雙擊/右鍵選單觸發的路徑會走到，都是直接從使用者輸入事件延伸出來，
        // 前景權限通常還在；但系統匣選單（TrayMenuWindow）點擊之後，視窗建立的當下前一個
        // 有前景權限的視窗（TrayMenuWindow 自己）已經先 Close() 掉了；另外背景實體透過 Named
        // Pipe 收到轉送參數（雙擊已經在系統匣裡的實體的 exe）時，建構這個新視窗（含 WebView2
        // 初始化）通常已經花掉太多時間，轉送行程給的搶焦權限早就過期——兩種情況單純 Show()＋
        // Activate() 都不保證新視窗會被搶到最前面，見 WindowActivation 上的說明。
        WindowActivation.ForceToForeground(mainWindow);
    }

    // ShellExtensionRegistrar 內部也算了一份一模一樣的值——兩邊是各自獨立的登錄檔登記職責，
    // 不特地共用一個欄位，這裡額外包成方法只是因為現在有兩個呼叫點（啟動時、設定切換時）。
    private static string GetAppExePath()
        => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileLocker.App.exe");

    private void CreateTrayIcon()
    {
        _trayIconManager = new TrayIconManager(
            GetAppExePath(),
            _settings!.Theme,
            openMainWindow: () => ShowMainWindow(null),
            openEncrypt: () => ShowMainWindow("encrypt"),
            openList: () => ShowMainWindow("list"),
            openFolderGuard: () => ShowMainWindow("folderGuard"),
            exitApplication: ExitApplicationFromTray);
    }

    /// <summary>設定頁「關閉視窗後留在系統匣」開關即時生效用——之前這個設定只在 OnStartup 讀取
    /// 一次，切換設定只會存檔、不會真的建立/移除系統匣圖示，導致關掉這個選項後背景執行照樣
    /// 繼續、開啟後也要重開 App 才有系統匣圖示，兩種情況都跟畫面上顯示的狀態對不起來。</summary>
    internal void ApplyMinimizeToTraySetting(bool enabled)
    {
        if (enabled)
        {
            if (_trayIconManager is null)
            {
                CreateTrayIcon();
            }
            return;
        }

        if (_trayIconManager is null)
        {
            return;
        }

        _trayIconManager.Dispose();
        _trayIconManager = null;

        // 使用者可能是從系統匣選單開設定頁改的——這種情況現在已經沒有任何視窗開著，關掉這個
        // 設定之後系統匣圖示也拿掉了，不能留在一個「看不見、殺不掉」的殭屍狀態，要立刻真正結束。
        ShutdownIfNoWindowsRemain();
    }

    /// <summary>設定頁「跟著 Windows 啟動」開關即時生效用——之前一樣只在 OnStartup 呼叫過一次
    /// StartupRegistrar，切換設定只會存檔、不會真的更新 Run 機碼，關掉這個選項要等下一次啟動
    /// 才會真的反登記，這段期間畫面上顯示「已關閉」但登錄檔其實還在，不一致。</summary>
    internal void ApplyLaunchAtStartupSetting(bool enabled)
        => StartupRegistrar.EnsureConsistent(enabled, GetAppExePath());

    /// <summary>系統匣選單「開啟 FileLocker」／雙擊圖示，以及「加密」「已加密清單」「資料夾防護」
    /// 三個分頁捷徑共用：已經有主視窗開著就把它搶到前景＋切分頁，沒有就開一個新的直接帶指定分頁。
    /// NotifyIcon 的事件是在建立它的執行緒（這裡是 WPF UI 執行緒）的訊息迴圈上觸發的，不需要
    /// 額外的 System.Windows.Forms.Application.Run()，也不需要 Dispatcher.Invoke 切執行緒。</summary>
    private void ShowMainWindow(string? initialAction)
    {
        var existingMainWindow = Windows.OfType<MainWindow>().FirstOrDefault();
        if (existingMainWindow is not null)
        {
            existingMainWindow.ApplyIncomingPaths(new List<string>(), initialAction);
        }
        else
        {
            OpenMainWindow(null, initialAction);
        }
    }

    /// <summary>系統匣選單「結束 FileLocker」——唯一真正結束程式的路徑，不透過
    /// ShutdownIfNoWindowsRemain（背景模式開啟時那個方法故意不結束程式）。</summary>
    private void ExitApplicationFromTray()
    {
        _trayIconManager?.Dispose();
        _trayIconManager = null;
        Shutdown();
    }

    private void ShutdownIfNoWindowsRemain()
    {
        if (Windows.Count > 0)
        {
            return;
        }

        // 背景模式開啟（系統匣圖示還在）：所有視窗都關了不代表使用者想結束程式，留著讓資料夾
        // 防護的閒置自動重新上鎖計時器繼續跑，只有系統匣選單的「結束 FileLocker」才會真的結束。
        if (_trayIconManager is not null)
        {
            return;
        }

        Shutdown();
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

                    // HandleLaunchArgs 丟例外要讓使用者看得到——之前整個 try 區塊共用同一個
                    // 靜默吞例外的 catch，導致「右鍵動作轉送過來、但視窗建立過程出錯」這種情況
                    // 完全沒有任何回饋，使用者只會覺得「什麼都沒發生」，沒辦法回報是哪裡壞了。
                    // Pipe 連線本身（等待連線、讀取資料）失敗是預期內、可以安靜重試的情境，
                    // 跟這裡分開處理。
                    try
                    {
                        Dispatcher.Invoke(() => HandleLaunchArgs(forwardedArgs));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => MessageBox.Show(
                            $"處理右鍵動作時發生錯誤：\n{ex}",
                            "FileLocker", MessageBoxButton.OK, MessageBoxImage.Error));
                    }
                }
                catch (Exception)
                {
                    // 這個背景監聽迴圈本身不能因為單次連線失敗就整個停掉（沒有 GUI 可以顯示錯誤），
                    // 吞掉繼續等下一次連線，最壞情況只是那一次轉送沒有成功。這裡只涵蓋 Pipe 連線/
                    // 讀取本身的失敗，不包含上面 HandleLaunchArgs 的例外（那個已經另外處理過了）。
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

            // Windows 有「防止搶焦點」機制：背景中的舊行程（沒有前景權限）呼叫 Window.Activate()
            // 內部其實是呼叫 SetForegroundWindow，但那個 API 在呼叫端行程不是目前前景行程時
            // 會被系統直接忽略——單純補上 Activate() 沒辦法讓背景執行個體真的把視窗搶到最上面。
            // 這個轉送行程是 Explorer 因為使用者剛剛的右鍵點擊直接產生的，本身握有前景權限，
            // 可以呼叫 AllowSetForegroundWindow(ASFW_ANY) 把這個權限短暫開放給任何行程，讓舊行程
            // 接下來呼叫的 Activate() 真的能生效，而不是被系統悄悄擋下、看起來完全沒反應。
            AllowSetForegroundWindow(AsfwAny);
        }
        catch (Exception)
        {
            // 轉送失敗（例如剛好在那個瞬間第一個實體正在重啟監聽迴圈）就放棄，
            // 這次操作沒反應，比意外開出第二個視窗互相打架更容易處理／不會造成資料風險。
        }
    }

    // ASFW_ANY：傳給 AllowSetForegroundWindow 代表「任何行程」，不用另外把目標行程的 PID
    // 透過 Pipe 傳回來比對——反正這個權限只維持到下一次使用者輸入為止，開放給任何行程用
    // 不會有安全疑慮（見 Win32 文件：AllowSetForegroundWindow 的效果在使用者下一次操作
    // 滑鼠／鍵盤時就會自動失效）。
    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}