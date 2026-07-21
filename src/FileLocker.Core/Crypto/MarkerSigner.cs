namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件第 8 節與第 6 節「vault.config.json 存放的金鑰」：
/// 用 HMAC-SHA256(uuid, VaultConfig.SigningKey) 簽署 .locked 指標檔，防止指標檔被竄改導致誤導向錯誤 UUID。
/// 這裡的金鑰只做完整性驗證，不涉及機密性，因此可以放心用明文存放在 vault.config.json 並隨 Vault 同步。
/// </summary>
public static class MarkerSigner
{
    /// <summary>
    /// TODO: 用 System.Security.Cryptography.HMACSHA256 實作
    ///   using var hmac = new HMACSHA256(vaultSigningKey);
    ///   var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(uuid));
    ///   return Convert.ToBase64String(hash);
    /// </summary>
    public static string Sign(string uuid, byte[] vaultSigningKey)
    {
        throw new NotImplementedException();
    }

    /// <summary>用固定時間比較（CryptographicOperations.FixedTimeEquals）驗證簽章，避免時序攻擊。</summary>
    public static bool Verify(string uuid, string signatureBase64, byte[] vaultSigningKey)
    {
        throw new NotImplementedException();
    }
}
