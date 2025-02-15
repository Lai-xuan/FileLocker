using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using FileLockerApp.Helpers;
using FileLockerApp.Properties;

namespace FileLockerApp
{
    public partial class App : Application
    {
        private static Mutex mutex = new Mutex(true, "{UniqueAppMutexName}");

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 取得 LockedDataPath，如果沒有設定則給預設路徑
            if (string.IsNullOrEmpty(FileLockerApp.Properties.Settings.Default.LockedDataPath))
            {
                string defaultPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "locked_data"
                );

                FileLockerApp.Properties.Settings.Default.LockedDataPath = defaultPath;
                FileLockerApp.Properties.Settings.Default.Save();
            }

            // 確保 LockedDataPath 存在
            string lockedDataPath = FileLockerApp.Properties.Settings.Default.LockedDataPath;
            if (!Directory.Exists(lockedDataPath))
            {
                Directory.CreateDirectory(lockedDataPath);
            }

            // 設定 SQLite 資料庫的完整路徑
            string databasePath = Path.Combine(lockedDataPath, "locked_data.db");

            // 初始化 SQLite 資料庫，並傳入 databasePath
            DatabaseManager.InitializeDatabase(databasePath);

            // 確保應用程式不會重複執行
            if (!mutex.WaitOne(TimeSpan.Zero, true))
            {
                MessageBox.Show("FileLocker 已經在執行中！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Application.Current.Shutdown();
                return;
            }

            // 檢查是否從 .locked 或 .flink 檔案啟動
            if (e.Args.Length > 0)
            {
                string filePath = e.Args[0];

                if (filePath.EndsWith(".locked") || filePath.EndsWith(".flink"))
                {
                    new QuickUnlockWindow(filePath).Show();
                    return;
                }
            }

            // 啟動主視窗
            new MainWindow().Show();
        }


        protected override void OnExit(ExitEventArgs e)
        {
            mutex.ReleaseMutex(); // 釋放互斥鎖
            base.OnExit(e);
        }
    }
}
