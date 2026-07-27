using System.Text.Json;

namespace FileLocker.Core.History;

public enum HistoryAction
{
    Encrypted,
    Decrypted,
    RecordDeleted
}

/// <summary>
/// 對應「使用紀錄」頁的一筆資料。跟 Vault 內的 .meta.json 是分開的兩件事：
/// 這筆紀錄就算對應的加密項目已經從 Vault 移除（被解密或刪除），也會繼續留著，
/// 單純是本機的操作留痕，不隨 Vault 雲端同步。
/// SourcePath／PasskeyEnabled／RecoveryKeyEnabled 只在 Encrypted 這筆才會有值；
/// UnlockMethod／RestoredPath 只在 Decrypted 這筆才會有值——都是選填欄位，
/// 舊版寫下的紀錄檔沒有這些欄位也能正常讀取（自動視為 null），不會壞掉。
/// </summary>
public record HistoryEntry(
    string Uuid,
    string OriginalName,
    HistoryAction Action,
    DateTimeOffset TimestampUtc,
    string? Detail,
    string? SourcePath = null,
    bool? PasskeyEnabled = null,
    bool? RecoveryKeyEnabled = null,
    string? UnlockMethod = null,
    string? RestoredPath = null);

/// <summary>
/// 用 JSON Lines 格式（每行一筆 JSON）附加寫入的簡單歷史紀錄檔，存在本機（不在 Vault 內）。
/// 選 JSON Lines 而不是單一 JSON 陣列，是因為附加寫入只需要開檔案寫一行、不需要每次都讀出整個
/// 陣列再重新序列化整個檔案，資料量大了之後負擔會小很多。
/// </summary>
public class HistoryLogger
{
    private static readonly object WriteLock = new();
    private readonly string _historyFilePath;

    public HistoryLogger(string historyFilePath)
    {
        _historyFilePath = historyFilePath;
        var dir = Path.GetDirectoryName(historyFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public void Append(HistoryEntry entry)
    {
        var json = JsonSerializer.Serialize(entry);
        lock (WriteLock)
        {
            File.AppendAllText(_historyFilePath, json + Environment.NewLine);
        }
    }

    /// <summary>單行損毀（例如寫到一半程式中斷）會被跳過，不影響讀取其他正常的行。</summary>
    public IReadOnlyList<HistoryEntry> ReadAll()
    {
        if (!File.Exists(_historyFilePath))
        {
            return Array.Empty<HistoryEntry>();
        }

        var results = new List<HistoryEntry>();
        foreach (var line in File.ReadLines(_historyFilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<HistoryEntry>(line);
                if (entry is not null)
                {
                    results.Add(entry);
                }
            }
            catch (JsonException)
            {
                // 略過損毀的單行紀錄，繼續讀取其他行。
            }
        }

        return results;
    }

    /// <summary>清空所有歷史紀錄。呼叫端（VaultProtocolHandlers.ClearHistory）在呼叫這裡之前
    /// 已經完成 Windows Hello 驗證，這裡本身不做任何驗證，純粹是檔案清空動作。</summary>
    public void ClearAll()
    {
        lock (WriteLock)
        {
            if (File.Exists(_historyFilePath))
            {
                File.WriteAllText(_historyFilePath, string.Empty);
            }
        }
    }
}