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
    /// </summary>
    public IEnumerable<LockedItemMetadata> ScanAll()
    {
        if (!Directory.Exists(VaultPath))
        {
            yield break;
        }

        foreach (var metaFilePath in Directory.EnumerateFiles(VaultPath, "*.meta.json"))
        {
            LockedItemMetadata? metadata = null;
            try
            {
                var json = File.ReadAllText(metaFilePath);
                metadata = JsonSerializer.Deserialize<LockedItemMetadata>(json);
            }
            catch (JsonException)
            {
                // 略過損毀的單一項目，繼續掃描其他項目。
            }
            catch (IOException)
            {
                // 例如檔案正被雲端同步用戶端鎖定寫入中，略過這次掃描，下次刷新再讀一次即可。
            }

            if (metadata is not null)
            {
                yield return metadata;
            }
        }
    }

    /// <summary>
    /// 刪除 Vault 內對應的 .enc 與 .meta.json。刻意設計成冪等（idempotent）：
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