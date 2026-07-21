using System.Security.Cryptography;
using System.Text;
using FileLocker.Core.Crypto;
using Xunit;

namespace FileLocker.Core.Tests;

public class AesGcmCipherTests
{
    [Fact]
    public void Encrypt_Then_Decrypt_ReturnsOriginalPlaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32); // AES-256
        var plaintext = Encoding.UTF8.GetBytes("這是測試用的檔案內容");

        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(key, plaintext);
        var decrypted = AesGcmCipher.Decrypt(key, nonce, ciphertext, tag);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WithWrongKey_ThrowsCryptographicException()
    {
        var correctKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("secret content");

        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(correctKey, plaintext);

        // 對應規格文件：密碼錯誤時 Auth Tag 驗證會失敗，這是 AES-GCM 內建的完整性保護，
        // 不是另外寫邏輯去檢查，這個測試就是在驗證這個保證真的有生效。
        Assert.ThrowsAny<CryptographicException>(() =>
            AesGcmCipher.Decrypt(wrongKey, nonce, ciphertext, tag));
    }

    [Fact]
    public void Decrypt_WithTamperedCiphertext_ThrowsCryptographicException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("secret content");

        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(key, plaintext);
        ciphertext[0] ^= 0xFF; // 模擬密文被竄改一個位元組

        Assert.ThrowsAny<CryptographicException>(() =>
            AesGcmCipher.Decrypt(key, nonce, ciphertext, tag));
    }
}