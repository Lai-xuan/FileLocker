using Microsoft.Win32;

namespace FileLocker.App;

/// <summary>
/// 「跟隨 Windows 啟動」——背景模式（見 AppSettings.BackgroundModeEnabled）開啟時，讓 FileLocker
/// 在使用者登入時安靜啟動（帶 `--startup` 旗標，不開任何視窗，只留系統匣圖示），資料夾防護的
/// 閒置自動重新上鎖才不用等使用者手動開一次 FileLocker 才生效。
///
/// 寫在 HKEY_CURRENT_USER\...\Run 底下，不是 Windows 工作排程器——跟 ShellExtensionRegistrar
/// 一樣的理由：每個使用者各自登記、不需要系統管理員權限，也不會有工作排程器那種「解除安裝沒清乾淨
/// 就變成系統層級孤兒工作」的殘留風險（單一登錄值，Windows 對失效的 Run 項目本來就會靜默略過）。
/// </summary>
internal static class StartupRegistrar
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FileLocker";
    private const string StartupArgFlag = "--startup";

    /// <summary>冪等——可以安全每次啟動都呼叫一次（自我修復，跟 ShellExtensionRegistrar.
    /// EnsureRegistered 同一個設計原則），也可以在使用者切換設定當下立即呼叫，不用等下次啟動
    /// 才生效／失效。enabled 時比對現有登錄值是否已經等於預期字串，不同才寫入；!enabled 時
    /// 登錄值存在才刪除，不存在就什麼都不做。</summary>
    public static void EnsureConsistent(bool enabled, string appExePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (!enabled)
        {
            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return;
        }

        var expectedCommand = $"\"{appExePath}\" {StartupArgFlag}";
        if (!string.Equals(key.GetValue(ValueName) as string, expectedCommand, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(ValueName, expectedCommand);
        }
    }
}
