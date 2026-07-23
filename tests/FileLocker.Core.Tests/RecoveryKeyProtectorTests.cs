using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 跟 PasskeyProtectorTests 不同，這裡全部都能自動化測試——恢復金鑰不牽涉任何 Windows Hello
/// 硬體互動，從產生到包裝/解包全部是純函式。
/// </summary>
public class RecoveryKeyProtectorTests
{
    [Fact]
    public void FormatForDisplay_ThenParseUserInput_RoundTripsOriginalBytes()
    {
        var original = RecoveryKeyProtector.GenerateRecoveryKeyBytes();

        var displayText = RecoveryKeyProtector.FormatForDisplay(original);
        var parsed = RecoveryKeyProtector.ParseUserInput(displayText);

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void ParseUserInput_IsCaseInsensitiveAndIgnoresDashesAndWhitespace()
    {
        var original = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
        var displayText = RecoveryKeyProtector.FormatForDisplay(original);

        // 模擬使用者手動輸入時可能的格式差異：小寫、拿掉分隔線、多打了空白。
        var messyInput = "  " + displayText.ToLowerInvariant().Replace("-", " ") + "  ";

        var parsed = RecoveryKeyProtector.ParseUserInput(messyInput);

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void ParseUserInput_WithInvalidCharacters_ReturnsNull()
    {
        // Base32 字母表裡沒有數字 0、1、8、9，這些字元混進去應該解析失敗。
        var result = RecoveryKeyProtector.ParseUserInput("00000-11111-88888-99999-00000-11111-8888");

        Assert.Null(result);
    }

    [Fact]
    public void ParseUserInput_WithWrongLength_ReturnsNull()
    {
        var tooShort = RecoveryKeyProtector.ParseUserInput("ABCDE-FGHIJ");

        Assert.Null(tooShort);
    }

    [Fact]
    public void DeriveWrappingKey_IsDeterministic()
    {
        var recoveryKey = RecoveryKeyProtector.GenerateRecoveryKeyBytes();

        var first = RecoveryKeyProtector.DeriveWrappingKey(recoveryKey);
        var second = RecoveryKeyProtector.DeriveWrappingKey(recoveryKey);

        Assert.Equal(first, second);
    }

    [Fact]
    public void WrapContentKey_ThenUnwrapContentKey_RoundTripsOriginalKey()
    {
        var recoveryKey = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
        var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKey);
        var originalContentKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = RecoveryKeyProtector.WrapContentKey(wrappingKey, originalContentKey);
        var unwrapped = RecoveryKeyProtector.UnwrapContentKey(wrappingKey, wrapped);

        Assert.Equal(originalContentKey, unwrapped);
    }

    [Fact]
    public void UnwrapContentKey_WithWrongRecoveryKey_ThrowsCryptographicException()
    {
        var correctRecoveryKey = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
        var wrongRecoveryKey = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
        var contentKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = RecoveryKeyProtector.WrapContentKey(RecoveryKeyProtector.DeriveWrappingKey(correctRecoveryKey), contentKey);
        var wrongWrappingKey = RecoveryKeyProtector.DeriveWrappingKey(wrongRecoveryKey);

        Assert.ThrowsAny<CryptographicException>(() => RecoveryKeyProtector.UnwrapContentKey(wrongWrappingKey, wrapped));
    }

    [Fact]
    public void GenerateRecoveryKeyBytes_ProducesDifferentValuesEachTime()
    {
        var first = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
        var second = RecoveryKeyProtector.GenerateRecoveryKeyBytes();

        Assert.NotEqual(first, second);
    }
}