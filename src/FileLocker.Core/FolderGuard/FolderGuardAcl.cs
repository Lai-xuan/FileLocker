using System.Security.AccessControl;
using System.Security.Principal;

namespace FileLocker.Core.FolderGuard;

/// <summary>
/// 對應 ADR-0001：純粹拒絕目前登入帳號在該資料夾上的 ACL 權限，不做擁有權轉移、不需要提權。
/// 資料夾看得到、點進去被拒絕（Windows 原生「存取被拒」錯誤），不處理父層列舉權限，也不搭配
/// 隱藏屬性——見規劃文件第 4 節。跟 VaultManager.RestrictToCurrentUser（允許目前使用者、鎖死
/// 別人）方向相反，這裡是拒絕目前使用者自己，而且刻意不吞例外：ACL 有沒有套用成功，就是這個
/// 功能存在的全部意義，失敗了必須讓呼叫端知道、回報給使用者，不能靜靜略過。
/// </summary>
public static class FolderGuardAcl
{
    private const FileSystemRights DeniedRights =
        FileSystemRights.ReadAndExecute | FileSystemRights.Write | FileSystemRights.Delete;

    public static void ApplyDeny(string folderPath)
    {
        var currentUser = GetCurrentUserSid();
        var directoryInfo = new DirectoryInfo(folderPath);
        var security = directoryInfo.GetAccessControl();

        security.AddAccessRule(new FileSystemAccessRule(
            currentUser, DeniedRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));

        directoryInfo.SetAccessControl(security);
    }

    public static void RemoveDeny(string folderPath)
    {
        var currentUser = GetCurrentUserSid();
        var directoryInfo = new DirectoryInfo(folderPath);
        var security = directoryInfo.GetAccessControl();

        security.RemoveAccessRule(new FileSystemAccessRule(
            currentUser, DeniedRights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));

        directoryInfo.SetAccessControl(security);
    }

    /// <summary>
    /// 對應規劃文件第 9 節「健壯性檢查」：即時查詢磁碟目前的 ACL 狀態，不是查記錄檔——
    /// 索引檔只是快取，這裡才是唯一的真相來源。找不到資料夾或讀取 ACL 失敗（例如權限問題）一律
    /// 視為「沒有防護中」，讓呼叫端（FolderGuardStore.ListWithSelfHeal）據此自我修復清單。
    /// </summary>
    public static bool IsDenyRuleActive(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return false;
        }

        try
        {
            var currentUser = GetCurrentUserSid();
            var security = new DirectoryInfo(folderPath).GetAccessControl();
            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Deny
                    && rule.IdentityReference is SecurityIdentifier sid
                    && sid.Value == currentUser.Value
                    && (rule.FileSystemRights & DeniedRights) == DeniedRights)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }

        return false;
    }

    private static SecurityIdentifier GetCurrentUserSid()
        => WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("無法取得目前使用者的 SID，無法操作資料夾防護的 ACL 規則。");
}
