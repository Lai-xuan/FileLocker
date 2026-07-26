using FileLocker.Core.Models;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應架構審查（2026-07-26）：這個測試類別本身就是「從 LockService 分離 Marker 狀態檢查」
/// 這項深化想證明的事——不需要 HistoryLogger／LockoutTracker／完整的 LockService，
/// 只要一個 VaultManager（純粹用來拿簽章金鑰）跟手動寫一份 LockedMarkerFile，就能測試
/// MarkerStatusChecker 的全部行為。
/// </summary>
public class MarkerStatusCheckerTests : IDisposable
{
    private readonly DirectoryInfo _vaultDir;
    private readonly DirectoryInfo _workDir;
    private readonly byte[] _signingKey;

    public MarkerStatusCheckerTests()
    {
        _vaultDir = Directory.CreateTempSubdirectory("FileLockerVault_");
        _workDir = Directory.CreateTempSubdirectory("FileLockerWork_");

        var vaultConfig = new VaultManager(_vaultDir.FullName).LoadOrCreateConfig();
        _signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);
    }

    public void Dispose()
    {
        if (_vaultDir.Exists) _vaultDir.Delete(recursive: true);
        if (_workDir.Exists) _workDir.Delete(recursive: true);
    }

    private static LockedItemMetadata CreateSampleMetadata(string uuid, string originalPath, ItemType type = ItemType.File) => new()
    {
        Uuid = uuid,
        OriginalName = Path.GetFileName(originalPath.TrimEnd(Path.DirectorySeparatorChar)),
        OriginalPath = originalPath,
        PasswordVerificationHash = "dummyHashBase64==",
        Salt = "dummySaltBase64==",
        Argon2TimeCost = 3,
        Argon2MemoryCostKb = 65536,
        Argon2Parallelism = 2,
        Type = type,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private void WriteMarkerAt(string markerPath, string uuid)
        => LockedMarkerFile.Create(uuid, _signingKey).WriteTo(markerPath);

    [Fact]
    public void CheckMarkerStatus_ForFileStillAtOriginalLocation_ReturnsFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "沒被搬動的檔案.txt");
        var markerPath = MarkerStatusChecker.ComputeMarkerPath(originalPath, isFolder: false);
        WriteMarkerAt(markerPath, uuid);

        var status = MarkerStatusChecker.CheckMarkerStatus(CreateSampleMetadata(uuid, originalPath));

        Assert.True(status.Found);
        Assert.Equal(markerPath, status.MarkerPath);
    }

    [Fact]
    public void CheckMarkerStatus_ForFolderStillAtOriginalLocation_ReturnsFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "沒被搬動的資料夾");
        var markerPath = MarkerStatusChecker.ComputeMarkerPath(originalPath, isFolder: true);
        WriteMarkerAt(markerPath, uuid);

        var status = MarkerStatusChecker.CheckMarkerStatus(CreateSampleMetadata(uuid, originalPath, ItemType.Folder));

        Assert.True(status.Found);
    }

    [Fact]
    public void CheckMarkerStatus_WhenMarkerFileMissing_ReturnsNotFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "從來沒建立過指標檔的檔案.txt");

        var status = MarkerStatusChecker.CheckMarkerStatus(CreateSampleMetadata(uuid, originalPath));

        Assert.False(status.Found);
        Assert.Null(status.MarkerPath);
    }

    [Fact]
    public void CheckMarkerStatus_WhenOriginalPositionReplacedByDifferentUuid_ReturnsNotFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var otherUuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "位置被別的項目取代.txt");
        var markerPath = MarkerStatusChecker.ComputeMarkerPath(originalPath, isFolder: false);

        // 同一個位置的指標檔實際指向另一個 UUID——例如使用者刪掉舊項目後，在原地重新加密了別的東西。
        WriteMarkerAt(markerPath, otherUuid);

        var status = MarkerStatusChecker.CheckMarkerStatus(CreateSampleMetadata(uuid, originalPath));

        Assert.False(status.Found);
    }

    [Fact]
    public void CheckMarkerStatus_ThreeArgOverload_MatchesMetadataOverloadResult()
    {
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "多載一致性測試.txt");
        var markerPath = MarkerStatusChecker.ComputeMarkerPath(originalPath, isFolder: false);
        WriteMarkerAt(markerPath, uuid);

        var viaMetadata = MarkerStatusChecker.CheckMarkerStatus(CreateSampleMetadata(uuid, originalPath));
        var viaFields = MarkerStatusChecker.CheckMarkerStatus(uuid, originalPath, ItemType.File);

        Assert.Equal(viaMetadata.Found, viaFields.Found);
        Assert.Equal(viaMetadata.MarkerPath, viaFields.MarkerPath);
    }

    [Fact]
    public void ComputeMarkerPath_ForFile_UsesNameWithoutExtensionPlusLockedSuffix()
    {
        var originalPath = Path.Combine(_workDir.FullName, "報告.docx");

        var markerPath = MarkerStatusChecker.ComputeMarkerPath(originalPath, isFolder: false);

        Assert.Equal(Path.Combine(_workDir.FullName, "報告.locked"), markerPath);
    }

    [Fact]
    public void ComputeMarkerPath_ForFolder_UsesFolderNamePlusLockedSuffix()
    {
        var originalPath = Path.Combine(_workDir.FullName, "我的資料夾");

        var markerPath = MarkerStatusChecker.ComputeMarkerPath(originalPath, isFolder: true);

        Assert.Equal(Path.Combine(_workDir.FullName, "我的資料夾.locked"), markerPath);
    }
}
