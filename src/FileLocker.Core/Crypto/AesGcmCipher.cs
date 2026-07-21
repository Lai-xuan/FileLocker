using System.Security.Cryptography;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件 3.1 節檔案格式：Nonce/IV(12 bytes) + Ciphertext + Auth Tag(16 bytes)。
/// 直接用 .NET 8 內建的 System.Security.Cryptography.AesGcm，不需要額外套件。
/// </summary>
public static class AesGcmCipher
{
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return (nonce, ciphertext, tag);
    }

    public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);

        // Tag 驗證失敗（密碼錯誤或密文被竄改）這裡會丟 CryptographicException，
        // 呼叫端（LockService）要接住並轉譯成「密碼錯誤或檔案已損毀」，不要把原始例外訊息透露給使用者。
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}