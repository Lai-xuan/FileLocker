using System.Globalization;
using System.Text;
using FileLocker.Core;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;

if (args.Length < 1)
{
    PrintUsage();
    return;
}

// 允許用環境變數指定 Vault 路徑，方便無 GUI 環境（排程工作、遠端伺服器）指到跟主程式
// 相同或不同的 Vault，不用寫死路徑或改程式碼——沒有設定的話就跟主程式一樣退回預設路徑。
var vaultPath = Environment.GetEnvironmentVariable("FILELOCKER_VAULT_PATH");
if (string.IsNullOrWhiteSpace(vaultPath))
{
    vaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileLocker", "Vault");
}
Directory.CreateDirectory(vaultPath);
Console.WriteLine($"Vault 位置：{vaultPath}");

var vault = new VaultManager(vaultPath);
var service = new LockService(vault);

// 這裡刻意不用 VaultIndexCache（GUI 用的 SQLite 加速層）——那層資料只靠一個常駐的
// FileSystemWatcher 保持最新，CLI 每次執行都是全新短命的行程，沒有常駐監看，
// 快取會立刻變成過時的殘影（實測：encrypt 完馬上在下一次呼叫 --list 完全看不到剛加密的項目）。
// VaultManager.ScanAll() 每次直接掃 Vault 資料夾裡的 .meta.json，慢一點但保證即時正確，
// 對一個「用完就結束」的行程來說這才是對的取捨。
var command = args[0];

switch (command)
{
    case "--encrypt":
        RequireArgs(2);
        await EncryptCommandAsync(args[1]);
        break;
    case "--unlock":
        RequireArgs(2);
        await UnlockCommandAsync(args[1]);
        break;
    case "--unlock-recovery":
        RequireArgs(3);
        await UnlockByRecoveryKeyCommandAsync(args[1], args[2], args.Length > 3 ? args[3] : null);
        break;
    case "--list":
        ListCommand();
        break;
    case "--delete":
        RequireArgs(2);
        await DeleteCommandAsync(args[1]);
        break;
    default:
        PrintUsage();
        break;
}

void RequireArgs(int minCount)
{
    if (args.Length < minCount)
    {
        PrintUsage();
        Environment.Exit(1);
    }
}

async Task EncryptCommandAsync(string targetPath)
{
    if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
    {
        Console.WriteLine($"錯誤：找不到 {targetPath}");
        return;
    }

    Console.Write("請輸入密碼：");
    var password = ReadPassword();
    Console.Write("\n請再輸入一次密碼確認：");
    var confirmPassword = ReadPassword();
    Console.WriteLine();

    if (password != confirmPassword)
    {
        Console.WriteLine("兩次輸入的密碼不一致，取消加密。");
        return;
    }

    if (string.IsNullOrEmpty(password))
    {
        Console.WriteLine("密碼不能是空的，取消加密。");
        return;
    }

    Console.Write("要順便產生恢復金鑰嗎？(y/N)：");
    var enableRecoveryKey = (Console.ReadLine() ?? "").Trim().Equals("y", StringComparison.OrdinalIgnoreCase);

    Console.Write("密碼提示（可留空，直接按 Enter）：");
    var hint = Console.ReadLine();

    // Passkey 刻意不在 CLI 提供——WinRT KeyCredentialManager 會跳出 Windows Hello 系統 UI，
    // 這是無 GUI 環境的存在意義相衝突的功能，之後如果要支援也應該是另一個獨立指令，不是這裡硬塞。
    Console.WriteLine("加密中...");
    var result = await service.EncryptAsync(
        targetPath, password, string.IsNullOrWhiteSpace(hint) ? null : hint,
        enableRecoveryKey: enableRecoveryKey);

    if (result.Success)
    {
        Console.WriteLine("加密成功！");
        Console.WriteLine($"  UUID：{result.Uuid}");
        Console.WriteLine($"  指標檔位置：{result.LockedMarkerPath}");
        if (!string.IsNullOrEmpty(result.RecoveryKey))
        {
            Console.WriteLine($"  恢復金鑰（請妥善保存，不會再顯示第二次）：{result.RecoveryKey}");
        }
    }
    else
    {
        Console.WriteLine($"加密失敗：{result.ErrorMessage}");
    }
}

async Task UnlockCommandAsync(string markerPath)
{
    if (!File.Exists(markerPath))
    {
        Console.WriteLine($"錯誤：找不到指標檔 {markerPath}");
        return;
    }

    Console.Write("請輸入密碼：");
    var password = ReadPassword();
    Console.WriteLine();

    Console.WriteLine("解密中...");
    var result = await service.DecryptAsync(markerPath, password);

    PrintUnlockResult(result);
}

async Task UnlockByRecoveryKeyCommandAsync(string uuid, string recoveryKey, string? destinationDir)
{
    Console.WriteLine("解密中...");
    var result = await service.DecryptByRecoveryKeyAsync(uuid, recoveryKey, destinationDir);

    PrintUnlockResult(result);
}

void PrintUnlockResult(UnlockResult result)
{
    if (result.Success)
    {
        Console.WriteLine("解密成功！");
        Console.WriteLine($"  已還原至：{result.RestoredPath}");
    }
    else
    {
        Console.WriteLine($"解密失敗：{result.ErrorMessage}");
    }
}

void ListCommand()
{
    var entries = vault.ScanAll().ToList();
    if (entries.Count == 0)
    {
        Console.WriteLine("Vault 目前是空的。");
        return;
    }

    foreach (var entry in entries)
    {
        Console.WriteLine($"{entry.Uuid}  [{entry.Type}]  {entry.OriginalName}");
        Console.WriteLine($"    原始路徑：{entry.OriginalPath}");
        Console.WriteLine($"    大小：{FormatSize(entry.OriginalSizeBytes)}  " +
            $"建立時間：{entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"    Passkey：{(entry.PasskeyEnabled ? "是" : "否")}  " +
            $"恢復金鑰：{(entry.RecoveryKeyEnabled ? "是" : "否")}" +
            (entry.ContainsNestedLocks.Count > 0 ? $"  內含 {entry.ContainsNestedLocks.Count} 個巢狀加密項目" : ""));
        Console.WriteLine();
    }
}

string FormatSize(long bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double size = bytes;
    var unitIndex = 0;
    while (size >= 1024 && unitIndex < units.Length - 1)
    {
        size /= 1024;
        unitIndex++;
    }
    return $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {units[unitIndex]}";
}

async Task DeleteCommandAsync(string uuid)
{
    Console.Write($"確定要永久刪除 {uuid} 嗎？此動作無法復原 (y/N)：");
    var confirm = (Console.ReadLine() ?? "").Trim();
    if (!confirm.Equals("y", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("已取消。");
        return;
    }

    var result = await service.TryDeleteRecordAsync(uuid);

    // CLI 沒有 GUI 那層 VaultIndexCache（SQLite 加速索引），每次都是直接掃 .meta.json，
    // 沒有「快取殘留孤兒紀錄」這個問題可言——RecordNotFound 這裡就是單純的「查無此 uuid」。
    if (!result.Success && result.ErrorCode == ErrorCodes.RecordNotFound)
    {
        Console.WriteLine($"找不到 UUID 為 {uuid} 的加密紀錄。");
        return;
    }

    if (result.Success)
    {
        Console.WriteLine("刪除成功。");
    }
    else if (result.BlockedByNestedLocks)
    {
        Console.WriteLine("刪除失敗：資料夾內還有巢狀加密項目，請先個別處理：");
        foreach (var nestedUuid in result.NestedUuids ?? [])
        {
            Console.WriteLine($"  {nestedUuid}");
        }
    }
    else
    {
        Console.WriteLine($"刪除失敗：{result.ErrorMessage}");
    }
}

// 主控台沒有內建的密碼遮罩輸入，自己用 Console.ReadKey 逐字元讀取，
// 顯示 * 取代實際字元，支援 Backspace 修改，Enter 結束輸入。
//
// Console.ReadKey 在標準輸入被重新導向時（腳本管線、排程工作丟進去的批次輸入）會直接丟例外，
// 不是回傳不正確的值——這正是「無 GUI 環境」預期會遇到的用法，所以這裡必須先偵測
// Console.IsInputRedirected，退回 Console.ReadLine()（沒有遮罩，但至少能動）。
string ReadPassword()
{
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? "";
    }

    var password = new StringBuilder();
    ConsoleKeyInfo key;

    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.Remove(password.Length - 1, 1);
                Console.Write("\b \b");
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
            Console.Write("*");
        }
    }

    return password.ToString();
}

void PrintUsage()
{
    Console.WriteLine("用法：");
    Console.WriteLine("  FileLocker.Cli --encrypt <檔案或資料夾路徑>");
    Console.WriteLine("  FileLocker.Cli --unlock <.locked 檔案路徑>");
    Console.WriteLine("  FileLocker.Cli --unlock-recovery <uuid> <恢復金鑰> [還原目的地資料夾]");
    Console.WriteLine("  FileLocker.Cli --list");
    Console.WriteLine("  FileLocker.Cli --delete <uuid>");
    Console.WriteLine();
    Console.WriteLine("環境變數 FILELOCKER_VAULT_PATH 可以覆寫預設 Vault 位置（未設定時跟主程式共用同一個預設路徑）。");
}
