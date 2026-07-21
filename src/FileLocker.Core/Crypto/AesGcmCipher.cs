namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件 3.1 節檔案格式：Nonce/IV(12 bytes) + Ciphertext + Auth Tag(16 bytes)。
/// 直接用 .NET 8 內建的 System.Security.Cryptography.AesGcm，不需要額外套件。
/// </summary>
public static class AesGcmCipher
{
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    /// <summary>
    /// TODO: 實作內容
    ///   var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
    ///   var ciphertext = new byte[plaintext.Length];
    ///   var tag = new byte[TagSizeBytes];
    ///   using var aes = new AesGcm(key, TagSizeBytes);
    ///   aes.Encrypt(nonce, plaintext, ciphertext, tag);
    /// 大型檔案建議改用 Stream 版本（分段讀寫），這裡先給位元組陣列版本方便先跑通單元測試。
    /// </summary>
    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] plaintext)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 解密並驗證 Auth Tag；Tag 驗證失敗（代表密碼錯誤或密文被竄改）AesGcm 會丟出
    /// CryptographicException，呼叫端要接住並轉換成「密碼錯誤或檔案已損毀」的訊息，不要洩漏細節。
    /// </summary>
    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        throw new NotImplementedException();
    }
}
