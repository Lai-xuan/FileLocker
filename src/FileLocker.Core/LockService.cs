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
    // 密文檔案內部佈局：[Nonce(12 bytes)][Tag(16 bytes)][Ciphertext(其餘)]，
    // 跟規格文件 3.1 節描述的邏輯欄位一致，只是這裡把 Header/Salt/Argon2 參數改放在 .meta.json（第 4 節的調整）。
    private const int HeaderLength = AesGcmCipher.NonceSizeBytes + AesGcmCipher.TagSizeBytes;

    private readonly VaultManager _vault;

    public LockService(VaultManager vault)
    {
        _vault = vault;
    }

    /// <summary>
    /// 對應 3.3 節完整流程。大型檔案/資料夾的壓縮與加解密都是同步 CPU/IO 密集工作，
    /// 包在 Task.Run 裡讓呼叫端（GUI）可以用 await 而不會卡住介面執行緒。
    /// </summary>
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
                // 對應規格文件 3.2 節「巢狀 .locked 項目」：加密前先掃描，記下裡面本來就存在的鎖定項目。
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

            var markerPath = GetMarkerPath(path, isFolder);
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

            // 加密內容已經安全寫入 Vault，這裡才清除原始明文——順序很重要，避免加密途中出錯卻已經刪了原始資料。
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

    /// <summary>
    /// 對應 3.4 節完整流程：讀 marker → 驗證簽章 → Argon2 重新衍生 → 比對驗證雜湊 → AES-GCM 解密驗證 Tag →
    /// 視型別決定直接寫回還是先 ExtractZipToFolder → 還原原始名稱 → 刪除 Vault 內對應項目與 marker。
    /// 密碼錯誤或簽章驗證失敗都回傳 Success=false + 對應 ErrorMessage，不拋例外給呼叫端接。
    /// </summary>
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

    /// <summary>
    /// 對應規格文件 3.2 節「刪除紀錄時，改成預設直接擋下來」：
    /// 若 ContainsNestedLocks 不是空的且 force=false，回傳 BlockedByNestedLocks=true，
    /// 讓 UI 顯示白話提示，不提供任何情況下的「一鍵強制刪除」入口——
    /// force 參數只保留給未來如果真的需要例外處理時用，預設呼叫永遠是 force=false。
    /// </summary>
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

    private static string GetMarkerPath(string originalPath, bool isFolder)
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