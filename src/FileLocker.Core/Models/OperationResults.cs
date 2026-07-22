namespace FileLocker.Core.Models;

public record LockResult(bool Success, string Uuid, string LockedMarkerPath, string? ErrorMessage = null);

public record UnlockResult(bool Success, string RestoredPath, string? ErrorMessage = null);

public record DeleteRecordResult(bool Success, bool BlockedByNestedLocks, IReadOnlyList<string>? NestedUuids = null, string? ErrorMessage = null);

/// <summary>
/// 對應清單頁的「盡力而為」檢查：只檢查 metadata.OriginalPath 反推出來的預期位置，
/// 不是掃描整個磁碟去找 .locked 檔案實際在哪——使用者若把它搬去別的地方，這裡就檢查不到，
/// 這是設計上刻意的取捨（完整掃描成本太高、也不一定找得到）。
/// </summary>
public record MarkerStatus(bool Found, string? MarkerPath, string? Message);