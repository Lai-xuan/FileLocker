using System.Text.Json;
using FileLocker.Core.Crypto;
using FileLocker.Core.Io;

namespace FileLocker.Core;

/// <summary>
/// 對應規格文件 3.3 節步驟 9：原位置產生的 {原名稱}.locked 檔案內容。
/// 內容只存 UUID + 簽章，不含路徑，這樣就算檔案被移動、或被包進另一個資料夾再加密（巢狀鎖定，見 3.2 節），
/// 依然能正確解析。
/// </summary>
public class LockedMarkerFile
{
    public required string Uuid { get; set; }
    public required string SignatureBase64 { get; set; }

    public static LockedMarkerFile Create(string uuid, byte[] vaultSigningKey)
        => new() { Uuid = uuid, SignatureBase64 = MarkerSigner.Sign(uuid, vaultSigningKey) };

    public bool VerifySignature(byte[] vaultSigningKey)
        => MarkerSigner.Verify(Uuid, SignatureBase64, vaultSigningKey);

    public void WriteTo(string path)
    {
        var json = JsonSerializer.Serialize(this);
        AtomicFile.WriteAllText(path, json);
    }

    /// <summary>
    /// 找不到檔案、或內容不是合法的 JSON／缺少必要欄位，一律回傳 null，不把例外往外拋——
    /// 呼叫端（雙擊 .locked 檔案的流程）只需要處理「讀得到內容」跟「讀不到/壞掉」兩種情況，
    /// 並在讀不到時顯示對應的錯誤提示，而不是讓整個流程因未處理例外而崩潰。
    /// </summary>
    public static LockedMarkerFile? ReadFrom(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var marker = JsonSerializer.Deserialize<LockedMarkerFile>(json);

            // Uuid 之後會被直接拿去組 Vault 內 {uuid}.enc / {uuid}.meta.json 的檔案路徑
            // （見 VaultManager.EncPath/MetaPath），指標檔內容本身又是未經信任的輸入
            // （只有簽章能保證沒被竄改，但驗證簽章是呼叫端的責任，不是這裡）——防禦深度起見，
            // 格式一律先驗證是合法 GUID，不是就當成解析失敗處理，避免非 GUID 字串
            // （例如含路徑分隔符/「..」）被當成路徑的一部分。
            if (marker is not null && !Guid.TryParse(marker.Uuid, out _))
            {
                return null;
            }

            return marker;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}