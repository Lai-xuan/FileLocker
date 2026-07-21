namespace FileLocker.Core.SecureDelete;

/// <summary>
/// 對應規格文件第 6 節與第 8 節：加密完成後刪除原始明文與暫存 zip 前，先覆寫隨機資料再刪除。
/// 需在文件/UI 中提醒使用者：SSD 上因為 wear-leveling 機制，覆寫不保證能物理清除所有底層資料，
/// 這裡做的是合理範圍內的最佳努力（best-effort），不是絕對保證。
/// </summary>
public static class SecureFileEraser
{
    /// <summary>
    /// TODO: 開啟檔案、寫入與原檔案等長的隨機位元組（RandomNumberGenerator.Fill）覆寫 passes 次，
    /// 每次覆寫後 Flush，最後才呼叫 File.Delete。
    /// </summary>
    public static void OverwriteAndDelete(string filePath, int passes = 1)
    {
        throw new NotImplementedException();
    }

    /// <summary>對資料夾內每個檔案個別呼叫 OverwriteAndDelete，最後才刪除資料夾本身。</summary>
    public static void OverwriteAndDeleteFolder(string folderPath, int passes = 1)
    {
        throw new NotImplementedException();
    }
}
