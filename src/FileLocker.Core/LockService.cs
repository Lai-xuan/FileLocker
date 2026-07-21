using FileLocker.Core.Crypto;
using FileLocker.Core.FolderPackaging;
using FileLocker.Core.Models;
using FileLocker.Core.SecureDelete;
using FileLocker.Core.Vault;

namespace FileLocker.Core;

/// <summary>
/// 對外的主要 API 入口——GUI、CLI 原型都只需要呼叫這一層，不需要知道底下 Crypto/Vault/FolderPackaging 的細節。
/// 對應規格文件 3.3（加密流程）、3.4（解密流程）、3.2 第 3 點（刪除防呆）。
/// </summary>
public class LockService
{
    private readonly VaultManager _vault;

    public LockService(VaultManager vault)
    {
        _vault = vault;
    }

    /// <summary>
    /// 對應 3.3 節完整流程：
    ///   1. 若 path 是資料夾 → FolderArchiver.FindNestedLockedFiles 檢查巢狀鎖定 → CompressToTempZip
    ///   2. Argon2KeyDerivation 產生 salt、衍生金鑰
    ///   3. AesGcmCipher.Encrypt
    ///   4. Guid.NewGuid() 當作新檔名，VaultManager 寫入 .enc / .meta.json
    ///   5. LockedMarkerFile.Create + WriteTo 寫入原位置
    ///   6. SecureFileEraser 清除原始檔案/資料夾與暫存 zip
    /// 呼叫端（GUI）應該把這個包在背景執行緒/Task 裡跑，避免阻塞介面；大型資料夾可透過 progress 回報壓縮/加密進度。
    /// </summary>
    public Task<LockResult> EncryptAsync(string path, string password, string? hint, IProgress<double>? progress = null)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 對應 3.4 節完整流程：讀 marker → 驗證簽章 → Argon2 重新衍生 → 比對驗證雜湊 → AES-GCM 解密驗證 Tag →
    /// 視型別決定直接寫回還是先 ExtractZipToFolder → 還原原始名稱 → 刪除 Vault 內對應項目與 marker。
    /// 密碼錯誤或簽章驗證失敗都回傳 Success=false + 對應 ErrorMessage，不拋例外給呼叫端接（介面上要能直接顯示訊息）。
    /// </summary>
    public Task<UnlockResult> DecryptAsync(string lockedMarkerPath, string password)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 對應規格文件 3.2 節「刪除紀錄時，改成預設直接擋下來」：
    /// 讀取 metadata，若 ContainsNestedLocks 不是空的且 force=false，回傳
    /// DeleteRecordResult(Success=false, BlockedByNestedLocks=true, NestedUuids=...)，
    /// 讓 UI 顯示白話提示（見規格文件 3.2 節文案），不提供任何情況下的「一鍵強制刪除」UI 入口——
    /// force 參數只保留給未來如果真的需要例外處理時用，預設呼叫永遠是 force=false。
    /// </summary>
    public Task<DeleteRecordResult> TryDeleteRecordAsync(string uuid, bool force = false)
    {
        throw new NotImplementedException();
    }
}
