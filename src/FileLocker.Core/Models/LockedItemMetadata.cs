namespace FileLocker.Core.Models;

/// <summary>
/// 對應規格文件第 4 節：每個加密項目獨立一份的 {uuid}.meta.json 內容。
/// 這份物件不是「加密金鑰」本身的載體——PasswordVerificationHash 只拿來驗證密碼是否正確，
/// 真正的加密金鑰永遠是當下用密碼 + Salt 即時算出來，不會被序列化進這個檔案。
/// </summary>
public class LockedItemMetadata
{
    /// <summary>對應 Vault 內 {Uuid}.enc 檔名。</summary>
    public required string Uuid { get; set; }

    public required string OriginalName { get; set; }

    /// <summary>加密當下的原始路徑，用來在解密時決定還原位置。</summary>
    public required string OriginalPath { get; set; }

    /// <summary>Argon2id 衍生後、用於「驗證密碼是否正確」的雜湊值（Base64）。不可逆推回密碼或加密金鑰。</summary>
    public required string PasswordVerificationHash { get; set; }

    /// <summary>本次加密使用的隨機 Salt（Base64）。</summary>
    public required string Salt { get; set; }

    public required int Argon2TimeCost { get; set; }

    public required int Argon2MemoryCostKb { get; set; }

    public required int Argon2Parallelism { get; set; }

    /// <summary>使用者設定的密碼提示，解密視窗顯示用。</summary>
    public string? Hint { get; set; }

    public required ItemType Type { get; set; }

    public long OriginalSizeBytes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastAccessedAtUtc { get; set; }

    /// <summary>
    /// 對應規格文件 3.2 節「巢狀 .locked 項目」設計：若此項目是資料夾，且封裝時偵測到內部
    /// 本來就含有其他 .locked 指標檔，記錄那些內層項目的 UUID。
    /// 只要這個清單不是空的，UI／LockService 在刪除這筆紀錄前必須擋下來，見 LockService.TryDeleteRecordAsync。
    /// </summary>
    public List<string> ContainsNestedLocks { get; set; } = new();
}
