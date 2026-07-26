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
/// 主動輪詢找出「觸發驗證後新出現、不屬於自己程式」的視窗，抓到就直接強制釘到最上層、搶前景。
///
/// 光呼叫 SetForegroundWindow 本身常常沒用：Windows 有內建的「防搶焦點」機制，只有目前
/// 持有輸入焦點的執行緒、或最近收到過使用者輸入的行程，才有權限把某個視窗搶到前景，
/// 單純呼叫 AllowSetForegroundWindow(ASFW_ANY) 給的權限不保證每次都被系統認可（連續兩次
/// 觸發驗證時，第一次搶到前景這件事本身可能就把我們行程的搶焦點權限「用掉」了，第二次
/// 因此失效）。ForceSetForegroundWindow 改用更直接的 AttachThreadInput 技巧：暫時把
/// 呼叫端執行緒的輸入佇列跟目前前景視窗的執行緒接在一起，讓系統誤判成「同一組輸入來源」，
/// 這樣呼叫 SetForegroundWindow 才不會被上述限制擋下來——這是繞過該限制最常見、最可靠的做法，
/// 三個原本直接呼叫 SetForegroundWindow 的地方（PrepareForegroundHandoff／ReclaimForeground／
/// PromoteNewForeignWindowAsync）都改用這個版本。
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
            ForceSetForegroundWindow(ownerWindowHandle);
        }

        AllowSetForegroundWindow(AsfwAny);
    }

    public static void ReclaimForeground(IntPtr ownerWindowHandle)
    {
        if (ownerWindowHandle != IntPtr.Zero)
        {
            ForceSetForegroundWindow(ownerWindowHandle);
        }
    }

    /// <summary>
    /// 繞過 Windows 的防搶焦點限制：暫時把呼叫端（我們自己）執行緒的輸入佇列跟目前前景視窗
    /// 的執行緒接在一起（AttachThreadInput），系統就會允許我們呼叫 SetForegroundWindow 生效，
    /// 結束後立刻解除接合，不影響其他視窗之間的正常輸入隔離。如果目前前景視窗本來就屬於
    /// 呼叫端這條執行緒（或根本沒有前景視窗），接合這一步沒有意義也會失敗，直接呼叫
    /// SetForegroundWindow 就好。
    /// </summary>
    private static bool ForceSetForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        var foregroundWindow = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();

        if (foregroundWindow == IntPtr.Zero || foregroundWindow == hWnd)
        {
            return SetForegroundWindow(hWnd);
        }

        var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        if (foregroundThreadId == currentThreadId)
        {
            return SetForegroundWindow(hWnd);
        }

        var attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
        try
        {
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    /// <summary>
    /// 背景輪詢最多 5 秒，找出「觸發驗證後新出現」的可見視窗，找到就強制釘到最上層＋搶前景。
    /// 透過 CancellationToken 在驗證完成（不管成功失敗）時提前停止，不會一直空轉到 5 秒逾時。
    ///
    /// 刻意不排除「跟我們自己同一個行程」的視窗：未封裝的 Win32 應用程式呼叫 WinRT 的
    /// KeyCredentialManager API 時，驗證 UI 有可能是透過行程內（in-process）brokered
    /// activation 顯示的新視窗，仍然算在我們自己的 ProcessId 底下——實測發現改良
    /// SetForegroundWindow 呼叫方式（見 ForceSetForegroundWindow）對第二次驗證完全沒有
    /// 效果，比起「搶不到焦點權限」，更可能是根本沒偵測到正確的視窗（被這條「排除自己行程」
    /// 的判斷式整個濾掉了），所以拿掉這個限制。也不是找到第一個候選就停手——持續在整個輪詢
    /// 期間反覆重新置頂／搶前景，防止使用者操作過程中系統又把它擠回後面。
    /// </summary>
    public static async Task PromoteNewForeignWindowAsync(CancellationToken cancellationToken)
    {
        var before = EnumerateVisibleTopLevelWindows();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        IntPtr? promotedWindow = null;

        while (!cancellationToken.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            var current = EnumerateVisibleTopLevelWindows();

            if (promotedWindow is { } known && current.Contains(known))
            {
                SetWindowPos(known, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                ForceSetForegroundWindow(known);
            }
            else
            {
                promotedWindow = null;

                foreach (var hwnd in current)
                {
                    if (before.Contains(hwnd))
                    {
                        continue;
                    }

                    SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
                    ForceSetForegroundWindow(hwnd);
                    promotedWindow = hwnd;
                    break;
                }
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}