using System.Runtime.InteropServices;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件 8.1 節「驗證視窗可能跳到背景」的緩解手法：未封裝的桌面應用程式呼叫
/// Windows Hello 相關 API 時，系統跳出的驗證視窗沒有正式的視窗擁有（ownership）關係，
/// 會有跳到背景、輸入框沒有自動取得焦點、驗證結束後焦點沒有還給呼叫端這幾個症狀。
///
/// PrepareForegroundHandoff／ReclaimForeground 是第一層緩解（讓自己的視窗先搶到前景、
/// 開放接下來的新視窗也能搶焦點），但實測發現連續兩次驗證（建立金鑰＋簽章）時，
/// 第二次不一定有效。PromoteNewForeignWindowAsync 是更直接的第二層做法：
/// 主動輪詢找出「觸發驗證後新出現、不屬於自己程式」的視窗，抓到就直接強制釘到最上層、搶前景，
/// 不依賴 Windows 的搶焦點權限機制猜測，直接命中目標視窗本身。
/// </summary>
internal static class WindowFocusHelper
{
    private const uint AsfwAny = 0xFFFFFFFF;
    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    public static void PrepareForegroundHandoff(IntPtr ownerWindowHandle)
    {
        if (ownerWindowHandle != IntPtr.Zero)
        {
            SetForegroundWindow(ownerWindowHandle);
        }

        AllowSetForegroundWindow(AsfwAny);
    }

    public static void ReclaimForeground(IntPtr ownerWindowHandle)
    {
        if (ownerWindowHandle != IntPtr.Zero)
        {
            SetForegroundWindow(ownerWindowHandle);
        }
    }

    /// <summary>
    /// 背景輪詢最多 5 秒，找出「觸發驗證後新出現、不屬於自己這個行程」的第一個可見視窗，
    /// 找到就強制釘到最上層＋搶前景。透過 CancellationToken 在驗證完成（不管成功失敗）時提前停止，
    /// 不會一直空轉到 5 秒逾時。
    /// </summary>
    public static async Task PromoteNewForeignWindowAsync(CancellationToken cancellationToken)
    {
        var ourProcessId = (uint)Environment.ProcessId;
        var before = EnumerateVisibleTopLevelWindows();
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var current = EnumerateVisibleTopLevelWindows();
            foreach (var hwnd in current)
            {
                if (before.Contains(hwnd))
                {
                    continue;
                }

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == ourProcessId)
                {
                    continue;
                }

                SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                SetForegroundWindow(hwnd);
                return;
            }

            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private static HashSet<IntPtr> EnumerateVisibleTopLevelWindows()
    {
        var windows = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd))
            {
                windows.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}