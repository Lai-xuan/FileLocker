namespace FileLocker.Core.Models;

public record LockResult(bool Success, string Uuid, string LockedMarkerPath, string? ErrorMessage = null);

public record UnlockResult(bool Success, string RestoredPath, string? ErrorMessage = null);

/// <summary>
/// 對應規格文件 3.2 節防呆機制：刪除紀錄失敗時，用這個結果類型告訴呼叫端「因為裡面還有巢狀鎖定」，
/// 而不是單純回傳 bool，方便 UI 顯示對應的白話提示文字。
/// </summary>
public record DeleteRecordResult(bool Success, bool BlockedByNestedLocks, IReadOnlyList<string>? NestedUuids = null, string? ErrorMessage = null);
