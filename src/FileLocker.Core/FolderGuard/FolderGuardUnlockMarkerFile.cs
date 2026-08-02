namespace FileLocker.Core.FolderGuard;

/// <summary>
/// 「雙擊已上鎖資料夾直接解鎖」這個選配功能用的標記檔——不是取代真正的資料夾，是在同一層另外
/// 放一個 <c>{資料夾名稱}.lockfolder</c> 檔案，雙擊它才觸發解鎖確認彈窗，跟加密功能的 `.locked`
/// 指標檔走同一套已經證明穩定的檔案關聯機制。之前用 Windows Shell Namespace Extension（讓資料夾
/// 本身偽裝成可瀏覽物件，透過 desktop.ini 的 CLSID2 接管雙擊行為）實測連續踩到兩個問題：曾經讓
/// explorer.exe 整個行程死結，拿掉 ACL 改善死結問題後，右鍵選單又整個消失（連帶失去「加密」這個
/// 無關的選項）——這兩個問題都出在 Explorer 對命名空間擴充的內部行為缺乏官方文件保證，不是我們
/// 自己的程式邏輯寫錯了什麼，因此放棄整條技術路線，改用這個檔案關聯機制：不需要 `IShellFolder`／
/// COM 命名空間物件，資料夾本身完全不受影響，維持真正的資料夾（外觀、「依類型分組」都正常），
/// ACL 保護強度也不用打折扣（見 <see cref="FolderGuardProtection"/>，兩種模式現在都套用一樣的 ACL，
/// 差別只在要不要多放這個標記檔）。
///
/// 標記檔內容只放真正資料夾的完整路徑（純文字，不是 JSON，這裡不需要額外格式）——不是靠檔名反推
/// 目標路徑，改資料夾名稱或搬動位置不會讓標記檔失去作用；雙擊時讀出這個路徑，交給既有的
/// <c>--folder-guard-unlock</c> 處理流程（見 App.xaml.cs 的 HandleFolderGuardUnlockLaunch）。
///
/// 標記檔是資料夾的「同層兄弟」，不是資料夾內部的東西，寫入/刪除它完全不受資料夾本身的 ACL 影響
/// （不像 desktop.ini 那樣要在 ACL 生效前搶著寫、還要另外補一條 Allow 規則），這也是這個方案比
/// 命名空間標記單純很多的原因之一。
/// </summary>
public static class FolderGuardUnlockMarkerFile
{
    public const string Extension = ".lockfolder";

    public static string GetMarkerPath(string folderPath)
        => folderPath.TrimEnd('\\', '/') + Extension;

    /// <summary>寫入/覆寫標記檔——資料夾本身的 ACL 不影響這個檔案（同層兄弟，不是子項目），
    /// 呼叫順序（在套用/移除 ACL 之前或之後）都不重要，跟舊版的命名空間標記不同。</summary>
    public static void Apply(string folderPath)
        => File.WriteAllText(GetMarkerPath(folderPath), folderPath);

    /// <summary>刪掉標記檔，找不到視為成功（本來就沒貼過標記，或已經被清過）。</summary>
    public static void Remove(string folderPath)
    {
        var markerPath = GetMarkerPath(folderPath);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    public static bool IsMarked(string folderPath) => File.Exists(GetMarkerPath(folderPath));

    /// <summary>雙擊標記檔時用：讀出裡面記錄的真正資料夾路徑。標記檔本身格式固定由
    /// <see cref="Apply"/> 寫入，讀取失敗（檔案不存在、被刪、I/O 錯誤）一律回傳 null，
    /// 呼叫端要能容忍找不到對應資料夾的情況，不能連累整個啟動流程。</summary>
    public static string? ReadTargetFolderPath(string markerFilePath)
    {
        if (!File.Exists(markerFilePath))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(markerFilePath).Trim();
            return content.Length > 0 ? content : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
