using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace FileLocker.App;

/// <summary>
/// 讓 FileLocker.App 自己在啟動時檢查／註冊 Shell Extension，不需要安裝程式知道任何 COM／
/// regsvr32 相關的事——安裝程式只要把 FileLockerShellExtension.dll 跟 FileLocker.App.exe
/// 放在同一個資料夾裡就好（一般的「應用程式內容資料夾」功能就夠了，見規格文件第 13 節）。
///
/// 全部寫在 HKEY_CURRENT_USER\Software\Classes 底下，不是 HKEY_CLASSES_ROOT——這是
/// Windows 官方支援的每個使用者各自登錄的機制，Explorer 會自動把它併進當前使用者看到的
/// HKEY_CLASSES_ROOT 合併視圖裡，效果完全一樣，但不需要系統管理員權限，安裝程式本身
/// 也不需要為了這件事另外要求提高權限。
/// </summary>
internal static class ShellExtensionRegistrar
{
    // 要跟 dllmain.cpp 裡的 CLSID_FileLockerShellExtension 保持完全一致。
    private const string ClsidString = "{A1B2C3D4-E5F6-4789-9ABC-DEF012345678}";
    private const string DllFileName = "FileLockerShellExtension.dll";

    // 要跟 folderguard_namespace.cpp 裡的 CLSID_FolderGuardNamespaceFolder、
    // FolderGuardNamespaceMarker.cs 裡的 NamespaceClsid 保持完全一致——「雙擊已上鎖資料夾
    // 直接解鎖」這個選配功能用的命名空間擴充，跟右鍵選單是完全獨立的 COM 類別/CLSID。
    private const string NamespaceClsidString = "{2A4376E0-C5FC-4126-8ACD-9FC8AA377AC1}";

    // SFGAO_FOLDER | SFGAO_FILESYSTEM | SFGAO_FILESYSANCESTOR——要跟 folderguard_namespace.cpp
    // 的 IShellFolder::GetAttributesOf 回傳值保持一致（不含 SFGAO_BROWSABLE／SFGAO_HASSUBFOLDER）。
    private const int NamespaceFolderAttributes = 0x70000000;

    /// <summary>
    /// 檢查、需要的話就（重新）註冊 Shell Extension。設計成每次啟動都可以安全呼叫——
    /// 已經註冊且路徑正確的話幾乎不花時間（只是讀一個登錄值來比對），不會拖慢正常啟動。
    /// 回傳 true 代表這次真的執行了註冊動作（通常代表是全新安裝，或應用程式資料夾被搬移過），
    /// 呼叫端可以依此決定要不要提示使用者重啟 Explorer 讓右鍵選單生效。
    /// </summary>
    // "*" 這個類別在 Windows Shell 登錄機制裡只涵蓋檔案，不包含資料夾——資料夾要另外登記在
    // "Directory" 底下右鍵選單才會出現。之前只登記了 "*"，導致右鍵資料夾完全看不到加密選項，
    // 這裡兩個都要登記。
    private static readonly string[] ContextMenuHandlerKeyPaths =
    [
        @"Software\Classes\*\shellex\ContextMenuHandlers\FileLocker",
        @"Software\Classes\Directory\shellex\ContextMenuHandlers\FileLocker"
    ];

    public static bool EnsureRegistered()
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, DllFileName);
        if (!File.Exists(dllPath))
        {
            // 開發階段常見情境：Shell Extension 還沒編譯，或還沒複製到這個資料夾——
            // 不當成錯誤，安靜跳過即可，不影響主程式其他功能運作。
            return false;
        }

        var fileHash = ComputeFileHash(dllPath);

        // 只比對路徑不夠——DLL 有可能原地被重新編譯覆蓋（路徑沒變、內容變了），這種情況也要
        // 判定成「需要重新註冊」，才能正確觸發呼叫端「請重啟 Explorer」的提示（見 App.xaml.cs）。
        var alreadyRegistered = string.Equals(ReadRegisteredDllPath(ClsidString), dllPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadRegisteredDllHash(ClsidString), fileHash, StringComparison.OrdinalIgnoreCase)
            && IsContextMenuHandlerFullyRegistered()
            && string.Equals(ReadRegisteredDllPath(NamespaceClsidString), dllPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadRegisteredDllHash(NamespaceClsidString), fileHash, StringComparison.OrdinalIgnoreCase)
            && IsNamespaceShellFolderRegistered();

        if (alreadyRegistered)
        {
            return false; // 已經註冊且指向正確路徑，不需要重做。
        }

        RegisterClsid(ClsidString, dllPath, fileHash);
        RegisterContextMenuHandler();

        RegisterClsid(NamespaceClsidString, dllPath, fileHash);
        RegisterNamespaceShellFolder();

        return true;
    }

    private static string? ReadRegisteredDllPath(string clsidString)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsidString}\InprocServer32");
        return key?.GetValue(null) as string;
    }

    private static string? ReadRegisteredDllHash(string clsidString)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{clsidString}\InprocServer32");
        return key?.GetValue("FileHash") as string;
    }

    private static void RegisterClsid(string clsidString, string dllPath, string fileHash)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{clsidString}\InprocServer32");
        key.SetValue(null, dllPath);
        key.SetValue("ThreadingModel", "Apartment");
        key.SetValue("FileHash", fileHash);
    }

    /// <summary>
    /// CLSID 底下的 ShellFolder 子機碼是 Explorer 判斷「這個 CLSID 是一個命名空間資料夾」的
    /// 必要登記，跟右鍵選單那組 ContextMenuHandlers 完全獨立——desktop.ini 裡的 CLSID 值要
    /// 能在這裡找到對應的 ShellFolder 登記，Explorer 才會真的把它當命名空間物件處理。
    /// </summary>
    private static void RegisterNamespaceShellFolder()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{NamespaceClsidString}\ShellFolder");
        key.SetValue("Attributes", NamespaceFolderAttributes, RegistryValueKind.DWord);
    }

    private static bool IsNamespaceShellFolderRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{NamespaceClsidString}\ShellFolder");
        return key?.GetValue("Attributes") is int attributes && attributes == NamespaceFolderAttributes;
    }

    /// <summary>
    /// 用檔案內容雜湊而不是修改時間／檔案大小來判斷「DLL 換了沒」——原地重新編譯覆蓋同一個
    /// 檔名時，這是唯一能可靠偵測到內容真的不同的方式。
    /// </summary>
    private static string ComputeFileHash(string dllPath)
    {
        var bytes = File.ReadAllBytes(dllPath);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>
    /// 檔案（"*"）跟資料夾（"Directory"）都要登記同一個 CLSID，右鍵選單才會同時對兩種情況出現。
    /// 已經裝過舊版（只登記了 "*"）的使用者，下次啟動時 IsContextMenuHandlerFullyRegistered
    /// 會偵測到 "Directory" 那筆缺漏，觸發重新註冊補上，不需要使用者手動重裝。
    /// </summary>
    private static void RegisterContextMenuHandler()
    {
        foreach (var keyPath in ContextMenuHandlerKeyPaths)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(null, ClsidString);
        }
    }

    private static bool IsContextMenuHandlerFullyRegistered()
    {
        foreach (var keyPath in ContextMenuHandlerKeyPaths)
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (!string.Equals(key?.GetValue(null) as string, ClsidString, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}