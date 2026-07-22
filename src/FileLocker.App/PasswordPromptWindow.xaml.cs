using System.IO;
using System.Windows;
using System.Windows.Input;
using FileLocker.Core;
using FileLocker.Core.Vault;

namespace FileLocker.App;

/// <summary>
/// 雙擊 .locked 檔案時跳出的獨立小視窗。刻意用原生 WPF（不透過 WebView2），
/// 目的是讓這個視窗盡量快跳出來——使用者只是想快速輸入密碼解密，不需要載入整個瀏覽器核心。
/// 跟主視窗的視覺精緻度是分開的優先順序：主視窗（HTML/WebView2）追求畫面細節，這裡追求反應速度。
/// </summary>
public partial class PasswordPromptWindow : Window
{
    private readonly string _lockedMarkerPath;
    private readonly LockService _lockService;

    public PasswordPromptWindow(string lockedMarkerPath, VaultManager vaultManager, LockService lockService)
    {
        InitializeComponent();

        _lockedMarkerPath = lockedMarkerPath;
        _lockService = lockService;

        // 先讀 marker 拿 UUID、查 metadata 顯示原始檔名跟提示——這一步不驗證簽章，純粹是為了
        // 顯示資訊給使用者看；真正的安全驗證（簽章 + 密碼）發生在使用者按下「解密」的那一刻。
        var marker = LockedMarkerFile.ReadFrom(lockedMarkerPath);
        var metadata = marker is not null ? vaultManager.LoadMetadata(marker.Uuid) : null;

        FileNameText.Text = metadata?.OriginalName ?? Path.GetFileNameWithoutExtension(lockedMarkerPath);
        HintText.Text = !string.IsNullOrWhiteSpace(metadata?.Hint)
            ? $"提示：{metadata.Hint}"
            : "沒有設定提示";

        Loaded += (_, _) => PasswordInput.Focus();
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e) => await TryUnlockAsync();

    private async void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await TryUnlockAsync();
        }
    }

    private async Task TryUnlockAsync()
    {
        UnlockButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
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
        PasswordInput.Focus();
        UnlockButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}