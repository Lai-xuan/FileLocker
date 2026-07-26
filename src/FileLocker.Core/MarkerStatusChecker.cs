using FileLocker.Core.Models;

namespace FileLocker.Core;

/// <summary>
/// 對應架構審查（2026-07-26）：從 LockService 分離出來的獨立 module——只回答「這個項目原本
/// 位置的 .locked 指標檔還在不在、UUID 對不對」，純粹是檔案系統查詢，不牽涉加密/解密/lockout
/// 邏輯，也完全不需要建構 LockService 那一整套依賴（VaultManager／HistoryLogger／
/// LockoutTracker）就能測試（見 MarkerStatusCheckerTests）。全部是無狀態的靜態方法，
/// 不用任何實例欄位，呼叫端不需要先建立任何物件。
/// </summary>
public static class MarkerStatusChecker
{
    public static MarkerStatus CheckMarkerStatus(LockedItemMetadata metadata)
        => CheckMarkerStatus(metadata.Uuid, metadata.OriginalPath, metadata.Type);

    /// <summary>
    /// 只吃清單頁實際需要的三個欄位，讓呼叫端（例如用 VaultIndexEntry 快取投影組出清單時）
    /// 不需要為了呼叫這個方法，硬湊一個帶假資料的完整 LockedItemMetadata。
    /// </summary>
    public static MarkerStatus CheckMarkerStatus(string uuid, string originalPath, ItemType type)
    {
        var expectedPath = ComputeMarkerPath(originalPath, type == ItemType.Folder);

        if (!File.Exists(expectedPath))
        {
            return new MarkerStatus(false, null, "指標檔可能被移動或刪除");
        }

        var marker = LockedMarkerFile.ReadFrom(expectedPath);
        if (marker is null)
        {
            return new MarkerStatus(false, null, "原本位置的檔案無法解析為指標檔");
        }

        if (marker.Uuid != uuid)
        {
            return new MarkerStatus(false, null, "原本的位置已經被別的加密項目取代", ConflictingUuid: marker.Uuid);
        }

        return new MarkerStatus(true, expectedPath, null);
    }

    public static string ComputeMarkerPath(string originalPath, bool isFolder)
    {
        var trimmedPath = originalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDir = isFolder
            ? Directory.GetParent(trimmedPath)?.FullName ?? throw new IOException($"無法判斷父資料夾：{originalPath}")
            : Path.GetDirectoryName(Path.GetFullPath(trimmedPath)) ?? throw new IOException($"無法判斷父資料夾：{originalPath}");

        var baseName = isFolder
            ? Path.GetFileName(trimmedPath)
            : Path.GetFileNameWithoutExtension(trimmedPath);

        return Path.Combine(parentDir, $"{baseName}.locked");
    }
}
