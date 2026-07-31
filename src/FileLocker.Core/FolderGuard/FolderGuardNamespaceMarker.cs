using System.Security.AccessControl;
using System.Security.Principal;

namespace FileLocker.Core.FolderGuard;

/// <summary>
/// 「雙擊已上鎖資料夾直接解鎖」這個選配功能（見規劃文件）用的 desktop.ini 命名空間標記——
/// 只負責貼/撕標記本身，不碰 ACL。ACL（<see cref="FolderGuardAcl"/>）才是防護有沒有生效的
/// 唯一真相來源，這個類別失敗只影響「雙擊體驗」，呼叫端要能容忍失敗、不能連累鎖定/解鎖本身。
/// </summary>
public static class FolderGuardNamespaceMarker
{
    // 跟 dllmain.cpp／folderguard_namespace.cpp 裡 FolderGuardNamespaceFolder 的 CLSID 保持完全一致，
    // 這裡改了那邊也要跟著改，兩邊各自獨立寫死同一個值（沒有共用來源，C# 跟 C++ 本來就是分開編譯）。
    private const string NamespaceClsid = "{2A4376E0-C5FC-4126-8ACD-9FC8AA377AC1}";

    // Windows Vista 之後，完整的命名空間 CLSID 綁定要用 CLSID2，舊式的 CLSID= 只給少數
    // 舊版相容情境用，現代 Explorer（含 Windows 10/11）不會拿它來做完整的 IShellFolder 綁定——
    // 兩個都寫，新舊系統都涵蓋到。
    private const string DesktopIniContent =
        "[.ShellClassInfo]\r\nCLSID=" + NamespaceClsid + "\r\nCLSID2=" + NamespaceClsid + "\r\n";

    /// <summary>
    /// 貼標記：寫 desktop.ini + 把資料夾本身設成系統屬性——這是 Explorer 判斷「這個資料夾要看
    /// desktop.ini 決定命名空間 CLSID」的必要訊號之一。呼叫端要在 <see cref="FolderGuardAcl.ApplyDeny"/>
    /// 之前呼叫這個方法：ACL 生效後目前使用者（含本程式自己）就無法再往資料夾裡寫東西了。
    /// </summary>
    public static void Apply(string folderPath)
    {
        var desktopIniPath = Path.Combine(folderPath, "desktop.ini");

        // 先清掉既有的隱藏/系統屬性，不然檔案已存在時 File.WriteAllText 可能因為屬性擋住寫入失敗。
        if (File.Exists(desktopIniPath))
        {
            File.SetAttributes(desktopIniPath, FileAttributes.Normal);
        }
        File.WriteAllText(desktopIniPath, DesktopIniContent);
        File.SetAttributes(desktopIniPath, FileAttributes.Hidden | FileAttributes.System);

        // desktop.ini 額外加一條明確 Allow 規則，蓋掉之後從資料夾繼承下來的 Deny——explorer.exe
        // 判斷「這個資料夾要不要改問命名空間 CLSID」是用目前使用者身份執行的，desktop.ini 如果
        // 也被鎖住讀不到，雙擊永遠不會走到我們的 COM 物件，只會落回 Windows 原生的「你沒有權限」
        // 對話框。Windows ACL 規則是明確權限（不管 Allow 或 Deny）永遠比繼承權限優先，所以只加
        // 這一個檔案的 Allow，不影響資料夾本身、也不影響資料夾裡其他真正的內容，保護強度不變——
        // desktop.ini 本身只有一行 CLSID 參照，不是機密內容。這裡要在資料夾套用 Deny 之前
        // （呼叫端還沒呼叫 FolderGuardAcl.ApplyDeny）就加好，這樣之後資料夾的繼承 Deny 傳播到
        // 這個既有檔案時，會被這條已經存在的明確 Allow 蓋過去。
        var currentUser = GetCurrentUserSid();
        var desktopIniInfo = new FileInfo(desktopIniPath);
        var desktopIniSecurity = desktopIniInfo.GetAccessControl();
        desktopIniSecurity.AddAccessRule(new FileSystemAccessRule(
            currentUser, FileSystemRights.Read, AccessControlType.Allow));
        desktopIniInfo.SetAccessControl(desktopIniSecurity);

        // Explorer 對「用 desktop.ini 客製化資料夾」的長年慣例：光有 System 不夠，還要一併設定
        // ReadOnly——這個位元在資料夾身上被借用做「這個資料夾有客製化設定，要去讀 desktop.ini」
        // 的標記，不是真的唯讀語意，兩個都要設 Explorer 才會真的去讀 CLSID。
        var folderAttributes = File.GetAttributes(folderPath);
        File.SetAttributes(folderPath, folderAttributes | FileAttributes.System | FileAttributes.ReadOnly);
    }

    /// <summary>
    /// 撕標記：刪掉 desktop.ini、清掉資料夾的系統屬性，讓資料夾恢復成完全正常、Explorer 用內建
    /// 邏輯瀏覽的普通資料夾。呼叫端要在 <see cref="FolderGuardAcl.RemoveDeny"/> 之後呼叫——
    /// 先解除 ACL 才能重新寫入資料夾內容。找不到 desktop.ini／資料夾也視為成功（本來就沒貼過標記）。
    /// </summary>
    public static void Remove(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        var folderAttributes = File.GetAttributes(folderPath);
        File.SetAttributes(folderPath, folderAttributes & ~FileAttributes.System & ~FileAttributes.ReadOnly);

        var desktopIniPath = Path.Combine(folderPath, "desktop.ini");
        if (File.Exists(desktopIniPath))
        {
            File.SetAttributes(desktopIniPath, FileAttributes.Normal);
            File.Delete(desktopIniPath);
        }
    }

    private static SecurityIdentifier GetCurrentUserSid()
        => WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("無法取得目前使用者的 SID，無法設定 desktop.ini 的存取權限。");
}
