namespace FileLocker.Core.FolderPackaging;

/// <summary>
/// 對應規格文件 3.2 節「封裝後加密」策略：資料夾 → 暫存 zip → 走既有檔案加密流程。
/// 暫存路徑一律放在 Path.GetTempPath()/FileLocker/ 底下，方便第 3.2 節提到的「啟動時掃描殘留暫存檔」清理邏輯統一處理。
/// </summary>
public static class FolderArchiver
{
    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "FileLocker");

    /// <summary>
    /// 將整個資料夾壓縮成暫存 zip，回傳暫存 zip 路徑。
    /// TODO: 用 System.IO.Compression.ZipFile.CreateFromDirectory 實作，
    /// 記得先 Directory.CreateDirectory(TempDirectory)。
    /// </summary>
    public static string CompressToTempZip(string folderPath)
    {
        throw new NotImplementedException();
    }

    /// <summary>TODO: 用 ZipFile.ExtractToDirectory 把暫存 zip 還原成資料夾結構。</summary>
    public static void ExtractZipToFolder(string zipPath, string destinationFolderPath)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// 對應規格文件 3.2 節「巢狀 .locked 項目」：加密前先遞迴掃描資料夾，
    /// 找出裡面所有 *.locked 檔案的路徑，回傳給呼叫端決定要不要跳出提示、要記錄哪些 UUID。
    /// TODO: Directory.EnumerateFiles(folderPath, "*.locked", SearchOption.AllDirectories)
    /// </summary>
    public static IReadOnlyList<string> FindNestedLockedFiles(string folderPath)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// App 啟動時呼叫：清掉 TempDirectory 底下任何殘留的暫存 zip
    /// （對應規格文件 3.2 節「例外處理」：加密流程中斷時避免明文暫存檔遺留在磁碟）。
    /// </summary>
    public static void CleanupOrphanedTempFiles()
    {
        throw new NotImplementedException();
    }
}
