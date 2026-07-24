namespace FileLocker.Core.Models;

/// <summary>
/// RecoveryKey 只有在這次加密啟用了恢復金鑰時才會有值，而且只有這一次回傳的時候看得到——
/// FileLocker 本身不會把它存在任何地方，GUI 收到後要立刻顯示給使用者、強制使用者做出「存成檔案」
/// 或「已經抄下來了」的選擇，不能只是靜靜地顯示過去就算了。
///
/// ErrorCode／ErrorDetail 是給前端多語言轉譯用的（見規格文件第 10 節）：ErrorMessage 保留固定的
/// 繁體中文文字，當前端找不到 ErrorCode 對應的翻譯時當後備顯示；ErrorCode 是一組固定的英文代碼
/// （見 ErrorCodes 類別），ErrorDetail 是代碼裡需要內嵌的動態內容（例如檔案路徑、例外訊息），
/// 這個欄位本身不翻譯，直接原樣嵌進前端對應語言的句子範本裡。
/// </summary>
public record LockResult(bool Success, string Uuid, string LockedMarkerPath, string? ErrorMessage = null, string? RecoveryKey = null, string? ErrorCode = null, string? ErrorDetail = null);

public record UnlockResult(bool Success, string RestoredPath, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 對應規格文件 3.2 節防呆機制：刪除紀錄失敗時，用這個結果類型告訴呼叫端「因為裡面還有巢狀鎖定」，
/// 而不是單純回傳 bool，方便 UI 顯示對應的白話提示文字。
/// </summary>
public record DeleteRecordResult(bool Success, bool BlockedByNestedLocks, IReadOnlyList<string>? NestedUuids = null, string? ErrorMessage = null, string? ErrorCode = null);

/// <summary>
/// 對應清單頁的「盡力而為」檢查：只檢查 metadata.OriginalPath 反推出來的預期位置，
/// 不是掃描整個磁碟去找 .locked 檔案實際在哪——使用者若把它搬去別的地方，這裡就檢查不到，
/// 這是設計上刻意的取捨（完整掃描成本太高、也不一定找得到）。
/// </summary>
public record MarkerStatus(bool Found, string? MarkerPath, string? Message);