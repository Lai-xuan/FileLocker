namespace FileLocker.Core.FolderGuard;

/// <summary>
/// 資料夾防護「上鎖」/「解鎖」統一的組合邏輯：ACL 拒絕規則（<see cref="FolderGuardAcl"/>）永遠是
/// 唯一的保護來源，不論有沒有啟用「雙擊解鎖」都一樣強——那個選配功能現在只是額外多放一個標記檔
/// （<see cref="FolderGuardUnlockMarkerFile"/>），不影響 ACL 本身（見該類別上的說明，這是放棄
/// Shell Namespace Extension 技術路線之後的設計，之前的版本啟用雙擊解鎖時會整個不套 ACL，
/// 保護強度打折扣，現在不用再犧牲）。<see cref="FileLocker.Core.FolderGuardService"/> 只呼叫
/// <see cref="Apply"/>／<see cref="Remove"/>，不直接碰下面兩個類別。
/// </summary>
internal static class FolderGuardProtection
{
    /// <summary>ACL 套用失敗要整個往外拋，呼叫端要整個回報失敗——這是保護有沒有生效的唯一
    /// 真相來源。標記檔失敗只影響「雙擊解鎖」這個加分體驗，不能連累鎖定本身，安靜吞掉例外。</summary>
    public static void Apply(string path, bool markerEnabled)
    {
        FolderGuardAcl.ApplyDeny(path);

        if (markerEnabled)
        {
            try
            {
                FolderGuardUnlockMarkerFile.Apply(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }

    /// <summary>標記檔不管上鎖當下開關是不是開的都要嘗試移除，避免開關中途被關掉、資料夾解鎖後
    /// 還留著沒清乾淨的殘留檔案。標記檔是同層兄弟檔案，不受資料夾本身存不存在影響，資料夾已經被
    /// 加密流程消耗掉（整個刪除）時仍然要清掉標記檔，不能因為 Directory.Exists 為 false 就整個
    /// 提早返回略過。</summary>
    public static void Remove(string path)
    {
        if (Directory.Exists(path))
        {
            FolderGuardAcl.RemoveDeny(path);
        }

        try
        {
            FolderGuardUnlockMarkerFile.Remove(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }

    /// <summary>統一的「這個資料夾目前是不是在防護中」判準，供 <see cref="FolderGuardStore.ListWithSelfHeal"/>
    /// 這類健壯性檢查使用——ACL 永遠是唯一來源，不用再理會標記檔（跟舊版 Plan 不同）。</summary>
    public static bool IsActive(string path)
        => Directory.Exists(path) && FolderGuardAcl.IsDenyRuleActive(path);

    /// <summary>全域「雙擊解鎖」開關切換時，對「已經鎖著」的資料夾補上/撕掉標記檔——ACL 本身
    /// 不需要跟著動，兩種模式現在共用同一套 ACL 保護，只差有沒有這個標記檔。</summary>
    public static void SwitchMode(string path, bool markerEnabled)
    {
        try
        {
            if (markerEnabled)
            {
                FolderGuardUnlockMarkerFile.Apply(path);
            }
            else
            {
                FolderGuardUnlockMarkerFile.Remove(path);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
    }
}
