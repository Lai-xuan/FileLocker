using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件 0.2 節與 3.3 節：密碼延展參數的預設值。
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

    /// <summary>切分出來的每把子金鑰長度（AES-256 金鑰需要 32 bytes）。</summary>
    public const int SubKeySizeBytes = 32;
}

/// <summary>
/// 對應規格文件 3.3 節步驟 5：從主金鑰切分出「加密金鑰」與「密碼驗證雜湊」兩個用途不同的子金鑰，
/// 確保就算 PasswordVerificationHash 外洩，也無法反推出可以解密內容的 EncryptionKey。
/// </summary>
public readonly record struct DerivedKeys(byte[] EncryptionKey, byte[] VerificationHash);

public static class Argon2KeyDerivation
{
    // HKDF 的 info 參數用固定、彼此不同的字串，確保兩把子金鑰之間無法互相推導。
    private static readonly byte[] EncryptionInfo = Encoding.UTF8.GetBytes("FileLocker/encryption/v1");
    private static readonly byte[] VerificationInfo = Encoding.UTF8.GetBytes("FileLocker/verification/v1");

    /// <summary>產生一份新的隨機 Salt，每次加密都要重新產生，不可重複使用。</summary>
    public static byte[] GenerateSalt()
        => RandomNumberGenerator.GetBytes(KeyDerivationDefaults.SaltSizeBytes);

    /// <summary>
    /// 用 Argon2id(password, salt) 衍生出主金鑰。
    /// 這一步是刻意設計成「慢」的（記憶體成本 + 時間成本），拖慢暴力破解的速度。
    ///
    /// 密碼安全性注意：Encoding.UTF8.GetBytes(password) 產生的中間位元組陣列用完會主動清零
    /// （見 CryptographicOperations.ZeroMemory 呼叫），不留在記憶體裡比必要的時間更久——
    /// 這是額外查資料庫審查時特別確認過的一點：這段位元組雖然只是密碼的 UTF-8 編碼副本，
    /// 但既然拿得到它的參照，就沒有理由不清掉。
    /// </summary>
    public static byte[] DeriveMasterKey(
        string password,
        byte[] salt,
        int timeCost = KeyDerivationDefaults.TimeCost,
        int memoryCostKb = KeyDerivationDefaults.MemoryCostKb,
        int parallelism = KeyDerivationDefaults.Parallelism)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                DegreeOfParallelism = parallelism,
                MemorySize = memoryCostKb,
                Iterations = timeCost
            };

            return argon2.GetBytes(KeyDerivationDefaults.MasterKeySizeBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// 用 HKDF 從主金鑰切出兩把用途不同的子金鑰。主金鑰本身已經是 Argon2id 輸出的高熵值，
    /// 這裡直接把它當作 HKDF 的 PRK（Pseudo-Random Key）使用 HKDF-Expand，不需要再做一次 HKDF-Extract。
    /// </summary>
    public static DerivedKeys SplitMasterKey(byte[] masterKey)
    {
        var encryptionKey = new byte[KeyDerivationDefaults.SubKeySizeBytes];
        var verificationHash = new byte[KeyDerivationDefaults.SubKeySizeBytes];

        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, encryptionKey, EncryptionInfo);
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, verificationHash, VerificationInfo);

        return new DerivedKeys(encryptionKey, verificationHash);
    }

    /// <summary>
    /// 對應規格文件 3.3 節步驟 3～5：把 DeriveMasterKey + SplitMasterKey 串起來的便利方法，
    /// 並在切分完成後主動清空記憶體中的主金鑰（規格文件第 8 節安全性考量）。
    /// LockService.EncryptAsync / DecryptAsync 應該呼叫這個方法，而不是分開呼叫上面兩個。
    /// </summary>
    public static DerivedKeys DeriveKeys(
        string password,
        byte[] salt,
        int timeCost = KeyDerivationDefaults.TimeCost,
        int memoryCostKb = KeyDerivationDefaults.MemoryCostKb,
        int parallelism = KeyDerivationDefaults.Parallelism)
    {
        var masterKey = DeriveMasterKey(password, salt, timeCost, memoryCostKb, parallelism);
        try
        {
            return SplitMasterKey(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    /// <summary>
    /// 對應規格文件 3.4 節步驟 3～4：重新用輸入的密碼衍生子金鑰，跟儲存在 .meta.json 的
    /// PasswordVerificationHash 用固定時間比較（避免時序攻擊洩漏比對進度），驗證密碼是否正確。
    /// 回傳值同時給呼叫端「密碼對不對」的結果，以及對的話拿到的 EncryptionKey，不用再算第二次。
    /// </summary>
    public static (bool IsValid, byte[]? EncryptionKey) VerifyPassword(
        string password,
        byte[] salt,
        byte[] storedVerificationHash,
        int timeCost = KeyDerivationDefaults.TimeCost,
        int memoryCostKb = KeyDerivationDefaults.MemoryCostKb,
        int parallelism = KeyDerivationDefaults.Parallelism)
    {
        var derived = DeriveKeys(password, salt, timeCost, memoryCostKb, parallelism);
        var isValid = CryptographicOperations.FixedTimeEquals(derived.VerificationHash, storedVerificationHash);

        if (isValid)
        {
            return (true, derived.EncryptionKey);
        }

        CryptographicOperations.ZeroMemory(derived.EncryptionKey);
        return (false, null);
    }
}