using System.Security.Cryptography;
using System.Text;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件第 8 節與第 6 節「vault.config.json 存放的金鑰」：
/// 用 HMAC-SHA256(uuid, VaultConfig.SigningKey) 簽署 .locked 指標檔，防止指標檔被竄改導致誤導向錯誤 UUID。
/// 這裡的金鑰只做完整性驗證，不涉及機密性，因此可以放心用明文存放在 vault.config.json 並隨 Vault 同步。
/// </summary>
public static class MarkerSigner
{
    public static string Sign(string uuid, byte[] vaultSigningKey)
    {
        using var hmac = new HMACSHA256(vaultSigningKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(uuid));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// 用固定時間比較驗證簽章，避免時序攻擊。簽章格式錯誤（不是合法的 Base64）視為驗證失敗，
    /// 不讓例外往外拋——呼叫端（開啟 .locked 檔案的流程）只需要處理「合法/不合法」兩種結果就好。
    /// </summary>
    public static bool Verify(string uuid, string signatureBase64, byte[] vaultSigningKey)
    {
        byte[] providedSignature;
        try
        {
            providedSignature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = Convert.FromBase64String(Sign(uuid, vaultSigningKey));

        // FixedTimeEquals 要求兩個陣列長度相同，長度不同直接視為不符（HMAC-SHA256 輸出長度固定，
        // 長度不對通常代表簽章本身格式就是錯的，不需要再進固定時間比較）。
        if (providedSignature.Length != expectedSignature.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature);
    }
}