using FileLocker.Cli;

namespace FileLocker.Cli.Tests;

/// <summary>CliExitCode.ForBatch 是「批次執行結果 → 對外行程結束碼」的唯一決策點，
/// Program.cs 跟這裡的測試呼叫的是同一個方法，保證行為一致。</summary>
public class CliExitCodeTests
{
    [Fact]
    public void ForBatch_AllSucceeded_ReturnsSuccess()
    {
        Assert.Equal(CliExitCode.Success, CliExitCode.ForBatch(successCount: 3, totalCount: 3));
    }

    [Fact]
    public void ForBatch_AllFailed_ReturnsPartialOrTotalFailure()
    {
        Assert.Equal(CliExitCode.PartialOrTotalFailure, CliExitCode.ForBatch(successCount: 0, totalCount: 3));
    }

    [Fact]
    public void ForBatch_MixedResults_ReturnsPartialOrTotalFailure()
    {
        Assert.Equal(CliExitCode.PartialOrTotalFailure, CliExitCode.ForBatch(successCount: 1, totalCount: 3));
    }

    [Fact]
    public void ForBatch_ZeroTotalCount_ReturnsSuccess()
    {
        Assert.Equal(CliExitCode.Success, CliExitCode.ForBatch(successCount: 0, totalCount: 0));
    }
}
