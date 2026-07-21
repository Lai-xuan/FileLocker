using FileLocker.Core.Crypto;

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

    /// <summary>TODO: 序列化成 JSON 寫入 path（System.Text.Json）。</summary>
    public void WriteTo(string path) => throw new NotImplementedException();

    /// <summary>TODO: 從 path 讀取並反序列化；找不到檔案或格式錯誤回傳 null 由呼叫端處理錯誤訊息。</summary>
    public static LockedMarkerFile? ReadFrom(string path) => throw new NotImplementedException();
}
