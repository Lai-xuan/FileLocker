using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;

namespace FileLocker.App;

// 對應架構審查（2026-07-27）：這個檔案只放無邊框視窗的 Win32/DWM 互操作跟視窗外框相關邏輯，
// 跟 MainWindow.xaml.cs 裡的 WebView2 初始化／IPC 派送／Vault 協定呼叫完全無關、彼此也不會
// 互相呼叫——這條邊界原本就是真的 seam，只是還沒變成檔案邊界，稽核「加密流程端到端做了什麼」
// 不需要再滑過這一大段視窗外框程式碼。partial class 讓兩邊互相呼叫（例如建構子裡的
// SourceInitialized += OnSourceInitialized）不用改任何呼叫端程式碼。
public partial class MainWindow
{
    // ---- 無邊框視窗的已知陷阱修正 ----
    //
    // 1. 圓角／陰影：Windows 11 的原生視窗圓角跟投影理論上會因為保留 WS_CAPTION（見下方第 3 點、
    //    MainWindow.xaml 的說明）自動維持，這裡的 DwmSetWindowAttribute 手動要回來當保險，
    //    即使非必要也不影響行為。Windows 10 沒有這個 DWM 屬性，呼叫會失敗，安靜略過即可。
    //
    // 2. 最大化超出工作區：WPF 內建的最大化尺寸計算沒有正確扣掉隱形的縮放邊框，導致視窗最大化時
    //    會往外超出工作區邊界幾個像素，剛好等於縮放邊框的寬度。使用者裝了會佔用螢幕空間的工具
    //    （工作列本身、或這次遇到的 MyDockFinder 這類第三方 Dock 工具）時，超出的那部分就會
    //    直接被蓋住看不到。修法是攔截 WM_GETMINMAXINFO 這個訊息，自己用系統回報的「工作區」
    //    （會扣掉所有登記佔用空間的工具，不只是內建工作列）算出正確的最大化尺寸。
    //
    // 3. 隱藏原生標題列，但保留 DWM 動畫：WindowStyle="SingleBorderWindow"（見 MainWindow.xaml）
    //    刻意保留 WS_CAPTION／WS_THICKFRAME，讓 DWM 把這個視窗當一般可動畫的視窗看待（最大化/
    //    還原才會有原生的長大/縮小動畫，這是之前 WindowStyle="None" + WindowChrome 做不到的）。
    //    代價是原生標題列會真的被畫出來，所以要自己攔截 WM_NCCALCSIZE，把非客戶區（標題列／
    //    邊框）視覺上收縮到 0（有樣式、但畫面上完全看不到）；再攔截 WM_NCHITTEST 自己判斷滑鼠
    //    落在哪個縮放邊界（標題列拖曳／雙擊最大化維持交給 WebView2 的 IsNonClientRegionSupportEnabled，
    //    這裡不搶著回 HTCAPTION，避免跟它打架）。
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectStruct rect);

    private const uint MonitorDefaultToNearest = 2;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;

    // 縮放邊界判斷用的門檻，DIP 單位——要跟 MainWindow.xaml 裡 WebView2 的 Margin 一致
    // （那圈真正的 WPF 空間是給這裡判斷縮放邊界用的滑鼠事件用的，見該處說明）。
    private const int ResizeBorderThicknessDip = 6;

    private const int SmCxsizeframe = 32;
    private const int SmCysizeframe = 33;
    private const int SmCxpaddedborder = 92;

    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointStruct Reserved;
        public PointStruct MaxSize;
        public PointStruct MaxPosition;
        public PointStruct MinTrackSize;
        public PointStruct MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectStruct
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public RectStruct Monitor;
        public RectStruct WorkArea;
        public int Flags;
    }

    /// <summary>
    /// 視窗邊緣那圈窄邊（見 MainWindow.xaml 的 WebView2 Margin 說明）是純 WPF 畫的，
    /// 顏色來自 Window.Background，不會自動跟著 HTML 裡的深色模式切換——這裡手動同步一次，
    /// 顏色數值刻意跟 App.vue 裡 .app--dark 的 --color-surface 對齊，兩邊要一起改。
    /// </summary>
    private void ApplyWindowBackgroundForTheme(string theme)
    {
        Background = theme == "dark"
            ? new SolidColorBrush(Color.FromRgb(0x23, 0x24, 0x28))
            : new SolidColorBrush(Colors.White);
    }

    /// <summary>
    /// 視窗控制代碼（HWND）建立完成的時機——這裡才拿得到 HWND，才能掛 WndProc 攔截跟設定
    /// DWM 屬性。比 Loaded 早，Loaded 是等 WPF 版面配置跑完，這裡只是視窗底層控制代碼剛建好。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        TryRestoreRoundedCorners(hwnd);

        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProc);
    }

    private static void TryRestoreRoundedCorners(IntPtr hwnd)
    {
        try
        {
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Windows 10 或更舊版本可能沒有這支 DLL／這個屬性，安靜略過，不影響其他功能。
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            ApplyCorrectMaximizedBounds(hwnd, lParam);
            handled = true;
        }
        else if (msg == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            handled = true;
            return HandleNcCalcSize(hwnd, lParam);
        }
        else if (msg == WmNcHitTest)
        {
            var hit = HandleNcHitTest(hwnd, lParam);
            if (hit.HasValue)
            {
                handled = true;
                return new IntPtr(hit.Value);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 把非客戶區（標題列／邊框）視覺上收縮到 0——不做任何事、讓傳進來的建議矩形（rgrc[0]，
    /// 跟 RectStruct 版面配置相同，只需要動這一個欄位）原封不動當作客戶區，整個視窗都變成
    /// 客戶區，畫面上看不到任何原生框線。
    ///
    /// 但視窗最大化時，Windows 對 WS_THICKFRAME 視窗會自動往外多墊一圈看不見的縮放邊框，
    /// 不處理的話最大化時內容會被裁掉一小圈——這裡要把建議矩形往內縮回這圈邊框的寬度
    /// （SM_CXSIZEFRAME/SM_CYSIZEFRAME 加上 SM_CXPADDEDBORDER，用 GetSystemMetricsForDpi
    /// 依視窗目前所在螢幕的 DPI 換算，不能用固定像素值，否則高 DPI 螢幕會裁太多／太少）。
    /// </summary>
    private static IntPtr HandleNcCalcSize(IntPtr hwnd, IntPtr lParam)
    {
        if (IsZoomed(hwnd))
        {
            var rect = Marshal.PtrToStructure<RectStruct>(lParam);

            var dpi = GetDpiForWindow(hwnd);
            var frameX = GetSystemMetricsForDpi(SmCxsizeframe, dpi) + GetSystemMetricsForDpi(SmCxpaddedborder, dpi);
            var frameY = GetSystemMetricsForDpi(SmCysizeframe, dpi) + GetSystemMetricsForDpi(SmCxpaddedborder, dpi);

            rect.Left += frameX;
            rect.Top += frameY;
            rect.Right -= frameX;
            rect.Bottom -= frameY;

            Marshal.StructureToPtr(rect, lParam, true);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 只負責回報縮放邊界，範圍外一律回傳 null（交給預設處理、落到 HTCLIENT）——標題列拖曳／
    /// 雙擊最大化不在這裡搶著回 HTCAPTION，維持交給 WebView2 的 IsNonClientRegionSupportEnabled
    /// 處理（見 Loaded 事件處理常式裡設定這個屬性的說明），避免兩邊都想處理 HTCAPTION 打架。
    /// 最大化時不需要（也不應該）判斷縮放邊界，直接交給預設處理。
    /// </summary>
    private static int? HandleNcHitTest(IntPtr hwnd, IntPtr lParam)
    {
        if (IsZoomed(hwnd))
        {
            return null;
        }

        var coords = lParam.ToInt64();
        var x = unchecked((short)(coords & 0xFFFF));
        var y = unchecked((short)((coords >> 16) & 0xFFFF));

        if (!GetWindowRect(hwnd, out var windowRect))
        {
            return null;
        }

        var dpi = GetDpiForWindow(hwnd);
        var border = (int)Math.Round(ResizeBorderThicknessDip * dpi / 96.0);

        var onLeft = x < windowRect.Left + border;
        var onRight = x >= windowRect.Right - border;
        var onTop = y < windowRect.Top + border;
        var onBottom = y >= windowRect.Bottom - border;

        if (onTop && onLeft) return HtTopLeft;
        if (onTop && onRight) return HtTopRight;
        if (onBottom && onLeft) return HtBottomLeft;
        if (onBottom && onRight) return HtBottomRight;
        if (onLeft) return HtLeft;
        if (onRight) return HtRight;
        if (onTop) return HtTop;
        if (onBottom) return HtBottom;

        return null;
    }

    /// <summary>
    /// 修正無邊框視窗最大化時超出工作區邊界的問題，同時補回最小視窗尺寸限制（見下方說明）。
    /// 用系統回報的「工作區」（會扣掉工作列、以及任何登記佔用螢幕空間的第三方工具，
    /// 例如使用者反映的 MyDockFinder）算出正確的最大化位置與尺寸，取代 WPF 內建的計算結果。
    ///
    /// 攔截這個訊息、把它標記成已處理之後，WPF 自己原本會把 Window.MinWidth／MinHeight
    /// 套進 MinTrackSize 欄位的預設邏輯就不會再執行了——這裡漏掉這步會導致視窗完全沒有
    /// 最小尺寸限制，可以被拖到比視窗控制按鈕還小（這是實測發現的真實 bug，不是假設）。
    /// 這裡要自己重新算一次，換算時要考慮 DPI 縮放比例，不能直接拿 WPF 的裝置無關單位
    /// 當成實際像素用，改成非 static 是因為需要存取 this.MinWidth／MinHeight 跟 this 本身
    /// （VisualTreeHelper.GetDpi 需要一個已經連上畫面的視覺元素）。
    /// </summary>
    private void ApplyCorrectMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);

        var dpi = VisualTreeHelper.GetDpi(this);
        mmi.MinTrackSize.X = (int)Math.Round(MinWidth * dpi.DpiScaleX);
        mmi.MinTrackSize.Y = (int)Math.Round(MinHeight * dpi.DpiScaleY);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var workArea = monitorInfo.WorkArea;
                var monitorArea = monitorInfo.Monitor;

                // 最大化位置要用「相對於螢幕左上角」的座標，不是相對於工作區本身——這是 Windows API
                // 的設計方式，容易在這裡搞錯方向導致算出來的位置整個偏掉。
                mmi.MaxPosition.X = workArea.Left - monitorArea.Left;
                mmi.MaxPosition.Y = workArea.Top - monitorArea.Top;
                mmi.MaxSize.X = workArea.Right - workArea.Left;
                mmi.MaxSize.Y = workArea.Bottom - workArea.Top;
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    /// <summary>
    /// 把目前是不是最大化狀態告訴前端，讓自訂標題列的按鈕圖示能跟著切換。
    /// WebView2 還沒初始化完成時直接跳過（例如視窗剛建立就被還原狀態變更觸發）。
    /// </summary>
    private void SendWindowStateToFrontend()
    {
        if (MainWebView?.CoreWebView2 is null)
        {
            return;
        }

        SendToFrontend(new { type = "windowStateChanged", isMaximized = WindowState == WindowState.Maximized });
    }
}
