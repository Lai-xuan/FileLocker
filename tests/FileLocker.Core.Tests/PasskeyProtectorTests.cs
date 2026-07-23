using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 只測 PasskeyProtector 裡不牽涉 Windows Hello 硬體互動的純函式部分。
/// IsSupportedAsync／CreateCredentialAsync／SignChallengeAsync 這些會跳出真的系統對話框、
/// 需要人親自操作驗證，沒辦法自動化測試，只能透過 GUI 手動驗證（跟獨立測試程式上已經做過的一樣）。
/// </summary>
public class PasskeyProtectorTests
{
    [Fact]
    public void DeriveWrappingKey_IsDeterministic_SameSignatureProducesSameKey()
    {
        // 對應規格文件 8.1 節：已經用真的 Windows Hello 簽章實測過決定性，這裡用假的位元組陣列
        // 驗證「衍生金鑰」這一步的邏輯本身也是決定性的（HKDF 本來就是決定性函式，這裡是回歸測試）。
        var fakeSignature = RandomNumberGenerator.GetBytes(256); // 模擬 RSA 2048-bit 簽章的長度

        var first = PasskeyProtector.DeriveWrappingKey(fakeSignature);
        var second = PasskeyProtector.DeriveWrappingKey(fakeSignature);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DeriveWrappingKey_DifferentSignature_ProducesDifferentKey()
    {
        var signatureA = RandomNumberGenerator.GetBytes(256);
        var signatureB = RandomNumberGenerator.GetBytes(256);

        var keyA = PasskeyProtector.DeriveWrappingKey(signatureA);
        var keyB = PasskeyProtector.DeriveWrappingKey(signatureB);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void WrapContentKey_ThenUnwrapContentKey_RoundTripsOriginalKey()
    {
        var wrappingKey = RandomNumberGenerator.GetBytes(32);
        var originalContentKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = PasskeyProtector.WrapContentKey(wrappingKey, originalContentKey);
        var unwrapped = PasskeyProtector.UnwrapContentKey(wrappingKey, wrapped);

        Assert.Equal(originalContentKey, unwrapped);
    }

    [Fact]
    public void UnwrapContentKey_WithWrongWrappingKey_ThrowsCryptographicException()
    {
        // 對應情境：Passkey 簽章跟當初包裝時不一致（例如換了裝置、或憑證被竄改），
        // 解包應該要失敗，而不是靜靜地回傳一份錯誤的內容金鑰。
        var correctWrappingKey = RandomNumberGenerator.GetBytes(32);
        var wrongWrappingKey = RandomNumberGenerator.GetBytes(32);
        var contentKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = PasskeyProtector.WrapContentKey(correctWrappingKey, contentKey);

        Assert.ThrowsAny<CryptographicException>(() => PasskeyProtector.UnwrapContentKey(wrongWrappingKey, wrapped));
    }

    [Fact]
    public void GenerateCredentialName_ProducesUniqueNamesEachTime()
    {
        // 對應規格文件 8.1 節：憑證名稱要夠獨特，降低跟其他程式撞名的機率。
        var nameA = PasskeyProtector.GenerateCredentialName();
        var nameB = PasskeyProtector.GenerateCredentialName();

        Assert.NotEqual(nameA, nameB);
        Assert.StartsWith("FileLocker-", nameA);
    }

    [Fact]
    public void GenerateChallenge_ProducesDifferentValuesEachTime()
    {
        var challengeA = PasskeyProtector.GenerateChallenge();
        var challengeB = PasskeyProtector.GenerateChallenge();

        Assert.NotEqual(challengeA, challengeB);
    }
}