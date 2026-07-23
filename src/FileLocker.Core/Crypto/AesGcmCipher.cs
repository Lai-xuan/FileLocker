using System.Security.Cryptography;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件 3.1 節檔案格式：Nonce/IV(12 bytes) + Ciphertext + Auth Tag(16 bytes)。
/// 直接用 .NET 內建的 System.Security.Cryptography.AesGcm，不需要額外套件。
/// Encrypt/Decrypt 都改用 ReadOnlySpan&lt;byte&gt; 收輸入，byte[] 可以隱式轉換過去，
/// 呼叫端既有的 byte[] 呼叫方式完全不用改；但呼叫端如果本來就手上有一個大陣列的某一段
/// （例如從一個大檔案內容裡切一段出來），可以直接用 array.AsSpan(start, length) 傳進來，
/// 不需要像陣列切片語法 array[a..b] 那樣多複製一份新陣列——這是 ChunkedCipher 會用到的地方。
/// </summary>
public static class AesGcmCipher
{
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(byte[] key, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return (nonce, ciphertext, tag);
    }

    /// <summary>
    /// 解密並驗證 Auth Tag；Tag 驗證失敗（代表密碼錯誤或密文被竄改）AesGcm 會丟出
    /// CryptographicException，呼叫端要接住並轉換成「密碼錯誤或檔案已損毀」的訊息，不要洩漏細節。
    /// </summary>
    public static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagSizeBytes);

        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }
}