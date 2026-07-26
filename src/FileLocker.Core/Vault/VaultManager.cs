using System.Security.Cryptography;
using System.Text.Json;
using FileLocker.Core.Models;

namespace FileLocker.Core.Vault;

/// <summary>
/// 對應規格文件第 4 節與第 6 節：Vault 資料夾內 {uuid}.enc / {uuid}.meta.json / vault.config.json 的讀寫層。
/// 這一層不做加解密邏輯（那是 LockService 的事），純粹是檔案系統存取，方便獨立做單元測試（可指向暫存資料夾）。
/// 也不做「巢狀鎖定不能刪除」這類業務規則判斷（那是 LockService.TryDeleteRecordAsync 的責任），
/// DeleteItem 只單純負責把檔案從 Vault 移除。
/// </summary>
public class VaultManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string VaultPath { get; }

    public VaultManager(string vaultPath)
    {
        VaultPath = vaultPath;
    }

    private string ConfigPath => Path.Combine(VaultPath, "vault.config.json");
    private string EncPath(string uuid) => Path.Combine(VaultPath, $"{uuid}.enc");

    /// <summary>
    /// 對應架構審查（2026-07-26）：曝露成 interface 的一部分，讓 VaultIndexCache 之類的呼叫端
    /// 需要知道某個 UUID 對應的 .meta.json 實際路徑時（例如查詢檔案的最後寫入時間）可以直接問
    /// 這裡，不用自己重算一次同樣的檔名規則——正規檔名只在這裡定義一次。
    /// </summary>
    public string GetMetaFilePath(string uuid) => MetaPath(uuid);

    private string MetaPath(string uuid) => Path.Combine(VaultPath, $"{uuid}.meta.json");

    /// <summary>
    /// 對應第 6 節：Vault 第一次啟動時若不存在 vault.config.json 就產生新的簽章金鑰；
    /// 已存在（例如接上既有的同步 Vault）就直接讀取沿用，確保多裝置共用同一把簽章金鑰。
    /// </summary>
    public VaultConfig LoadOrCreateConfig()
    {
        Directory.CreateDirectory(VaultPath);

        if (File.Exists(ConfigPath))
        {
            var existingJson = File.ReadAllText(ConfigPath);
            var existingConfig = JsonSerializer.Deserialize<VaultConfig>(existingJson)
                ?? throw new InvalidDataException($"Vault 設定檔損毀，無法解析：{ConfigPath}");
            return existingConfig;
        }

        var signingKey = RandomNumberGenerator.GetBytes(32);
        var newConfig = new VaultConfig
        {
            SigningKeyBase64 = Convert.ToBase64String(signingKey)
        };

        var json = JsonSerializer.Serialize(newConfig, JsonOptions);
        File.WriteAllText(ConfigPath, json);

        return newConfig;
    }

    public void SaveMetadata(LockedItemMetadata metadata)
    {
        Directory.CreateDirectory(VaultPath);
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(MetaPath(metadata.Uuid), json);
    }

    /// <summary>找不到、或內容損毀，一律回傳 null，由呼叫端決定要顯示什麼錯誤訊息（不拋例外）。</summary>
    public LockedItemMetadata? LoadMetadata(string uuid)
    {
        var path = MetaPath(uuid);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<LockedItemMetadata>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 對應第 4 節：App 啟動或清單頁刷新時，掃描 Vault 內全部 *.meta.json 建立/更新本機快取索引。
    /// 遇到單一檔案損毀（JSON 解析失敗）會跳過該筆、不中斷整個掃描，確保一個壞掉的項目不會讓
    /// 使用者連清單都看不到——這對雲端同步情境尤其重要，同步中的檔案偶爾會短暫讀到不完整內容。
    ///
    /// 對應雲端同步情境測試（2026-07-24）：這裡刻意用檔案「內容」裡的 UUID 判斷是否重複，
    /// 不是用檔名——雲端同步用戶端偵測到衝突時，常見做法是另外存一份帶著裝置名稱的「衝突副本」
    /// 檔案（檔名不同）。單純的重複副本內容通常完全一樣，回傳哪一份都無所謂；但如果是真正的
    /// 分歧（兩台裝置各自對同一個項目做了不同修改，例如一邊開了 Passkey 一邊沒開），
    /// 就要有個判斷依據決定回傳哪一份——這裡用檔案的最後寫入時間，同一個 UUID 只回傳
    /// 寫入時間最新的那份，比單純「哪個先被列舉到就用哪個」更合理，至少行為是決定性的、
    /// 偏好保留較新的變更。
    /// </summary>
    public IEnumerable<LockedItemMetadata> ScanAll()
    {
        if (!Directory.Exists(VaultPath))
        {
            yield break;
        }

        var byUuid = new Dictionary<string, (LockedItemMetadata Metadata, DateTime LastWriteUtc)>();

        foreach (var metaFilePath in Directory.EnumerateFiles(VaultPath, "*.meta.json"))
        {
            LockedItemMetadata? metadata;
            DateTime lastWriteUtc;
            try
            {
                var json = File.ReadAllText(metaFilePath);
                metadata = JsonSerializer.Deserialize<LockedItemMetadata>(json);
                lastWriteUtc = File.GetLastWriteTimeUtc(metaFilePath);
            }
            catch (JsonException)
            {
                // 略過損毀的單一項目，繼續掃描其他項目。
                continue;
            }
            catch (IOException)
            {
                // 例如檔案正被雲端同步用戶端鎖定寫入中，略過這次掃描，下次刷新再讀一次即可。
                continue;
            }

            if (metadata is null)
            {
                continue;
            }

            if (!byUuid.TryGetValue(metadata.Uuid, out var existing) || lastWriteUtc > existing.LastWriteUtc)
            {
                byUuid[metadata.Uuid] = (metadata, lastWriteUtc);
            }
        }

        foreach (var entry in byUuid.Values)
        {
            yield return entry.Metadata;
        }
    }

    /// <summary>
    /// 刪除 Vault 內對應的 .enc 與 .meta.json。刻意設計成幂等（idempotent）：
    /// 檔案本來就不存在時不拋例外，讓呼叫端可以安全地重複呼叫而不用先檢查存在與否。
    /// </summary>
    public void DeleteItem(string uuid)
    {
        var encPath = EncPath(uuid);
        var metaPath = MetaPath(uuid);

        if (File.Exists(encPath))
        {
            File.Delete(encPath);
        }

        if (File.Exists(metaPath))
        {
            File.Delete(metaPath);
        }
    }

    public Stream OpenEncryptedContentRead(string uuid) => File.OpenRead(EncPath(uuid));

    public Stream OpenEncryptedContentWrite(string uuid)
    {
        Directory.CreateDirectory(VaultPath);
        return File.Create(EncPath(uuid));
    }
}