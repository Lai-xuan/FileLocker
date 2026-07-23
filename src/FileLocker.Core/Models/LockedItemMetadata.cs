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

    // ---- 對應規格文件 8.1 節「Passkey 快速解鎖」，以下四個欄位只有啟用時才會有值 ----

    /// <summary>是否有為這個項目啟用 Passkey 快速解鎖。false 時，下面三個欄位一律為 null。</summary>
    public bool PasskeyEnabled { get; set; }

    /// <summary>這個項目專屬的 Windows Hello 裝置金鑰名稱（帶隨機 GUID，見 PasskeyProtector.GenerateCredentialName）。</summary>
    public string? PasskeyCredentialName { get; set; }

    /// <summary>簽章用的隨機挑戰資料（Base64）。本身不是機密，外洩也沒關係，純粹是簽章的輸入。</summary>
    public string? PasskeyChallenge { get; set; }

    /// <summary>用 Passkey 簽章衍生出的包裝金鑰加密過的內容金鑰（Base64），格式：Nonce+Tag+Ciphertext。</summary>
    public string? PasskeyWrappedContentKey { get; set; }

    // ---- 對應規格文件「恢復金鑰」，以下欄位只有啟用時才會有值 ----

    /// <summary>是否有為這個項目啟用恢復金鑰。false 時，下面的欄位為 null。</summary>
    public bool RecoveryKeyEnabled { get; set; }

    /// <summary>用恢復金鑰衍生出的包裝金鑰加密過的內容金鑰（Base64），格式同 PasskeyWrappedContentKey。</summary>
    public string? RecoveryKeyWrappedContentKey { get; set; }
}