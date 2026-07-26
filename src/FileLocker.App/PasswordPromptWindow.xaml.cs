using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FileLocker.Core;
using FileLocker.Core.Vault;

namespace FileLocker.App;

/// <summary>
/// 雙擊 .locked 檔案時跳出的獨立小視窗。刻意用原生 WPF（不透過 WebView2），
/// 目的是讓這個視窗盡量快跳出來——使用者只是想快速輸入密碼解密，不需要載入整個瀏覽器核心。
/// 如果這個項目有啟用 Passkey 快速解鎖（見規格文件 8.1 節），視窗一開啟就自動觸發 Windows Hello 驗證，
/// 使用者把驗證視窗關掉（放棄這次嘗試）才會退回密碼輸入畫面，並保留按鈕讓使用者可以重試。
///
/// 無邊框視覺對齊主視窗設計系統（見 PasswordPromptWindow.xaml 開頭的說明），但技術做法刻意
/// 比主視窗簡單：這個視窗沒有最大化功能，不需要比照 MainWindow 攔截 WM_NCCALCSIZE/
/// WM_NCHITTEST 保留 DWM 動畫，純粹 WindowStyle="None" + DwmSetWindowAttribute 圓角 +
/// DragMove() 拖曳即可。
/// </summary>
public partial class PasswordPromptWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly string _lockedMarkerPath;
    private readonly LockService _lockService;
    private readonly string _uuid;
    private readonly bool _passkeyEnabled;
    private bool _isBusy;

    public PasswordPromptWindow(string lockedMarkerPath, VaultManager vaultManager, LockService lockService, string theme)
    {
        InitializeComponent();

        ApplyTheme(theme);

        _lockedMarkerPath = lockedMarkerPath;
        _lockService = lockService;

        // 先讀 marker 拿 UUID、查 metadata 顯示原始檔名、提示，以及是否啟用了 Passkey——這一步不驗證簽章，
        // 純粹是為了顯示資訊給使用者看；真正的安全驗證（簽章 + 密碼／Passkey）發生在使用者實際嘗試解鎖時。
        var marker = LockedMarkerFile.ReadFrom(lockedMarkerPath);
        var metadata = marker is not null ? vaultManager.LoadMetadata(marker.Uuid) : null;

        _uuid = marker?.Uuid ?? "";
        _passkeyEnabled = metadata?.PasskeyEnabled ?? false;

        FileNameText.Text = metadata?.OriginalName ?? Path.GetFileNameWithoutExtension(lockedMarkerPath);
        HintText.Text = !string.IsNullOrWhiteSpace(metadata?.Hint)
            ? $"提示：{metadata.Hint}"
            : "沒有設定提示";

        PasskeyButton.Visibility = _passkeyEnabled ? Visibility.Visible : Visibility.Collapsed;

        Loaded += async (_, _) =>
        {
            if (_passkeyEnabled)
            {
                await TryPasskeyUnlockAsync();
            }
            else
            {
                PasswordInput.Focus();
            }
        };
    }

    private async void PasskeyButton_Click(object sender, RoutedEventArgs e) => await TryPasskeyUnlockAsync();

    private async Task TryPasskeyUnlockAsync()
    {
        if (_isBusy)
        {
            return;
        }
        _isBusy = true;

        SetBusyState(true);
        PasskeyStatusText.Text = "正在使用 Passkey 驗證，請通過 Windows Hello...";
        PasskeyStatusText.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;

        var hwnd = new WindowInteropHelper(this).Handle;

        // 還原位置跟密碼路徑保持一致：用 .locked 檔案目前所在的資料夾，而不是 metadata 裡記錄的原始路徑——
        // 避免使用者把 .locked 檔案搬到別的地方之後，兩條解鎖路徑（密碼／Passkey）還原到不同位置。
        var markerParentDir = Path.GetDirectoryName(Path.GetFullPath(_lockedMarkerPath));

        var result = await _lockService.DecryptByPasskeyAsync(_uuid, hwnd, markerParentDir);

        if (result.Success)
        {
            Close();
            return;
        }

        // Passkey 沒完成（使用者把驗證視窗關掉、取消，或驗證失敗）：退回密碼輸入，
        // 保留 Passkey 按鈕讓使用者可以重試——有可能只是不小心關掉或按錯，不代表使用者不想用 Passkey。
        SetBusyState(false);
        PasskeyStatusText.Text = "Passkey 未完成驗證，可以重試，或直接輸入密碼。";
        PasskeyStatusText.Visibility = Visibility.Visible;
        PasswordInput.Focus();

        _isBusy = false;
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e) => await TryPasswordUnlockAsync();

    private async Task TryPasswordUnlockAsync()
    {
        if (_isBusy)
        {
            return;
        }
        _isBusy = true;

        SetBusyState(true);
        ErrorText.Visibility = Visibility.Collapsed;

        var result = await _lockService.DecryptAsync(_lockedMarkerPath, PasswordInput.Password);

        if (result.Success)
        {
            Close();
            return;
        }

        ErrorText.Text = result.ErrorMessage;
        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.Clear();
        SetBusyState(false);
        PasswordInput.Focus();

        _isBusy = false;
    }

    private void SetBusyState(bool isBusy)
    {
        PasswordInput.IsEnabled = !isBusy;
        UnlockButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !isBusy;
        PasskeyButton.IsEnabled = !isBusy;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 標題列（自訂畫的，不是原生標題列）按下左鍵直接呼叫 WPF 原生的 DragMove() 拖曳整個視窗——
    /// 不需要主視窗那套 WebView2 app-region 機制，這裡是純 WPF 內容，DragMove 本來就是給
    /// 這種情境用的標準做法。左上角的關閉圓形按鈕是獨立的 Button，點擊事件會被它自己吃掉、
    /// 不會冒泡到這裡，兩者不會互相干擾。
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 顏色對齊 App.vue 的設計系統（:root / .app--dark 那組 CSS 變數），依主題覆寫
    /// Window.Resources 裡定義的 DynamicResource 色彩，一份 XAML 同時支援亮色/深色。
    /// </summary>
    private void ApplyTheme(string theme)
    {
        var isDark = theme == "dark";

        SetBrush("SurfaceBrush", isDark ? "#232428" : "#FFFFFF");
        SetBrush("WindowBorderBrush", isDark ? "#34363C" : "#E1E4EA");
        SetBrush("BorderStrongBrush", isDark ? "#454850" : "#C9CDD6");
        SetBrush("TextBrush", isDark ? "#ECEDEF" : "#1B1E24");
        SetBrush("TextSecondaryBrush", isDark ? "#B0B4BC" : "#454A54");
        SetBrush("TextTertiaryBrush", isDark ? "#82868F" : "#6B707A");
        SetBrush("AccentBrush", isDark ? "#D9A83B" : "#A8770F");
        SetBrush("DangerBrush", isDark ? "#E17153" : "#B14328");
    }

    private void SetBrush(string resourceKey, string colorHex)
    {
        Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
    }

    /// <summary>
    /// HWND 建立完成才拿得到控制代碼，這裡才能呼叫 DWM 要回 Windows 11 圓角——WindowStyle="None"
    /// 拿掉原生標題列的同時，也會把圓角一起拿掉，變成直角方框。跟 MainWindow.xaml.cs 的
    /// TryRestoreRoundedCorners 是同一段邏輯，各自獨立呼叫，程式碼量小，不值得為了共用
    /// 去處理兩個型別之間的耦合。
    /// </summary>
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
            // Windows 10 或更舊版本可能沒有這支 DLL／這個屬性，安靜略過，不影響其他功能。
        }
    }
}