using System.Security.Cryptography;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件 0.2 節與 3.3 節：密碼延伸參數的預設值。
/// 數值先給常見的安全建議起點，之後可以依實際測試裝置的效能微調
/// （記憶體成本越高越抗 GPU 暴力破解，但加解密會變慢，需要抓平衡）。
/// </summary>
public static class KeyDerivationDefaults
{
    public const int TimeCost = 3;
    public const int MemoryCostKb = 65536; // 64 MB
    public const int Parallelism = 2;
    public const int SaltSizeBytes = 16;

    /// <summary>Argon2id 輸出的主金鑰長度（bytes），之後會再用 HKDF 切成兩把子金鑰。</summary>
    public const int MasterKeySizeBytes = 32;
}

/// <summary>
/// 對應規格文件 3.3 節步驟 5：從主金鑰切分出「加密金鑰」與「密碼驗證雜湊」兩個用途不同的子金鑰，
/// 確保就算 PasswordVerificationHash 外洩，也無法反推出可以解密內容的 EncryptionKey。
/// </summary>
public readonly record struct DerivedKeys(byte[] EncryptionKey, byte[] VerificationHash);

public static class Argon2KeyDerivation
{
    /// <summary>產生一份新的隨機 Salt，每次加密都要重新產生，不可重複使用。</summary>
    public static byte[] GenerateSalt()
        => RandomNumberGenerator.GetBytes(KeyDerivationDefaults.SaltSizeBytes);

    /// <summary>
    /// 用 Argon2id(password, salt) 衍生出主金鑰。
    /// TODO: 用 Konscious.Security.Cryptography.Argon2id 實作
    ///   var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password));
    ///   argon2.Salt = salt; argon2.DegreeOfParallelism = parallelism;
    ///   argon2.MemorySize = memoryCostKb; argon2.Iterations = timeCost;
    ///   return argon2.GetBytes(KeyDerivationDefaults.MasterKeySizeBytes);
    /// </summary>
    public static byte[] DeriveMasterKey(
        string password,
        byte[] salt,
        int timeCost = KeyDerivationDefaults.TimeCost,
        int memoryCostKb = KeyDerivationDefaults.MemoryCostKb,
        int parallelism = KeyDerivationDefaults.Parallelism)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 用 HKDF 從主金鑰切出兩把用途不同的子金鑰（info 參數需固定不同字串，例如 "FileLocker/encryption" 與
    /// "FileLocker/verification"，確保兩把金鑰彼此無法互相推導）。
    /// TODO: 用 System.Security.Cryptography.HKDF.Expand 實作
    /// </summary>
    public static DerivedKeys SplitMasterKey(byte[] masterKey)
    {
        throw new NotImplementedException();
    }
}
