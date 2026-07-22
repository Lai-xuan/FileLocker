using System.Text;
using FileLocker.Core;
using FileLocker.Core.Vault;

if (args.Length < 2)
{
    PrintUsage();
    return;
}

var command = args[0];
var path = args[1];

// CLI 原型先用固定的預設 Vault 路徑，之後 GUI 的 Vault 設定精靈（規格文件第 6 節）再讓使用者自訂。
var vaultPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "FileLocker", "Vault");
Directory.CreateDirectory(vaultPath);
Console.WriteLine($"Vault 位置：{vaultPath}");

var vault = new VaultManager(vaultPath);
var service = new LockService(vault);

switch (command)
{
    case "--encrypt":
        await EncryptCommandAsync(path);
        break;
    case "--unlock":
        await UnlockCommandAsync(path);
        break;
    default:
        PrintUsage();
        break;
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

    Console.Write("密碼提示（可留空，直接按 Enter）：");
    var hint = Console.ReadLine();

    Console.WriteLine("加密中...");
    var result = await service.EncryptAsync(targetPath, password, string.IsNullOrWhiteSpace(hint) ? null : hint);

    if (result.Success)
    {
        Console.WriteLine("加密成功！");
        Console.WriteLine($"  UUID：{result.Uuid}");
        Console.WriteLine($"  指標檔位置：{result.LockedMarkerPath}");
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

// 主控台沒有內建的密碼遮罩輸入，自己用 Console.ReadKey 逐字元讀取，
// 顯示 * 取代實際字元，支援 Backspace 修改，Enter 結束輸入。
string ReadPassword()
{
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
}