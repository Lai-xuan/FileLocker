using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using FileLocker.Core.FolderPackaging;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.SecureDelete;
using FileLocker.Core.Security;
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
    private readonly LockoutTracker? _lockout;

    /// <summary>
    /// historyLogger／lockoutTracker 都是選填的：CLI 原型或單元測試不一定需要，傳 null 就單純不記錄／不鎖定，
    /// 不影響加密/解密本身的行為。
    /// </summary>
    public LockService(VaultManager vault, HistoryLogger? historyLogger = null, LockoutTracker? lockoutTracker = null)
    {
        _vault = vault;
        _history = historyLogger;
        _lockout = lockoutTracker;
    }

    /// <summary>
    /// 注意：這裡刻意不整個包進 Task.Run——實測發現 Passkey 相關的 WinRT 呼叫如果整個在背景執行緒
    /// 上執行，第二次（簽章）的 Windows Hello 驗證視窗會抓不到正確的視窗焦點/啟用狀態（懷疑跟 WinRT
    /// 的執行緒環境有關）。只有純檔案 I/O／加密運算的部分（EncryptToVault）丟進背景執行緒，
    /// Passkey 相關呼叫留在呼叫端原本的執行緒（通常是 UI 執行緒）上直接 await。
    /// </summary>
    public async Task<LockResult> EncryptAsync(
        string path, string password, string? hint,
        bool enablePasskey = false, IntPtr ownerWindowHandle = default,
        bool enableRecoveryKey = false, string? batchId = null,
        IProgress<double>? progress = null)
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

        EncryptionResult encryptResult;
        try
        {
            encryptResult = await Task.Run(() => EncryptToVault(path, isFolder, password));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LockResult(false, "", "", $"加密過程發生錯誤：{ex.Message}");
        }
        catch (Exception ex)
        {
            // EncryptToVault 內部已經自己接住所有例外，理論上這裡不會再丟出來——
            // 保留這層純粹是防禦性寫法，避免未來改動時漏接某個例外型別導致整個 App 崩潰。
            return new LockResult(false, "", "", $"加密過程發生未預期的錯誤：{ex.Message}");
        }

        try
        {
            if (!encryptResult.Success)
            {
                return new LockResult(false, "", "", encryptResult.ErrorMessage);
            }

            string? passkeyCredentialName = null;
            string? passkeyChallengeBase64 = null;
            string? passkeyWrappedKeyBase64 = null;

            // 對應規格文件 8.1 節：Passkey 是「額外」的一道門，這裡失敗（不支援裝置、使用者取消、
            // 驗證失敗）都不影響密碼加密本身的成功與否，只是這個項目最終沒有啟用 Passkey 快速解鎖。
            if (enablePasskey && await PasskeyProtector.IsSupportedAsync())
            {
                var credentialName = PasskeyProtector.GenerateCredentialName();
                if (await PasskeyProtector.CreateCredentialAsync(credentialName, ownerWindowHandle))
                {
                    var challenge = PasskeyProtector.GenerateChallenge();
                    var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);

                    if (signature is not null)
                    {
                        var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
                        try
                        {
                            passkeyWrappedKeyBase64 = PasskeyProtector.WrapContentKey(wrappingKey, encryptResult.EncryptionKey!);
                            passkeyCredentialName = credentialName;
                            passkeyChallengeBase64 = Convert.ToBase64String(challenge);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(wrappingKey);
                            CryptographicOperations.ZeroMemory(signature);
                        }
                    }
                    else
                    {
                        // 使用者取消或驗證失敗：清掉剛剛建立的裝置金鑰，不留下一把沒被用到的憑證。
                        await PasskeyProtector.DeleteCredentialAsync(credentialName);
                    }
                }
            }

            string? recoveryKeyWrappedBase64 = null;
            string? recoveryKeyDisplayText = null;

            // 恢復金鑰是純本機運算（產生隨機值、HKDF、AES-GCM），不牽涉任何 Windows API，
            // 不需要像 Passkey 那樣顧慮執行緒環境，直接同步做完即可。
            if (enableRecoveryKey)
            {
                var recoveryKeyBytes = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
                try
                {
                    recoveryKeyDisplayText = RecoveryKeyProtector.FormatForDisplay(recoveryKeyBytes);
                    var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
                    try
                    {
                        recoveryKeyWrappedBase64 = RecoveryKeyProtector.WrapContentKey(wrappingKey, encryptResult.EncryptionKey!);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(wrappingKey);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(recoveryKeyBytes);
                }
            }

            var vaultConfig = _vault.LoadOrCreateConfig();

            var metadata = new LockedItemMetadata
            {
                Uuid = encryptResult.Uuid!,
                OriginalName = originalName,
                OriginalPath = path,
                PasswordVerificationHash = encryptResult.PasswordVerificationHashBase64!,
                Salt = encryptResult.SaltBase64!,
                Argon2TimeCost = KeyDerivationDefaults.TimeCost,
                Argon2MemoryCostKb = KeyDerivationDefaults.MemoryCostKb,
                Argon2Parallelism = KeyDerivationDefaults.Parallelism,
                Hint = hint,
                Type = type,
                OriginalSizeBytes = encryptResult.OriginalSizeBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ContainsNestedLocks = encryptResult.NestedUuids!,
                PasskeyEnabled = passkeyWrappedKeyBase64 is not null,
                PasskeyCredentialName = passkeyCredentialName,
                PasskeyChallenge = passkeyChallengeBase64,
                PasskeyWrappedContentKey = passkeyWrappedKeyBase64,
                RecoveryKeyEnabled = recoveryKeyWrappedBase64 is not null,
                RecoveryKeyWrappedContentKey = recoveryKeyWrappedBase64,
                BatchId = batchId
            };
            _vault.SaveMetadata(metadata);

            var signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);
            var marker = LockedMarkerFile.Create(encryptResult.Uuid!, signingKey);
            marker.WriteTo(markerPath);

            // 到這裡，加密內容、metadata、marker 都已經成功寫入——資料本身已經安全了。
            // 清除原始明文是「收尾」動作，這一步就算失敗，也不代表加密本身失敗，
            // 所以特別包一層自己的 try/catch，不讓它跟著外層的 catch 把整個結果判定成失敗
            // （否則使用者會看到「加密失敗」，卻不知道其實 Vault 裡已經有一份有效的加密紀錄了）。
            string? cleanupWarning = null;
            try
            {
                await Task.Run(() =>
                {
                    if (isFolder)
                    {
                        SecureFileEraser.OverwriteAndDeleteFolder(path);
                    }
                    else
                    {
                        SecureFileEraser.OverwriteAndDelete(path);
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = $"加密已完成，但清除原始檔案時發生錯誤，請手動確認並刪除原始檔案：{ex.Message}";
            }

            _history?.Append(new HistoryEntry(
                encryptResult.Uuid!, originalName, HistoryAction.Encrypted, DateTimeOffset.UtcNow, hint,
                SourcePath: path,
                PasskeyEnabled: passkeyWrappedKeyBase64 is not null,
                RecoveryKeyEnabled: recoveryKeyWrappedBase64 is not null));

            return new LockResult(true, encryptResult.Uuid!, markerPath, cleanupWarning, recoveryKeyDisplayText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // metadata／marker 這一段如果寫到一半失敗（例如 marker.WriteTo 因為磁碟滿了而丟出例外），
            // 之前可能已經成功把 metadata 寫進 Vault 了——盡力把這個孤兒項目清掉，避免清單頁出現一筆
            // 沒有對應 .locked 指標檔、永遠打不開的幽靈紀錄。
            TryCleanupOrphanedVaultEntry(encryptResult.Uuid);
            return new LockResult(false, "", "", $"加密過程發生錯誤：{ex.Message}");
        }
        catch (Exception ex)
        {
            TryCleanupOrphanedVaultEntry(encryptResult.Uuid);
            return new LockResult(false, "", "", $"加密過程發生未預期的錯誤：{ex.Message}");
        }
        finally
        {
            if (encryptResult.EncryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptResult.EncryptionKey);
            }
            if (encryptResult.TempZipPath is not null)
            {
                SecureFileEraser.OverwriteAndDelete(encryptResult.TempZipPath);
            }
        }
    }

    private void TryCleanupOrphanedVaultEntry(string? uuid)
    {
        if (string.IsNullOrEmpty(uuid))
        {
            return;
        }
        try
        {
            _vault.DeleteItem(uuid);
        }
        catch (Exception)
        {
            // 盡力而為，清不掉就算了，不能讓清理失敗又拋出新的例外蓋掉原本要回報的錯誤。
        }
    }

    private sealed record EncryptionResult(
        bool Success,
        string? ErrorMessage,
        string? Uuid,
        byte[]? EncryptionKey,
        string? PasswordVerificationHashBase64,
        string? SaltBase64,
        long OriginalSizeBytes,
        List<string>? NestedUuids,
        string? TempZipPath);

    /// <summary>
    /// 純粹的檔案 I/O／加密運算部分，不牽涉任何 Windows Hello / WinRT 呼叫，安全地丟進背景執行緒執行。
    /// 回傳的 EncryptionKey 刻意不在這裡清零——呼叫端（EncryptAsync）還要拿它去做 Passkey 包裝，
    /// 用完才會清零，見 EncryptAsync 的 finally 區塊。
    /// </summary>
    private EncryptionResult EncryptToVault(string path, bool isFolder, string password)
    {
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
            using (var plaintextStream = File.OpenRead(contentPath))
            using (var encStream = _vault.OpenEncryptedContentWrite(uuid))
            {
                ChunkedCipher.EncryptStream(derived.EncryptionKey, plaintextStream, encStream);
            }

            return new EncryptionResult(
                true, null, uuid, derived.EncryptionKey,
                Convert.ToBase64String(derived.VerificationHash), Convert.ToBase64String(salt),
                originalSizeBytes, nestedUuids, tempZipToCleanup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EncryptionResult(
                false, $"加密過程發生錯誤：{ex.Message}", null, null, null, null, 0, null, tempZipToCleanup);
        }
        catch (Exception ex)
        {
            // 兜底：任何沒特別預期到的例外（例如底層密碼學函式庫丟出的例外）都不應該讓整個 App 崩潰，
            // 一律轉換成失敗結果回傳，讓 GUI 能顯示錯誤訊息而不是整個程式當掉。
            return new EncryptionResult(
                false, $"加密過程發生未預期的錯誤：{ex.Message}", null, null, null, null, 0, null, tempZipToCleanup);
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

        var destinationParentDir = ResolveDestinationParentDir(metadata, destinationDir, out var resolveError);
        if (destinationParentDir is null)
        {
            return new UnlockResult(false, "", resolveError!);
        }

        var result = DecryptAndRestore(metadata, password, destinationParentDir);

        if (result.Success)
        {
            CleanupMarkerIfMatches(metadata, uuid);
        }

        return result;
    }

    /// <summary>
    /// 對應規格文件 8.1 節「Passkey 快速解鎖」：不需要密碼，改用 Windows Hello 簽章衍生出的
    /// 包裝金鑰解開內容金鑰。ownerWindowHandle 用來套用視窗焦點緩解（見 PasskeyProtector.SignChallengeAsync）。
    /// </summary>
    public async Task<UnlockResult> DecryptByPasskeyAsync(string uuid, IntPtr ownerWindowHandle, string? destinationDir = null)
    {
        var metadata = _vault.LoadMetadata(uuid);
        if (metadata is null)
        {
            return new UnlockResult(false, "", "找不到對應的加密紀錄");
        }

        if (!metadata.PasskeyEnabled || metadata.PasskeyCredentialName is null
            || metadata.PasskeyChallenge is null || metadata.PasskeyWrappedContentKey is null)
        {
            return new UnlockResult(false, "", "這個項目沒有啟用 Passkey 快速解鎖");
        }

        var challenge = Convert.FromBase64String(metadata.PasskeyChallenge);
        var signature = await PasskeyProtector.SignChallengeAsync(metadata.PasskeyCredentialName, challenge, ownerWindowHandle);
        if (signature is null)
        {
            return new UnlockResult(false, "", "Passkey 驗證失敗或已取消");
        }

        byte[] contentKey;
        try
        {
            var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
            try
            {
                contentKey = PasskeyProtector.UnwrapContentKey(wrappingKey, metadata.PasskeyWrappedContentKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }
        catch (CryptographicException)
        {
            return new UnlockResult(false, "", "Passkey 解包內容金鑰失敗，資料可能已損毀");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        var destinationParentDir = ResolveDestinationParentDir(metadata, destinationDir, out var resolveError);
        if (destinationParentDir is null)
        {
            CryptographicOperations.ZeroMemory(contentKey);
            return new UnlockResult(false, "", resolveError!);
        }

        var result = await Task.Run(() => RestoreFromKey(metadata, contentKey, destinationParentDir, "passkey"));

        if (result.Success)
        {
            CleanupMarkerIfMatches(metadata, uuid);
        }

        return result;
    }

    /// <summary>對應「恢復金鑰」備援路徑：不需要密碼、不需要 Windows Hello，用使用者自己抄下來的恢復金鑰解鎖。</summary>
    public Task<UnlockResult> DecryptByRecoveryKeyAsync(string uuid, string recoveryKeyInput, string? destinationDir = null)
        => Task.Run(() => DecryptByRecoveryKeyCore(uuid, recoveryKeyInput, destinationDir));

    private UnlockResult DecryptByRecoveryKeyCore(string uuid, string recoveryKeyInput, string? destinationDir)
    {
        var metadata = _vault.LoadMetadata(uuid);
        if (metadata is null)
        {
            return new UnlockResult(false, "", "找不到對應的加密紀錄");
        }

        if (!metadata.RecoveryKeyEnabled || metadata.RecoveryKeyWrappedContentKey is null)
        {
            return new UnlockResult(false, "", "這個項目沒有啟用恢復金鑰");
        }

        var recoveryKeyBytes = RecoveryKeyProtector.ParseUserInput(recoveryKeyInput);
        if (recoveryKeyBytes is null)
        {
            return new UnlockResult(false, "", "恢復金鑰格式不正確，請確認有沒有打錯或漏掉字元");
        }

        byte[] contentKey;
        try
        {
            var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
            try
            {
                contentKey = RecoveryKeyProtector.UnwrapContentKey(wrappingKey, metadata.RecoveryKeyWrappedContentKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }
        catch (CryptographicException)
        {
            return new UnlockResult(false, "", "恢復金鑰不正確");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKeyBytes);
        }

        var destinationParentDir = ResolveDestinationParentDir(metadata, destinationDir, out var resolveError);
        if (destinationParentDir is null)
        {
            CryptographicOperations.ZeroMemory(contentKey);
            return new UnlockResult(false, "", resolveError!);
        }

        var result = RestoreFromKey(metadata, contentKey, destinationParentDir, "recoveryKey");

        if (result.Success)
        {
            CleanupMarkerIfMatches(metadata, uuid);
        }

        return result;
    }

    /// <summary>DecryptByUuidCore／DecryptByPasskeyAsync 共用：算出解密後要還原到哪個資料夾。</summary>
    private static string? ResolveDestinationParentDir(LockedItemMetadata metadata, string? destinationDir, out string? errorMessage)
    {
        errorMessage = null;

        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
            return destinationDir;
        }

        var originalParentDir = Path.GetDirectoryName(Path.GetFullPath(metadata.OriginalPath));
        if (originalParentDir is null)
        {
            errorMessage = "無法判斷原始路徑所在的資料夾";
            return null;
        }

        Directory.CreateDirectory(originalParentDir);
        return originalParentDir;
    }

    /// <summary>
    /// DecryptByUuidCore／DecryptByPasskeyAsync／DecryptByRecoveryKeyAsync 共用：解密成功後，
    /// 反推原本 .locked 應該在的位置，有（而且真的是同一個 UUID、簽章也驗證通過）就清掉，
    /// 避免留下一個已經失效、會誤導使用者的指標檔；沒有就跳過。
    /// 這裡刻意額外驗證簽章、不能只看 UUID 是否相符——metadata.OriginalPath 是明文的本機資料，
    /// 沒有簽章保護，理論上可能被竄改；如果只看 UUID，攻擊者只要能在算出來的位置預先放一個
    /// UUID 對得上的假檔案，就有機會誘使這裡刪掉非預期的檔案。加上簽章驗證後，
    /// 攻擊者還得知道 Vault 的簽章金鑰才偽造得出通過驗證的假指標檔，門檻高很多。
    /// </summary>
    private void CleanupMarkerIfMatches(LockedItemMetadata metadata, string uuid)
    {
        var expectedMarkerPath = ComputeMarkerPath(metadata.OriginalPath, metadata.Type == ItemType.Folder);
        if (!File.Exists(expectedMarkerPath))
        {
            return;
        }

        var marker = LockedMarkerFile.ReadFrom(expectedMarkerPath);
        if (marker is null || marker.Uuid != uuid)
        {
            return;
        }

        var vaultConfig = _vault.LoadOrCreateConfig();
        var signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);
        if (!marker.VerifySignature(signingKey))
        {
            return;
        }

        File.Delete(expectedMarkerPath);
    }

    /// <summary>密碼路徑：驗證密碼、拿到內容金鑰後，交給 RestoreFromKey 做剩下的還原工作。</summary>
    private UnlockResult DecryptAndRestore(LockedItemMetadata metadata, string password, string destinationParentDir)
    {
        if (_lockout is not null)
        {
            var lockoutStatus = _lockout.CheckStatus(metadata.Uuid);
            if (lockoutStatus.IsLockedOut)
            {
                return new UnlockResult(false, "", $"密碼錯誤次數過多，請在 {FormatRemaining(lockoutStatus.RemainingLockout!.Value)}後再試");
            }
        }

        var salt = Convert.FromBase64String(metadata.Salt);
        var storedHash = Convert.FromBase64String(metadata.PasswordVerificationHash);

        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(
            password, salt, storedHash,
            metadata.Argon2TimeCost, metadata.Argon2MemoryCostKb, metadata.Argon2Parallelism);

        if (!isValid || encryptionKey is null)
        {
            _lockout?.RecordFailedAttempt(metadata.Uuid);
            return new UnlockResult(false, "", "密碼錯誤");
        }

        _lockout?.RecordSuccess(metadata.Uuid);
        return RestoreFromKey(metadata, encryptionKey, destinationParentDir, "password");
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        return remaining.TotalMinutes >= 1
            ? $"{Math.Ceiling(remaining.TotalMinutes)} 分鐘"
            : $"{Math.Ceiling(remaining.TotalSeconds)} 秒";
    }

    /// <summary>
    /// DecryptAndRestore（密碼路徑）跟 DecryptByPasskeyAsync／DecryptByRecoveryKeyAsync 共用的核心還原邏輯：
    /// 拿到內容金鑰之後，解密內容、寫回目的地、清除 Vault 內的項目、記錄歷史紀錄。
    /// 呼叫端負責把 encryptionKey 準備好（不管是密碼衍生、Passkey 解包，還是恢復金鑰解包出來的），
    /// 這裡負責用完清零；unlockMethod 只是拿來寫進使用紀錄，不影響解密邏輯本身。
    /// </summary>
    /// <summary>
    /// 安全檢查：metadata.OriginalName 理論上只會是加密當下用 Path.GetFileName 取出的單純檔名，
    /// 但 .meta.json 是明文的本機檔案，沒有像 .locked 指標檔那樣有 HMAC 簽章保護，理論上可能被竄改或損毀。
    /// 如果不檢查就直接拿去 Path.Combine，一個被竄改成絕對路徑（或帶 ".." 路徑穿越片段）的檔名，
    /// 可能導致解密內容被寫到使用者指定的還原資料夾之外的任意位置——這裡在真的動筆寫檔案之前擋掉這種情況。
    /// </summary>
    private static bool IsSafeRestoreFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        if (Path.IsPathRooted(name))
        {
            return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        if (name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    private UnlockResult RestoreFromKey(LockedItemMetadata metadata, byte[] encryptionKey, string destinationParentDir, string unlockMethod)
    {
        if (!IsSafeRestoreFileName(metadata.OriginalName))
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            return new UnlockResult(false, "", "這筆紀錄的檔名資訊看起來不正常（可能已損毀或被竄改），為了安全拒絕還原");
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
                // 解密中途失敗（密碼錯誤/Passkey 解包錯誤在這裡不會發生，因為呼叫端已經先驗證過；
                // 這裡會是內容損毀/被竄改），不留下一個寫到一半、內容不完整的檔案在磁碟上誤導使用者。
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
            _history?.Append(new HistoryEntry(
                metadata.Uuid, metadata.OriginalName, HistoryAction.Decrypted, DateTimeOffset.UtcNow, null,
                UnlockMethod: unlockMethod, RestoredPath: destinationPath));

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
        catch (Exception ex)
        {
            return new UnlockResult(false, "", $"解密過程發生未預期的錯誤：{ex.Message}");
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

            // Vault 裡的加密內容刪掉之後，原本位置的 .locked 指標檔會變成一個指向不存在內容的死連結——
            // 順便清掉它，避免使用者之後雙擊到一個只會顯示「找不到對應的加密紀錄」的失效檔案。
            // 沿用跟解密成功後一樣的簽章驗證邏輯，確保不會誤刪到別的項目的指標檔。
            CleanupMarkerIfMatches(metadata, uuid);

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