using System.Drawing;
using System.Windows.Forms;

namespace FileLocker.App;

/// <summary>
/// 系統匣圖示——背景模式開啟時，關閉所有視窗不會結束程式（見 App.xaml.cs 的
/// ShutdownIfNoWindowsRemain），改成留在這裡，資料夾防護的閒置自動重新上鎖計時器才能持續運作。
///
/// 圖示本身用 System.Windows.Forms.NotifyIcon（WPF 沒有原生托盤圖示 API，混用 WinForms 是標準
/// 做法），但右鍵選單改用自製的 WPF 視窗（TrayMenuWindow），不是 NotifyIcon 內建的
/// ContextMenuStrip——WinForms 的 ContextMenuStrip 是純 GDI 彈出視窗，套用 DWM 圓角會造成殘影
/// （曾經實測出現過，見 TrayMenuWindow 上的說明），WPF 視窗本身就是正常的 DWM 合成表面，
/// 用同一套圓角技巧不會有這個問題。選單文字沿用整個 App 現有的慣例（跟 ShellExtensionRegistrar
/// 那個「已完成右鍵選單設定」訊息框一樣，原生系統層級文字固定用繁體中文，不像 Vue 前端那樣走
/// 雙語 i18n——這是這個 codebase 既有的界線，不是這裡才決定的）。
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly string _theme;
    private readonly Action _openMainWindow;
    private readonly Action _openEncrypt;
    private readonly Action _openList;
    private readonly Action _openFolderGuard;
    private readonly Action _exitApplication;
    private TrayMenuWindow? _menuWindow;

    public TrayIconManager(
        string exePath,
        string theme,
        Action openMainWindow,
        Action openEncrypt,
        Action openList,
        Action openFolderGuard,
        Action exitApplication)
    {
        _theme = theme;
        _openMainWindow = openMainWindow;
        _openEncrypt = openEncrypt;
        _openList = openList;
        _openFolderGuard = openFolderGuard;
        _exitApplication = exitApplication;

        // 直接從執行檔本身抽出已經內嵌的圖示（ApplicationIcon 編譯時期就打包進 exe 資源），
        // 不需要另外準備、複製一份 .ico 檔到輸出目錄——跟 ShellExtensionRegistrar 的
        // DefaultIcon fallback（"{exePath}",0）是同一個思路，只是這裡是直接在程式碼裡抽取。
        var icon = Icon.ExtractAssociatedIcon(exePath);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "FileLocker",
            Visible = true
        };
        // 沒有指定 ContextMenuStrip：右鍵選單自己接管（見 MouseUp），不用 NotifyIcon 內建的
        // WinForms 選單機制。
        _notifyIcon.MouseUp += NotifyIcon_MouseUp;
        _notifyIcon.DoubleClick += (_, _) => _openMainWindow();
    }

    private void NotifyIcon_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        ShowMenu();
    }

    /// <summary>同一時間只會有一個選單視窗——如果剛好還沒收起（理論上不太會發生，Deactivated
    /// 已經會自動關閉，這裡只是防禦性處理），先關掉舊的再開新的，不留兩個疊在一起。</summary>
    private void ShowMenu()
    {
        _menuWindow?.Close();
        _menuWindow = new TrayMenuWindow(
            _theme,
            Cursor.Position,
            _openMainWindow,
            _openEncrypt,
            _openList,
            _openFolderGuard,
            _exitApplication);
        _menuWindow.Closed += (_, _) => _menuWindow = null;
        _menuWindow.Show();
    }

    public void Dispose()
    {
        _menuWindow?.Close();
        // Visible = false 要在 Dispose() 之前——NotifyIcon 的圖示殘影（工作列右下角那個小方塊）
        // 有時候要等下一次滑鼠移過去才會消失，先明確關閉可見性可以避免這個殘影更明顯。
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
