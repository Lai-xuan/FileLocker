using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FileLocker.Core;

namespace FileLocker.App;

/// <summary>
/// 右鍵「解鎖」跳出的原生小視窗，見規劃文件說明——Passkey 已啟用時只顯示 Passkey 相關 UI，
/// 沒有自動退回密碼（跟分頁清單頁的解鎖/全部解鎖同一條規則）；沒啟用 Passkey 才顯示密碼欄位。
/// </summary>
public partial class FolderGuardUnlockPromptWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly IReadOnlyList<string> _paths;
    private readonly FolderGuardService _folderGuardService;
    private readonly bool _passkeyEnabled;
    private bool _isBusy;

    public FolderGuardUnlockPromptWindow(IReadOnlyList<string> paths, FolderGuardService folderGuardService, string theme)
    {
        InitializeComponent();
        ApplyTheme(theme);

        _paths = paths;
        _folderGuardService = folderGuardService;
        _passkeyEnabled = folderGuardService.IsPasskeyEnabled;

        MessageText.Text = paths.Count == 1
            ? $"你要將「{Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))}」解鎖嗎？"
            : $"你要將這 {paths.Count} 個資料夾解鎖嗎？";

        if (_passkeyEnabled)
        {
            PasskeyStatusText.Visibility = Visibility.Visible;
            PasskeyStatusText.Text = "正在使用 Passkey 驗證，請通過 Windows Hello...";
            UnlockButton.Content = "重試 Passkey";
            UnlockButton.IsEnabled = true;
        }
        else
        {
            PasswordPanel.Visibility = Visibility.Visible;
        }

        Loaded += async (_, _) =>
        {
            // 兩種模式都先 Activate 一次——這個視窗很可能是背景執行個體透過 Named Pipe 收到
            // 轉送過來才建立的，不搶一次前景，密碼模式視窗可能被壓在其他視窗底下沒人看到。
            Activate();

            if (_passkeyEnabled)
            {
                // 跟 PasswordPromptWindow 同樣的理由：讓出一輪 Dispatcher，確保這個視窗的作用中
                // 狀態已經穩定，才觸發 Passkey，避免焦點被搶回去蓋掉驗證視窗。
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Input);
                await TryPasskeyUnlockAsync();
            }
            else
            {
                PasswordInput.Focus();
            }
        };
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_passkeyEnabled)
        {
            await TryPasskeyUnlockAsync();
        }
        else
        {
            await TryPasswordUnlockAsync();
        }
    }

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
        var result = await _folderGuardService.UnlockFoldersAsync(_paths, password: null, hwnd);

        if (result.Success)
        {
            await ShowSuccessAndCloseAsync();
            return;
        }

        // Passkey 沒完成（使用者把驗證視窗關掉、取消，或驗證失敗）：保留重試按鈕，不會自動
        // 退回密碼——Passkey 已設定的資料夾防護操作一律只認 Passkey，逃生門是設定頁的「停用 Passkey」。
        SetBusyState(false);
        PasskeyStatusText.Text = "Passkey 未完成驗證，可以重試，或到設定頁停用 Passkey。";

        _isBusy = false;
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

        var hwnd = new WindowInteropHelper(this).Handle;
        var result = await _folderGuardService.UnlockFoldersAsync(_paths, PasswordInput.Password, hwnd);

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

    private void SetBusyState(bool isBusy)
    {
        CancelButton.IsEnabled = !isBusy;

        if (_passkeyEnabled)
        {
            UnlockButton.IsEnabled = !isBusy;
            return;
        }

        PasswordInput.IsEnabled = !isBusy;
        UnlockButton.IsEnabled = !isBusy && !string.IsNullOrEmpty(PasswordInput.Password);
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }
        UnlockButton.IsEnabled = !string.IsNullOrEmpty(PasswordInput.Password);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _isBusy)
        {
            return;
        }
        Close();
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

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
    }
}
