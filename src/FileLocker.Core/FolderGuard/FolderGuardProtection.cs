namespace FileLocker.Core.FolderGuard;

/// <summary>
/// 資料夾防護「上鎖」/「解鎖」各自要做兩件事、且順序不能顛倒（一個是命名空間標記
/// <see cref="FolderGuardNamespaceMarker"/>，一個是 ACL 拒絕規則 <see cref="FolderGuardAcl"/>，
/// 兩者方向相反）——這裡是唯一知道這個順序限制的地方。<see cref="FileLocker.Core.FolderGuardService"/>
/// 只呼叫 <see cref="Apply"/>／<see cref="Remove"/>，不直接碰下面兩個類別，順序寫錯的可能性
/// 從「每個呼叫端都要記得」收斂成「這一個模組內部」。
/// </summary>
internal static class FolderGuardProtection
{
    /// <summary>
    /// 命名空間標記要在 ACL 生效「之前」貼——Deny 規則套用後，目前使用者（含本程式自己）就
    /// 無法再往資料夾裡寫 desktop.ini 了。標記失敗只影響「雙擊解鎖」這個加分體驗，不能連累
    /// 鎖定本身，安靜吞掉例外；ACL 套用失敗則整個往外拋，呼叫端要整個回報失敗。
    /// </summary>
    public static void Apply(string path, bool markerEnabled)
    {
        if (markerEnabled)
        {
            try
            {
                FolderGuardNamespaceMarker.Apply(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }

        FolderGuardAcl.ApplyDeny(path);
    }

    /// <summary>
    /// 撕標記要在 RemoveDeny 之後（先解除 ACL 才能重新寫入資料夾內容），且不管上鎖當下標記開關
    /// 是不是開的都要嘗試——避免開關中途被關掉、資料夾解鎖後還留著沒清乾淨的 desktop.ini 殘留。
    /// 資料夾已經不存在（例如被加密流程消耗掉）時兩件事都不用做，視為成功。
    /// </summary>
    public static void Remove(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        FolderGuardAcl.RemoveDeny(path);

        try
        {
            FolderGuardNamespaceMarker.Remove(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }
}
