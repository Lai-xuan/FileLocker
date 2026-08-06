using System.Windows;

namespace FileLocker.App;

/// <summary>
/// 單純呼叫 Activate()（本質上是 SetForegroundWindow）在「雙擊被 Mutex 擋下來的行程，
/// 透過 Named Pipe 轉送參數給已經在跑的實體」這條路徑上不可靠：轉送行程呼叫
/// AllowSetForegroundWindow(ASFW_ANY)（見 App.TryForwardArgsToRunningInstance）給的搶焦權限
/// 只在很短時間內有效（下一次使用者輸入事件、或系統認定經過太久就會失效），但從 Pipe 收到
/// 轉送參數到真正呼叫 Activate() 之間，往往還要先建構一個全新的 MainWindow（含 WebView2
/// 初始化），這段時間常常已經足夠讓權限失效——視窗其實有被建立/顯示出來，只是沒有真的被搶到
/// 最前面，使用者會覺得「已經在系統匣裡的 FileLocker，再雙擊 exe 完全沒反應」。
/// </summary>
internal static class WindowActivation
{
    /// <summary>
    /// Topmost 切換一次是視窗管理員層級的 z-order 操作，不依賴 AllowSetForegroundWindow
    /// 給的、會過期的搶焦權限，兩個行程之間隔了多久都不受影響，能確保視窗真的被拉到最上層。
    /// </summary>
    public static void ForceToForeground(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Topmost = true;
        window.Topmost = false;
        window.Activate();
    }
}
