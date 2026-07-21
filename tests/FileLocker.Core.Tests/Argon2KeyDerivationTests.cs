using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using Xunit;

namespace FileLocker.Core.Tests;

public class Argon2KeyDerivationTests
{
    // 測試用刻意調低 Argon2 參數，只是為了讓測試跑快一點，不是實際上線要用的安全參數。
    private const int FastTimeCost = 1;
    private const int FastMemoryCostKb = 8192; // 8 MB
    private const int FastParallelism = 1;

    [Fact]
    public void DeriveKeys_SamePasswordAndSalt_ProducesSameKeys()
    {
        var password = "correct horse battery staple";
        var salt = Argon2KeyDerivation.GenerateSalt();

        var first = Argon2KeyDerivation.DeriveKeys(password, salt, FastTimeCost, FastMemoryCostKb, FastParallelism);
        var second = Argon2KeyDerivation.DeriveKeys(password, salt, FastTimeCost, FastMemoryCostKb, FastParallelism);

        Assert.Equal(first.EncryptionKey, second.EncryptionKey);
        Assert.Equal(first.VerificationHash, second.VerificationHash);
    }

    [Fact]
    public void DeriveKeys_EncryptionKeyAndVerificationHash_AreDifferent()
    {
        // 對應規格文件：兩把子金鑰用途不同，就算其中一把外洩也不能推出另一把。
        var password = "correct horse battery staple";
        var salt = Argon2KeyDerivation.GenerateSalt();

        var keys = Argon2KeyDerivation.DeriveKeys(password, salt, FastTimeCost, FastMemoryCostKb, FastParallelism);

        Assert.NotEqual(keys.EncryptionKey, keys.VerificationHash);
    }

    [Fact]
    public void DeriveKeys_DifferentSalt_ProducesDifferentKeys()
    {
        var password = "correct horse battery staple";
        var saltA = Argon2KeyDerivation.GenerateSalt();
        var saltB = Argon2KeyDerivation.GenerateSalt();

        var keysA = Argon2KeyDerivation.DeriveKeys(password, saltA, FastTimeCost, FastMemoryCostKb, FastParallelism);
        var keysB = Argon2KeyDerivation.DeriveKeys(password, saltB, FastTimeCost, FastMemoryCostKb, FastParallelism);

        Assert.NotEqual(keysA.EncryptionKey, keysB.EncryptionKey);
    }

    [Fact]
    public void VerifyPassword_CorrectPassword_ReturnsValidAndMatchingEncryptionKey()
    {
        var password = "correct horse battery staple";
        var salt = Argon2KeyDerivation.GenerateSalt();
        var originalKeys = Argon2KeyDerivation.DeriveKeys(password, salt, FastTimeCost, FastMemoryCostKb, FastParallelism);

        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(
            password, salt, originalKeys.VerificationHash, FastTimeCost, FastMemoryCostKb, FastParallelism);

        Assert.True(isValid);
        Assert.Equal(originalKeys.EncryptionKey, encryptionKey);
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsInvalidAndNullKey()
    {
        var salt = Argon2KeyDerivation.GenerateSalt();
        var originalKeys = Argon2KeyDerivation.DeriveKeys("correct password", salt, FastTimeCost, FastMemoryCostKb, FastParallelism);

        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(
            "wrong password", salt, originalKeys.VerificationHash, FastTimeCost, FastMemoryCostKb, FastParallelism);

        Assert.False(isValid);
        Assert.Null(encryptionKey);
    }
}