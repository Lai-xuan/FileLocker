using System.IO;
using System.Security.Cryptography;
using FileLocker.Core.FolderGuard;
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

    // dllmain.cpp 的 IsFolderGuardLocked 執行期讀這個登錄值，不再自己硬編一份拒絕權限遮罩——
    // FolderGuardAcl.DeniedRightsMask 才是唯一定義處，見該常數上的說明。
    private const string FolderGuardDeniedRightsMaskValueName = "FolderGuardDeniedRightsMask";

    // 「雙擊已上鎖資料夾直接解鎖」選配功能用的標記檔副檔名關聯——見 FolderGuardUnlockMarkerFile
    // 上的說明：之前用 Shell Namespace Extension（CLSID2）接管資料夾本身的做法，實測連續踩到
    // explorer.exe 死結、右鍵選單整個消失兩個問題，改成走跟 `.locked` 一樣的檔案關聯機制，
    // 不需要任何 COM 命名空間物件，只需要一般的副檔名開啟動作登錄。
    private const string LockFolderMarkerProgId = "FileLocker.LockFolderMarker";
    private const string LockFolderMarkerUnlockArgFlag = "--folder-guard-unlock-marker";
    private const string LockFolderMarkerIconFileName = "LockFolderMarker.ico";

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
        var appExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileLocker.App.exe");

        // 只比對路徑不夠——DLL 有可能原地被重新編譯覆蓋（路徑沒變、內容變了），這種情況也要
        // 判定成「需要重新註冊」，才能正確觸發呼叫端「請重啟 Explorer」的提示（見 App.xaml.cs）。
        var lockFolderMarkerIconPath = Path.Combine(AppContext.BaseDirectory, LockFolderMarkerIconFileName);

        var alreadyRegistered =
            IsClsidFullyRegistered(ClsidString, dllPath, fileHash, IsContextMenuHandlerFullyRegistered)
            && IsFolderGuardDeniedRightsMaskCurrent()
            && IsLockFolderMarkerAssociationRegistered(appExePath, lockFolderMarkerIconPath);

        if (alreadyRegistered)
        {
            return false; // 已經註冊且指向正確路徑，不需要重做。
        }

        RegisterClsidAndHandler(ClsidString, dllPath, fileHash, RegisterContextMenuHandler);
        WriteFolderGuardDeniedRightsMask();

        RegisterLockFolderMarkerAssociation(appExePath, lockFolderMarkerIconPath);

        return true;
    }

    /// <summary>
    /// 右鍵選單、命名空間資料夾這兩組 CLSID 註冊本質相同（寫入 InprocServer32 + 各自一組
    /// 「掛勾」子機碼），共用同一個「這組 CLSID 是否已完整註冊」判斷，兩邊只是各自的掛勾驗證
    /// 邏輯（<paramref name="isHandlerRegistered"/>）不同——不需要各自維護一份平行的判斷式。
    /// </summary>
    private static bool IsClsidFullyRegistered(string clsidString, string dllPath, string fileHash, Func<bool> isHandlerRegistered)
        => string.Equals(ReadRegisteredDllPath(clsidString), dllPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadRegisteredDllHash(clsidString), fileHash, StringComparison.OrdinalIgnoreCase)
            && isHandlerRegistered();

    /// <summary>對稱於 <see cref="IsClsidFullyRegistered"/>：註冊 CLSID 本身，再執行各自的掛勾註冊。</summary>
    private static void RegisterClsidAndHandler(string clsidString, string dllPath, string fileHash, Action registerHandler)
    {
        RegisterClsid(clsidString, dllPath, fileHash);
        registerHandler();
    }

    private static void WriteFolderGuardDeniedRightsMask()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\CLSID\{ClsidString}\InprocServer32");
        key.SetValue(FolderGuardDeniedRightsMaskValueName, FolderGuardAcl.DeniedRightsMask, RegistryValueKind.DWord);
    }

    private static bool IsFolderGuardDeniedRightsMaskCurrent()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{ClsidString}\InprocServer32");
        return key?.GetValue(FolderGuardDeniedRightsMaskValueName) is int value && value == FolderGuardAcl.DeniedRightsMask;
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
    /// `.lockfolder` 副檔名關聯：純標準的「這個副檔名要用哪個程式打開」登記，不涉及任何 COM
    /// 命名空間物件——`ProgId\shell\open\command` 指到 `FileLocker.App.exe`，帶
    /// <see cref="LockFolderMarkerUnlockArgFlag"/> 旗標＋標記檔路徑（`%1`）當參數，
    /// App.xaml.cs 收到後讀出標記檔內容拿到真正資料夾路徑，走既有的解鎖流程。圖示用專屬的
    /// `LockFolderMarker.ico`（見專案檔內 Content 項目，會跟著建置/發佈輸出一起帶走）——找不到
    /// 這個檔案（理論上不該發生，但避免因為找不到圖示就整段登錄失敗）就退回借用主程式圖示。
    /// </summary>
    private static void RegisterLockFolderMarkerAssociation(string appExePath, string iconPath)
    {
        using var extKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{FolderGuardUnlockMarkerFile.Extension}");
        extKey.SetValue(null, LockFolderMarkerProgId);

        using var commandKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{LockFolderMarkerProgId}\shell\open\command");
        commandKey.SetValue(null, $"\"{appExePath}\" {LockFolderMarkerUnlockArgFlag} \"%1\"");

        using var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{LockFolderMarkerProgId}\DefaultIcon");
        iconKey.SetValue(null, File.Exists(iconPath) ? $"\"{iconPath}\",0" : $"\"{appExePath}\",0");
    }

    private static bool IsLockFolderMarkerAssociationRegistered(string appExePath, string iconPath)
    {
        using var extKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{FolderGuardUnlockMarkerFile.Extension}");
        if (!string.Equals(extKey?.GetValue(null) as string, LockFolderMarkerProgId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var commandKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{LockFolderMarkerProgId}\shell\open\command");
        var expectedCommand = $"\"{appExePath}\" {LockFolderMarkerUnlockArgFlag} \"%1\"";
        if (!string.Equals(commandKey?.GetValue(null) as string, expectedCommand, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var iconKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{LockFolderMarkerProgId}\DefaultIcon");
        var expectedIcon = File.Exists(iconPath) ? $"\"{iconPath}\",0" : $"\"{appExePath}\",0";
        return string.Equals(iconKey?.GetValue(null) as string, expectedIcon, StringComparison.OrdinalIgnoreCase);
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