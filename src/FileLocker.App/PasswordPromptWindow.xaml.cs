using System.IO;
using System.Windows;
using System.Windows.Interop;
using FileLocker.Core;
using FileLocker.Core.Vault;

namespace FileLocker.App;

/// <summary>
/// 雙擊 .locked 檔案時跳出的獨立小視窗。刻意用原生 WPF（不透過 WebView2），
/// 目的是讓這個視窗盡量快跳出來——使用者只是想快速輸入密碼解密，不需要載入整個瀏覽器核心。
/// 如果這個項目有啟用 Passkey 快速解鎖（見規格文件 8.1 節），視窗一開啟就自動觸發 Windows Hello 驗證，
/// 使用者把驗證視窗關掉（放棄這次嘗試）才會退回密碼輸入畫面，並保留按鈕讓使用者可以重試。
/// </summary>
public partial class PasswordPromptWindow : Window
{
    private readonly string _lockedMarkerPath;
    private readonly LockService _lockService;
    private readonly string _uuid;
    private readonly bool _passkeyEnabled;
    private bool _isBusy;

    public PasswordPromptWindow(string lockedMarkerPath, VaultManager vaultManager, LockService lockService)
    {
        InitializeComponent();

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
}