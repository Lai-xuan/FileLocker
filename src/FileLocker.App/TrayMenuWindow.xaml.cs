using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FileLocker.App;

/// <summary>
/// 系統匣圖示右鍵選單，取代原本的 WinForms ContextMenuStrip（見 TrayIconManager 上的說明）。
/// </summary>
public partial class TrayMenuWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    // 內容固定（5 個按鈕＋2 條分隔線），高度可以預先估算，不用等版面量測完成才能定位——
    // 拿這個常數在視窗顯示「之前」就算出大概要放在螢幕的哪裡，避免 Loaded 之後才重新定位
    // 導致選單先閃一下在錯的地方。實際渲染高度由 SizeToContent="Height" 決定，這個常數
    // 只影響定位的準確度（差個幾像素，不影響能不能用）。
    private const double EstimatedHeightDip = 240;
    private const double WidthDip = 200;

    private readonly System.Drawing.Point _cursorPhysicalPosition;
    private readonly Action _openMainWindow;
    private readonly Action _openEncrypt;
    private readonly Action _openList;
    private readonly Action _openFolderGuard;
    private readonly Action _exitApplication;

    // 按選單項目時 Close() 會讓視窗失去作用中狀態，同步觸發 Window_Deactivated，裡面也呼叫
    // Close()——沒有這個旗標會重入呼叫已經在關閉中的視窗的 Close()，WPF 在這個情境下會丟
    // InvalidOperationException，這個例外沒被接住的話會直接讓整個行程當掉（曾經實測發生過：
    // 點選單項目沒反應、整個 App 跟著消失）。呼叫端要做的事延後到 Closed 事件才執行，
    // 確保視窗真的關閉完成之後才去開新視窗／結束程式，不會跟關閉流程本身互相打架。
    private bool _isClosing;
    private Action? _pendingAction;

    public TrayMenuWindow(
        string theme,
        System.Drawing.Point cursorPhysicalPosition,
        Action openMainWindow,
        Action openEncrypt,
        Action openList,
        Action openFolderGuard,
        Action exitApplication)
    {
        InitializeComponent();
        ApplyTheme(theme);

        _cursorPhysicalPosition = cursorPhysicalPosition;
        _openMainWindow = openMainWindow;
        _openEncrypt = openEncrypt;
        _openList = openList;
        _openFolderGuard = openFolderGuard;
        _exitApplication = exitApplication;
    }

    /// <summary>錨定在游標附近、貼著工作列的內側邊緣——工作列停靠在哪一側，靠比較
    /// Screen.WorkingArea 跟 Screen.Bounds 的差異判斷（工作列會把 WorkingArea 從對應那一側
    /// 往內縮），比單純看游標在螢幕上半／下半準：直接對應到系統匣圖示實際所在的那一側，
    /// 工作列在螢幕下緣（最常見）選單就往上彈，工作列在上緣選單就往下彈。左右兩側同理只處理
    /// 垂直方向的翻轉，水平位置固定跟著游標 X 座標（貼著螢幕邊界自動往內收），工作列停在
    /// 螢幕左/右側這種較少見的情境水平位置一樣會被收在螢幕內、不會被裁掉，只是不會特別把
    /// 選單整個橫向翻到游標另一側——這個情境太少見，不值得為此把左右也加一套翻轉邏輯。
    ///
    /// 全程用原生 SetWindowPos 設定物理像素座標，不透過 WPF 的 Left/Top（DIP）——這個 codebase
    /// 的無邊框視窗普遍會自訂非客戶區（見 MainWindow.Chrome.cs），實測 WPF 內建的座標系統／
    /// 自動置中邏輯在這類視窗上不可靠（曾經改用 PresentationSource.CompositionTarget 做
    /// DIP 換算，一樣算出錯誤位置），直接用 Win32 API 操作物理像素才是可預期、好驗證的做法。</summary>
    private void PositionNearCursor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        var estimatedWidthPx = (int)Math.Round(WidthDip * scale);
        var estimatedHeightPx = (int)Math.Round(EstimatedHeightDip * scale);

        var screen = System.Windows.Forms.Screen.FromPoint(_cursorPhysicalPosition);
        var workingArea = screen.WorkingArea;
        var bounds = screen.Bounds;

        var taskbarAtTop = workingArea.Top > bounds.Top;
        var y = taskbarAtTop
            ? workingArea.Top
            : workingArea.Bottom - estimatedHeightPx;
        y = Math.Clamp(y, workingArea.Top, workingArea.Bottom - estimatedHeightPx);

        var x = Math.Clamp(_cursorPhysicalPosition.X - estimatedWidthPx, workingArea.Left, workingArea.Right - estimatedWidthPx);

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private void ApplyTheme(string theme)
    {
        var isDark = theme == "dark";
        SetBrush("SurfaceBrush", isDark ? "#232428" : "#FFFFFF");
        SetBrush("WindowBorderBrush", isDark ? "#34363C" : "#E1E4EA");
        SetBrush("TextBrush", isDark ? "#ECEDEF" : "#1B1E24");
        SetBrush("AccentTintBrush", isDark ? "#3A331F" : "#F5EBD6");
        SetBrush("SeparatorBrush", isDark ? "#34363C" : "#E1E4EA");
        SetBrush("DangerBrush", isDark ? "#E17153" : "#B14328");
    }

    private void SetBrush(string resourceKey, string colorHex)
    {
        Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Windows 10 或更舊版本可能沒有這支 DLL／這個屬性，安靜略過。
        }

        // 要在原生控制代碼建立好之後才能拿到 PresentationSource／CompositionTarget，
        // 見 PositionNearCursor 上的說明。這時候視窗還沒真的顯示出來，這裡設定 Left/Top
        // 不會有先閃一下錯誤位置再跳過去的問題。
        PositionNearCursor();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
    }

    // 標準彈出選單行為：點選單以外的地方（視窗失去作用中狀態）就自動收起。CloseOnce 擋掉
    // 這裡跟按鈕點擊那邊同時都想關閉視窗時的重入呼叫（見 _isClosing 上的說明）。
    private void Window_Deactivated(object? sender, EventArgs e) => CloseOnce();

    // 視窗真的關閉完成才執行呼叫端要做的事（開新視窗／結束程式），不要在 Close() 呼叫的當下
    // 就立刻執行——這時候視窗還在關閉流程中，任何一步丟例外都會讓還沒執行到的動作永遠不會發生，
    // 使用者只會看到「點了沒反應」，還無從得知是哪裡出的錯。
    private void Window_Closed(object? sender, EventArgs e)
    {
        var action = _pendingAction;
        _pendingAction = null;
        if (action is null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"處理系統匣選單動作時發生錯誤：\n{ex}", "FileLocker", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseOnce()
    {
        if (_isClosing)
        {
            return;
        }
        _isClosing = true;
        Close();
    }

    private void InvokeAndClose(Action action)
    {
        _pendingAction = action;
        CloseOnce();
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_openMainWindow);

    private void EncryptButton_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_openEncrypt);

    private void ListButton_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_openList);

    private void FolderGuardButton_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_openFolderGuard);

    private void ExitButton_Click(object sender, RoutedEventArgs e) => InvokeAndClose(_exitApplication);
}
