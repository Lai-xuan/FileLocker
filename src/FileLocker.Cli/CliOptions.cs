namespace FileLocker.Cli;

/// <summary>某個命令列呼叫解析後的所有靜默批次模式旗標——純資料，跟 Console/檔案系統/
/// LockService 完全無關，方便單元測試（見 CliArgumentParserTests）。Program.cs 只負責
/// 「把解析結果接到 Console.Write/ReadPassword/service 呼叫」，決策邏輯全部在這裡。</summary>
public sealed record CliOptions(
    bool PasswordFromStdin,
    string? PasswordFilePath,
    bool RecoveryKeyEnabled,
    string? Hint,
    bool SkipConfirmation);

/// <summary>旗標解析失敗（缺值、旗標互斥）時丟出，Program.cs 接住後印訊息＋PrintUsage()，
/// 用既有的 CliExitCode.UsageError 結束——不是未預期的例外，是使用者輸入錯誤的正常回報方式。</summary>
public sealed class CliArgumentException(string message) : Exception(message);

public static class CliArgumentParser
{
    private const string PasswordStdinFlag = "--password-stdin";
    private const string PasswordFileFlag = "--password-file";
    private const string RecoveryKeyFlag = "--recovery-key";
    private const string HintFlag = "--hint";
    private const string YesFlag = "--yes";

    /// <summary>從 --encrypt/--unlock/--delete 之後的參數區段解析出 CliOptions，同時把剩下
    /// 不是旗標的參數（路徑/uuid 列表）分離出來回傳，保留原始順序。旗標順序不拘、可以跟位置
    /// 參數混雜——用具名旗標而不是位置參數，避免未來新增旗標時位置參數的順序爆炸。</summary>
    public static (CliOptions Options, List<string> RemainingArgs) Parse(IReadOnlyList<string> args)
    {
        var passwordFromStdin = false;
        string? passwordFilePath = null;
        var recoveryKeyEnabled = false;
        string? hint = null;
        var skipConfirmation = false;
        var remaining = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case PasswordStdinFlag:
                    passwordFromStdin = true;
                    break;
                case PasswordFileFlag:
                    passwordFilePath = RequireValue(args, ref i, PasswordFileFlag);
                    break;
                case RecoveryKeyFlag:
                    recoveryKeyEnabled = true;
                    break;
                case HintFlag:
                    hint = RequireValue(args, ref i, HintFlag);
                    break;
                case YesFlag:
                    skipConfirmation = true;
                    break;
                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

        if (passwordFromStdin && passwordFilePath is not null)
        {
            throw new CliArgumentException($"{PasswordStdinFlag} 跟 {PasswordFileFlag} 不能同時使用，請只選一種密碼來源。");
        }

        var options = new CliOptions(passwordFromStdin, passwordFilePath, recoveryKeyEnabled, hint, skipConfirmation);
        return (options, remaining);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int i, string flagName)
    {
        if (i + 1 >= args.Count)
        {
            throw new CliArgumentException($"{flagName} 後面必須接一個值。");
        }
        i++;
        return args[i];
    }
}

/// <summary>批次執行結果 → 對外行程結束碼的唯一決策點，Program.cs 跟測試都呼叫同一個方法，
/// 保證行為一致。</summary>
public static class CliExitCode
{
    public const int Success = 0;
    public const int UsageError = 1;
    // 批次中至少一筆失敗（含全部失敗）——刻意不細分「部分失敗」跟「全部失敗」，腳本幾乎都只看
    // exit code 是否非 0，細分沒有實際需求。
    public const int PartialOrTotalFailure = 2;
    // 使用者/腳本主動取消（例如刪除確認被拒絕、沒帶 --yes）：這跟「有嘗試但失敗」意義不同，
    // 腳本可能想分開處理「我沒做」跟「我做了但失敗」，所以獨立一個代碼。
    public const int Cancelled = 3;

    public static int ForBatch(int successCount, int totalCount)
        => successCount == totalCount ? Success : PartialOrTotalFailure;
}
