using System.Security.Cryptography;

namespace FileLocker.Core.SecureDelete;

/// <summary>
/// 對應規格文件第 6 節與第 8 節：加密完成後刪除原始明文與暫存 zip 前，先覆寫隨機資料再刪除。
/// 需在文件/UI 中提醒使用者：SSD 上因為 wear-leveling 機制，覆寫不保證能物理清除所有底層資料，
/// 這裡做的是合理範圍內的最佳努力（best-effort），不是絕對保證。
/// </summary>
public static class SecureFileEraser
{
    private const int BufferSizeBytes = 1024 * 1024; // 1 MB，避免大檔案一次配置過大的緩衝區

    /// <summary>檔案不存在時直接視為已完成（冪等），不拋例外，方便呼叫端安全地重複呼叫。</summary>
    public static void OverwriteAndDelete(string filePath, int passes = 1)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var length = new FileInfo(filePath).Length;

        if (length > 0)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.None);
            var buffer = new byte[Math.Min(length, BufferSizeBytes)];

            for (var pass = 0; pass < passes; pass++)
            {
                stream.Position = 0;
                var remaining = length;

                while (remaining > 0)
                {
                    var chunkSize = (int)Math.Min(buffer.Length, remaining);
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, chunkSize));
                    stream.Write(buffer, 0, chunkSize);
                    remaining -= chunkSize;
                }

                stream.Flush();
            }
        }

        File.Delete(filePath);
    }

    /// <summary>對資料夾內每個檔案個別覆寫刪除，最後才刪除資料夾本身。資料夾不存在時同樣視為已完成。</summary>
    public static void OverwriteAndDeleteFolder(string folderPath, int passes = 1)
    {
        if (!Directory.Exists(folderPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            OverwriteAndDelete(filePath, passes);
        }

        Directory.Delete(folderPath, recursive: true);
    }
}