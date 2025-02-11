using System;
using System.Threading;
using System.Windows;

namespace FileLockerApp
{
    public partial class App : Application
    {
        private static Mutex mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool isNewInstance;
            mutex = new Mutex(true, "Global\\FileLockerApp_Mutex", out isNewInstance);

            if (!isNewInstance)
            {
                MessageBox.Show("應用程式已經在執行中。", "警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                Application.Current.Shutdown();
                return;
            }

            base.OnStartup(e);

            // 這裡手動建立 MainWindow（確保不會開兩個視窗）
            MainWindow mainWindow = new MainWindow();

            // 如果是透過 .locked 檔案關聯開啟的
            if (e.Args.Length > 0)
            {
                string filePath = e.Args[0];

                if (filePath.EndsWith(".locked", StringComparison.OrdinalIgnoreCase))
                {
                    mainWindow.FilePathTextBox.Text = filePath;
                }
            }

            mainWindow.Show();
        }
    }
}
