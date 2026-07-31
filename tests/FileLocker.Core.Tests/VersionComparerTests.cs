using FileLocker.Core.UpdateCheck;
using Xunit;

namespace FileLocker.Core.Tests;

public class VersionComparerTests
{
    [Theory]
    [InlineData("1.0.0", "v1.0.0", false)] // 相等版本
    [InlineData("1.0.0", "v1.1.0", true)] // latest 較新
    [InlineData("1.1.0", "v1.0.0", false)] // latest 較舊
    [InlineData("1.0.0", "1.1.0", true)] // 兩邊都不帶 v 前綴
    [InlineData("1.9.0", "v1.10.0", true)] // 數字比較，不是字串比較——字串比較會把 "1.10.0" 誤判成比 "1.9.0" 舊
    [InlineData("1.0", "v1.0.0", true)] // 段數較少時，Version 缺的欄位視為 -1，比對起來仍然算「較舊」
    [InlineData("1.0", "v1.0.1", true)] // 不同段數，latest 較新
    [InlineData("1.0.0", "not-a-version", false)] // latest 格式異常
    [InlineData("not-a-version", "v1.0.0", false)] // current 格式異常
    [InlineData("1.0.0", "", false)] // latest 空字串
    [InlineData("", "v1.0.0", false)] // current 空字串
    [InlineData(null, "v1.0.0", false)] // current 為 null
    [InlineData("1.0.0", null, false)] // latest 為 null
    public void IsNewerVersionAvailable_ReturnsExpected(string? currentVersion, string? latestTag, bool expected)
    {
        var result = VersionComparer.IsNewerVersionAvailable(currentVersion, latestTag);

        Assert.Equal(expected, result);
    }
}
