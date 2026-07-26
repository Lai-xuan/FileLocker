using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FileLocker.Core;
using FileLocker.Core.Vault;

namespace FileLocker.App;

/// <summary>
/// 雙擊 .locked 檔案時跳出的獨立小視窗。刻意用原生 WPF（不透過 WebView2），
/// 目的是讓這個視窗盡量快跳出來——使用者只是想快速輸入密碼解密，不需要載入整個瀏覽器核心。
/// 如果這個項目有啟用 Passkey 快速解鎖（見規格文件 8.1 節），視窗一開啟就自動觸發 Windows Hello 驗證，
/// 使用者把驗證視窗關掉（放棄這次嘗試）才會退回密碼輸入畫面，並保留按鈕讓使用者可以重試。
/// 若有啟用恢復金鑰，也提供跟主視窗解密頁一樣的恢復金鑰解鎖入口。
///
/// 無邊框視覺對齊主視窗設計系統（見 PasswordPromptWindow.xaml 開頭的說明），但技術做法刻意
/// 比主視窗簡單：這個視窗沒有最大化功能，不需要比照 MainWindow 攔截 WM_NCCALCSIZE/
/// WM_NCHITTEST 保留 DWM 動畫，純粹 WindowStyle="None" + DwmSetWindowAttribute 圓角 +
/// DragMove() 拖曳即可。也刻意不設 Topmost——見 PasswordPromptWindow.xaml 開頭的說明。
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
    private readonly bool _recoveryKeyEnabled;
    private readonly bool _hasAnyAltAuth;
    private bool _isBusy;

    // 密碼／恢復金鑰是互斥的兩種輸入模式，切換時互換可見的欄位跟標籤；Passkey 不算一種
    // 「模式」，是點下去就直接觸發驗證的獨立捷徑，不影響這個狀態。
    private enum UnlockInputMode { Password, RecoveryKey }
    private UnlockInputMode _mode = UnlockInputMode.Password;

    public PasswordPromptWindow(string lockedMarkerPath, VaultManager vaultManager, LockService lockService, string theme)
    {
        InitializeComponent();

        ApplyTheme(theme);

        _lockedMarkerPath = lockedMarkerPath;
        _lockService = lockService;

        // 先讀 marker 拿 UUID、查 metadata 顯示原始檔名、提示，以及是否啟用了 Passkey／恢復金鑰——
        // 這一步不驗證簽章，純粹是為了顯示資訊給使用者看；真正的安全驗證（簽章 + 密碼／Passkey／
        // 恢復金鑰）發生在使用者實際嘗試解鎖時。
        var marker = LockedMarkerFile.ReadFrom(lockedMarkerPath);
        var metadata = marker is not null ? vaultManager.LoadMetadata(marker.Uuid) : null;

        _uuid = marker?.Uuid ?? "";
        _passkeyEnabled = metadata?.PasskeyEnabled ?? false;
        _recoveryKeyEnabled = metadata?.RecoveryKeyEnabled ?? false;

        FileNameText.Text = metadata?.OriginalName ?? Path.GetFileNameWithoutExtension(lockedMarkerPath);
        HintText.Text = !string.IsNullOrWhiteSpace(metadata?.Hint)
            ? $"提示：{metadata.Hint}"
            : "沒有設定提示";

        PasskeyButton.Visibility = _passkeyEnabled ? Visibility.Visible : Visibility.Collapsed;
        RecoveryKeyButton.Visibility = _recoveryKeyEnabled ? Visibility.Visible : Visibility.Collapsed;

        // 「或」分隔線只在至少有一種替代解鎖方式時才出現；只有一種可用時，讓那顆按鈕
        // 撐滿整行（Grid.ColumnSpan=2），不要留一半空白的欄位。存成欄位是因為切到恢復金鑰
        // 模式時會整組隱藏分隔線／並排按鈕，切回密碼模式時要用同一個值決定該不該恢復顯示。
        _hasAnyAltAuth = _passkeyEnabled || _recoveryKeyEnabled;
        AltAuthDivider.Visibility = _hasAnyAltAuth ? Visibility.Visible : Visibility.Collapsed;

        if (_passkeyEnabled && !_recoveryKeyEnabled)
        {
            Grid.SetColumnSpan(PasskeyButton, 2);
        }
        else if (_recoveryKeyEnabled && !_passkeyEnabled)
        {
            Grid.SetColumnSpan(RecoveryKeyButton, 2);
        }

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

        var result = await _lockService.DecryptByPasskeyAsync(_uuid, hwnd, GetMarkerParentDir());

        if (result.Success)
        {
            await ShowSuccessAndCloseAsync();
            return;
        }

        // Passkey 沒完成（使用者把驗證視窗關掉、取消，或驗證失敗）：退回目前的輸入模式，
        // 保留 Passkey 按鈕讓使用者可以重試——有可能只是不小心關掉或按錯，不代表使用者不想用 Passkey。
        SetBusyState(false);
        PasskeyStatusText.Text = "Passkey 未完成驗證，可以重試，或直接輸入密碼。";
        PasskeyStatusText.Visibility = Visibility.Visible;
        FocusActiveInput();

        _isBusy = false;
    }

    /// <summary>
    /// 切換到恢復金鑰輸入模式：隱藏密碼欄位跟密碼以外的解鎖捷徑（分隔線＋並排按鈕），改顯示
    /// 恢復金鑰欄位跟「返回使用密碼」連結——視覺上像換了一頁，不是原地多長出一個欄位。
    /// 也一併收掉 Passkey 狀態文字，避免「...或直接輸入密碼」這句話跟畫面上已經看不到密碼欄位
    /// 的狀態對不上。跟 Passkey 不同，恢復金鑰需要使用者先輸入才能送出，不是點下去就直接觸發
    /// 驗證，所以是「切換模式」而不是「立刻嘗試解鎖」。
    /// </summary>
    private void RecoveryKeyButton_Click(object sender, RoutedEventArgs e)
    {
        _mode = UnlockInputMode.RecoveryKey;

        PasswordFieldLabel.Visibility = Visibility.Collapsed;
        PasswordInput.Visibility = Visibility.Collapsed;
        AltAuthDivider.Visibility = Visibility.Collapsed;
        AltAuthGrid.Visibility = Visibility.Collapsed;
        PasskeyStatusText.Visibility = Visibility.Collapsed;

        RecoveryKeyFieldLabel.Visibility = Visibility.Visible;
        RecoveryKeyInput.Visibility = Visibility.Visible;
        BackToPasswordButton.Visibility = Visibility.Visible;

        ErrorText.Visibility = Visibility.Collapsed;
        RecoveryKeyInput.Focus();
    }

    /// <summary>
    /// 從恢復金鑰模式切回密碼模式——分隔線／並排捷徑按鈕要不要恢復顯示看 _hasAnyAltAuth，
    /// 不是無條件恢復（沒有任何替代解鎖方式的項目，切回去不該憑空多一條分隔線）。
    /// </summary>
    private void BackToPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        _mode = UnlockInputMode.Password;

        RecoveryKeyFieldLabel.Visibility = Visibility.Collapsed;
        RecoveryKeyInput.Visibility = Visibility.Collapsed;
        BackToPasswordButton.Visibility = Visibility.Collapsed;

        PasswordFieldLabel.Visibility = Visibility.Visible;
        PasswordInput.Visibility = Visibility.Visible;
        AltAuthDivider.Visibility = _hasAnyAltAuth ? Visibility.Visible : Visibility.Collapsed;
        AltAuthGrid.Visibility = _hasAnyAltAuth ? Visibility.Visible : Visibility.Collapsed;

        ErrorText.Visibility = Visibility.Collapsed;
        PasswordInput.Focus();
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == UnlockInputMode.RecoveryKey)
        {
            await TryRecoveryKeyUnlockAsync();
        }
        else
        {
            await TryPasswordUnlockAsync();
        }
    }

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
            await ShowSuccessAndCloseAsync();
            return;
        }

        ErrorText.Text = result.ErrorMessage;
        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.Clear();
        SetBusyState(false);
        PasswordInput.Focus();

        _isBusy = false;
    }

    private async Task TryRecoveryKeyUnlockAsync()
    {
        if (_isBusy)
        {
            return;
        }
        _isBusy = true;

        SetBusyState(true);
        ErrorText.Visibility = Visibility.Collapsed;

        var result = await _lockService.DecryptByRecoveryKeyAsync(_uuid, RecoveryKeyInput.Text, GetMarkerParentDir());

        if (result.Success)
        {
            await ShowSuccessAndCloseAsync();
            return;
        }

        ErrorText.Text = result.ErrorMessage;
        ErrorText.Visibility = Visibility.Visible;
        SetBusyState(false);
        RecoveryKeyInput.Focus();

        _isBusy = false;
    }

    /// <summary>
    /// 解鎖成功的收尾回饋：不是一個要使用者等待的獨立頁面，是視窗消失前約半秒的動畫——
    /// SuccessOverlay 疊在 MainContentPanel 上面（見 XAML 說明），淡入播完、停留一小段時間
    /// 讓使用者看清楚打勾跟文字，然後才真正關閉視窗。SetBusyState(true) 在呼叫端已經設過，
    /// 這裡不用重複鎖輸入框。
    /// </summary>
    private async Task ShowSuccessAndCloseAsync()
    {
        SuccessOverlay.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SuccessOverlay.BeginAnimation(OpacityProperty, fadeIn);

        await Task.Delay(500);
        Close();
    }

    /// <summary>
    /// 還原位置跟密碼／恢復金鑰兩條路徑保持一致：用 .locked 檔案目前所在的資料夾，而不是
    /// metadata 裡記錄的原始路徑——避免使用者把 .locked 檔案搬到別的地方之後，不同解鎖方式
    /// 還原到不同位置。
    /// </summary>
    private string? GetMarkerParentDir() => Path.GetDirectoryName(Path.GetFullPath(_lockedMarkerPath));

    private void FocusActiveInput()
    {
        if (_mode == UnlockInputMode.RecoveryKey)
        {
            RecoveryKeyInput.Focus();
        }
        else
        {
            PasswordInput.Focus();
        }
    }

    private void SetBusyState(bool isBusy)
    {
        PasswordInput.IsEnabled = !isBusy;
        RecoveryKeyInput.IsEnabled = !isBusy;
        UnlockButton.IsEnabled = !isBusy;
        CancelButton.IsEnabled = !isBusy;
        PasskeyButton.IsEnabled = !isBusy;
        RecoveryKeyButton.IsEnabled = !isBusy;
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
        SetBrush("ChipBackgroundBrush", isDark ? "#2C2E33" : "#F1F2F5");
        SetBrush("SuccessBrush", isDark ? "#4EAE76" : "#2E7D4F");
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
