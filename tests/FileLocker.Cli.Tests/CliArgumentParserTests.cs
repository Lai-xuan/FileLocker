using FileLocker.Cli;

namespace FileLocker.Cli.Tests;

/// <summary>
/// CliArgumentParser.Parse 是 CLI 靜默批次模式的唯一決策點——把「--password-stdin 這種旗標
/// 出現了沒有」跟「Console/檔案系統/LockService 怎麼用密碼」這兩件事分開，純資料進、純資料出，
/// 不需要真的去讀 Console 或建立 LockService 就能測完全部分支。
/// </summary>
public class CliArgumentParserTests
{
    [Fact]
    public void Parse_PasswordStdinFlag_SetsPasswordFromStdinTrue()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt", "--password-stdin"]);

        Assert.True(options.PasswordFromStdin);
    }

    [Fact]
    public void Parse_PasswordFileFlag_SetsPasswordFilePath()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt", "--password-file", "pw.txt"]);

        Assert.Equal("pw.txt", options.PasswordFilePath);
    }

    [Fact]
    public void Parse_NoPasswordFlags_BothStdinAndFileAreDefaultOff()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt"]);

        Assert.False(options.PasswordFromStdin);
        Assert.Null(options.PasswordFilePath);
    }

    [Fact]
    public void Parse_RecoveryKeyFlag_SetsRecoveryKeyEnabledTrue_DefaultIsFalse()
    {
        var (withFlag, _) = CliArgumentParser.Parse(["file.txt", "--recovery-key"]);
        var (withoutFlag, _) = CliArgumentParser.Parse(["file.txt"]);

        Assert.True(withFlag.RecoveryKeyEnabled);
        Assert.False(withoutFlag.RecoveryKeyEnabled);
    }

    [Fact]
    public void Parse_HintFlag_CapturesFollowingValue()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt", "--hint", "my hint"]);

        Assert.Equal("my hint", options.Hint);
    }

    [Fact]
    public void Parse_HintFlag_MissingValue_ThrowsUsageError()
    {
        Assert.Throws<CliArgumentException>(() => CliArgumentParser.Parse(["file.txt", "--hint"]));
    }

    [Fact]
    public void Parse_YesFlag_SetsSkipConfirmationTrue()
    {
        var (options, _) = CliArgumentParser.Parse(["uuid1", "--yes"]);

        Assert.True(options.SkipConfirmation);
    }

    [Fact]
    public void Parse_FlagsMixedWithPositionalPaths_SeparatesFlagsFromRemainingArgs()
    {
        var (options, remaining) = CliArgumentParser.Parse(
            ["file1.txt", "--password-stdin", "--recovery-key", "file2.txt"]);

        Assert.True(options.PasswordFromStdin);
        Assert.True(options.RecoveryKeyEnabled);
        Assert.Equal(["file1.txt", "file2.txt"], remaining);
    }

    [Fact]
    public void Parse_PasswordStdinAndPasswordFileBothPresent_IsRejectedAsConflicting()
    {
        Assert.Throws<CliArgumentException>(() =>
            CliArgumentParser.Parse(["file.txt", "--password-stdin", "--password-file", "pw.txt"]));
    }
}
