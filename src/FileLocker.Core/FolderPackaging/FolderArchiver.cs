using System.IO.Compression;

namespace FileLocker.Core.FolderPackaging;

/// <summary>
/// 對應規格文件 3.2 節「封裝後加密」策略：資料夾 → 暫存 zip → 走既有檔案加密流程。
/// 暫存路徑一律放在 Path.GetTempPath()/FileLocker/ 底下，方便 CleanupOrphanedTempFiles 統一清理。
/// </summary>
public static class FolderArchiver
{
    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "FileLocker");

    /// <summary>將整個資料夾壓縮成暫存 zip，回傳暫存 zip 路徑。呼叫端負責在加密完成後用 SecureFileEraser 清除這個暫存檔。</summary>
    public static string CompressToTempZip(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"找不到資料夾：{folderPath}");
        }

        Directory.CreateDirectory(TempDirectory);
        var tempZipPath = Path.Combine(TempDirectory, $"{Guid.NewGuid()}.zip");

        // includeBaseDirectory: false，讓 zip 內是資料夾「裡面」的內容，不多包一層跟原資料夾同名的目錄，
        // 這樣解壓縮回原始位置時，還原出來的結構才會跟原本一致。
        ZipFile.CreateFromDirectory(folderPath, tempZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        return tempZipPath;
    }

    /// <summary>把暫存 zip 還原成資料夾結構到指定目的地。</summary>
    public static void ExtractZipToFolder(string zipPath, string destinationFolderPath)
    {
        Directory.CreateDirectory(destinationFolderPath);
        ZipFile.ExtractToDirectory(zipPath, destinationFolderPath, overwriteFiles: false);
    }

    /// <summary>
    /// 對應規格文件 3.2 節「巢狀 .locked 項目」：加密前先遞迴掃描資料夾，
    /// 找出裡面所有 *.locked 檔案的路徑，回傳給呼叫端決定要不要跳出提示、要記錄哪些 UUID。
    /// </summary>
    public static IReadOnlyList<string> FindNestedLockedFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(folderPath, "*.locked", SearchOption.AllDirectories).ToList();
    }

    /// <summary>
    /// App 啟動時呼叫：清掉 TempDirectory 底下任何殘留的暫存 zip
    /// （對應規格文件 3.2 節「例外處理」：加密流程中斷時避免明文暫存檔遺留在磁碟）。
    /// 單一檔案刪除失敗（例如還被鎖定中）不中斷整個清理流程，留給下次啟動再試一次。
    /// </summary>
    public static void CleanupOrphanedTempFiles()
    {
        if (!Directory.Exists(TempDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(TempDirectory))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 檔案可能還被其他行程鎖定中，略過，下次啟動再嘗試清理。
            }
        }
    }
}