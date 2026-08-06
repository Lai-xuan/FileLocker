# FileLocker 技術規格文件

版本：v3.2（第 21～24 節重新整理：資料夾防護、軟體更新檢查依序調整為第 21、22 節，已知限制與待辦事項拆成兩個獨立項目並移至文件最末尾，符合 CLAUDE.md 撰寫規範）| 最後更新：2026-08-02

---

## 1. 專案總覽

**目標**：Windows 檔案／資料夾保護工具，提供兩種互相獨立、安全等級不同的保護機制（見 [`CONTEXT.md`](CONTEXT.md) 完整術語表）：

- **加密**：使用者在檔案總管選取檔案或資料夾，右鍵加密，加密後內容集中存放在管理區（Vault），原位置留下一個 `.locked` 指標檔。雙擊指標檔或在 App 裡操作，輸入密碼（或用 Passkey、恢復金鑰）即可還原到原位置或指定位置。這是系統既有、最強的保護等級。
- **資料夾防護（Folder Guard）**：純粹透過 Windows ACL 拒絕目前帳號的存取權來限制資料夾，不加密內容、資料夾原地保留不搬動。接受較弱保護等級的取捨，因為防的是「隨手瀏覽」，不是「蓄意繞過權限」——見第 21 節。

**技術選型**：C#/.NET 10 後端 + WebView2（Vue 3 + Vite）前端 + C++ Shell Extension。理由：Registry/COM 這塊逃不掉底層要碰 Windows API，把它壓縮成一個獨立、輕量的 Shell Extension 元件；其餘商業邏輯、資料庫存取、加密全部用 C#（生態成熟、除錯工具好），不需要在多種語言之間切換心智負擔；前端用 HTML/CSS/JS 可以最大化調整空間，樣式想改就改 CSS，不受 XAML 的樣板限制。

**核心特性**：
1. 前後端分離架構：C# 後端（Core Engine + Protocol 分派層）+ Vue 3 前端（透過 WebView2 呈現），兩邊只透過一份 JSON 訊息協定溝通（見第 13 節）
2. 主頁 + 已加密項目清單（檔案與資料夾皆可解密並選擇儲存位置）
3. 加密後內容改名為 UUID，移至集中管理區（Vault）
4. 原位置留下 `.locked` 指標檔，內容經 HMAC-SHA256 簽章防竄改
5. 雙擊 `.locked` 檔案跳出原生 WPF 密碼輸入視窗，正確後解密還原
6. 支援右鍵批次選取多個檔案/資料夾一次加密；CLI 也支援批次加密／解密／刪除
7. 三種互相獨立的解鎖方式：密碼（必要）、Passkey（Windows Hello，裝置綁定）、恢復金鑰（一次性顯示的備援代碼）
8. Vault 位置可由使用者隨時在設定頁自訂，可指向雲端同步資料夾達成跨裝置加密備份
9. 「關鍵操作驗證」機制：清除使用紀錄等破壞性操作可設定 Windows Hello 驗證門檻
10. 介面設計參考 Apple HIG 與 emilkowalski/skills 的動效細節做法
11. 支援繁體中文／英文雙語，前端文案與後端常見錯誤情境皆有對應翻譯
12. 資料夾防護（Folder Guard）：獨立分頁，右鍵直接上鎖/解鎖資料夾，共用密碼＋選配 Passkey，純 ACL 限制不加密內容（見第 21 節）
13. 設定頁可一鍵檢查軟體更新，直接下載並啟動安裝程式（見第 22 節）

---

## 2. 系統架構

```
┌───────────────────────────────────────────────────────────────┐
│                        FileLocker.exe (WPF)                     │
│  ┌────────────────────────┐  postMessage   ┌──────────────────┐ │
│  │ 前端 (WebView2 / Vue 3)  │ ◄────JSON────► │ MainWindow.xaml.cs│ │
│  │  - 4 個主頁籤（見 §14）   │                │ （訊息分派層，switch│ │
│  │  - 自訂彈窗/通知元件      │                │  對應 30+ 種訊息） │ │
│  └────────────────────────┘                └─────────┬────────┘ │
│                                                        │          │
│                                          ┌─────────────▼───────┐ │
│                                          │ VaultProtocolHandlers │ │
│                                          │（純 C#，無 WPF/WebView2│ │
│                                          │  依賴，可單元測試）    │ │
│                                          └─────────┬────────────┘ │
└────────────────────────────────────────────────────┼──────────────┘
                                                       ▼
                                    ┌──────────────────────────────┐
                                    │   FileLocker.Core（獨立 DLL）   │
                                    │  LockService／VaultManager／   │
                                    │  Crypto／History／Settings     │
                                    └───────────────┬────────────────┘
                                                     ▼
                       ┌─────────────────────────────────────────────┐
                       ▼                                             ▼
        ┌────────────────────────────────┐         ┌──────────────────────────────┐
        │   Vault（集中管理區）              │         │  FileLockerShellExtension.dll  │
        │  {uuid}.enc + {uuid}.meta.json  │         │  C++/COM IContextMenu，         │
        │  + vault.config.json            │         │  常駐於 explorer.exe 行程內      │
        └────────────────────────────────┘         └──────────────────────────────┘
```

`FileLocker.Core` 是獨立的 .NET Class Library，`FileLocker.App`（WPF+WebView2 host）與 `FileLocker.Cli` 都直接參照、呼叫同一顆核心，維持前後端分離；`VaultProtocolHandlers` 是介於「WebView2 訊息格式」與「Core 業務邏輯」之間的一層薄轉譯層，本身不知道 WebView2/JSON 訊息的存在，只吃/吐 C# 型別，因此可以直接單元測試，不需要真的開一個視窗（見 `VaultProtocolHandlersTests`）。

---

## 3. 金鑰衍生與加密演算法

### 3.1 密碼 → 金鑰（Argon2id + HKDF）

`Crypto/KeyDerivation.cs`，套件為 `Konscious.Security.Cryptography`：

| 參數 | 值 |
|---|---|
| Argon2 變體 | Argon2id |
| Time cost | 3 |
| Memory cost | 65536 KB（64 MB） |
| Parallelism | 2 |
| Salt 長度 | 16 bytes（`RandomNumberGenerator` 產生） |
| 主金鑰長度 | 32 bytes |

流程：`Argon2id(password, salt)` 先衍生出一把 32-byte 主金鑰，再用 HKDF-Expand(SHA-256) 從主金鑰切分成兩把用途不同的子金鑰（`SplitMasterKey`），各自對應固定的 info 字串：

- `"FileLocker/encryption/v1"` → **加密金鑰**（實際拿去做 AES-GCM）
- `"FileLocker/verification/v1"` → **驗證雜湊**（存進 `.meta.json`，用來在還沒真正解密前先確認密碼對不對）

這樣設計的理由：驗證雜湊本身不能拿來加密／解密任何內容，就算 `.meta.json` 外洩，也只洩漏了一個無法逆推回密碼、也無法直接拿來解密的雜湊值。`VerifyPassword` 用 `CryptographicOperations.FixedTimeEquals` 做固定時間比對，避免時序攻擊；驗證失敗時金鑰陣列會被歸零。

### 3.2 內容加密（AES-256-GCM + 串流分塊）

`Crypto/AesGcmCipher.cs` 直接用 .NET 內建的 `System.Security.Cryptography.AesGcm`（不需要額外套件）：Nonce 12 bytes、Auth Tag 16 bytes，每次加密都重新產生隨機 Nonce。

`.NET` 的 `AesGcm` 是一次性 AEAD API，一定要拿到完整的明文/密文緩衝區，沒有原生的串流/漸進式介面。要做到「不用把整個檔案讀進記憶體就能加解密」，`Crypto/ChunkedCipher.cs` 自己把檔案切成一塊一塊（預設 1 MB／塊），每一塊各自獨立做一次完整的 AES-GCM 加密（各自的 Nonce/Tag）：

```
密文串流格式（每個區塊重複到串流結束，沒有全域 Magic Header）：
┌──────────────────────────────────────────────┐
│ 區塊明文長度 (4 bytes, big-endian, Int32)         │
│ Nonce (12 bytes)                               │
│ 密文（長度＝上面那個區塊明文長度）                    │
│ Auth Tag (16 bytes)                             │
└──────────────────────────────────────────────┘
```

- 每一塊都各自驗證完整性，其中一塊被竄改，解密到那一塊就會拋出 `CryptographicException`，但不影響已經處理過的前面幾塊——不過呼叫端（`LockService`）仍然會把已經寫出去的部分輸出視為不可信、整份刪除，不會留下「解密到一半」的殘檔。
- 解密時逐塊直接寫進輸出串流，記憶體裡不會同時存在「整份」明文，每處理完一塊就把該塊的明文緩衝區歸零。
- 長度前綴有 64 MB 上限（`MaxChunkLengthBytes`），防止讀到損毀/被竄改的長度值時嘗試配置荒謬大小的陣列。
- 檔案是「檔案」還是「資料夾封裝後的 zip」，這個型別資訊記錄在 `.meta.json` 的 `Type` 欄位，不在 `.enc` 內容本身裡面——`.enc` 純粹是上述串流分塊密文，沒有另外的容器格式或版本標頭。

### 3.3 檔案完整性與竄改防護

除了上面提到的「加密內容本身」防篡改（每個區塊自帶 Auth Tag），系統裡還有兩層獨立的完整性保護，職責分開：

- **`.locked` 指標檔**：HMAC-SHA256 簽章，見第 12.3 節。
- **`vault.config.json` 存取控制**：Windows ACL 限制成當前使用者，見第 10.1 節。

---

## 4. 加密／解密流程（`LockService`）

`LockService`（`src/FileLocker.Core/LockService.cs`）建構子：`LockService(VaultManager vault, HistoryLogger? historyLogger = null, LockoutTracker? lockoutTracker = null)`。

### 4.1 公開方法一覽

```csharp
Task<LockResult> EncryptAsync(string path, string password, string? hint,
    bool enablePasskey = false, IntPtr ownerWindowHandle = default,
    bool enableRecoveryKey = false, string? batchId = null,
    IProgress<double>? progress = null)

Task<UnlockResult> DecryptAsync(string lockedMarkerPath, string password)
Task<UnlockResult> DecryptByUuidAsync(string uuid, string password, string? destinationDir = null)
Task<UnlockResult> DecryptByPasskeyAsync(string uuid, IntPtr ownerWindowHandle, string? destinationDir = null)
Task<UnlockResult> DecryptByRecoveryKeyAsync(string uuid, string recoveryKeyInput, string? destinationDir = null)
Task<VerifyPasswordResult> VerifyPasswordAsync(string uuid, string password)
Task<DeleteRecordResult> TryDeleteRecordAsync(string uuid, bool force = false)
```

### 4.2 加密流程

1. 驗證路徑存在（檔案或資料夾）、目標指標檔路徑尚未被佔用（`MarkerStatusChecker`）。
2. 背景執行緒（`Task.Run`）：若為資料夾，先掃描巢狀 `.locked` 項目（`FolderArchiver.FindNestedLockedFiles`），壓縮成暫存 zip（`FolderArchiver.CompressToTempZip`，見第 5 節）。
3. 產生隨機 salt，`Argon2KeyDerivation.DeriveKeys` 衍生金鑰，產生新 UUID，`ChunkedCipher.EncryptStream` 串流寫入 `{uuid}.enc`。
4. **回到 UI 執行緒**（不整個放進背景執行緒——Windows Hello 的 WinRT API 有自己的執行緒模型要求）：若啟用 Passkey，建立憑證、簽章挑戰、包裝內容金鑰（見第 6 節）；失敗會清掉剛建立的憑證。
5. 若啟用恢復金鑰，產生金鑰、包裝內容金鑰（純同步流程，不牽涉 WinRT，見第 7 節）。
6. 組出 `LockedItemMetadata`，寫入 `{uuid}.meta.json`；建立經簽章的 `.locked` 指標檔並寫入原路徑。
7. 安全清除原始明文檔案/資料夾（見第 12.4 節）——這一步失敗只回傳警告，不影響整體加密結果判定為成功。
8. 寫入一筆 `HistoryEntry`（`Encrypted`）。
9. 中途任何一步失敗，`TryCleanupOrphanedVaultEntry` 會清掉已經半途寫入的 Vault 項目；`finally` 一律歸零記憶體中的加密金鑰、刪除暫存 zip。

### 4.3 四種解密路徑

| 方法 | 觸發情境 | 關鍵差異 |
|---|---|---|
| `DecryptAsync`（指標檔） | 雙擊 `.locked` 檔案 | 先驗證指標檔 HMAC 簽章，還原到指標檔目前所在的資料夾，成功後刪除指標檔 |
| `DecryptByUuidAsync`（密碼） | 已加密清單頁點密碼解鎖 | 不需要指標檔存在，直接用 UUID 查 metadata；成功後用 `CleanupMarkerIfMatches` 反查、驗證後才刪除對應指標檔（防止誤刪別的項目的指標檔） |
| `DecryptByPasskeyAsync` | 清單頁或密碼小視窗按「使用 Passkey 解鎖」 | 要求 `PasskeyEnabled=true`，走 `PasskeyProtector.SignChallengeAsync` 拿到簽章、`UnwrapContentKey` 還原內容金鑰，不需要密碼、不受密碼鎖定機制限制 |
| `DecryptByRecoveryKeyAsync` | 清單頁或密碼小視窗按「使用恢復金鑰解鎖」 | 要求 `RecoveryKeyEnabled=true`，`RecoveryKeyProtector.ParseUserInput` 解析使用者輸入、還原內容金鑰 |

四條路徑最終都匯流到同一個私有輔助方法完成「解密＋還原＋清理」（依是否已經在背景執行緒而分成 `DecryptAndRestore`／`FinishAfterKeyResolved` 兩個入口，邏輯共用）：`RestoreFromKey` 會檢查 `IsSafeRestoreFileName`（拒絕帶路徑分隔符、`..`、非法字元的原始檔名——防止被竄改過的 `.meta.json` 拿來做路徑穿越攻擊）、檢查目的地是否已有同名檔案/資料夾、資料夾類型先解到暫存 zip 再解壓縮、檔案類型直接串流解密到目的地，失敗時刪除已寫出的部分內容。

### 4.4 密碼驗證與其餘操作共用的核心

`VerifyPasswordAndDeriveKey`（私有，被 `DecryptAndRestore` 與 `VerifyPasswordCore` 共用）：先查 `LockoutTracker.CheckStatus`，鎖定中直接回傳 `ErrorCodes.LockedOut` + 剩餘秒數；否則用 `Argon2KeyDerivation.VerifyPassword` 比對，並依結果呼叫 `LockoutTracker` 記錄成功/失敗次數。`VerifyPasswordAsync`（給「永久刪除前重新輸入密碼」用）不使用衍生出來的金鑰，驗證完立刻歸零，因為這個情境只需要證明「這個人知道密碼」，不需要真的解密內容。

### 4.5 批次操作限制

批次（多選）加密時，Passkey／恢復金鑰勾選框自動鎖住不能勾——多個項目時，每個項目都要重新驗證一次 Passkey 或各自產生不同的恢復金鑰，顯示與保存流程會太複雜、太打擾人。批次加密只能用密碼，單一項目加密才能額外開啟 Passkey／恢復金鑰。同一批次加密的項目共用一個 `batchId`（`Guid.NewGuid()`，只有項目數 > 1 才產生），清單頁會摺疊成一組顯示，可展開個別操作或「全部解鎖」（僅支援密碼路徑，`DecryptBatchAsync`）。

---

## 5. 資料夾加密設計

採用「封裝後加密」策略：資料夾加密時，先用 `System.IO.Compression.ZipFile.CreateFromDirectory` 把整個資料夾打包成一個暫存 zip，再把這個 zip 當成一份「檔案」丟進第 4 節的檔案加密流程（完全複用，不需要另外設計一套資料夾專屬的加解密機制）。

**為什麼要先壓縮再加密**：AES-GCM 加密後的內容本質上是隨機亂碼，加密完才做壓縮完全沒有效果，所以「先打包/壓縮、再加密」這個順序是必要的，不能反過來。

**壓縮等級用 `CompressionLevel.NoCompression`，不是 `Optimal`**：這個 zip 純粹是拿來當「把整個資料夾打包成一份東西」的容器，用途不是省空間。大容量資料夾最常見的組成（影片、照片）本身就已經是壓縮格式，DEFLATE 對這類內容幾乎沒有壓縮效果，卻要吃滿 CPU 時間——先花 CPU 做完整的 DEFLATE 壓縮，對最終檔案大小沒有實質貢獻，反而是加密大型資料夾速度的關鍵影響因素。

理由與取捨：
- 複雜度最低：資料夾加密 = 「壓縮」+「既有檔案加密流程」，不需要另外維護一套「多檔 UUID 對應資料夾樹狀結構」的索引邏輯。
- 完整性天然保證：整包資料夾是一個 AEAD 單元（串流分塊），要嘛整包驗證成功、要嘛失敗，不會有「資料夾內某幾個檔案解密成功、某幾個失敗」的中間狀態。
- 取捨：解密資料夾時需要暫存空間先解壓縮，非常大的資料夾（例如數十 GB）會需要對應的磁碟暫存空間與時間，這點會在 UI 加密前顯示「預估所需空間」提示使用者。

### 5.1 巢狀 `.locked` 項目的處理

資料夾加密時若遞迴掃描發現內含既有的 `.locked` 指標檔（代表裡面本身就包著之前單獨加密過的內容），處理方式：

1. **加密當下**：跳出一個不擋流程的資訊性提示（toast，2-3 秒後自動消失）：「這裡面有 2 個項目原本是分開鎖著的，加密完成後可以在『已加密清單』裡個別找到、解開它們。」刻意**不**做成需要使用者按確認才能繼續的對話框——巢狀項目自己的 Vault 紀錄（`.enc`／`.meta.json`）是完全獨立的檔案，不會因為外層資料夾加密或之後刪除外層紀錄而消失或損毀（`DecryptByUuidAsync` 本來就不需要指標檔存在），所以沒有真正需要使用者停下來確認的風險。
2. **外層資料夾的 `{uuid}.meta.json` 記下內層清單**：`ContainsNestedLocks` 欄位（`List<string>`），記錄裡面有哪些內層 UUID。
3. **刪除紀錄時，預設直接擋下來**：`TryDeleteRecordAsync` 檢查到 `ContainsNestedLocks` 不是空的且 `force=false`，回傳 `DeleteRecordResult(Success:false, BlockedByNestedLocks:true, NestedUuids:...)`；前端只給「先去解鎖」跟「取消」兩個選項，不提供一鍵強制刪除的按鈕。擋下的理由不是「資料會真的遺失」（巢狀項目本身不會），而是避免使用者失去追蹤線索——外層紀錄一刪，「裡面還有東西」的提醒就沒了。
4. **巢狀項目自己的指標檔狀態顯示**：巢狀項目原本位置的 `.locked` 指標檔會隨外層資料夾整個被壓縮進 zip、外層資料夾本身也被刪除，所以查詢這個內層項目時會找不到它原本的指標檔——程式會反查是不是被哪個有巢狀鎖定標記的外層項目收進去了，查得到就顯示「該檔案的指標檔已經收進『{外層資料夾名稱}』這個資料夾一起加密了」（`ErrorCodes.MarkerPackedIntoContainer`），查不到才顯示通用的「指標檔可能被移動或刪除」（`ErrorCodes.MarkerNotFound`）。

### 5.2 暫存空間位置與清理流程

暫存空間固定在 `Path.GetTempPath()\FileLocker\`（`FolderArchiver.TempDirectory`）。完整生命週期：

1. 資料夾 → 壓縮成暫存 zip（`{暫存guid}.zip`）
2. 暫存 zip → 加密 → 寫入 Vault 的 `{uuid}.enc`
3. 加密成功寫入 Vault 後：**刪除暫存 zip**（`SecureFileEraser.OverwriteAndDelete`，先覆寫隨機資料再刪除，因為這個暫存 zip 本身就是完整的明文壓縮內容）
4. 接著刪除**原始資料夾**本身，同樣需要安全覆寫
5. **例外處理**：App 啟動時會呼叫 `FolderArchiver` 的清理方法，掃描 `%TEMP%\FileLocker\` 資料夾，清除任何殘留的暫存 zip（避免程式崩潰、斷電導致明文資料一直留在磁碟上）。

原位置的 `.locked` 指標檔：資料夾加密完成後，整個原始資料夾會被刪除，改成在同一位置產生一個 `{資料夾名稱}.locked` 檔案（單一檔案，非資料夾），內容同樣只記錄對應 UUID（+ 簽章）。

---

## 6. Passkey（Windows Hello）機制

`Crypto/PasskeyProtector.cs`，靜態類別，底層用 `Windows.Security.Credentials.KeyCredentialManager`（TPM 保護的裝置金鑰）——**不使用** `Windows.Security.Credentials.UI.UserConsentVerifier`：後者搭配本機儲存密鑰是已被證實可繞過的組合，`KeyCredentialManager` 才是這裡採用的方案。

### 6.1 API

```csharp
static Task<bool> IsSupportedAsync()
static string GenerateCredentialName()              // "FileLocker-{Guid:N}"
static byte[] GenerateChallenge()                    // 32 random bytes
static Task<bool> CreateCredentialAsync(string credentialName, IntPtr ownerWindowHandle)
static Task<byte[]?> SignChallengeAsync(string credentialName, byte[] challenge, IntPtr ownerWindowHandle)
static Task DeleteCredentialAsync(string credentialName)
static byte[] DeriveWrappingKey(byte[] signature)                 // HKDF-Expand(SHA256), info="FileLocker/passkey-wrap/v1"
static string WrapContentKey(byte[] wrappingKey, byte[] contentKey)      // Base64(nonce 12B + tag 16B + ciphertext)
static byte[] UnwrapContentKey(byte[] wrappingKey, string wrappedBase64) // 驗證失敗拋 CryptographicException
```

`CreateCredentialAsync` 用 `KeyCredentialCreationOption.ReplaceExisting`，重複呼叫（例如清單頁重新設定某項目的 Passkey）會直接覆蓋舊憑證，不需要「先刪除再建立」的額外邏輯。`SignChallengeAsync` 失敗（使用者取消、驗證失敗、裝置不支援）一律回傳 `null`，呼叫端不區分失敗原因，統一當作「這次沒通過」。

### 6.2 內容金鑰包裝方式

Passkey **不是**直接拿來加密檔案內容，而是拿來「包裝」（wrap）真正的內容金鑰：加密當下，先用一般的隨機內容金鑰做 AES-GCM 加密，再用「這次 Passkey 挑戰簽章 → HKDF 衍生出的包裝金鑰」把內容金鑰包起來存進 `.meta.json`（`PasskeyWrappedContentKey`）。解密時反過來：先通過 Windows Hello 拿到簽章、衍生出同一把包裝金鑰、解開包裝拿到真正的內容金鑰，才能繼續解密內容。好處：換裝置或重新設定 Passkey，不需要重新加密整份內容，只需要重新包裝這一把小小的金鑰。

### 6.3 視窗焦點問題（`WindowFocusHelper`）

未封裝的 Win32 桌面程式呼叫 WinRT 的 Windows Hello API 時，已知會有系統驗證視窗搶不到前景焦點、或被主視窗蓋住的問題。`Crypto/WindowFocusHelper.cs`（internal）用 P/Invoke（`AttachThreadInput` 技巧）處理：`PrepareForegroundHandoff`／`ReclaimForeground`／`ForceSetForegroundWindow`／`PromoteNewForeignWindowAsync`（每 50ms 輪詢 `EnumWindows`，最多 5 秒，把新出現的可見視窗強制拉到最上層）。`PasswordPromptWindow` 也因此不設 `Topmost="True"`——曾經實測發現永久置頂反而會擋住之後才彈出的 Windows Hello 驗證視窗。

### 6.4 密碼鎖定機制不適用於 Passkey

密碼錯誤鎖定要防的是「不知道密碼卻反覆亂猜」；Passkey 每次都需要真的通過這台裝置的 Windows Hello（TPM 硬體把關），沒有能單純用軟體反覆嘗試的「猜」的環節，能持續通過驗證的人本來就已經滿足「合法使用者」門檻，額外鎖定沒有意義。Windows Hello 本身在作業系統層級也有自己的防暴力破解機制（PIN 連續錯誤會被 TPM 自動拉長等待時間）。

---

## 7. 恢復金鑰機制

`Crypto/RecoveryKeyProtector.cs`，靜態類別，運作原理跟 Passkey（第 6.2 節）幾乎一樣——同樣是「衍生一把包裝金鑰去包裝內容金鑰」，差別只在包裝金鑰的來源：

| | Passkey | 恢復金鑰 |
|---|---|---|
| 包裝金鑰來源 | Windows Hello 挑戰簽章（HKDF） | 使用者輸入的恢復金鑰本身（HKDF） |
| info 字串 | `"FileLocker/passkey-wrap/v1"` | `"FileLocker/recovery-wrap/v1"` |
| 裝置綁定 | 是（TPM） | 否（純資料，抄下來就能在任何裝置用） |

### 7.1 產生與顯示格式

`GenerateRecoveryKeyBytes()` 產生 32 bytes 隨機資料，`FormatForDisplay` 用自訂 Base32（字母表 `ABCDEFGHIJKLMNOPQRSTUVWXYZ234567`，RFC4648 無填充）編碼，每 5 個字元一組用 `-` 相連（例如 `ABCDE-FGHIJ-KLMNO-...`），方便使用者抄寫核對。`ParseUserInput` 反過來：去掉所有非英數字元、轉大寫、Base32 解碼，長度不是剛好 32 bytes 就視為格式錯誤（`ErrorCodes.RecoveryKeyInvalidFormat`）。

### 7.2 顯示與保存的一次性設計

恢復金鑰**只會在產生當下顯示一次**，關掉那個畫面之後就再也看不到——前端的恢復金鑰顯示彈窗是整個 App 裡刻意做出視覺差異的畫面（黃銅色蠟封圖示、獨立標題排版，見第 14.5 節），強制使用者先複製/存檔/確認已抄下來才能關閉（唯一排除在 Esc 快捷關閉範圍外的彈窗）。複製到剪貼簿的內容 45 秒後自動清空（只有剪貼簿內容還是剛複製的這份時才清空，避免蓋掉使用者後來自己複製的別的東西）。

---

## 8. 關鍵操作驗證機制（Critical Action）

一組**不綁定任何特定加密項目**的 App 層級 Windows Hello 憑證，用來在執行「清除使用紀錄」這類破壞性、但本身不是加解密操作的動作前，額外要求一次身份驗證——直接沿用第 6 節的 `PasskeyProtector`，而不是另外設計一套獨立的「關鍵變動密碼」系統（後者需要一整套新的密碼儲存/設定/變更 UI，對這個用途而言太重）。

### 8.1 後端 API（`VaultProtocolHandlers`）

```csharp
bool IsCriticalActionConfigured { get; }                       // !string.IsNullOrEmpty(settings.CriticalActionCredentialName)
Task<bool> SetupCriticalActionAsync(IntPtr ownerWindowHandle)   // 建立/覆蓋憑證，成功才存進 AppSettings
Task<bool> VerifyCriticalActionAsync(IntPtr ownerWindowHandle)  // 挑戰簽章驗證，不分失敗原因
Task DisableCriticalActionAsync()                                // 刪除底層憑證＋清空設定值，呼叫前需自行先驗證過
void ClearHistory()                                              // 純粹清空歷史檔案，不含任何驗證邏輯
```

`AppSettings.CriticalActionCredentialName`（`string?`）是唯一的狀態欄位：`null` 代表沒設定過／已停用；非空代表已設定。這個欄位同時身兼「是否啟用」兩用，沒有另外的布林開關——好處是「停用」跟「未設定過」是同一種狀態，不需要維護兩套邏輯分支。

### 8.2 前端使用情境與驗證強度

| 操作 | 是否需要先設定過 | 驗證時機 |
|---|---|---|
| **清除使用紀錄** | 是，沒設定過整個功能直接擋住，不給「不驗證」的路徑 | 每次清除前都要重新通過一次 Windows Hello |
| **搬移 Vault 位置** | 否——沒設定過就直接放行，維持原本行為 | 只有已經設定過，才會在開啟資料夾選擇器前先要求驗證 |
| **停用關鍵操作驗證本身** | 是 | 停用前必須先通過一次驗證，避免已取得裝置操作權限的第三人單純點一下就把保護關掉 |

清除紀錄的完整互動是刻意設計成三步驟（不是「先驗證再問一次」的兩步驟）：

1. 彈窗「確定要刪除所有紀錄嗎？」，確定按鈕本身就是「使用 Passkey 驗證身份」的觸發鍵（紅底白字，帶白色版 Passkey 圖示）。
2. 按下後才真的觸發 Windows Hello 挑戰簽章。
3. 驗證通過後才跳出第二個、純文字的最終確認彈窗（不再需要 Passkey 圖示，因為身份已經驗證過），確認後才真的送出清除。

---

## 9. 密碼錯誤鎖定機制

`Security/LockoutTracker.cs`，只作用在**密碼**這條解鎖路徑（Passkey／恢復金鑰不受影響，理由見第 6.4 節）。

| 參數 | 值 |
|---|---|
| 觸發門檻 | 連續錯誤 5 次 |
| 基礎鎖定時間 | 30 秒 |
| 最長鎖定時間 | 3600 秒（1 小時） |
| 延遲公式 | `min(30 × 2^min(連續錯誤次數-5, 10), 3600)` 秒，每多錯一次鎖越久，指數成長直到封頂 |

狀態儲存在一個獨立的本機 JSON 檔案（不放在 Vault 內、不隨雲端同步——鎖定狀態是「這台裝置」的暫時狀態，不該跨裝置共享），以 UUID 為鍵，寫入用 `AtomicFile.WriteAllText`（原子寫入），存取全部包在一個靜態鎖裡避免併發寫入互相干擾。驗證成功會整筆清掉重置。

---

## 10. Vault 與本機索引

### 10.1 `VaultManager`：Vault 檔案佈局

```
{VaultPath}/
├── vault.config.json      # SchemaVersion=1, SigningKeyBase64（256-bit，見 12.3 節）
├── {uuid}.enc             # 串流分塊密文（見第 3.2 節）
└── {uuid}.meta.json       # 每個加密項目一份獨立的 metadata
```

`LoadOrCreateConfig()` 首次建立 Vault 時產生隨機簽章金鑰，寫入後立刻呼叫 `RestrictToCurrentUser`——用 Windows ACL（`SetAccessRuleProtection(true, false)` + 只授權 `WindowsIdentity.GetCurrent().User` 的 `FullControl`）限制 `vault.config.json` 只有目前使用者能存取；這段是 best-effort（`UnauthorizedAccessException`／`PlatformNotSupportedException`／`IOException` 都吞掉不中斷流程），不是絕對防護。

`ScanAll()` 掃描所有 `*.meta.json`，用內容（UUID）而非檔名去重——雲端同步偶爾會產生「衝突副本」檔名，這裡偏好保留 `File.GetLastWriteTimeUtc` 較新的那份，行為決定性、可預期。

### 10.2 `LockedItemMetadata`（`.meta.json` 內容）

`Uuid`／`OriginalName`／`OriginalPath`／`PasswordVerificationHash`／`Salt`／`Argon2TimeCost`／`Argon2MemoryCostKb`／`Argon2Parallelism`／`Hint`／`Type`／`OriginalSizeBytes`／`CreatedAtUtc`／`LastAccessedAtUtc`／`ContainsNestedLocks`／`PasskeyEnabled`／`PasskeyCredentialName`／`PasskeyChallenge`／`PasskeyWrappedContentKey`／`RecoveryKeyEnabled`／`RecoveryKeyWrappedContentKey`／`BatchId`。

### 10.3 本機加速索引（`VaultIndexCache` + `VaultChangeWatcher`）

不用單一中央 SQLite 資料庫存所有紀錄——多裝置透過雲端同步資料夾共用 Vault 時，多個裝置同時寫入同一個 SQLite 檔案容易造成同步衝突甚至資料庫損毀。真正的資料來源永遠是檔案系統裡一份一份獨立的 `.meta.json`；SQLite 只拿來做**本機、唯讀、加速用途**的索引快取：

- 快取資料庫檔案存在 Vault **之外**（例如 `%LocalAppData%\FileLocker\VaultIndexCache\`），檔名用 Vault 路徑正規化後的 SHA-256 前 16 hex 字元命名——絕對不能跟著雲端同步跑，那樣又會重蹈中央 SQLite 資料庫的同步衝突問題。
- `Microsoft.Data.Sqlite`，`journal_mode=WAL`、`synchronous=NORMAL`；所有連線存取都包在同一個鎖裡（`SqliteConnection` 本身不是執行緒安全的）。
- `VaultChangeWatcher` 用 `FileSystemWatcher` 監看 Vault 資料夾裡的 `*.meta.json`，兩層 debounce：每個檔案各自的計時器（預設 300ms）收斂同一檔案的重複事件，外層再有一個全域計時器（預設 750ms）合併短時間內大量檔案異動成一次通知，只更新變動到的那幾筆快取項目，不用整份重建。`FileSystemWatcher` 內部緩衝區溢位時（`OnError`）直接觸發全量 `Rebuild()`。
- 快取本身如果跟磁碟不一致，`ScanAll()` 的結果永遠是最終校正依據。

---

## 11. Vault 設定與雲端同步

### 11.1 Vault 位置

- **首次啟動的預設路徑**：目前沒有獨立的設定精靈畫面——App 啟動時（`App.xaml.cs`）若偵測到 `AppSettings.VaultPath` 是空的，會靜默把它設成預設路徑 `%LocalAppData%\FileLocker\Vault\` 並寫回 `%AppData%\FileLocker\settings.json`（`AppSettingsManager`，`Save` 走原子寫入），不會跳出任何畫面詢問使用者。
- **變更 Vault 路徑**：設定頁提供「瀏覽資料夾」按鈕，任何時候都能用，不限首次啟動。
- **搬移 Vault 路徑**：設定頁「搬移到新位置...」——同路徑或目的地非空資料夾都會被擋下（`VAULT_MOVE_SAME_PATH`／`VAULT_MOVE_DESTINATION_NOT_EMPTY`），成功後需要重新啟動 FileLocker 才會生效（不在同一個執行中的 App 裡熱替換正在使用的 `VaultManager`，避免跟進行中的加解密操作互相干擾）。若已設定過「關鍵操作驗證」，開始搬移前會先要求一次 Windows Hello（見第 8 節）。

### 11.2 雲端同步的運作模式

**核心概念：本工具本身不做雲端上傳，而是「把 Vault 資料夾指向使用者既有雲端同步軟體（OneDrive/Dropbox/Google Drive 等）的本機同步資料夾」，讓同步軟體去做它原本就會做的事。**

1. 使用者在設定頁把 Vault 路徑指定為例如 `C:\Users\X\OneDrive\FileLockerVault\`。
2. 加密流程完全不變：FileLocker 把 `{uuid}.enc` 與 `{uuid}.meta.json` 寫進這個資料夾。
3. 同步用戶端偵測到資料夾內容變化，自動把這些檔案上傳到雲端——**它上傳的是已經加密過的密文**，同步服務本身完全看不到明文內容，達到「零知識」的效果（跟 Cryptomator 的 Vault 概念相同：加密工具負責加密，同步工具負責搬運）。
4. 另一台裝置安裝 FileLocker、登入同一個雲端帳號，同步用戶端把整個 Vault 資料夾同步下來，App 啟動時掃描 Vault 重建「已加密清單」。
5. 使用者輸入密碼即可在任一台裝置上解密——**密碼本身不會、也不應該透過雲端同步**（只有驗證用雜湊會隨 `.meta.json` 一起同步，這是安全的，因為雜湊本身無法逆推回密碼）。

需要提醒使用者的限制：

- `.locked` 指標檔通常留在文件、桌面等一般資料夾，**不在** Vault 內，所以指標檔本身**不會**自動跨裝置同步。在 A 裝置加密的檔案，B 裝置的 Vault 會有對應的 `.enc`，但沒有那個 `.locked` 指標檔，要透過「已加密清單」頁面直接解密，而不是雙擊指標檔案。
- 若兩台裝置幾乎同時對**同一個** UUID 做操作，獨立 `.meta.json` 的設計能避免資料庫層級的損毀，但檔案本身仍可能出現同步軟體自己的「衝突副本」，這是雲端同步軟體的通用限制，非本工具能完全避免。

---

## 12. 安全性設計總覽

- **原始明文安全清除**：`SecureFileEraser.OverwriteAndDelete`，先用 `RandomNumberGenerator` 覆寫隨機資料（預設 1 pass）再刪除；SSD 上因為 wear-leveling 機制不保證能物理清除所有底層資料，這是合理範圍內的最佳努力，需在文件與 UI 中告知使用者。
- **密碼錯誤鎖定機制**：見第 9 節。
- **記憶體中金鑰清空**：`CryptographicOperations.ZeroMemory` 主動清除各處衍生出來的金鑰/明文緩衝區；密碼在 C# 中以 `string` 保存這件事本身有已知限制（`string` 不可變、無法像 byte 陣列一樣安全歸零），這是目前架構下接受的權衡，`SecureString` 已被棄用且跨平台不友善，不是更好的替代方案。
- **AES-GCM 例外處理接基底類別**：驗證失敗時 .NET 拋出的具體例外型別會隨 .NET 版本而不同。統一用 `catch (CryptographicException)` 這個基底類別去接、轉譯成「密碼錯誤或檔案已損毀」，不寫死接某個具體子類別，避免未來 .NET 版本更新後接不到、程式直接崩潰。
- **加密流程的「收尾清除」失敗不等於加密失敗**：原始明文的安全清除是內容已經安全寫入 Vault 之後的收尾動作，這一步失敗只回傳警告訊息（結果仍標記為成功）。
- **Vault 內容刪除順序**：永久刪除時先刪 `.meta.json`、後刪 `.enc`。中途中斷的最壞情況是留下一個沒人指向的孤兒 `.enc`（浪費空間但不會誤導），而不是留下一筆指向不存在內容的「幽靈」metadata。
- **Vault 相關檔案一律走原子寫入**：`vault.config.json`、`.meta.json`、`.locked` 指標檔、`settings.json` 都用「先寫暫存檔、成功後才原子改名」的方式寫入，避免程式中斷或雲端同步用戶端同時讀取時，讀到內容不完整的損毀檔案。
- **`.meta.json` 外洩風險**：即使只存驗證雜湊（非可逆），仍建議整個 Vault 資料夾用作業系統層級權限限制存取。

### 12.1 Vault 簽章金鑰的角色邊界

`vault.config.json` 裡的簽章金鑰用途**僅限於**驗證 `.locked` 指標檔沒有被竄改（HMAC-SHA256，見第 12.3 節）。這把金鑰**不是**、也**不能**拿來解密任何 `.enc` 檔案——每份被加密的檔案/資料夾，其真正的加密金鑰是在使用者輸入密碼當下即時衍生出來的，不會被儲存在任何地方（包括 `vault.config.json`），使用者忘記密碼一樣無法復原。也正因為這把「簽章金鑰」外洩最壞情況只是有心人能偽造出一個指向錯誤 UUID 的假指標檔（仍然需要正確密碼才能解密任何內容），所以它可以安全地跟著 Vault 一起用明文存放並隨雲端同步分享給所有裝置，不需要額外加密保護。

### 12.2 `.locked` 指標檔完整性（簽章機制）

用 HMAC-SHA256（`Crypto/MarkerSigner.cs`）。金鑰不是每台裝置各自隨機產生（那樣裝置 A 加密、裝置 B 因為雲端同步拿到 Vault 後會驗證失敗），而是在 Vault 初次建立時產生一把隨機的「Vault 金鑰」，存在 `vault.config.json` 中（跟著 `.enc`／`.meta.json` 一起同步）。`.locked` 指標檔內容 = `{Uuid, SignatureBase64}` 的 JSON，`SignatureBase64 = HMAC-SHA256(UUID, Vault金鑰)`；開啟指標檔時，程式讀取本機已知的 Vault 金鑰重新計算 HMAC 比對，不相符就代表指標檔被竄改過或指向錯誤的 UUID，中止流程並提示使用者。`LockedMarkerFile.ReadFrom` 額外驗證 `Uuid` 欄位必須是合法 GUID 格式（`Guid.TryParse`），防止竄改過的 UUID 字串被直接拿去拼 Vault 檔案路徑造成路徑穿越。這個機制的目的單純是「防止指標檔被誤改/竄改導致解到錯誤內容」，真正的存取控制仍然是由密碼 + Argon2 驗證雜湊把關，兩者職責分開。

### 12.3 密碼遺失情境

務必明確告知「密碼遺失=無法復原」，不做後門機制。

---

## 13. 前後端 IPC 協定

前端（Vue，透過 WebView2）與後端（C#，`MainWindow.xaml.cs`）之間只透過一份 JSON 訊息協定溝通：前端用 `window.chrome.webview.postMessage({type, ...})` 送出，後端 `CoreWebView2.WebMessageReceived` 統一接收後依 `type` 分派；後端用 `SendToFrontend(new {type, ...})`（`CoreWebView2.PostWebMessageAsJson`，`JsonNamingPolicy.CamelCase`）送回，前端在一個 `messageHandlers` 物件裡依 `type` 對應處理函式。拖放檔案是唯一的例外，走 `postMessageWithAdditionalObjects`（見第 14.1 節），因為需要夾帶 `CoreWebView2File` 物件、不是純 JSON 資料。

### 13.1 `useIpc.js` 封裝

`src/composables/useIpc.js`：

```js
sendMessage(type, payload = {})                          // fire-and-forget
requestMessage(requestType, responseType, payload = {})  // 回傳 Promise，用 responseType 當 key 存 resolver
resolvePending(responseType, data)                        // messageHandlers 裡對應的 handler 呼叫這個來 resolve
```

`requestMessage` 用「回應訊息的 type 字串」而非獨立 request id 當關聯鍵，代表同一個 `responseType` 同時間只能有一個進行中的請求——這是刻意的簡化假設，符合目前所有使用情境（不會同時發兩個「設定關鍵操作驗證」的請求）。

### 13.2 前端 → 後端訊息（`MainWindow.OnWebMessageReceived` case 對應表）

| 訊息 type | 處理方法 |
|---|---|
| `encrypt` | `HandleEncryptRequestAsync`（批次，串流回報進度） |
| `decrypt` / `decryptByUuid` / `decryptByPasskey` / `decryptByRecoveryKey` | 對應四條解密路徑 |
| `decryptBatch` | `HandleDecryptBatchRequestAsync`（密碼路徑批次） |
| `getPathSizes` | `HandleGetPathSizesRequestAsync` |
| `checkNestedLocks` | `HandleCheckNestedLocksRequestAsync` |
| `saveRecoveryKeyToFile` | `HandleSaveRecoveryKeyToFileRequest` |
| `inspectLockedFile` | `HandleInspectLockedFileRequest` |
| `getSettings` / `updateSetting` | `HandleGetSettingsRequest` / `HandleUpdateSettingRequest` |
| `setupCriticalAction` / `verifyCriticalAction` / `disableCriticalAction` / `clearHistory` | 見第 8 節 |
| `pickVaultFolder` / `changeVaultPath` | Vault 搬移流程 |
| `pickFile` / `pickFolder` | 原生檔案/資料夾選擇器 |
| `listVault` / `listHistory` | 清單頁兩個子頁籤 |
| `deleteRecord` / `verifyPasswordForDelete` | 永久刪除流程 |
| `windowMinimize` / `windowMaximizeToggle` / `windowClose` | 視窗控制，內嵌處理，不透過 Protocol 層 |
| `filesDroppedFromWebView` | 拖放檔案（見第 14.1 節） |
| 其餘未知 `type` | 記錄到 console，不中斷 |

任何處理過程中拋出例外都會被最外層 try/catch 接住，回傳 `{type:"error", message}`。

### 13.3 後端 → 前端訊息（`messageHandlers` 對應表，節錄關鍵分組）

- **加密**：`encryptBatchStarted` / `encryptItemResult` / `encryptBatchDone`
- **解密**：`decryptResult` / `decryptByUuidResult` / `decryptByPasskeyResult` / `decryptByRecoveryKeyResult` / `decryptBatchStarted` / `decryptBatchItemResult` / `decryptBatchDone`
- **清單/設定**：`vaultList` / `vaultChanged` / `historyList` / `settingsResult` / `updateSettingResult` / `changeVaultPathResult`
- **關鍵操作驗證**：`setupCriticalActionResult` / `verifyCriticalActionResult` / `disableCriticalActionResult` / `clearHistoryResult`
- **其餘**：`pathPicked` / `pathsPicked` / `pathPickCancelled` / `pathSizesResult` / `nestedLockCheckResult` / `saveRecoveryKeyToFileResult` / `inspectLockedFileResult` / `deleteRecordResult` / `verifyPasswordForDeleteResult` / `windowStateChanged` / `filesDropped` / `initialPaths`（第二個執行個體把路徑轉交過來時用）/ `error`

`pathSizesResult`／`nestedLockCheckResult`／`setupCriticalActionResult`／`verifyCriticalActionResult`／`disableCriticalActionResult` 這幾個單純是 `resolvePending()` 的傳遞站，實際邏輯在呼叫端的 `await requestMessage(...)` 之後。

### 13.4 `VaultProtocolHandlers`：Protocol 層完整介面

這一層是 `MainWindow` 與 `LockService`/`VaultManager` 之間的轉譯層，本身不依賴任何 WPF/WebView2 型別，可以直接單元測試：

```csharp
IAsyncEnumerable<EncryptItemResponse> EncryptBatchAsync(IReadOnlyList<string> paths, string password, string? hint, bool enablePasskey, bool enableRecoveryKey, IntPtr ownerWindowHandle)
Task<UnlockResult> DecryptAsync(string lockedMarkerPath, string password)
Task<UnlockResult> DecryptByUuidAsync(string uuid, string password, string? destinationDir)
Task<UnlockResult> DecryptByPasskeyAsync(string uuid, IntPtr ownerWindowHandle, string? destinationDir)
Task<UnlockResult> DecryptByRecoveryKeyAsync(string uuid, string recoveryKeyInput, string? destinationDir)
IAsyncEnumerable<DecryptBatchItemResponse> DecryptBatchAsync(IReadOnlyList<string> uuids, string password)
InspectLockedFileResponse InspectLockedFile(string path)
Task<IReadOnlyList<PathSizeInfo>> GetPathSizesAsync(IReadOnlyList<string> paths)
Task<int> CheckNestedLockCountAsync(IReadOnlyList<string> paths)
SettingsResponse GetSettings()
bool IsCriticalActionConfigured { get; }
Task<bool> SetupCriticalActionAsync(IntPtr ownerWindowHandle)
Task<bool> VerifyCriticalActionAsync(IntPtr ownerWindowHandle)
Task DisableCriticalActionAsync()
void ClearHistory()
UpdateSettingResponse UpdateSetting(string key, string value)
Task<ChangeVaultPathResponse> ChangeVaultPathAsync(string newPath)
Task<IReadOnlyList<VaultListItemResponse>> ListVaultAsync()
IReadOnlyList<HistoryListItemResponse> ListHistory()
Task<DeleteRecordResult> DeleteRecordAsync(string uuid)
Task<VerifyPasswordResult> VerifyPasswordAsync(string uuid, string password)
```

`ListVaultAsync` 會用 `VaultIndexCache.GetItems()` 當資料來源、自我修復孤兒快取列，逐項檢查即時的指標檔狀態（`MarkerStatusChecker`），用 `AsParallel()` 平行化，依建立時間新到舊排序。`DeleteRecordAsync` 把「查無此紀錄」也當成功處理（自我修復孤兒快取項目）。

---

## 14. GUI 設計

前端技術：HTML + CSS + WebView2 host + Vue 3（Composition API），Vite 建置，透過第 13 節的訊息協定跟後端溝通。

### 14.1 視窗外觀（原生 WPF 層，`MainWindow.Chrome.cs`）

主視窗是 macOS 風格的無邊框視窗：`WindowStyle="SingleBorderWindow"`（不用 `WindowStyle="None"`，見下方「最大化動畫」）+ 自己在 `WndProc` 攔截 `WM_NCCALCSIZE`／`WM_NCHITTEST`／`WM_GETMINMAXINFO`，標題列由 HTML 端自己畫，左上角紅黃綠三顆按鈕。

- **拖曳/縮放**：不用 JS 追游標位置自己實作（會抖動）——用 WebView2 的 `IsNonClientRegionSupportEnabled` + CSS `app-region: drag`，把拖曳交給作業系統原生處理。
- **WebView2 吃光邊緣縮放事件**：WebView2 內部是獨立的原生子視窗，會把邊緣縮放需要的滑鼠事件整個截走（微軟已確認的已知 bug，MicrosoftEdge/WebView2Feedback#4538）。解法：`Margin="6"` 給 WebView2 控制項留一圈真正的 WPF 空間，縮放偵測才抓得到，代價是視窗邊緣有一條窄窄的實色邊，顏色跟著深色模式同步變化。
- **圓角**：`DwmSetWindowAttribute`（`DWMWA_WINDOW_CORNER_PREFERENCE=33`, `DWMWCP_ROUND=2`）手動要回 Windows 11 原生圓角；`DllNotFoundException` 靜默吞掉，當作 Windows 10 的預期後備行為。
- **`WM_GETMINMAXINFO`**：修正 WPF 內建的最大化邊界計算沒扣掉「隱形縮放邊框」導致跟工作列/第三方 Dock 工具重疊的問題，改用 `GetMonitorInfo` 工作區重新計算；攔截這個訊息會讓 WPF 內建最小尺寸限制整個失效，必須自己把 `Window.MinWidth`／`MinHeight` 換算回 `MinTrackSize` 補回去。
- **最大化/還原動畫**：保留原生樣式（`WS_CAPTION`／`WS_THICKFRAME`）讓 DWM 把這個視窗當一般可動畫視窗看待，`WM_NCCALCSIZE` 把非客戶區視覺上收縮到 0（有樣式、但畫面上完全看不到），`WM_NCHITTEST` 只處理縮放邊界判斷（不搶 `HTCAPTION`，那個交給 WebView2 的 `IsNonClientRegionSupportEnabled`）——這是 VS Code／Windows Terminal／Chromium 在 Win32 上做無邊框視窗的標準手法。

**拖放檔案支援**：不透過 WPF 層級的原生拖放（同一種 airspace 問題，WebView2 的原生子視窗會把整個拖放操作攔死）。JS 端接住 HTML5 `drop` 事件，用 `chrome.webview.postMessageWithAdditionalObjects` 把 `File` 物件連同訊息送到 C# 端，C# 收到 `CoreWebView2File`，讀 `.Path` 屬性拿到真正磁碟路徑（一般網頁的 `File` 物件拿不到真正路徑，這是 WebView2 專門為原生桌面 App 開的口子）。拖放進來的路徑合併進加密頁籤現有清單（去重複），不是整份取代。

**單一執行個體**：`App.xaml.cs` 用具名 `Mutex` 判斷是否為第一個執行個體；第二個（含右鍵動作、雙擊 `.locked` 檔案等各種再次啟動情境）一律只把命令列參數透過 Named Pipe 轉交給既有執行個體（`HandleLaunchArgs`／`MainWindow.ApplyIncomingPaths`）後自己結束，不開任何視窗。有兩個曾經修過的坑：

- **釋放不屬於自己的 Mutex 會讓轉送行程崩潰**：第二個執行個體從未真正持有這個 Mutex（`Mutex(true, ...)` 的 `initiallyOwned` 只有在真的建立新 Mutex 時才生效），`OnExit` 若無條件呼叫 `ReleaseMutex()` 會在轉送完參數、正要結束的瞬間丟出未處理例外把整個轉送行程弄崩潰——外部看起來就是「右鍵動作完全沒反應」。現在用一個欄位（`_ownsSingleInstanceMutex`，只有真正的第一個執行個體才是 `true`）判斷要不要釋放。
- **背景執行個體搶不到前景焦點**：既有視窗 `Activate()` 內部就是呼叫 Win32 `SetForegroundWindow`，但 Windows 有「防止搶焦點」機制——呼叫端行程如果不是目前的前景行程，這個 API 會被系統直接忽略，畫面上完全沒反應。負責轉送參數的第二個執行個體是 Explorer 因為使用者剛剛的滑鼠點擊直接產生的，本身握有前景權限，轉送完參數後呼叫 `AllowSetForegroundWindow(ASFW_ANY)` 把這個權限短暫開放出來，第一個執行個體接下來呼叫的 `Activate()` 才能真的把視窗搶到最上面。

**開發／正式環境切換**：`#if DEBUG` 導到 `http://localhost:5173/`（Vite 開發伺服器）；Release 用 `SetVirtualHostNameToFolderMapping("filelocker.local", webAppFolder, Deny)` 導到打包進 `webapp/` 資料夾的靜態檔案，網址固定 `https://filelocker.local/index.html`。`NavigationStarting` 有網址白名單檢查，非允許來源一律 `Cancel`；`NewWindowRequested` 一律 `Handled=true`，擋掉所有 `window.open()`／`target="_blank"` 彈出視窗。

### 14.2 設計系統

色彩、字體、動效全部走 CSS 自訂屬性：`--color-bg/surface/border/border-strong/text/text-secondary/text-tertiary/accent/accent-hover/accent-soft/accent-border/success/success-soft/danger/danger-soft`、`--font-ui`（`'IBM Plex Sans', -apple-system, 'Segoe UI', sans-serif`）、`--font-mono`（IBM Plex Mono）、`--radius-sm/md/lg`、`--shadow-xs/sm/md/modal`、`--ease-out: cubic-bezier(0.23, 1, 0.32, 1)`、`--duration-fast: 150ms` / `--duration-base: 200ms`。深色模式（`.app--dark`，套在根節點）整組覆蓋一次變數值即可，其他樣式規則不用重寫；強調色深色模式下從 `#A8770F` 調亮到 `#D9A83B` 維持對比度。

- **色彩**：扣著「鎖與鑰匙」主題發想，冷灰藍背景 + 深墨黑文字（非純黑）+ 黃銅色作為唯一強調色，跟常見的科技產品藍色調拉開距離。
- **字體**：`IBM Plex Sans`（介面）+ `IBM Plex Mono`（恢復金鑰、路徑等技術性內容），透過 `@fontsource` npm 套件把字體檔案直接包進專案，不連網路抓 Google Fonts（維持 App 完全離線運作）。
- **層次**：內容貼齊視窗邊緣只留內距，整個視窗是同一個表面色，不做「灰色背景中央飄浮一張白色卡片」的網頁式排版；分組靠留白節奏跟局部陰影/分隔線，不靠外層包一個框。
- **按鈕層級**：`.button--primary`（強調色底）／`.button--secondary`（表面色底+邊框）／`.button--danger`（危險色底）／`.link-button`（無邊框無底色，最低份量，用於次要/退出類動作，例如恢復金鑰模式的「返回使用密碼」）。
- **動效**：分頁切換（加密/解密/已加密清單/設定）是高頻率操作，故意只用純透明度淡入淡出（120ms，`mode="out-in"`），不加位移；加密精靈的步驟切換是偶爾、慎重的操作，用有方向性的位移滑動（`--duration-base` + `--ease-out`，下一步往左、上一步往右）；清單列進場用純 CSS `@keyframes`（280ms，`backwards` fill，只在真正插入 DOM 時觸發一次）；全站支援 `prefers-reduced-motion`。
- **內容寬度**：表單類頁籤（加密/解密/設定）維持適中寬度（760px）；已加密清單頁（表格內容）寬度上限放寬到 1180px，跟著視窗寬度伸展，切換頁籤時寬度變化有過渡動畫。
- **長文字處理**：長路徑/長檔名一律截斷成一行 + 刪節號，滑鼠移上去用原生 `title` 屬性顯示完整內容。

### 14.3 主視窗四個頁籤

- **加密**：三步驟精靈（`encryptStep` 1→2→3）。
  - **步驟一（選項目）**：多選檔案對話框 + 可重複點「選擇資料夾」加入，或拖放。還沒選項目時是佔滿版面的拖放卡片（虛線框，同時是空狀態跟拖放目標）；已選取時改實線邊框清單卡片；選超過一個項目時 Passkey／恢復金鑰勾選框自動鎖住（見第 4.5 節）。
  - **步驟二（密碼與選項）**：密碼＋確認密碼、提示、Passkey／恢復金鑰勾選。送出後先問一次每個項目的檔案大小（`getPathSizes`），估算耗時跑一個前快後慢（`1 - Math.pow(1-t, 2.2)`）、只逼近 92% 的進度條動畫，真正完成時才補滿；含資料夾的批次會先顯示「壓縮中」再切「加密中」；下方即時顯示每個項目的加密結果。
  - **步驟三（完成頁）**：批次真正結束後才切過來，顯示最終結果清單＋「完成」按鈕，使用者主動按下才回到步驟一並清空表單。
- **解密**：手動選 `.locked` 檔案 + 輸入密碼（Enter 直接送出）；選檔後自動查詢是否支援 Passkey／恢復金鑰快速解鎖；還原位置固定用 `.locked` 檔案目前所在的資料夾。
- **已加密清單**：兩個子頁籤：
  - **已加密檔案**：讀取 Vault 目前實際存在的項目，可用密碼／Passkey／恢復金鑰個別解鎖；「永久刪除」獨立成表格每列最前面一欄的圖示按鈕，點下去要求重新輸入密碼（`VerifyPasswordAsync`，見第 4.4 節）驗證通過後再跳最終確認彈窗，兩層都通過才真的送出刪除。同一批次加密的項目摺疊成一組。第一次載入顯示骨架畫面（最短顯示 300ms 避免資料回來太快一閃而過）。
  - **使用紀錄**：本機操作日誌（`history.jsonl`），跟 Vault 目前狀態無關，項目就算已經解密或刪除，紀錄仍然保留；可設定「關鍵操作驗證」後清除（見第 8 節）。
- **設定**：Vault 位置顯示＋搬移按鈕（含第 8 節的條件式驗證）；語言下拉；主題按鈕；關鍵操作驗證區塊（設定/重新設定/停用）；使用說明按鈕（彈窗，分「基本操作」「運作原理」「注意事項」「關鍵操作驗證」四段）。

### 14.4 密碼輸入小視窗（`PasswordPromptWindow`）

原生 WPF、不透過 WebView2（讓視窗盡快跳出來，不需要載入整個瀏覽器核心），雙擊 `.locked` 檔案時跳出。若該項目有啟用 Passkey，視窗一開啟就自動觸發 Windows Hello 驗證（見第 6.3 節的焦點處理）；使用者把驗證視窗關掉才會退回密碼輸入畫面，同時保留「使用 Passkey 解鎖」按鈕讓使用者可以重試。

無邊框（`WindowStyle="None"` + `DwmSetWindowAttribute` 圓角 + WPF 原生 `DragMove()`），技術做法比主視窗簡單——`ResizeMode="NoResize"`、沒有最大化功能，不需要比照主視窗攔截 `WM_NCCALCSIZE`／`WM_NCHITTEST`。

**字體**：內嵌 IBM Plex Sans TC（`Assets/Fonts/`，只保留 Regular／SemiBold／Bold 三個實際用到的字重，`.csproj` 以 `<Resource>` 嵌入）——這是網頁端 IBM Plex Sans 的正體中文版本，同一個字型家族，且 Regular 檔案本身內建完整拉丁字母，英數字也直接吃這套字型，不需要再靠 Segoe UI 補西文；Segoe UI／Microsoft JhengHei UI 留在 fallback 清單最後面當保底。SemiBold 字重有自己獨立的字型家族名稱「IBM Plex Sans TC SmBld」（不是「...SemiBold」，Regular／Bold 兩個檔案才共用「IBM Plex Sans TC」這個家族名稱），需要在對應的 `TextBlock` 上明確指定 `FontFamily`，不能只靠 `FontWeight="SemiBold"` 隱性配對。標題文字（檔名）與網頁端彈窗標題（18px／SemiBold）對齊字級字重手感；密碼輸入框明確釘住高度（跟下方 Passkey／恢復金鑰按鈕一致的 42px），避免 CJK 字型較高的行高 metrics 把整個欄位連同遮罩點點一起撐大。

### 14.5 自訂互動元件（取代原生瀏覽器對話框）

原生的 `alert()`／`confirm()`／`prompt()` 在桌面應用程式裡會顯示瀏覽器痕跡，全部改用自訂元件：

- **通知（取代 `alert()`）**：畫面右下角的通知卡片，成功/失敗各自有對應圖示跟顏色，6 秒自動消失。
- **確認對話框（取代 `confirm()`）**：`askConfirm(message, options)` 回傳 Promise，`options` 支援 `confirmLabel`／`variant`（`'default'|'danger'`）／`confirmIconUrl`（用於清除紀錄流程的 Passkey 圖示按鈕，見第 8.2 節）。
- **三選一對話框**：例如「還原到原始位置」還是「自己選位置」，兩個各自標明意圖的按鈕直向堆疊，真正的取消是點背景或按 Esc。
- **密碼輸入彈窗**：遮罩密碼欄位，template ref + `nextTick` 手動聚焦（原生 `autofocus` 對 Vue 動態插入元素不可靠）；同時支援單一解密、批次解密、永久刪除前密碼再驗證三種模式。
- **全部彈窗支援 Esc 關閉**——**恢復金鑰顯示彈窗刻意排除在外**（見第 7.2 節）。

### 14.6 恢復金鑰顯示彈窗：整個 App 的簽名視覺元素

刻意跟其他畫面拉開視覺差異——整個 App 裡風險最高、最需要使用者專注的一刻。使用者自製的蠟封圖示（`Locked_Wax_Seal.svg`，黃銅色）疊在彈窗左上角、明顯超出邊界；標題獨立一行、字級放大、字距收緊；恢復金鑰本身用等寬字體加大字距顯示在虛線框裡。

**螢幕截圖保護不做**：技術上不可行——連截圖都截不到的技術，靠的是作業系統層級的 DRM 硬體保護路徑，只對授權的加密影音內容開放，一般網頁/App 顯示的文字內容用不到這個機制。「一次性顯示 + 不留任何副本」的設計本身才是真正有意義的保護。

### 14.7 圖示

Passkey／恢復金鑰／主題切換／巢狀鎖定／警示，全部用使用者自製的 SVG 圖示，依目前是亮色還是深色模式，用 Vue computed 自動切換對應的黑/白線條版本（`Passkey_Black/White.svg`、`Recovery_Key_Black/White.svg`、`Light_Mode_Black/White.svg`、`Dark_Mode_Black/White.svg`、`Lock_Light/Dark.svg`、`Warning_Light/Dark.svg`），`Locked_Wax_Seal.svg` 只有單一版本（固定用在恢復金鑰彈窗）。

App 圖示（工作列/Alt+Tab）：純平面白色鑰匙孔圖形 + 黃銅色圓角方塊底，已匯出 `.ico` 多解析度格式並在 `FileLocker.App.csproj` 用 `<ApplicationIcon>` 接進去，已生效。`.locked` 副檔名圖示（同樣的黃銅色蠟封鎖頭造型）已隨安裝程式接入檔案關聯設定，見第 16.4 節。

---

## 15. CLI 介面（`FileLocker.Cli`）

已隨安裝程式一起發布並加入系統 PATH，見第 19 節。

```
FileLocker.Cli --encrypt <路徑1> [路徑2 ...]
FileLocker.Cli --unlock <.locked 路徑1> [路徑2 ...]
FileLocker.Cli --unlock-recovery <uuid> <恢復金鑰> [還原目的地資料夾]
FileLocker.Cli --list
FileLocker.Cli --delete <uuid1> [uuid2 ...]
```

- `--encrypt`／`--unlock`／`--delete` 都支援一次傳多個路徑或 uuid：密碼（或刪除確認）只問一次，套用到所有項目，個別項目的成功/失敗各自列出，結尾印一行「N 筆成功、M 筆失敗」（只有多筆時才印）。內部邏輯跟 GUI 端的批次加密（`EncryptBatchAsync`）同一套：項目數 > 1 才產生 `batchId`。
- `--unlock-recovery` 維持單筆——uuid + 恢復金鑰是一對一綁定，沒有自然的批次意義。
- **環境變數 `FILELOCKER_VAULT_PATH`**：覆寫預設 Vault 位置，未設定時跟 GUI 主程式共用同一個預設路徑（`%LocalAppData%\FileLocker\Vault`），方便無 GUI 環境（排程工作、遠端伺服器）指到跟主程式相同或不同的 Vault。
- **密碼輸入**：`Console.IsInputRedirected` 時（管線/排程輸入）退回不遮罩的 `Console.ReadLine()`（`Console.ReadKey` 在輸入重新導向時會直接丟例外）；一般互動情境逐字元讀取、顯示 `*`、支援 Backspace。
- **不用 `VaultIndexCache`**：CLI 每次執行都是全新短命的行程，沒有常駐的 `FileSystemWatcher` 保持快取最新，直接呼叫 `VaultManager.ScanAll()` 全量掃描（慢一點但保證即時正確）——實測過如果用快取，加密完馬上下一次 `--list` 會看不到剛加密的項目。
- **Passkey 不在 CLI 提供**：`KeyCredentialManager` 一定會跳出 Windows Hello 系統 UI，這跟「無 GUI 環境可操作」的存在目的直接衝突；之後如果要支援，應該是另一個獨立指令，不是塞進 `--encrypt` 裡。

---

## 16. 資源管理器整合機制

### 16.1 為什麼批次（多選）加密需要正規 Shell Extension

Windows 的輕量「Registry 動詞註冊」在使用者**多選檔案**按右鍵時，命令列的 `%1` 只會帶入其中一個檔案路徑，無法拿到完整選取清單。要支援「一次選多個檔案/資料夾加密」，必須實作標準的 **COM `IContextMenu` Shell Extension**，透過 `IDataObject`／`CF_HDROP` 取得完整的多選清單。

### 16.2 Shell Extension（`FileLockerShellExtension.dll`）

C++（`dllmain.cpp`），CLSID `{A1B2C3D4-E5F6-4789-9ABC-DEF012345678}`。實作 `IShellExtInit`／`IContextMenu`：

- `Initialize`：解析 `CF_HDROP`（`DragQueryFileW` 迴圈）取得完整多選路徑清單。
- `QueryContextMenu`：插入單一選單項目「使用 FileLocker 加密」（`CMF_DEFAULTONLY` 時跳過）。
- `InvokeCommand`：解析 `FileLocker.App.exe` 路徑（跟 DLL 同資料夾），組裝命令列並用 `CreateProcessW` 啟動。
  - **交接方式**：估計命令列長度 > 8000 字元、或選取項目 > 50 個，改成把完整路徑清單寫進一個暫存 `.txt` 檔（`GetTempFileNameW`，UTF-16LE with BOM），只把這個 txt 檔路徑當成單一命令列參數（`@<tempfile>`）傳給主程式；未超過門檻則每個路徑各自加引號（完整的 MS 文件記載的跳脫演算法，不是天真的加引號）當命令列參數傳遞。

這個 DLL 會被直接載入到 `explorer.exe` 這個行程裡面執行（不是獨立行程），加密邏輯、GUI 邏輯、資料庫全部不碰，全部交給 `FileLocker.exe`（一個獨立的一般行程）處理，把風險較高、除錯較麻煩的程式碼範圍縮到最小。

### 16.3 右鍵選單登錄：主要機制在 App 端自我註冊，不是安裝程式

**實際負責登錄的是 `FileLocker.App` 裡的 `ShellExtensionRegistrar.cs`（C#），不是 Shell Extension DLL 自己的 `DllRegisterServer`**（那個 C++ 匯出函式只會登記 `*`，是給手動 `regsvr32` 測試用的次要/備用路徑，實務上不是主要機制）。`ShellExtensionRegistrar.EnsureRegistered()` 在 App 每次啟動時呼叫：

- 檢查 DLL 是否存在於 `AppContext.BaseDirectory`（開發階段常見還沒編譯/複製過去，安靜跳過，不當錯誤）。
- 比對登錄的 DLL 路徑是否跟目前路徑一致、且 `*` 與 `Directory` 兩個 `shellex\ContextMenuHandlers\FileLocker` 鍵都存在且指向正確 CLSID，兩者皆符合才視為「已完整註冊」，否則自動（重新）寫入。
- 寫入位置：`HKEY_CURRENT_USER\Software\Classes\CLSID\{clsid}\InprocServer32`（DLL 路徑＋`ThreadingModel=Apartment`）、`HKEY_CURRENT_USER\Software\Classes\*\shellex\ContextMenuHandlers\FileLocker`、`HKEY_CURRENT_USER\Software\Classes\Directory\shellex\ContextMenuHandlers\FileLocker`——**`*` 只涵蓋檔案，不含資料夾，兩個都要登記右鍵選單才會同時對檔案跟資料夾出現**（這是實際修過的 bug：早期只登記 `*`，導致右鍵資料夾看不到加密選項）。
- 全部寫在 `HKEY_CURRENT_USER` 底下，不是 `HKEY_CLASSES_ROOT`／`HKEY_LOCAL_MACHINE`——每個使用者各自登錄的官方支援機制，Explorer 會自動把它併進當前使用者看到的 `HKEY_CLASSES_ROOT` 合併視圖，效果相同但不需要系統管理員權限。
- 回傳值代表「這次真的執行了註冊動作」，呼叫端依此決定要不要提示使用者重啟 Explorer 讓右鍵選單生效。

這個設計的好處：**安裝程式完全不需要知道任何 COM／regsvr32 相關的事**，只要把編譯好的 `FileLockerShellExtension.dll` 跟 `FileLocker.App.exe` 放在同一個資料夾（一般的「應用程式內容資料夾」功能就夠了）；已經裝過舊版（登錄不完整）的使用者，下次啟動也會自動偵測缺漏並補上，不需要手動重裝。

### 16.4 `.locked` 副檔名關聯（已完成，由安裝程式設定檔處理）

跟第 16.3 節的右鍵選單（App 自我註冊）不同，`.locked` 副檔名關聯是**安裝當下由安裝程式一次性建立**的機器層級關聯，不是 App 每次啟動自我檢查的東西——這類關聯天生就該在安裝／解除安裝時成對建立/清除，不需要 App 執行期反覆確認。實際做法是在 `installer_config.json`（mac-style-windows-installer 讀取的設定檔）裡宣告：

```json
{
    "file_associations": [".locked"],
    "doc_icon": "doc_icon.ico"
}
```

安裝程式據此建立標準的副檔名關聯（雙擊 `.locked` 檔案 = 執行 `FileLocker.App.exe`，帶檔案路徑當參數，跟第 4.3 節 `DecryptAsync` 的觸發路徑一致），並把 `doc_icon.ico`（黃銅色蠟封鎖頭造型，見第 14.7 節）設成該副檔名的圖示。

### 16.5 32/64 位元

現代 Windows 的 Explorer 是 64 位元行程，DLL 需要編譯成 64 位元版本；程式碼裡沒有另外的 32 位元特別處理邏輯。

---

## 17. 多語言

支援繁體中文（`zh-TW`）／英文（`en`）。`src/FileLocker.Web/src/locales/` 底下 `zh-TW.json`、`en.json` 兩份語言包（各 211 個扁平 key，一一對應），`App.vue` 內建 `t(key, params)` 翻譯函式（找不到對應語言檔或找不到 key 時退回繁體中文，再找不到就直接顯示 key 本身，方便開發時發現漏翻的字串），前端全部靜態文字都走 `t()` 呼叫。語言選擇存在 `AppSettings.Language`，設定頁下拉即時切換；App 啟動時就會主動要一次設定值套用語言，不等使用者點進設定頁才生效。

**主要 key 命名空間**：`tab`／`encrypt`／`decrypt`／`list`／`history`／`confirm`／`settings`／`recoveryKeyModal`／`passwordPrompt`／`recoveryKeyPrompt`／`alert`／`error`／`help`／`window`／`choice`／`confirmDialog` 等。

**後端錯誤代碼系統**：`LockResult`／`UnlockResult`／`DeleteRecordResult`／`ChangeVaultPathResponse` 都有 `ErrorCode`（固定英文代碼，見 `FileLocker.Core.Models.ErrorCodes`，共 32 個常數，涵蓋密碼錯誤、密碼鎖定中、找不到紀錄、Passkey／恢復金鑰各種失敗情境、還原目的地已有同名檔案/資料夾、加密內容損毀、搬移 Vault 失敗、存恢復金鑰檔案失敗等）跟 `ErrorDetail`（代碼裡要內嵌的動態內容，本身不翻譯直接嵌進句子範本，例如鎖定剩餘秒數由後端提供原始數字、格式化交給前端依語言處理）兩個欄位，`ErrorMessage` 保留固定繁體中文文字當後備。前端 `translateError(errorCode, errorDetail, fallbackMessage)` 函式查 `error.{errorCode}` 這個 key，查不到就退回顯示原本的 `fallbackMessage`。

語言選單裡每個語言的選項名稱，固定用該語言自己的名稱顯示（例如「繁體中文」不會因為目前介面是英文就被翻譯成「Traditional Chinese」），這是語言選擇器的標準慣例。

---

## 18. 版本相容性策略（以「不需要額外相容性程式碼」為原則）

- **後端執行環境**：.NET 10，使用 `self-contained` 發布模式打包執行環境進安裝檔，使用者電腦不需要另外安裝 .NET Runtime。
- **前端執行環境**：WebView2 Runtime。Windows 11 已內建；Windows 10 透過近年的 Edge 更新幾乎必然已內建。若真的偵測不到，直接使用官方的 WebView2 Bootstrapper 自動補裝。
- **最低系統需求建議**：Windows 10 1809 以上（WebView2 支援下限）。不特別支援 Windows 7/8。
- **COM Shell Extension** 需要區分 32/64 位元 DLL 註冊，這是 Windows Shell Extension 機制本身的硬性要求，安裝程式會依系統架構自動選擇對應 DLL。

---

## 19. 安裝程式打包

**已完成並可用**。最終編譯完成的 `FileLocker.App.exe`（含 C# 主程式 + Shell Extension DLL）打包成安裝檔，沿用既有的 **[mac-style-windows-installer](https://github.com/Lai-xuan/mac-style-windows-installer)** 專案，透過 `installer_config.json` 宣告安裝內容（`app_name`／`main_exe`／`file_associations`／`doc_icon`／`dependencies`／EULA 文字等，見第 16.4 節），不需要另外寫安裝腳本邏輯。

安裝流程已對接的項目：主程式與 Shell Extension DLL（放同一資料夾，見第 16.3 節，安裝程式不需要處理任何 COM 登錄邏輯，App 啟動時自我註冊）、`.locked` 副檔名關聯與圖示（見第 16.4 節，`installer_config.json` 的 `file_associations`／`doc_icon`）、`.NET Desktop Runtime` 相依套件偵測安裝（`dependencies: ["dotnet_desktop"]`）、解除安裝程式（`uninstall.exe`）、安裝清單（`install_manifest.json`，供解除安裝時精確比對要移除哪些檔案）。安裝完成後的資料夾內容即為第 22 節「軟體更新檢查」下載回來的更新包會覆蓋的同一份結構。

**CLI 打包**：`FileLocker.App.csproj` 新增三個 Release-only MSBuild Target（`BuildCli`／`CopyCliForRelease`／`AddCliToPublishOutput`，比照 §16 前端 `webapp/` 那組模式），把 `FileLocker.Cli` 另外建置後複製進輸出目錄下的獨立 `cli/` 子資料夾，`dotnet build` 跟 `dotnet publish` 兩種輸出都涵蓋到。放獨立子資料夾（不跟 GUI 混在同一層）是因為 CLI 有自己一份完整的相依 DLL，且搭配安裝程式新增的 `path_target_exe` 欄位——`installer_config.json` 另外加上：
```json
"add_to_path": true,
"path_target_exe": "cli\\FileLocker.Cli.exe"
```
只把 `cli/` 這一層加進系統 PATH，不會把 GUI 那堆 DLL 所在的安裝根目錄整個暴露進使用者的 PATH。

**沒有數位簽章**：使用者第一次執行安裝檔，Windows SmartScreen 大機率會跳警告，要解決需要另外採購程式碼簽署憑證，這不是安裝程式工具本身能解決的事。這也代表目前完全沒有偵測執行檔本身是否被竄改的機制——數位簽章除了消除 SmartScreen 警告，更重要的作用是讓 Windows 能自動驗證執行檔完整性，這是業界標準做法，但需要外部採購憑證的商業流程。「程式自己在啟動時檢查自身雜湊值」這種做法評估後不採用：攻擊者只要能竄改執行檔內容，同樣能連檢查邏輯本身一起改掉，只能擋住意外損毀、擋不住真正有心的竄改，容易給人錯誤的安全感。

---

## 20. 開發進度總覽

| 項目 | 內容 | 狀態 |
|---|---|---|
| Core Engine | 檔案/資料夾加密解密邏輯、UUID 機制、Argon2id+AES-256-GCM+ChunkedCipher、Zip 封裝、單元測試 | 完成 |
| Passkey／恢復金鑰 | Windows Hello 內容金鑰包裝、Base32 恢復金鑰生成/解析 | 完成 |
| 關鍵操作驗證 | 設定/停用（自身受驗證保護）、清除紀錄強制驗證、搬移 Vault 條件式驗證 | 完成 |
| Metadata 層 | `.meta.json` 讀寫、SQLite 本機快取索引、`FileSystemWatcher` 即時監控 | 完成 |
| CLI | `--encrypt`／`--unlock`／`--unlock-recovery`／`--list`／`--delete`，支援批次操作（多路徑/uuid）、`FILELOCKER_VAULT_PATH` 環境變數與管線輸入 | 完成（不涵蓋 Passkey，設計上刻意排除） |
| WebView2 + Vue 3 前端 | 4 個主頁籤、密碼視窗、Vault 位置設定（瀏覽資料夾/搬移）、GUI 視覺美化（無邊框視窗、設計系統、深色模式、拖放檔案、進度條、動效細節）、IPC 協定 | 完成 |
| Shell Extension（C++ 最小化元件） | 右鍵選單、多選批次支援、資料夾防護上鎖/解鎖兩個命令 id | 完成 |
| Shell Extension 自動註冊 | App 啟動時自我檢查/註冊（`*` + `Directory` 加密選單、資料夾防護命名空間 CLSID），不需安裝程式處理 | 完成 |
| `.locked` 副檔名關聯 | 由安裝程式 `installer_config.json` 的 `file_associations` 處理 | 完成 |
| App／`.locked` 圖示 | 設計定案、匯出 `.ico`、接進專案 | 完成（App 圖示接進 `.csproj`；`.locked` 圖示接進安裝程式 `doc_icon`） |
| 安全性強化 | 安全清除、密碼鎖定機制、指標檔簽章驗證、Vault ACL 硬化 | 完成並測試 |
| 密碼小視窗字型 | 內嵌 IBM Plex Sans TC，跟網頁端字型家族一致 | 完成 |
| 多語言 | 前端靜態文字、後端錯誤代碼（含搬移 Vault／存恢復金鑰失敗訊息） | 完成 |
| 雲端同步情境測試 | 模擬多裝置同步、衝突情境 | 自動化測試完成；跨裝置人工實測待使用者自行進行 |
| 資料夾防護（Folder Guard） | 純 ACL 資料夾存取限制（不加密）、共用密碼＋選配 Passkey、右鍵上鎖/解鎖、清單頁管理，見第 21 節 | 完成 |
| 資料夾防護：雙擊已上鎖資料夾直接解鎖 | `.lockfolder` 標記檔＋檔案關聯技術路線（取代已放棄的 Shell Namespace Extension） | 邏輯完成、單元測試通過，實際雙擊互動待人工實測，見第 21.6 節 |
| 軟體更新檢查 | 設定頁一鍵檢查 GitHub Release、下載安裝檔並啟動，見第 22 節 | 完成 |
| 打包安裝程式 | 對接 mac-style-windows-installer，含 `.locked` 檔案關聯、圖示接入 | 完成，見第 19 節 |
| CLI 隨裝發布 | `FileLocker.Cli` 打包進 `cli/` 子資料夾，安裝程式透過 `path_target_exe` 加入系統 PATH | 完成，見第 19 節 |

---

## 21. 資料夾防護（Folder Guard）

獨立於「加密」之外的第二種保護機制：**不加密內容**，純粹透過 Windows ACL 拒絕目前登入帳號對某資料夾的存取權，資料夾原地保留、不搬動、不需要提權。定位是「防隨手瀏覽」，不是「防蓄意繞過」——完整的威脅模型與機制取捨推理見 [`docs/adr/0001-folder-guard-deny-acl-not-ownership-transfer.md`](docs/adr/0001-folder-guard-deny-acl-not-ownership-transfer.md)；設計訪談的原始逐項紀錄見 [`FileLocker_資料夾防護_功能規劃.md`](FileLocker_資料夾防護_功能規劃.md)（規劃文件，現已實作完成，本節是併入後的目前狀態說明）。

跟「加密」分頁刻意保持語彙區隔：加密用「加密／解密」，資料夾防護用「上鎖／解鎖」，兩邊動詞互不共用，避免使用者混淆兩種保護等級的差異。

### 21.1 憑證模型

- **整個功能共用一組密碼＋選配 Passkey**（`FolderGuardService`），不是每個資料夾各自一組。第一次上鎖任何資料夾前，強制先完成這組共用憑證的設定。
- 密碼必填、Passkey 選配，密碼永遠是保底解鎖手段——這個功能沒有像加密那樣的「恢復金鑰」可以兜底，不能讓 Passkey 變成唯一解鎖方式。
- 密碼錯誤鎖定套用跟加密一樣的機制（`LockoutTracker`，連續錯 5 次、指數退避最長 1 小時），但鍵值是固定代表整個功能的常數鍵，不是逐項目 UUID——鎖定會影響「所有」正在上鎖的資料夾，這是刻意接受的取捨。Passkey 略過鎖定機制，理由同第 6.4 節。
- 忘記密碼、Passkey 也失效時，仍可透過檔案總管「內容→安全性→進階」拿回資料夾存取權——這不是加密，沒有無法復原的風險，設定頁會主動告知這件事。

### 21.2 ACL 機制

`FolderGuardAcl.ApplyDeny`/`RemoveDeny`：對目前登入帳號的 SID 加上（或移除）一條拒絕 `ReadAndExecute | Write | Delete`（`FileSystemRights` 組合值 `0x301BF`，剛好等於 .NET `FileSystemRights.Modify`）、`ContainerInherit | ObjectInherit` 繼承旗標的 ACE。不處理父層列舉權限、不搭配隱藏屬性——資料夾在檔案總管裡看得到，雙擊進去才會被拒絕（Windows 原生「存取被拒」錯誤視窗，不攔截、不替換成自訂畫面）。拒絕 `Delete` 權限連帶擋住重新命名（NTFS 底下重新命名一個物件需要對該物件本身的 `Delete` 權限）。

這條 Deny 規則會繼承給資料夾內所有既有與新增的子項目（`ContainerInherit | ObjectInherit`）。「雙擊解鎖」用的 `.lockfolder` 標記檔（見第 21.6 節）是資料夾的同層兄弟檔案，不是子項目，不受這條繼承規則影響，寫入/刪除不需要另外處理 ACL。

ACL 拒絕規則掛在目前登入帳號的 SID 上，FileLocker App 自己的行程也是用同一個帳號跑，**加密流程讀取被上鎖的資料夾時一樣會被拒絕存取**，不只是使用者在檔案總管點不進去而已（見第 21.5 節）。

### 21.3 Shell Extension 整合

`FileLockerShellExtension.dll`（`dllmain.cpp`）在原本「使用 FileLocker 加密」選單項目之外，多插入第二個命令 id（`idCmdFirst + 1`），依 `IsFolderGuardLocked` 現場查詢的防護狀態決定要顯示「將所選資料夾上鎖」還是「將所選資料夾解鎖」：

- `IsFolderGuardLocked`：用 `GetNamedSecurityInfoW` 讀取目前的 DACL，逐條比對是否有一條 `ACCESS_DENIED_ACE`、SID 等於目前使用者、且 `Mask` 包含 `kFolderGuardDeniedRights`（`0x301BF`，必須跟 `FolderGuardAcl.cs` 的 C# 端算出來的值完全一致，兩邊各自獨立判斷，任何一邊改了遮罩值另一邊沒跟著改，選單就會永久誤判）。ACL 是唯一的判準，不論有沒有啟用「雙擊解鎖」都一樣（見第 21.6 節）。
- 「上鎖」/「解鎖」選單項目只在選取的項目**全部是資料夾**時才出現，混到任何一個檔案就整個不顯示（不做「自動忽略檔案只鎖資料夾」這種隱性行為）；選取範圍內鎖定狀態不一致（`Mixed`：有些鎖有些沒鎖）時，兩個都不顯示，避免使用者搞不清楚這次點下去的動作。
- `InvokeCommand` 依命令 id 組出 `--folder-guard-lock` 或 `--folder-guard-unlock` 命令列旗標啟動 `FileLocker.App.exe`，跟現有「直接傳路徑＝加密」預設行為區隔開（見 `App.xaml.cs` `HandleLaunchArgs`）。
- 支援多選批次：因為憑證是共用一組，批次上鎖/解鎖不需要處理加密批次的複雜度（第 4.5 節那種「多選時 Passkey 勾選框鎖住」的問題），純粹對每個選取的資料夾各自套用同一組 ACL 規則。

### 21.4 上鎖／解鎖互動

- **右鍵「上鎖」**：已設定過共用密碼時，直接跳出原生 WPF 小視窗（`FolderGuardConfirmLockWindow`，技術上比照 `PasswordPromptWindow`，不透過 WebView2）確認「你要將『OO』上鎖嗎？」，**上鎖本身不需要輸入密碼**（密碼只用來驗證解鎖身份，不是上鎖的必要條件），確認彈窗本身已足夠防止手滑誤觸。尚未設定過共用密碼則改為開啟主程式、跳到「資料夾防護」分頁引導完成首次設定，設定完成後才真的上鎖這次選取的資料夾。
- **右鍵「解鎖」**：跳出 `FolderGuardUnlockPromptWindow`，有設定 Passkey 就優先跳 Windows Hello 驗證，使用者把驗證視窗關掉才退回密碼輸入畫面（比照第 14.4 節 `PasswordPromptWindow` 遇到 Passkey 項目時的既有互動模式）。右鍵一定顯示「解鎖」代表已經是鎖定狀態，不會有「還沒設定過」要導去首次設定的分支。
- **分頁內清單頁操作**：獨立分頁管理所有上鎖中的資料夾，可個別解鎖、一次全部解鎖（`UnlockAllAsync`）；已解鎖項目可「前往資料夾」直接開啟總管，或「再次上鎖」恢復保護。健壯性檢查：清單頁載入時針對索引裡每個路徑用 `FolderGuardProtection.IsActive`（等同直接查 ACL）即時檢查，不符合就視為「已不在防護中」，比照 `VaultManager.ScanAll()`「以磁碟實際狀態為準，索引只是加速用途」的既有設計原則。

### 21.5 與加密流程的互動

`LockService` 建構時透過委派 `getGuardedFolderPaths` 得知目前哪些資料夾正在防護中（見 `App.xaml.cs`），加密流程一開始掃描到選取範圍內含正在上鎖的資料夾，會先跳出彈窗列出被擋的子資料夾清單，要求先解鎖才能繼續（驗證方式同第 21.4 節），不會讓加密流程半途讀取 ACL 拒絕的資料夾而失敗。已解鎖並被加密流程消耗（打包進外層 zip）的資料夾，在資料夾防護索引裡對應的項目也會一併清除。

### 21.6 選配功能：雙擊已上鎖資料夾直接解鎖（`.lockfolder` 標記檔）

`AppSettings`／`FolderGuardData` 的 `DoubleClickUnlockEnabled`（預設 `false`）控制的選配功能：雙擊一個已上鎖的資料夾，不看到 Windows 原生「存取被拒」畫面，而是直接跳出解鎖確認彈窗。

**放棄過的技術路線：Windows Shell Namespace Extension**。第一版做法是用 `desktop.ini` 的 `CLSID2` 鍵把資料夾本身偽裝成一個自訂 COM `IShellFolder` 物件（`folderguard_namespace.cpp`／`folderguard_namespace.h`，獨立 CLSID `{2A4376E0-C5FC-4126-8ACD-9FC8AA377AC1}`），攔截 Explorer 對這個資料夾的瀏覽行為。實測連續踩到兩個問題：

1. **資料夾同時偽裝成可瀏覽物件、又套 Deny ACL 拒絕存取**，讓 `explorer.exe` 整個行程進入無法從任何權限層級終止的死結狀態，只能重開機解除——根因診斷指向 Explorer 原生 `CFSFolder` 解析這個資料夾時，會先做自己的存取檢查，撞到 Deny ACE 時內部同步跳出的權限提示對話框邏輯，完全不在我們的程式碼控制範圍內。
2. 移除 ACL、只靠命名空間物件自己擋下瀏覽之後，死結問題解決，但改成**右鍵選單整個消失**（連跟資料夾防護無關的「加密」選項也一起不見）——`CLSID2` 一旦接管資料夾身分，Explorer 對右鍵選單的處理也跟著整個改道，不再走標準的 `Directory\shellex\ContextMenuHandlers` 選單鏈，而我們自己在命名空間物件裡提供的 `IContextMenu`（`FolderGuardNamespaceContextMenu`）也沒有被如預期呼叫到。

兩次實測、兩種截然不同的失敗模式，都出在 Explorer 對 Shell Namespace Extension 缺乏官方文件保證的內部行為，不是我們自己的 COM 邏輯寫錯了什麼——判斷這條技術路線不值得繼續投入，`folderguard_namespace.cpp`／`.h` 已整個移除，`ShellExtensionRegistrar` 也不再註冊這組 CLSID。

**目前採用的技術路線：檔案關聯標記檔**，跟加密功能的 `.locked` 指標檔走同一套已經證明穩定的機制，不需要任何 COM 命名空間物件：

- `FolderGuardUnlockMarkerFile`（C#）：上鎖時（若啟用這個選配功能）在資料夾旁邊、同一層額外建立一個 `{資料夾名稱}.lockfolder` 檔案，內容純文字記錄真正資料夾的完整路徑（不是靠檔名反推，資料夾改名或搬動不會讓標記檔失效）。這個標記檔是資料夾的**同層兄弟**，不是資料夾內部的東西，寫入/刪除完全不受資料夾本身的 ACL 影響，不需要像舊版 `desktop.ini` 那樣搶在 ACL 生效前寫入、還要另外補一條 Allow 規則。
- `FolderGuardProtection.Apply`/`Remove`/`SwitchMode`：ACL 現在永遠是唯一的保護來源，兩種模式（啟用/不啟用雙擊解鎖）保護強度完全一樣，差別只在要不要多放這個標記檔——不像舊版方案需要在 ACL 與命名空間標記之間二選一、還要處理兩者互斥切換的邊界情況。
- `ShellExtensionRegistrar.RegisterLockFolderMarkerAssociation`：App 啟動時在 `HKEY_CURRENT_USER\Software\Classes\.lockfolder` 底下自我註冊標準的副檔名開啟動作（純資料，不是 COM 登錄），指到 `FileLocker.App.exe --folder-guard-unlock-marker "%1"`，圖示直接借用主程式執行檔本身內嵌的圖示，不需要另外準備 `.ico` 資源。
- `App.xaml.cs` 的 `HandleFolderGuardUnlockMarkerLaunch`：收到的參數是標記檔自己的路徑，讀出裡面記錄的真正資料夾路徑後，轉呼叫既有的 `HandleFolderGuardUnlockLaunch`（跟右鍵選單「解鎖」共用同一套流程）；讀不到的標記檔（被搬移/刪除/內容損毀）直接忽略，不中止批次裡其他還讀得到的項目。

**這個做法的代價**：資料夾旁邊多了一個額外的 `.lockfolder` 項目，在「依檔案類型分組」的檢視下會被歸到跟資料夾不同的分組，不是原本設想的「雙擊資料夾本身」那麼無縫——這是使用者在確認方向時已經知情接受的取捨，換來的是不用再碰任何 COM／`IShellFolder` 程式碼，風險大幅降低，且 ACL 保護強度完全不受影響（舊版命名空間方案為了避開死結，一度必須放棄 ACL，保護範圍縮小成「只擋 Explorer 雙擊」，這個做法不需要這個犧牲）。

**驗證狀態**：C# 端邏輯已補上單元測試（`FolderGuardDoubleClickModeTests.cs`）並全數通過，Shell Extension DLL 移除命名空間擴充後重新編譯無警告無錯誤，但**實際雙擊互動尚未經過完整人工實測驗證**。

---

## 22. 軟體更新檢查

設定頁一鍵檢查是否有新版本（`MainWindow.xaml.cs` 的 `HandleCheckForUpdatesRequestAsync`）：

- **版本比對來源**：只讀取安裝內容資料夾裡的 `installer_config.json`（`FetchLatestGitHubReleaseAsync` 呼叫 `https://api.github.com/repos/Lai-xuan/FileLocker/releases/latest` 取得最新 Tag／說明／下載連結，跟本機 `installer_config.json` 裡的版本號比較）——這個檔案是 mac-style-windows-installer 安裝時才會放進安裝資料夾的（見第 19 節），直接以原始碼執行（`dotnet run`）的開發環境找不到這個檔案，不會顯示版本資訊，也不算錯誤情境。
- 發現新版本會自動跳出彈窗，內容是 GitHub Release 說明的 Markdown 渲染結果（獨立可捲動框框，避免長篇說明撐爆版面）。
- 確認更新後直接下載安裝檔並啟動安裝程式；**安裝程式確認成功啟動後才關閉 FileLocker 本體**，避免「先關自己、安裝程式卻沒真的啟動」導致使用者以為在更新、實際上什麼都沒發生，也避免安裝時本體檔案還被鎖住導致覆蓋失敗。
- 需要能連上 `api.github.com`；沒有網路或請求失敗只當作「這次沒查到更新」，不當成錯誤彈窗打斷使用者。

---

## 23. 已知限制（非缺陷，為取捨或現有技術/流程的限制）

- **CLI 不涵蓋 Passkey**：設計決定，見第 15 節。`KeyCredentialManager` 一定會跳出 Windows Hello 系統 UI，這跟「無 GUI 環境可操作」的 CLI 存在目的直接衝突；未來若要支援，應為獨立指令，不塞進 `--encrypt` 裡。
- **沒有數位簽章，也沒有執行檔完整性驗證機制**：詳見第 19 節的評估與取捨——需要另外採購程式碼簽署憑證，不是安裝程式工具本身能解決的事；「程式自己在啟動時檢查自身雜湊值」評估後不採用，因為攻擊者能竄改執行檔內容的同時，也能連檢查邏輯本身一起改掉，只能擋住意外損毀、擋不住真正有心的竄改，容易給人錯誤的安全感。安裝檔與更新下載回來的安裝檔執行時，Windows SmartScreen 可能會跳出警告。
- **軟體更新檢查僅支援透過正式安裝版比對版本**：見第 22 節，需要本機存在 `installer_config.json`（僅由 mac-style-windows-installer 安裝時放入）且能連上 `api.github.com`；直接以原始碼執行的開發版不會顯示版本資訊，這不算錯誤情境。
- **資料夾防護「雙擊已上鎖資料夾直接解鎖」啟用時，保護方式多一個額外的 `.lockfolder` 檔案**：見第 21.6 節，這個標記檔在「依檔案類型分組」的檔案總管檢視下會被歸到跟資料夾不同的分組——是確認技術方向時已知情接受的取捨，換來的是不需要任何 COM／`IShellFolder` 程式碼、ACL 保護強度也不受影響。
- **背景模式（系統匣常駐）下，主視窗與系統匣選單的實際彈出位置不穩定**：`MainWindow` 為了保留 DWM 原生最大化動畫，自行攔截 `WM_NCCALCSIZE`／`WM_GETMINMAXINFO` 把非客戶區視覺收縮到 0（見「無邊框視窗」相關章節），這使得 WPF 內建的視窗定位邏輯（`WindowStartupLocation="CenterScreen"`，以及系統匣選單原本用 `PresentationSource.CompositionTarget` 換算 DPI 的做法）算出來的位置會偏掉。已改成不透過 WPF 座標系統，直接用 `GetWindowRect`／`GetMonitorInfo`／`SetWindowPos` 等 Win32 API 以物理像素運算並設定位置，但實測後主視窗與系統匣選單的彈出位置仍不如預期（主視窗會跑到螢幕角落、選單會跑到螢幕最頂端而非游標附近）——已排查兩輪，根因尚未完全確認，暫時擱置；不影響功能本身可以正常開啟與操作，僅彈出的位置不理想。

---

## 24. 待辦事項

### 24.1 進行中

- **雲端同步跨裝置人工實測**：見第 11.2 節，目前僅完成自動化測試（模擬多裝置同步、衝突情境），跨裝置的完整人工實測待使用者自行進行。
- **雙擊已上鎖資料夾直接解鎖的實際互動驗證**：見第 21.6 節，`.lockfolder` 標記檔機制的 C# 端邏輯已完成並通過單元測試，但實際雙擊標記檔跳出解鎖彈窗的互動流程尚待完整人工實測。
- **背景模式主視窗／系統匣選單彈出位置排查**：見第 23 節已知限制，已改用 Win32 API 直接以物理像素定位，但實測位置仍不如預期，根因尚未確認，待後續找時間深入排查。

### 24.2 已完成之待辦

以下項目過去曾列在待辦／已知限制中，目前已完成，記錄於此保留歷史脈絡：

- **打包安裝程式**：對接 mac-style-windows-installer，含 `.locked` 檔案關聯與圖示接入，見第 19 節。
- **CLI 隨裝發布**：`FileLocker.Cli` 打包進 `cli/` 子資料夾，安裝程式透過 `path_target_exe` 加入系統 PATH，見第 19 節。
- **資料夾防護（Folder Guard）功能開發**：純 ACL 資料夾存取限制、共用密碼＋選配 Passkey、右鍵上鎖/解鎖、清單頁管理，見第 21 節。
- **軟體更新檢查功能開發**：設定頁一鍵檢查 GitHub Release、下載安裝檔並啟動，見第 22 節。
