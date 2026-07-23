using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using FileLocker.Core.FolderPackaging;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.SecureDelete;
using FileLocker.Core.Vault;

namespace FileLocker.Core;

/// <summary>
/// 對外的主要 API 入口——GUI、CLI 原型都只需要呼叫這一層，不需要知道底下 Crypto/Vault/FolderPackaging 的細節。
/// 對應規格文件 3.3（加密流程）、3.4（解密流程）、3.2 第 3 點（刪除防呆）。
/// </summary>
public class LockService
{
    private readonly VaultManager _vault;
    private readonly HistoryLogger? _history;

    /// <summary>
    /// historyLogger 是選填的：CLI 原型或單元測試不一定需要歷史紀錄，傳 null 就單純不記錄，
    /// 不影響加密/解密本身的行為。
    /// </summary>
    public LockService(VaultManager vault, HistoryLogger? historyLogger = null)
    {
        _vault = vault;
        _history = historyLogger;
    }

    public Task<LockResult> EncryptAsync(string path, string password, string? hint, IProgress<double>? progress = null)
        => Task.Run(() => EncryptCore(path, password, hint));

    private LockResult EncryptCore(string path, string password, string? hint)
    {
        var isFolder = Directory.Exists(path);
        var isFile = File.Exists(path);

        if (!isFolder && !isFile)
        {
            return new LockResult(false, "", "", $"找不到檔案或資料夾：{path}");
        }

        var type = isFolder ? ItemType.Folder : ItemType.File;

        var originalName = isFolder
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(path);

        // 先做這個便宜的檢查，才去做壓縮資料夾這種可能很花時間的工作——
        // 目標位置已經有指標檔的話，應該儘早失敗，不要白白先把整個資料夾壓縮完才發現要失敗。
        var markerPath = ComputeMarkerPath(path, isFolder);
        if (File.Exists(markerPath))
        {
            return new LockResult(false, "", "", $"目標位置已經有一個指標檔了：{markerPath}");
        }

        var nestedUuids = new List<string>();
        string contentPath;
        string? tempZipToCleanup = null;

        try
        {
            if (isFolder)
            {
                foreach (var nestedMarkerPath in FolderArchiver.FindNestedLockedFiles(path))
                {
                    var nestedMarker = LockedMarkerFile.ReadFrom(nestedMarkerPath);
                    if (nestedMarker is not null)
                    {
                        nestedUuids.Add(nestedMarker.Uuid);
                    }
                }

                contentPath = FolderArchiver.CompressToTempZip(path);
                tempZipToCleanup = contentPath;
            }
            else
            {
                contentPath = path;
            }

            var originalSizeBytes = new FileInfo(contentPath).Length;

            var salt = Argon2KeyDerivation.GenerateSalt();
            var derived = Argon2KeyDerivation.DeriveKeys(password, salt);
            var uuid = Guid.NewGuid().ToString();

            // 串流處理：一次只把一個 chunk（預設 1MB）的明文留在記憶體，不管檔案多大，
            // 記憶體用量都不會跟著檔案大小線性增加（見 ChunkedCipher 的分塊加密設計）。
            try
            {
                using var plaintextStream = File.OpenRead(contentPath);
                using var encStream = _vault.OpenEncryptedContentWrite(uuid);
                ChunkedCipher.EncryptStream(derived.EncryptionKey, plaintextStream, encStream);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derived.EncryptionKey);
            }

            var vaultConfig = _vault.LoadOrCreateConfig();

            var metadata = new LockedItemMetadata
            {
                Uuid = uuid,
                OriginalName = originalName,
                OriginalPath = path,
                PasswordVerificationHash = Convert.ToBase64String(derived.VerificationHash),
                Salt = Convert.ToBase64String(salt),
                Argon2TimeCost = KeyDerivationDefaults.TimeCost,
                Argon2MemoryCostKb = KeyDerivationDefaults.MemoryCostKb,
                Argon2Parallelism = KeyDerivationDefaults.Parallelism,
                Hint = hint,
                Type = type,
                OriginalSizeBytes = originalSizeBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ContainsNestedLocks = nestedUuids
            };
            _vault.SaveMetadata(metadata);

            var signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);
            var marker = LockedMarkerFile.Create(uuid, signingKey);
            marker.WriteTo(markerPath);

            // 到這裡，加密內容、metadata、marker 都已經成功寫入——資料本身已經安全了。
            // 清除原始明文是「收尾」動作，這一步就算失敗，也不代表加密本身失敗，
            // 所以特別包一層自己的 try/catch，不讓它跟著外層的 catch 把整個結果判定成失敗
            // （否則使用者會看到「加密失敗」，卻不知道其實 Vault 裡已經有一份有效的加密紀錄了）。
            string? cleanupWarning = null;
            try
            {
                if (isFolder)
                {
                    SecureFileEraser.OverwriteAndDeleteFolder(path);
                }
                else
                {
                    SecureFileEraser.OverwriteAndDelete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = $"加密已完成，但清除原始檔案時發生錯誤，請手動確認並刪除原始檔案：{ex.Message}";
            }

            _history?.Append(new HistoryEntry(uuid, originalName, HistoryAction.Encrypted, DateTimeOffset.UtcNow, hint));

            return new LockResult(true, uuid, markerPath, cleanupWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LockResult(false, "", "", $"加密過程發生錯誤：{ex.Message}");
        }
        finally
        {
            if (tempZipToCleanup is not null)
            {
                SecureFileEraser.OverwriteAndDelete(tempZipToCleanup);
            }
        }
    }

    /// <summary>對應原本雙擊 .locked 檔案的解密流程：先讀 marker 驗證簽章，再往下走。</summary>
    public Task<UnlockResult> DecryptAsync(string lockedMarkerPath, string password)
        => Task.Run(() => DecryptViaMarkerCore(lockedMarkerPath, password));

    private UnlockResult DecryptViaMarkerCore(string lockedMarkerPath, string password)
    {
        var marker = LockedMarkerFile.ReadFrom(lockedMarkerPath);
        if (marker is null)
        {
            return new UnlockResult(false, "", "找不到或無法解析這個 .locked 檔案");
        }

        var vaultConfig = _vault.LoadOrCreateConfig();
        var signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);

        if (!marker.VerifySignature(signingKey))
        {
            return new UnlockResult(false, "", "指標檔驗證失敗，內容可能已被竄改");
        }

        var metadata = _vault.LoadMetadata(marker.Uuid);
        if (metadata is null)
        {
            return new UnlockResult(false, "", "在集中管理區找不到對應的加密內容");
        }

        var parentDir = Path.GetDirectoryName(Path.GetFullPath(lockedMarkerPath));
        if (parentDir is null)
        {
            return new UnlockResult(false, "", "無法判斷指標檔所在的資料夾");
        }

        var result = DecryptAndRestore(metadata, password, parentDir);

        if (result.Success)
        {
            // 這個路徑本來就是從 marker 檔案本身進來的，解密成功後直接刪除它就好，不用再檢查存不存在。
            File.Delete(lockedMarkerPath);
        }

        return result;
    }

    /// <summary>
    /// 對應「已加密清單」頁直接選項目解密：不需要事先找到 .locked 檔案，直接用 UUID 從 Vault 解密。
    /// destinationDir 為 null 時（使用者選擇「還原到原始位置」），退而求其次用加密當下記錄的原始路徑
    /// 所在的資料夾；使用者若指定了 destinationDir（自己選了另一個地方存），就用那個位置。
    /// 不論還原到哪裡，解密成功後都會反推出原本 .locked 應該在的位置，檢查那裡現在還有沒有東西——
    /// 有（而且真的是同一個 UUID）就清掉，避免留下一個已經失效、會誤導使用者的指標檔；沒有就跳過，不當成錯誤。
    /// 這個檢查永遠是根據「原始位置」判斷，跟這次實際存去哪裡無關，因為 .locked 指標檔本來就只可能出現在
    /// 原始位置，不會出現在使用者這次選的新位置。
    /// </summary>
    public Task<UnlockResult> DecryptByUuidAsync(string uuid, string password, string? destinationDir = null)
        => Task.Run(() => DecryptByUuidCore(uuid, password, destinationDir));

    private UnlockResult DecryptByUuidCore(string uuid, string password, string? destinationDir)
    {
        var metadata = _vault.LoadMetadata(uuid);
        if (metadata is null)
        {
            return new UnlockResult(false, "", "找不到對應的加密紀錄");
        }

        string destinationParentDir;
        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            destinationParentDir = destinationDir;
        }
        else
        {
            var originalParentDir = Path.GetDirectoryName(Path.GetFullPath(metadata.OriginalPath));
            if (originalParentDir is null)
            {
                return new UnlockResult(false, "", "無法判斷原始路徑所在的資料夾");
            }
            destinationParentDir = originalParentDir;
        }

        Directory.CreateDirectory(destinationParentDir);

        var result = DecryptAndRestore(metadata, password, destinationParentDir);

        if (result.Success)
        {
            var expectedMarkerPath = ComputeMarkerPath(metadata.OriginalPath, metadata.Type == ItemType.Folder);
            if (File.Exists(expectedMarkerPath))
            {
                var marker = LockedMarkerFile.ReadFrom(expectedMarkerPath);
                if (marker is not null && marker.Uuid == uuid)
                {
                    File.Delete(expectedMarkerPath);
                }
                // 若那個位置的 .locked 屬於別的項目（例如同位置後來又加密了別的東西），不動它。
            }
        }

        return result;
    }

    /// <summary>
    /// DecryptViaMarkerCore 跟 DecryptByUuidCore 共用的核心解密邏輯：驗證密碼、解密內容、寫回目的地、
    /// 清除 Vault 內的項目、記錄歷史紀錄。兩者的差異只在「怎麼拿到 metadata」跟「目的地資料夾怎麼決定」，
    /// 所以拆出來共用，避免同一段解密流程要維護兩份。
    /// </summary>
    private UnlockResult DecryptAndRestore(LockedItemMetadata metadata, string password, string destinationParentDir)
    {
        var salt = Convert.FromBase64String(metadata.Salt);
        var storedHash = Convert.FromBase64String(metadata.PasswordVerificationHash);

        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(
            password, salt, storedHash,
            metadata.Argon2TimeCost, metadata.Argon2MemoryCostKb, metadata.Argon2Parallelism);

        if (!isValid || encryptionKey is null)
        {
            return new UnlockResult(false, "", "密碼錯誤");
        }

        var destinationPath = Path.Combine(destinationParentDir, metadata.OriginalName);

        if (metadata.Type == ItemType.Folder)
        {
            if (Directory.Exists(destinationPath))
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
                return new UnlockResult(false, "", $"還原失敗，目的地已經有同名資料夾：{destinationPath}");
            }
            Directory.CreateDirectory(FolderArchiver.TempDirectory);
        }
        else if (File.Exists(destinationPath))
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            return new UnlockResult(false, "", $"還原失敗，目的地已經有同名檔案：{destinationPath}");
        }

        // 資料夾的話先解密寫進一個暫存 zip，再解壓縮還原成資料夾結構；檔案的話直接解密寫到目的地。
        var actualWritePath = metadata.Type == ItemType.Folder
            ? Path.Combine(FolderArchiver.TempDirectory, $"{Guid.NewGuid()}.zip")
            : destinationPath;

        try
        {
            try
            {
                // 串流解密：一次只處理一個 chunk，全程不會有「整份明文」同時存在記憶體裡。
                using (var encStream = _vault.OpenEncryptedContentRead(metadata.Uuid))
                using (var outputStream = File.Create(actualWritePath))
                {
                    ChunkedCipher.DecryptStream(encryptionKey, encStream, outputStream);
                }
            }
            catch
            {
                // 解密中途失敗（密碼錯誤在這裡不會發生，因為上面已經先驗證過；這裡會是內容損毀/被竄改），
                // 不留下一個寫到一半、內容不完整的檔案在磁碟上誤導使用者。
                if (File.Exists(actualWritePath))
                {
                    try { File.Delete(actualWritePath); } catch (IOException) { /* 盡力而為，清不掉就算了 */ }
                }
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }

            if (metadata.Type == ItemType.Folder)
            {
                try
                {
                    FolderArchiver.ExtractZipToFolder(actualWritePath, destinationPath);
                }
                finally
                {
                    SecureFileEraser.OverwriteAndDelete(actualWritePath);
                }
            }

            _vault.DeleteItem(metadata.Uuid);
            _history?.Append(new HistoryEntry(metadata.Uuid, metadata.OriginalName, HistoryAction.Decrypted, DateTimeOffset.UtcNow, null));

            return new UnlockResult(true, destinationPath);
        }
        catch (CryptographicException)
        {
            return new UnlockResult(false, "", "解密失敗，加密內容可能已損毀");
        }
        catch (InvalidDataException ex)
        {
            return new UnlockResult(false, "", $"解密失敗，加密內容已損毀：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UnlockResult(false, "", $"解密過程發生錯誤：{ex.Message}");
        }
    }

    public Task<DeleteRecordResult> TryDeleteRecordAsync(string uuid, bool force = false)
        => Task.Run(() =>
        {
            var metadata = _vault.LoadMetadata(uuid);
            if (metadata is null)
            {
                return new DeleteRecordResult(false, false, null, "找不到對應的加密紀錄");
            }

            if (metadata.ContainsNestedLocks.Count > 0 && !force)
            {
                return new DeleteRecordResult(false, true, metadata.ContainsNestedLocks);
            }

            _vault.DeleteItem(uuid);
            _history?.Append(new HistoryEntry(uuid, metadata.OriginalName, HistoryAction.RecordDeleted, DateTimeOffset.UtcNow, null));

            return new DeleteRecordResult(true, false);
        });

    public MarkerStatus CheckMarkerStatus(LockedItemMetadata metadata)
    {
        var expectedPath = ComputeMarkerPath(metadata.OriginalPath, metadata.Type == ItemType.Folder);

        if (!File.Exists(expectedPath))
        {
            return new MarkerStatus(false, null, "在原本的位置找不到指標檔，可能已被移動或刪除");
        }

        var marker = LockedMarkerFile.ReadFrom(expectedPath);
        if (marker is null)
        {
            return new MarkerStatus(false, null, "原本位置的檔案無法解析為指標檔");
        }

        if (marker.Uuid != metadata.Uuid)
        {
            return new MarkerStatus(false, null, "原本的位置已經被別的加密項目取代");
        }

        return new MarkerStatus(true, expectedPath, null);
    }

    public static string ComputeMarkerPath(string originalPath, bool isFolder)
    {
        var trimmedPath = originalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDir = isFolder
            ? Directory.GetParent(trimmedPath)?.FullName ?? throw new IOException($"無法判斷父資料夾：{originalPath}")
            : Path.GetDirectoryName(Path.GetFullPath(trimmedPath)) ?? throw new IOException($"無法判斷父資料夾：{originalPath}");

        var baseName = isFolder
            ? Path.GetFileName(trimmedPath)
            : Path.GetFileNameWithoutExtension(trimmedPath);

        return Path.Combine(parentDir, $"{baseName}.locked");
    }
}