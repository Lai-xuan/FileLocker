using System.Text.Json;
using FileLocker.Core.Io;

namespace FileLocker.Core.FolderGuard;

/// <summary>
/// 對應規劃文件第 11 節：憑證與清單資料獨立於 Vault 之外的本機儲存層。純粹是檔案系統存取，
/// 跟 VaultManager 對 Vault 的定位一致——不做 ACL 操作（FolderGuardAcl 的事）也不做業務規則
/// 判斷（FolderGuardService 的事），方便獨立做單元測試。
/// </summary>
public class FolderGuardStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public FolderGuardStore(string filePath)
    {
        _filePath = filePath;
    }

    public FolderGuardData Load()
    {
        if (!File.Exists(_filePath))
        {
            return new FolderGuardData();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<FolderGuardData>(json) ?? new FolderGuardData();
        }
        catch (JsonException)
        {
            return new FolderGuardData();
        }
    }

    public void Save(FolderGuardData data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicFile.WriteAllText(_filePath, json);
    }

    /// <summary>
    /// 對應規劃文件第 9 節「健壯性檢查」：以磁碟目前實際的 ACL 狀態為準，不照單全收儲存檔內容。
    /// Locked 狀態的項目如果路徑不存在、或 ACL 拒絕規則已經不在了（例如使用者自己在檔案總管改回
    /// 權限），視為「已不在防護中」，直接從清單移除並同步寫回，不留殘留紀錄；Unlocked 狀態的項目
    /// 不做 ACL 檢查——它本來就沒有對應的 ACL 規則要驗證，只是使用者還沒手動清掉的紀錄。
    /// </summary>
    public IReadOnlyList<FolderGuardEntry> ListWithSelfHeal()
    {
        var data = Load();
        var stillValid = new List<FolderGuardEntry>();
        var changed = false;

        foreach (var entry in data.Entries)
        {
            if (entry.Status == FolderGuardStatus.Locked
                && (!Directory.Exists(entry.Path) || !FolderGuardAcl.IsDenyRuleActive(entry.Path)))
            {
                changed = true;
                continue;
            }

            stillValid.Add(entry);
        }

        if (changed)
        {
            data.Entries = stillValid;
            Save(data);
        }

        return stillValid;
    }
}
