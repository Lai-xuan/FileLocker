namespace FileLocker.Core.Models;

/// <summary>
/// 對應規格文件 3.1 節「型別標記」：區分被加密的是單一檔案還是資料夾（封裝後加密）。
/// </summary>
public enum ItemType
{
    File = 0,
    Folder = 1
}
