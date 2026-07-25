using FileLocker.Core.Models;

namespace FileLocker.Core.Vault;

/// <summary>
/// 對應規格文件第 4 節「本機唯讀快取索引」：VaultIndexCache 讀出來的投影內容，只含清單頁
/// 實際會用到的欄位。刻意不含 PasswordVerificationHash／Salt／Passkey 與恢復金鑰的
/// WrappedContentKey 等解密專用的敏感欄位——解密流程本來就是直接呼叫
/// VaultManager.LoadMetadata(uuid) 讀單一份 .meta.json，不需要快取加速，
/// 這些欄位也就不該多存一份到 SQLite 快取資料庫裡。
/// </summary>
public sealed record VaultIndexEntry(
    string Uuid,
    string OriginalName,
    string OriginalPath,
    ItemType Type,
    bool PasskeyEnabled,
    bool RecoveryKeyEnabled,
    string? BatchId,
    long OriginalSizeBytes,
    string? Hint,
    DateTimeOffset CreatedAtUtc,
    int NestedLockCount)
{
    /// <summary>從完整的 LockedItemMetadata 投影出快取需要的子集欄位。</summary>
    public static VaultIndexEntry FromMetadata(LockedItemMetadata metadata) => new(
        metadata.Uuid,
        metadata.OriginalName,
        metadata.OriginalPath,
        metadata.Type,
        metadata.PasskeyEnabled,
        metadata.RecoveryKeyEnabled,
        metadata.BatchId,
        metadata.OriginalSizeBytes,
        metadata.Hint,
        metadata.CreatedAtUtc,
        metadata.ContainsNestedLocks.Count);
}
