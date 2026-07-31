namespace FileLocker.Core.FolderGuard;

/// <summary>資料夾目前的防護狀態——Locked 有實際 ACL 拒絕規則在生效；Unlocked 是使用者在分頁內
/// 主動解鎖、還沒手動刪除的殘留紀錄（見規劃文件第 9 節），本身不對應任何 ACL 規則。</summary>
public enum FolderGuardStatus
{
    Locked,
    Unlocked
}

/// <summary>單一被防護（或曾經被防護）資料夾的紀錄。</summary>
public class FolderGuardEntry
{
    public string Path { get; set; } = "";
    public FolderGuardStatus Status { get; set; }
    public DateTime LockedAtUtc { get; set; }
    public DateTime? UnlockedAtUtc { get; set; }
}

/// <summary>
/// `guarded-folders.json` 的完整內容：共用憑證（密碼驗證雜湊＋選配 Passkey）＋被防護資料夾清單。
/// 憑證欄位皆為 null 代表整個資料夾防護功能還沒設定過（見 FolderGuardService.IsConfigured）。
/// </summary>
public class FolderGuardData
{
    public string? PasswordSaltBase64 { get; set; }
    public string? PasswordVerificationHashBase64 { get; set; }
    public bool PasskeyEnabled { get; set; }
    public string? PasskeyCredentialName { get; set; }

    /// <summary>設定頁選配開關（預設關閉）：開啟後，鎖定資料夾時會額外貼上 desktop.ini 命名空間
    /// 標記，讓雙擊該資料夾直接跳出解鎖視窗（見 FolderGuardNamespaceMarker）——這條路徑跑在
    /// explorer.exe 行程裡面，風險跟純右鍵選單擴充不同，所以刻意做成選配、預設關閉。</summary>
    public bool DoubleClickUnlockEnabled { get; set; }

    public List<FolderGuardEntry> Entries { get; set; } = new();
}
