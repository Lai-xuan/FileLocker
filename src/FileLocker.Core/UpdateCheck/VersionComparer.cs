namespace FileLocker.Core.UpdateCheck;

/// <summary>比較目前安裝版本（installer_config.json 的 "version"，例如 "1.0.0"）跟 GitHub
/// release tag（例如 "v1.0.0"）。兩邊都先去掉開頭的 v/V 再用 Version 做數字比較，不是字串
/// 比較——字串比較會讓 "1.10.0" 被誤判成比 "1.9.0" 舊。任一邊格式解析失敗視為「沒有更新」，
/// 不要讓格式異常的 tag 誤判成需要更新。</summary>
public static class VersionComparer
{
    public static bool IsNewerVersionAvailable(string? currentVersion, string? latestTag)
    {
        if (!TryParse(currentVersion, out var current) || !TryParse(latestTag, out var latest))
        {
            return false;
        }
        return latest > current;
    }

    private static bool TryParse(string? raw, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        var trimmed = raw.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out version!);
    }
}
