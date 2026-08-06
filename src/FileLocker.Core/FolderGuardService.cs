using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using FileLocker.Core.FolderGuard;
using FileLocker.Core.Models;
using FileLocker.Core.Security;

namespace FileLocker.Core;

/// <summary>
/// 對外門面，整合資料夾防護子系統：FolderGuardAcl（ACL 套用/移除/檢查）、FolderGuardStore
/// （憑證與清單持久化）、PasskeyProtector（純身份驗證用途，不做內容金鑰包裝——資料夾防護沒有
/// 內容金鑰，ACL 拒絕規則不是從密碼算出來的，見 ADR-0001）、Argon2KeyDerivation（只需要驗證雜湊，
/// 不需要衍生加密金鑰）、LockoutTracker（暴力猜測防護，鍵值固定用 <see cref="LockoutKey"/>，
/// 代表整個共用密碼，不是像加密那樣每個項目各自一把）。
///
/// 跟 LockService 是平行、互不依賴的獨立子系統——App 層要串接兩者互動的情境（例如加密流程撞到
/// 巢狀防護資料夾）時，由呼叫端（Protocol 層）協調，不讓這兩個 Core 服務互相依賴對方型別。
/// </summary>
public class FolderGuardService
{
    private const string LockoutKey = "folder-guard-unlock";

    private readonly FolderGuardStore _store;
    private readonly LockoutTracker _lockoutTracker;

    public FolderGuardService(FolderGuardStore store, LockoutTracker lockoutTracker)
    {
        _store = store;
        _lockoutTracker = lockoutTracker;
    }

    /// <summary>整個資料夾防護功能是否已經設定過共用密碼——沒設定過就不能上鎖任何資料夾
    /// （規劃文件第 6 節：第一次上鎖前必須先完成首次設定）。</summary>
    public bool IsConfigured => _store.Load().PasswordVerificationHashBase64 is not null;

    public bool IsPasskeyEnabled => _store.Load().PasskeyEnabled;
    public bool IsDoubleClickUnlockEnabled => _store.Load().DoubleClickUnlockEnabled;
    public bool IsAutoRelockEnabled => _store.Load().AutoRelockEnabled;
    public int AutoRelockMinutes => _store.Load().AutoRelockMinutes;

    /// <summary>RelockExpiredEntriesAsync 真的重新上鎖了至少一個項目時觸發，只帶被重新上鎖的
    /// 路徑清單——沒有項目過期就不觸發，呼叫端（App.xaml.cs／MainWindow）靠這個約定判斷
    /// 要不要跳 toast 通知，不用自己另外檢查清單是否為空。</summary>
    public event EventHandler<IReadOnlyList<string>>? EntriesAutoRelocked;

    /// <summary>給 LockService 的巢狀防護檢查用（見 LockService 建構子的 getGuardedFolderPaths 參數）：
    /// 只回傳目前真的在 Locked 狀態、且通過自我修復檢查的路徑。</summary>
    public async Task<IReadOnlyList<string>> GetLockedPathsAsync()
        => await Task.Run(() => _store.ListWithSelfHeal()
            .Where(e => e.Status == FolderGuardStatus.Locked)
            .Select(e => e.Path)
            .ToList());

    public async Task<IReadOnlyList<FolderGuardEntry>> ListAsync()
        => await Task.Run(() => _store.ListWithSelfHeal());

    /// <summary>清單頁「刪除」按鈕：只能移除已經是 Unlocked 狀態的殘留紀錄——還在 Locked 狀態的項目
    /// 不該被直接從清單裡拿掉（那樣會讓使用者以為資料夾不再防護中，但 ACL 其實還在）。</summary>
    public async Task RemoveFromListAsync(string path)
    {
        await Task.Run(() =>
        {
            var data = _store.Load();
            data.Entries.RemoveAll(e => e.Status == FolderGuardStatus.Unlocked && PathsEqual(e.Path, path));
            _store.Save(data);
        });
    }

    // ---- 設定 ----

    public async Task SetupCredentialAsync(string password)
    {
        await Task.Run(() =>
        {
            var salt = Argon2KeyDerivation.GenerateSalt();
            var derived = Argon2KeyDerivation.DeriveKeys(password, salt);

            var data = _store.Load();
            data.PasswordSaltBase64 = Convert.ToBase64String(salt);
            data.PasswordVerificationHashBase64 = Convert.ToBase64String(derived.VerificationHash);
            _store.Save(data);

            CryptographicOperations.ZeroMemory(derived.EncryptionKey);
            CryptographicOperations.ZeroMemory(derived.VerificationHash);
        });
    }

    public async Task<bool> SetupPasskeyAsync(IntPtr ownerWindowHandle)
    {
        var credentialName = PasskeyProtector.GenerateCredentialName();
        var created = await PasskeyProtector.CreateCredentialAsync(credentialName, ownerWindowHandle);
        if (!created)
        {
            return false;
        }

        var data = _store.Load();
        data.PasskeyCredentialName = credentialName;
        data.PasskeyEnabled = true;
        _store.Save(data);
        return true;
    }

    /// <summary>只停用 Passkey、保留密碼，停用前一樣先驗證身份（密碼/Passkey，Passkey 優先）——
    /// 但呼叫端（App.vue 的 disableFolderGuardPasskeyAction）刻意保留「Passkey 驗證失敗就退回密碼」
    /// 的 fallback，不像其他四個驗證點那樣 Passkey 已設定就只認 Passkey：這顆按鈕本來就是 Passkey
    /// 硬體壞掉時的逃生門，如果連這裡都不能退回密碼，使用者就真的被鎖死了。</summary>
    public async Task<FolderGuardUnlockResult> DisablePasskeyAsync(string? password, IntPtr ownerWindowHandle)
    {
        var verify = await VerifyCredentialAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return verify;
        }

        var data = _store.Load();
        if (data.PasskeyCredentialName is { } credentialName)
        {
            await PasskeyProtector.DeleteCredentialAsync(credentialName);
        }
        data.PasskeyCredentialName = null;
        data.PasskeyEnabled = false;
        _store.Save(data);

        return new FolderGuardUnlockResult(true);
    }

    /// <summary>整個功能停用前先驗證身份（密碼/Passkey，Passkey 優先），避免任何能打開 App 的人
    /// 直接拿掉共用密碼跟 Passkey 憑證——跟 <see cref="UnlockFolderAsync"/>／<see cref="UnlockAllAsync"/>
    /// 同一個「先 verify 再做事」的結構。</summary>
    public async Task<FolderGuardUnlockResult> DisableAsync(string? password, IntPtr ownerWindowHandle)
    {
        var verify = await VerifyCredentialAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return verify;
        }

        await DisableCoreAsync();
        return new FolderGuardUnlockResult(true);
    }

    /// <summary>清掉密碼跟 Passkey 憑證，但刻意不主動解除任何正在上鎖中的資料夾的 ACL——停用
    /// 「防護機制」不代表使用者想要現有已上鎖的資料夾全部曝光，那應該是使用者自己逐一決定解鎖的事
    /// （呼叫端在停用前應該先引導使用者解鎖所有項目，或明確告知這個差異）。</summary>
    private async Task DisableCoreAsync()
    {
        var data = _store.Load();
        if (data.PasskeyCredentialName is { } credentialName)
        {
            await PasskeyProtector.DeleteCredentialAsync(credentialName);
        }

        data.PasswordSaltBase64 = null;
        data.PasswordVerificationHashBase64 = null;
        data.PasskeyCredentialName = null;
        data.PasskeyEnabled = false;
        _store.Save(data);
    }

    // ---- 驗證 ----

    /// <summary>Passkey 已設定時優先嘗試；沒設定、或呼叫端明確不想用（tryPasskeyFirst=false，例如
    /// 使用者在密碼輸入畫面手動選擇改用密碼）時走密碼路徑。密碼路徑受 LockoutTracker 保護，Passkey
    /// 路徑略過鎖定機制（跟規格文件 6.4 節「密碼鎖定機制不適用於 Passkey」同一個理由：TPM 硬體
    /// 驗證沒有能用軟體反覆嘗試的「猜」的環節）。</summary>
    public async Task<FolderGuardUnlockResult> VerifyCredentialAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
    {
        var data = _store.Load();

        if (tryPasskeyFirst && data.PasskeyEnabled && data.PasskeyCredentialName is { } credentialName)
        {
            var challenge = PasskeyProtector.GenerateChallenge();
            var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);
            if (signature is not null)
            {
                CryptographicOperations.ZeroMemory(signature);
                return new FolderGuardUnlockResult(true);
            }

            // Passkey 已設定但驗證失敗/被使用者取消，且呼叫端沒有同時附上密碼——新的前端行為是
            // 「Passkey 已設定就只走 Passkey」，不會再退回密碼，所以這裡要回傳專屬錯誤碼，不能讓它
            // 掉進下面的「密碼錯誤」分支，那個訊息在這個情境下是誤導的（使用者根本沒打密碼）。
            if (password is null)
            {
                return new FolderGuardUnlockResult(false, "Passkey 驗證失敗或已取消", ErrorCode: ErrorCodes.FolderGuardPasskeyFailed);
            }
        }

        if (data.PasswordSaltBase64 is null || data.PasswordVerificationHashBase64 is null)
        {
            return new FolderGuardUnlockResult(false, "尚未設定資料夾防護密碼", ErrorCode: ErrorCodes.FolderGuardNotConfigured);
        }

        if (password is null)
        {
            return new FolderGuardUnlockResult(false, "密碼錯誤", ErrorCode: ErrorCodes.FolderGuardPasswordIncorrect);
        }

        var lockoutStatus = _lockoutTracker.CheckStatus(LockoutKey);
        if (lockoutStatus.IsLockedOut)
        {
            var remainingSeconds = (int)Math.Ceiling(lockoutStatus.RemainingLockout!.Value.TotalSeconds);
            return new FolderGuardUnlockResult(false, "嘗試次數過多，請稍後再試",
                ErrorCode: ErrorCodes.FolderGuardLockedOut, ErrorDetail: remainingSeconds.ToString());
        }

        var salt = Convert.FromBase64String(data.PasswordSaltBase64);
        var storedHash = Convert.FromBase64String(data.PasswordVerificationHashBase64);
        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(password, salt, storedHash);

        if (encryptionKey is not null)
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
        }

        if (!isValid)
        {
            _lockoutTracker.RecordFailedAttempt(LockoutKey);
            return new FolderGuardUnlockResult(false, "密碼錯誤", ErrorCode: ErrorCodes.FolderGuardPasswordIncorrect);
        }

        _lockoutTracker.RecordSuccess(LockoutKey);
        return new FolderGuardUnlockResult(true);
    }

    // ---- 上鎖 ----

    /// <summary>上鎖不需要密碼驗證（規劃文件第 6 節：密碼只用來驗證解鎖身份，不是上鎖的必要條件）。</summary>
    public async Task<FolderGuardResult> LockFolderAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            return new FolderGuardResult(false, "找不到資料夾", ErrorCode: ErrorCodes.FolderGuardPathNotFolder);
        }

        var data = _store.Load();
        if (data.Entries.Any(e => e.Status == FolderGuardStatus.Locked && PathsEqual(e.Path, path)))
        {
            return new FolderGuardResult(false, "此資料夾已在防護中", ErrorCode: ErrorCodes.FolderGuardAlreadyLocked);
        }

        // 命名空間標記／ACL 兩件事的順序限制交給 FolderGuardProtection 統一處理，這裡不需要
        // 知道「哪個要先」——ApplyDeny 失敗要整個回報失敗，標記失敗已經在裡面安靜吞掉了。
        try
        {
            await Task.Run(() => FolderGuardProtection.Apply(path, data.DoubleClickUnlockEnabled));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return new FolderGuardResult(false, $"套用存取限制失敗：{ex.Message}",
                ErrorCode: ErrorCodes.FolderGuardAclApplyFailed, ErrorDetail: ex.Message);
        }

        data.Entries.RemoveAll(e => PathsEqual(e.Path, path));
        data.Entries.Add(new FolderGuardEntry { Path = path, Status = FolderGuardStatus.Locked, LockedAtUtc = DateTime.UtcNow });
        _store.Save(data);

        return new FolderGuardResult(true);
    }

    /// <summary>右鍵多選批次上鎖：每個路徑各自獨立成功/失敗，不會因為其中一個失敗就中止其他的。</summary>
    public async Task<IReadOnlyList<FolderGuardResult>> LockFoldersAsync(IReadOnlyList<string> paths)
    {
        var results = new List<FolderGuardResult>(paths.Count);
        foreach (var path in paths)
        {
            results.Add(await LockFolderAsync(path));
        }
        return results;
    }

    // ---- 解鎖 ----

    /// <summary>keepInListAsUnlocked：分頁清單頁操作傳 true（解鎖後留在清單顯示「已解鎖」，見規劃文件
    /// 第 9 節）；臨時彈窗解鎖（加密流程撞到巢狀防護資料夾）傳 false（不留記錄，直接從清單消失）。</summary>
    public async Task<FolderGuardUnlockResult> UnlockFolderAsync(string path, string? password, IntPtr ownerWindowHandle, bool keepInListAsUnlocked)
    {
        var verify = await VerifyCredentialAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return verify;
        }

        return await UnlockFolderCoreAsync(path, keepInListAsUnlocked);
    }

    /// <summary>解鎖 ACL 本身，跟身份驗證分開，讓「解鎖全部」可以只驗證一次密碼/Passkey，
    /// 對多個資料夾各自呼叫這個內部方法，不用每個資料夾都重新問一次密碼。</summary>
    private async Task<FolderGuardUnlockResult> UnlockFolderCoreAsync(string path, bool keepInListAsUnlocked)
    {
        // 命名空間標記／ACL 兩件事的順序限制交給 FolderGuardProtection 統一處理（解鎖方向跟
        // 上鎖相反：先解 ACL 才能重新寫入資料夾內容，再撕標記），這裡不需要知道「哪個要先」。
        try
        {
            await Task.Run(() => FolderGuardProtection.Remove(path));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return new FolderGuardUnlockResult(false, $"解除存取限制失敗：{ex.Message}",
                ErrorCode: ErrorCodes.FolderGuardAclRemoveFailed, ErrorDetail: ex.Message);
        }

        var data = _store.Load();
        var entry = data.Entries.FirstOrDefault(e => PathsEqual(e.Path, path));
        if (entry is not null)
        {
            if (keepInListAsUnlocked)
            {
                entry.Status = FolderGuardStatus.Unlocked;
                entry.UnlockedAtUtc = DateTime.UtcNow;
            }
            else
            {
                data.Entries.Remove(entry);
            }
            _store.Save(data);
        }

        return new FolderGuardUnlockResult(true);
    }

    /// <summary>清單頁「解鎖全部」：先驗證一次密碼/Passkey，成功才逐一解鎖清單中所有正在上鎖的項目，
    /// 全部留在清單顯示「已解鎖」。</summary>
    public async Task<FolderGuardUnlockResult> UnlockAllAsync(string? password, IntPtr ownerWindowHandle)
    {
        var verify = await VerifyCredentialAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return verify;
        }

        var lockedPaths = _store.Load().Entries
            .Where(e => e.Status == FolderGuardStatus.Locked)
            .Select(e => e.Path)
            .ToList();

        foreach (var path in lockedPaths)
        {
            await UnlockFolderCoreAsync(path, keepInListAsUnlocked: true);
        }

        return new FolderGuardUnlockResult(true);
    }

    /// <summary>右鍵選單「解鎖」：對右鍵選取的這幾個資料夾解鎖，只驗證一次密碼/Passkey，
    /// 解鎖後留在清單顯示「已解鎖」（跟分頁清單頁的個別/全部解鎖行為一致）。</summary>
    public async Task<FolderGuardUnlockResult> UnlockFoldersAsync(IReadOnlyList<string> paths, string? password, IntPtr ownerWindowHandle)
    {
        var verify = await VerifyCredentialAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return verify;
        }

        foreach (var path in paths)
        {
            await UnlockFolderCoreAsync(path, keepInListAsUnlocked: true);
        }

        return new FolderGuardUnlockResult(true);
    }

    /// <summary>設定頁「雙擊已上鎖資料夾直接解鎖」開關切換：對目前清單裡所有 Locked 的項目補上/
    /// 撕掉標記檔（<see cref="FolderGuardProtection.SwitchMode"/>），讓開關生效範圍涵蓋「已經鎖著
    /// 的資料夾」，不是只影響之後新鎖的——ACL 本身不用跟著動，兩種模式共用同一套 ACL 保護。個別
    /// 資料夾切換失敗不中止整批（跟 LockFolderAsync／UnlockFolderCoreAsync 同一個容錯原則），
    /// 開關本身還是要成功切換並存檔。</summary>
    public async Task SetDoubleClickUnlockEnabledAsync(bool enabled)
    {
        var data = _store.Load();
        data.DoubleClickUnlockEnabled = enabled;
        _store.Save(data);

        var lockedPaths = data.Entries
            .Where(e => e.Status == FolderGuardStatus.Locked)
            .Select(e => e.Path)
            .ToList();

        foreach (var path in lockedPaths)
        {
            try
            {
                await Task.Run(() => FolderGuardProtection.SwitchMode(path, enabled));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }

    /// <summary>設定頁「解鎖後閒置自動重新上鎖」開關切換：minutes 不做互動式驗證，直接 clamp 到
    /// 最小 1（沒有腳本／使用者能透過這個入口傳入 0 或負數造成整批項目立刻被重新上鎖的意外行為）。</summary>
    public async Task SetAutoRelockAsync(bool enabled, int minutes)
    {
        await Task.Run(() =>
        {
            var data = _store.Load();
            data.AutoRelockEnabled = enabled;
            data.AutoRelockMinutes = Math.Max(1, minutes);
            _store.Save(data);
        });
    }

    /// <summary>解鎖後閒置自動重新上鎖的核心判斷：App.xaml.cs 的 DispatcherTimer（週期性）跟啟動
    /// 補跑都呼叫同一個方法，兩者都只是「現在該不該把某個 Unlocked 項目重新上鎖」的同一個判斷，
    /// 呼叫端不用關心是計時器觸發還是啟動補跑。方法本身是冪等的——沒到期的項目每次呼叫都直接
    /// 略過，重複呼叫不會有副作用。AutoRelockEnabled 關閉時整個方法是 no-op，回傳空清單、不觸發
    /// EntriesAutoRelocked 事件。單筆重新上鎖失敗（跟 LockFolderAsync／LockFoldersAsync 同一個
    /// 容錯原則）不中止整批，只是不會出現在回傳清單裡。</summary>
    public async Task<IReadOnlyList<string>> RelockExpiredEntriesAsync()
    {
        var data = _store.Load();
        if (!data.AutoRelockEnabled)
        {
            return Array.Empty<string>();
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(data.AutoRelockMinutes);
        var expiredPaths = data.Entries
            .Where(e => e.Status == FolderGuardStatus.Unlocked && e.UnlockedAtUtc is not null && e.UnlockedAtUtc.Value <= cutoff)
            .Select(e => e.Path)
            .ToList();

        var relocked = new List<string>(expiredPaths.Count);
        foreach (var path in expiredPaths)
        {
            var result = await LockFolderAsync(path);
            if (result.Success)
            {
                relocked.Add(path);
            }
        }

        if (relocked.Count > 0)
        {
            EntriesAutoRelocked?.Invoke(this, relocked);
        }

        return relocked;
    }

    /// <summary>加密流程撞到巢狀防護資料夾、使用者確認解鎖後呼叫：解鎖但不留清單記錄
    /// （規劃文件第 7.2、9 節：臨時彈窗解鎖不留痕跡）。這些資料夾接下來會被加密流程整個消耗掉
    /// （原始資料夾會被刪除），不留記錄也避免之後 ListWithSelfHeal 還要多做一輪自我修復。</summary>
    public async Task<FolderGuardUnlockResult> UnlockForEncryptionAsync(IReadOnlyList<string> paths, string? password, IntPtr ownerWindowHandle)
    {
        var verify = await VerifyCredentialAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return verify;
        }

        foreach (var path in paths)
        {
            await UnlockFolderCoreAsync(path, keepInListAsUnlocked: false);
        }

        return new FolderGuardUnlockResult(true);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
