using System.Security.Cryptography;
using System.Text;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件 8.1 節「Passkey 快速解鎖」：用 KeyCredentialManager（TPM 保護的裝置金鑰）保護內容金鑰，
/// 而不是用 UserConsentVerifier + 本機儲存密鑰這種已被證實可繞過的組合（見規格文件說明的實際漏洞案例）。
///
/// 這個類別刻意把「牽涉 Windows Hello 互動、無法自動化測試」跟「純函式、可以單元測試」的部分分開：
/// IsSupportedAsync／CreateCredentialAsync／SignChallengeAsync／DeleteCredentialAsync 都要跟真正的
/// Windows Hello 硬體互動，沒辦法在自動化測試裡驗證（會跳出真的系統對話框、需要人操作），只能透過
/// GUI 手動測試；DeriveWrappingKey／WrapContentKey／UnwrapContentKey／GenerateChallenge／
/// GenerateCredentialName 都是純函式，不牽涉任何 Windows API，可以直接餵假資料做單元測試。
/// </summary>
public static class PasskeyProtector
{
    private const int WrappingKeySizeBytes = 32;
    private const int ChallengeSizeBytes = 32;

    // HKDF 的 info 參數固定，確保「Passkey 包裝金鑰」跟系統裡其他用途的衍生金鑰彼此無法互相推導。
    private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("FileLocker/passkey-wrap/v1");

    public static Task<bool> IsSupportedAsync() => KeyCredentialManager.IsSupportedAsync().AsTask();

    /// <summary>
    /// 憑證名稱刻意帶一組專屬 GUID，不要取固定的通用名稱——規格文件 8.1 節提過，未封裝應用程式的
    /// Windows Hello 憑證只綁定在使用者帳號層級，沒有 MSIX AppContainer 那種硬隔離，名稱夠獨特可以
    /// 降低被其他程式意外撞名的機率（不是完全消除，只是緩解）。
    /// </summary>
    public static string GenerateCredentialName() => $"FileLocker-{Guid.NewGuid():N}";

    public static byte[] GenerateChallenge() => RandomNumberGenerator.GetBytes(ChallengeSizeBytes);

    /// <summary>
    /// 建立這個項目專屬的裝置金鑰。呼叫這個方法本身就會觸發一次 Windows Hello 驗證，
    /// 這是建立金鑰的必要條件，不是可以跳過的步驟。
    /// </summary>
    public static async Task<bool> CreateCredentialAsync(string credentialName, IntPtr ownerWindowHandle)
    {
        WindowFocusHelper.PrepareForegroundHandoff(ownerWindowHandle);

        using var cts = new CancellationTokenSource();
        var promoteTask = WindowFocusHelper.PromoteNewForeignWindowAsync(cts.Token);

        var result = await KeyCredentialManager.RequestCreateAsync(credentialName, KeyCredentialCreationOption.ReplaceExisting);

        cts.Cancel();
        WindowFocusHelper.ReclaimForeground(ownerWindowHandle);

        return result.Status == KeyCredentialStatus.Success;
    }

    /// <summary>
    /// 對指定的挑戰資料簽章，觸發一次 Windows Hello 驗證，並套用規格文件 8.1 節的三段式視窗焦點緩解。
    /// 回傳 null 代表找不到憑證、使用者取消，或驗證失敗——呼叫端不需要區分細節，統一當作「這次沒解鎖成功」處理。
    /// </summary>
    public static async Task<byte[]?> SignChallengeAsync(string credentialName, byte[] challenge, IntPtr ownerWindowHandle)
    {
        var openResult = await KeyCredentialManager.OpenAsync(credentialName);
        if (openResult.Status != KeyCredentialStatus.Success)
        {
            return null;
        }

        WindowFocusHelper.PrepareForegroundHandoff(ownerWindowHandle);

        var challengeBuffer = CryptographicBuffer.CreateFromByteArray(challenge);

        using var cts = new CancellationTokenSource();
        var promoteTask = WindowFocusHelper.PromoteNewForeignWindowAsync(cts.Token);

        var signResult = await openResult.Credential.RequestSignAsync(challengeBuffer);

        cts.Cancel();
        WindowFocusHelper.ReclaimForeground(ownerWindowHandle);

        if (signResult.Status != KeyCredentialStatus.Success)
        {
            return null;
        }

        CryptographicBuffer.CopyToByteArray(signResult.Result, out var signatureBytes);
        return signatureBytes ?? Array.Empty<byte>();
    }

    public static Task DeleteCredentialAsync(string credentialName) => KeyCredentialManager.DeleteAsync(credentialName).AsTask();

    /// <summary>
    /// 從簽章結果衍生出用來包裝內容金鑰的「包裝金鑰」。純函式，不牽涉任何 Windows API，
    /// 單元測試時可以直接餵一組固定的假位元組陣列模擬簽章結果，不需要真的觸發 Windows Hello。
    /// 已經在獨立測試程式上實測確認：同一把裝置金鑰、同一份挑戰資料，多次簽章結果一致（決定性），
    /// 這個前提是這整套設計能成立的關鍵，見規格文件 8.1 節。
    /// </summary>
    public static byte[] DeriveWrappingKey(byte[] signature)
    {
        var wrappingKey = new byte[WrappingKeySizeBytes];
        HKDF.Expand(HashAlgorithmName.SHA256, signature, wrappingKey, HkdfInfo);
        return wrappingKey;
    }

    /// <summary>用包裝金鑰把內容金鑰包起來，回傳可以直接存進 .meta.json 的 Base64 字串。</summary>
    public static string WrapContentKey(byte[] wrappingKey, byte[] contentKey)
    {
        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(wrappingKey, contentKey);

        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);

        return Convert.ToBase64String(combined);
    }

    /// <summary>解開存在 .meta.json 裡的包裝內容金鑰。包裝金鑰錯誤（例如簽章跟當初不一致）會丟出 CryptographicException。</summary>
    public static byte[] UnwrapContentKey(byte[] wrappingKey, string wrappedBase64)
    {
        var combined = Convert.FromBase64String(wrappedBase64);
        var nonce = combined.AsSpan(0, AesGcmCipher.NonceSizeBytes);
        var tag = combined.AsSpan(AesGcmCipher.NonceSizeBytes, AesGcmCipher.TagSizeBytes);
        var ciphertext = combined.AsSpan(AesGcmCipher.NonceSizeBytes + AesGcmCipher.TagSizeBytes);

        return AesGcmCipher.Decrypt(wrappingKey, nonce, ciphertext, tag);
    }
}