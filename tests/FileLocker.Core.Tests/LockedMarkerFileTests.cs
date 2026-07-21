using System.Security.Cryptography;
using FileLocker.Core;
using Xunit;

namespace FileLocker.Core.Tests;

public class LockedMarkerFileTests
{
    [Fact]
    public void Create_ThenVerifySignature_ReturnsTrue()
    {
        var vaultKey = RandomNumberGenerator.GetBytes(32);
        var marker = LockedMarkerFile.Create(Guid.NewGuid().ToString(), vaultKey);

        Assert.True(marker.VerifySignature(vaultKey));
    }

    [Fact]
    public void WriteTo_ThenReadFrom_RoundTripsContent()
    {
        var vaultKey = RandomNumberGenerator.GetBytes(32);
        var original = LockedMarkerFile.Create(Guid.NewGuid().ToString(), vaultKey);
        var tempPath = Path.GetTempFileName();

        try
        {
            original.WriteTo(tempPath);
            var loaded = LockedMarkerFile.ReadFrom(tempPath);

            Assert.NotNull(loaded);
            Assert.Equal(original.Uuid, loaded!.Uuid);
            Assert.Equal(original.SignatureBase64, loaded.SignatureBase64);
            Assert.True(loaded.VerifySignature(vaultKey));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ReadFrom_NonexistentFile_ReturnsNull()
    {
        var nonexistentPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.locked");

        var result = LockedMarkerFile.ReadFrom(nonexistentPath);

        Assert.Null(result);
    }

    [Fact]
    public void ReadFrom_MalformedJson_ReturnsNullInsteadOfThrowing()
    {
        var tempPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(tempPath, "這不是合法的 JSON 內容 {{{");

            var result = LockedMarkerFile.ReadFrom(tempPath);

            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void TamperedUuidAfterWrite_FailsSignatureVerification()
    {
        // 整合情境：模擬有人打開 .locked 檔案、手動把裡面的 UUID 換掉，簽章卻沒跟著換，
        // 這是規格文件第 8 節要防的「指標檔完整性」攻擊，驗證整條保護鍊真的有串起來。
        var vaultKey = RandomNumberGenerator.GetBytes(32);
        var marker = LockedMarkerFile.Create(Guid.NewGuid().ToString(), vaultKey);
        var tempPath = Path.GetTempFileName();

        try
        {
            marker.WriteTo(tempPath);
            var loaded = LockedMarkerFile.ReadFrom(tempPath)!;
            loaded.Uuid = Guid.NewGuid().ToString(); // 模擬竄改

            Assert.False(loaded.VerifySignature(vaultKey));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}