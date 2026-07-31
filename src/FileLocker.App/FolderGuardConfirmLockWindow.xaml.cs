using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileLocker.Core;

namespace FileLocker.App;

/// <summary>
/// 右鍵「上鎖」在共用密碼已經設定過時的快速路徑（見規劃文件第 6 節）：不開主視窗，跳這個原生
/// 小視窗確認、按下就直接套用 ACL，不需要輸入密碼——上鎖本身不是需要驗證身份的動作。
/// openEncryptTab 是呼叫端（App.xaml.cs）注入的回呼，負責真正開啟/喚起 MainWindow 並帶入這批
/// 路徑到加密分頁；這個視窗本身不知道 MainWindow 怎麼建立，維持單向依賴。
/// </summary>
public partial class FolderGuardConfirmLockWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly IReadOnlyList<string> _paths;
    private readonly FolderGuardService _folderGuardService;
    private readonly Action<IReadOnlyList<string>> _openEncryptTab;
    private bool _isBusy;

    public FolderGuardConfirmLockWindow(
        IReadOnlyList<string> paths, FolderGuardService folderGuardService, string theme,
        Action<IReadOnlyList<string>> openEncryptTab)
    {
        InitializeComponent();
        ApplyTheme(theme);

        _paths = paths;
        _folderGuardService = folderGuardService;
        _openEncryptTab = openEncryptTab;

        MessageText.Text = paths.Count == 1
            ? $"你要將「{Path.GetFileName(paths[0].TrimEnd(Path.DirectorySeparatorChar))}」上鎖嗎？"
            : $"你要將這 {paths.Count} 個資料夾上鎖嗎？";
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }
        _isBusy = true;
        LockButton.IsEnabled = false;

        var results = await _folderGuardService.LockFoldersAsync(_paths);

        if (results.All(r => r.Success))
        {
            Close();
            return;
        }

        var failedCount = results.Count(r => !r.Success);
        MessageText.Text = $"上鎖失敗（{failedCount}/{_paths.Count} 個項目）：{results.First(r => !r.Success).ErrorMessage}";
        MessageText.Foreground = (Brush)Resources["TextSecondaryBrush"];
        LockButton.IsEnabled = true;
        _isBusy = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void GoToEncryptButton_Click(object sender, RoutedEventArgs e)
    {
        _openEncryptTab(_paths);
        Close();
    }

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
    }

    private void SetBrush(string resourceKey, string colorHex)
    {
        Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
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
