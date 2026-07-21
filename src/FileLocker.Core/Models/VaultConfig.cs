namespace FileLocker.Core.Models;

/// <summary>
/// 對應規格文件第 6 節：Vault 根目錄的 vault.config.json。
/// 只存放「簽章金鑰」（HMAC 用，見 MarkerSigner），不是加密金鑰，外洩最壞情況只是能偽造 .locked
/// 指標檔的 UUID 指向，仍然需要正確密碼才能解密任何內容，因此允許明文隨 Vault 一起雲端同步。
/// </summary>
public class VaultConfig
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>256-bit 隨機金鑰（Base64），首次建立 Vault 時產生一次，之後固定不變。</summary>
    public required string SigningKeyBase64 { get; set; }
}
