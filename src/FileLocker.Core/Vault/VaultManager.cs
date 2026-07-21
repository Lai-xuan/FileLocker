using FileLocker.Core.Models;

namespace FileLocker.Core.Vault;

/// <summary>
/// 對應規格文件第 4 節與第 6 節：Vault 資料夾內 {uuid}.enc / {uuid}.meta.json / vault.config.json 的讀寫層。
/// 這一層不做加解密邏輯（那是 LockService 的事），純粹是檔案系統存取，方便日後獨立做單元測試（可指向暫存資料夾）。
/// </summary>
public class VaultManager
{
    public string VaultPath { get; }

    public VaultManager(string vaultPath)
    {
        VaultPath = vaultPath;
    }

    private string ConfigPath => Path.Combine(VaultPath, "vault.config.json");
    private string EncPath(string uuid) => Path.Combine(VaultPath, $"{uuid}.enc");
    private string MetaPath(string uuid) => Path.Combine(VaultPath, $"{uuid}.meta.json");

    /// <summary>
    /// 對應第 6 節：Vault 第一次啟動時若不存在 vault.config.json 就產生新的簽章金鑰；已存在就直接讀取沿用。
    /// TODO: 用 RandomNumberGenerator.GetBytes(32) 產生新金鑰、System.Text.Json 讀寫。
    /// </summary>
    public VaultConfig LoadOrCreateConfig()
    {
        throw new NotImplementedException();
    }

    /// <summary>TODO: System.Text.Json 序列化寫入 {uuid}.meta.json。</summary>
    public void SaveMetadata(LockedItemMetadata metadata)
    {
        throw new NotImplementedException();
    }

    /// <summary>找不到回傳 null，由呼叫端決定要顯示什麼錯誤訊息。</summary>
    public LockedItemMetadata? LoadMetadata(string uuid)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 對應第 4 節：App 啟動或清單頁刷新時，掃描 Vault 內全部 *.meta.json 建立/更新本機快取索引。
    /// 這裡先回傳完整清單，本機 SQLite 快取的建置邏輯之後再包一層在這之上。
    /// </summary>
    public IEnumerable<LockedItemMetadata> ScanAll()
    {
        throw new NotImplementedException();
    }

    /// <summary>刪除 Vault 內對應的 .enc 與 .meta.json（呼叫前的巢狀鎖定檢查由 LockService 負責，這裡不做業務邏輯判斷）。</summary>
    public void DeleteItem(string uuid)
    {
        throw new NotImplementedException();
    }

    public Stream OpenEncryptedContentRead(string uuid) => File.OpenRead(EncPath(uuid));

    public Stream OpenEncryptedContentWrite(string uuid) => File.Create(EncPath(uuid));
}
