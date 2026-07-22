using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using FileLocker.Core.FolderPackaging;
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
    private const int HeaderLength = AesGcmCipher.NonceSizeBytes + AesGcmCipher.TagSizeBytes;

    private readonly VaultManager _vault;

    public LockService(VaultManager vault)
    {
        _vault = vault;
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

            var originalName = isFolder
                ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : Path.GetFileName(path);

            var markerPath = ComputeMarkerPath(path, isFolder);
            if (File.Exists(markerPath))
            {
                return new LockResult(false, "", "", $"目標位置已經有一個指標檔了：{markerPath}");
            }

            var salt = Argon2KeyDerivation.GenerateSalt();
            var derived = Argon2KeyDerivation.DeriveKeys(password, salt);
            byte[] plaintext = File.ReadAllBytes(contentPath);
            var originalSizeBytes = plaintext.LongLength;

            byte[] nonce, ciphertext, tag;
            try
            {
                (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(derived.EncryptionKey, plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derived.EncryptionKey);
                Array.Clear(plaintext, 0, plaintext.Length);
            }

            var uuid = Guid.NewGuid().ToString();

            using (var encStream = _vault.OpenEncryptedContentWrite(uuid))
            {
                encStream.Write(nonce, 0, nonce.Length);
                encStream.Write(tag, 0, tag.Length);
                encStream.Write(ciphertext, 0, ciphertext.Length);
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

            if (isFolder)
            {
                SecureFileEraser.OverwriteAndDeleteFolder(path);
            }
            else
            {
                SecureFileEraser.OverwriteAndDelete(path);
            }

            return new LockResult(true, uuid, markerPath);
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

    public Task<UnlockResult> DecryptAsync(string lockedMarkerPath, string password)
        => Task.Run(() => DecryptCore(lockedMarkerPath, password));

    private UnlockResult DecryptCore(string lockedMarkerPath, string password)
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

        var salt = Convert.FromBase64String(metadata.Salt);
        var storedHash = Convert.FromBase64String(metadata.PasswordVerificationHash);

        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(
            password, salt, storedHash,
            metadata.Argon2TimeCost, metadata.Argon2MemoryCostKb, metadata.Argon2Parallelism);

        if (!isValid || encryptionKey is null)
        {
            return new UnlockResult(false, "", "密碼錯誤");
        }

        try
        {
            byte[] rawContent;
            using (var encStream = _vault.OpenEncryptedContentRead(marker.Uuid))
            using (var memoryStream = new MemoryStream())
            {
                encStream.CopyTo(memoryStream);
                rawContent = memoryStream.ToArray();
            }

            if (rawContent.Length < HeaderLength)
            {
                return new UnlockResult(false, "", "加密內容已損毀（檔案長度不足）");
            }

            var nonce = rawContent[..AesGcmCipher.NonceSizeBytes];
            var tag = rawContent[AesGcmCipher.NonceSizeBytes..HeaderLength];
            var ciphertext = rawContent[HeaderLength..];

            byte[] plaintext;
            try
            {
                plaintext = AesGcmCipher.Decrypt(encryptionKey, nonce, ciphertext, tag);
            }
            catch (CryptographicException)
            {
                return new UnlockResult(false, "", "解密失敗，加密內容可能已損毀");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }

            var parentDir = Path.GetDirectoryName(Path.GetFullPath(lockedMarkerPath))
                ?? throw new IOException("無法判斷指標檔所在的資料夾");
            var destinationPath = Path.Combine(parentDir, metadata.OriginalName);

            if (metadata.Type == ItemType.Folder)
            {
                if (Directory.Exists(destinationPath))
                {
                    return new UnlockResult(false, "", $"還原失敗，目的地已經有同名資料夾：{destinationPath}");
                }

                Directory.CreateDirectory(FolderArchiver.TempDirectory);
                var tempZipPath = Path.Combine(FolderArchiver.TempDirectory, $"{Guid.NewGuid()}.zip");
                try
                {
                    File.WriteAllBytes(tempZipPath, plaintext);
                    FolderArchiver.ExtractZipToFolder(tempZipPath, destinationPath);
                }
                finally
                {
                    SecureFileEraser.OverwriteAndDelete(tempZipPath);
                }
            }
            else
            {
                if (File.Exists(destinationPath))
                {
                    return new UnlockResult(false, "", $"還原失敗，目的地已經有同名檔案：{destinationPath}");
                }

                File.WriteAllBytes(destinationPath, plaintext);
            }

            Array.Clear(plaintext, 0, plaintext.Length);

            _vault.DeleteItem(marker.Uuid);
            File.Delete(lockedMarkerPath);

            return new UnlockResult(true, destinationPath);
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
            return new DeleteRecordResult(true, false);
        });

    /// <summary>
    /// 對應清單頁「盡力而為」的指標檔狀態檢查：只檢查 metadata.OriginalPath 反推出來的預期位置，
    /// 檢查那裡現在是否還有一個屬於這個 UUID 的 .locked 檔案。使用者若把 .locked 搬到別的地方，
    /// 這裡就檢查不到、會回報「找不到」——這不代表資料真的遺失（Vault 裡的 .enc 還在），
    /// 只是無法確認目前 .locked 指標檔實際在哪裡。
    /// </summary>
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
            // 那個位置現在的 .locked 檔案是別的項目的（例如使用者在同樣位置又加密了別的東西）。
            return new MarkerStatus(false, null, "原本的位置已經被別的加密項目取代");
        }

        return new MarkerStatus(true, expectedPath, null);
    }

    /// <summary>
    /// 從原始路徑推算對應的 .locked 指標檔應該在哪裡：同一個父資料夾，副檔名整個換成 .locked
    /// （檔案是把原副檔名拿掉，資料夾是直接接在資料夾名稱後面）。EncryptCore 跟 CheckMarkerStatus 共用。
    /// </summary>
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