using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using Xunit;

namespace FileLocker.Core.Tests;

public class MarkerSignerTests
{
    [Fact]
    public void Sign_IsDeterministic_SameInputsProduceSameSignature()
    {
        // 跟 Argon2 不同，HMAC 沒有隨機 Salt，同樣的 uuid + 金鑰要每次都算出一樣的簽章，
        // 這樣同一個 .locked 檔案不管什麼時候重新驗證都要能通過。
        var uuid = Guid.NewGuid().ToString();
        var key = RandomNumberGenerator.GetBytes(32);

        var first = MarkerSigner.Sign(uuid, key);
        var second = MarkerSigner.Sign(uuid, key);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Verify_WithCorrectSignatureAndKey_ReturnsTrue()
    {
        var uuid = Guid.NewGuid().ToString();
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = MarkerSigner.Sign(uuid, key);

        Assert.True(MarkerSigner.Verify(uuid, signature, key));
    }

    [Fact]
    public void Verify_WithTamperedUuid_ReturnsFalse()
    {
        // 模擬有人把 .locked 檔案內容裡的 UUID 換成別的、簽章卻沒跟著換，這是簽章機制原本要擋的攻擊情境。
        var originalUuid = Guid.NewGuid().ToString();
        var tamperedUuid = Guid.NewGuid().ToString();
        var key = RandomNumberGenerator.GetBytes(32);
        var signature = MarkerSigner.Sign(originalUuid, key);

        Assert.False(MarkerSigner.Verify(tamperedUuid, signature, key));
    }

    [Fact]
    public void Verify_WithWrongKey_ReturnsFalse()
    {
        // 模擬另一個 Vault（不同的簽章金鑰）產生的指標檔被誤放進這個 Vault 的情境。
        var uuid = Guid.NewGuid().ToString();
        var correctKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var signature = MarkerSigner.Sign(uuid, correctKey);

        Assert.False(MarkerSigner.Verify(uuid, signature, wrongKey));
    }

    [Fact]
    public void Verify_WithMalformedSignature_ReturnsFalseInsteadOfThrowing()
    {
        var uuid = Guid.NewGuid().ToString();
        var key = RandomNumberGenerator.GetBytes(32);

        var result = MarkerSigner.Verify(uuid, "這不是合法的 Base64 字串", key);

        Assert.False(result);
    }
}