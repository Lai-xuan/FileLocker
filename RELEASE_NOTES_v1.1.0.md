# FileLocker v1.1.0

## 繁體中文

新增「資料夾防護」與「軟體更新檢查」兩項功能，並修正多處介面細節。

### 亮點

- **資料夾防護（全新功能）**：獨立於加密之外的第二種保護方式，純粹透過 Windows 存取權限（ACL）限制資料夾，不加密內容——只能防止普通人隨手點開，無法防止蓄意繞過權限的人存取，重要資料仍建議使用「加密」。
  - 檔案總管右鍵可直接對資料夾「上鎖」與「解鎖」；首次使用需先設定共用密碼，之後可另外啟用 Passkey 快速解鎖。已上鎖的資料夾右鍵選單會改顯示「解鎖」，同一個入口進出，不用額外記兩套操作。
  - 獨立分頁統一管理所有上鎖中的資料夾：可個別解鎖、一次全部解鎖；已解鎖項目可「前往資料夾」直接開啟總管，或「再次上鎖」恢復保護。
  - 加密流程偵測到選取範圍內含正在上鎖的資料夾時，會先提示解鎖再繼續，避免漏掉裡面的內容。
  - Passkey 已設定時，解鎖／停用一律只認 Passkey，不會自動退回密碼；設定頁另外提供「停用 Passkey」（保留密碼），作為 Passkey 硬體異常時的逃生門，避免使用者被鎖死。
  - 忘記密碼、Passkey 也無法使用時，仍可透過檔案總管「內容→安全性→進階」手動拿回資料夾存取權——這不是加密，沒有無法復原的風險。
  - FileLocker 已在背景執行時，右鍵「上鎖」／「解鎖」原本完全沒有反應：轉送這次動作的行程會在轉送完畢後嘗試釋放一個自己從未持有的系統鎖而當掉，現在已修正，並補上讓確認小視窗確實跳到最前面的處理（背景行程原本無法自行搶回前景焦點）。
  - 右鍵選單原本無法正確判斷資料夾是否已上鎖（位元遮罩算錯，永遠判定成「未鎖定」，導致解鎖選項不會出現），已修正。
- **軟體更新檢查（全新功能）**：設定頁可一鍵檢查是否有新版本，發現更新會自動跳出彈窗，內容是 GitHub Release 說明的 Markdown 渲染結果（獨立可捲動框框，不會撐爆版面）；確認後直接下載安裝檔並啟動安裝程式，安裝程式成功啟動才會關閉 FileLocker 本體，避免安裝時檔案被鎖住而失敗。
- **正式安裝程式**：對接 [mac-style-windows-installer](https://github.com/Lai-xuan/mac-style-windows-installer) 已完成並可用，含 `.locked` 副檔名關聯與圖示、解除安裝程式。
- **使用說明**：補上「資料夾防護」章節，說明功能定位與操作方式。
- **介面細節修正**：解密／資料夾防護等多處密碼欄位補上「顯示/隱藏密碼」切換；資料夾防護清單版面重新調整，欄位寬度與按鈕對齊問題一併修正。

### 已知限制

- 「資料夾防護」的右鍵選單項目需要重新編譯過的 Shell Extension（`FileLockerShellExtension.dll`）才會出現；如果是從舊版直接更新、右鍵選單還沒看到「鎖定資料夾」，通常是因為系統上還在使用舊版右鍵選單登錄，重新安裝最新版本、必要時重新啟動檔案總管即可。
- 資料夾防護的「雙擊已上鎖資料夾直接解鎖」是實驗性功能，預設關閉：實測曾經在特定情境下造成 `explorer.exe` 整個行程死結（需重開機才能解除），程式碼保留但暫不繼續開發測試。
- 軟體更新檢查需要能連上 GitHub（`api.github.com`），且僅支援透過正式安裝版（含 `installer_config.json`）比對版本；直接以原始碼執行的開發版不會顯示版本資訊。
- 安裝程式仍未申請數位簽章，執行安裝檔或更新下載回來的安裝檔時，Windows SmartScreen 可能會跳出警告，點「其他資訊」→「仍要執行」即可繼續。
- 密碼遺失無法復原，沒有任何後門機制——請務必妥善保存密碼與恢復金鑰。

---

## English

Adds two new features — Folder Guard and software update checking — plus several interface refinements.

### Highlights

- **Folder Guard (new)**: a second, separate protection method alongside encryption — it restricts a folder purely through Windows access permissions (ACL) without encrypting its contents. It only stops casual browsing, not a determined attacker bypassing permissions; use Encrypt for anything truly sensitive.
  - Right-click a folder in File Explorer to lock or unlock it directly. The first time you use it, set a shared password; afterward you can optionally enable Passkey for quick unlocking. Once a folder is locked, the same context menu entry switches to "Unlock" — one entry point for both directions.
  - A dedicated tab manages all locked folders: unlock individually or all at once. Unlocked entries can be opened directly in Explorer via "Open Folder," or re-locked with "Lock Again."
  - If an encryption request includes a folder that contains locked sub-folders, you'll be prompted to unlock them first so nothing inside gets skipped.
  - Once Passkey is enabled, unlocking and disabling only accept Passkey — there's no automatic fallback to the password. Settings includes a standalone "Disable Passkey" option (keeping the password) as an escape hatch if the Passkey hardware ever stops working, so you're never locked out.
  - If you forget the password and Passkey isn't available either, you can still manually reclaim access via Explorer's Properties → Security → Advanced — this isn't encryption, so there's no unrecoverable risk.
  - Fixed: right-click Lock/Unlock did nothing while FileLocker was already running in the background. The process that forwards the click to the running instance used to crash right after forwarding (releasing a system lock it never owned), and the confirmation window couldn't reliably grab foreground focus from a background process either — both are now fixed.
  - Fixed: the context menu couldn't correctly tell whether a folder was already locked (a miscalculated bitmask always evaluated to "not locked," so "Unlock" never appeared).
- **Software update check (new)**: check for updates from Settings with one click. When a new version is found, a dialog pops up automatically showing the GitHub release notes rendered as Markdown in a scrollable box (so long notes never overflow the dialog). Confirming downloads the installer and launches it directly — FileLocker only closes itself once the installer has actually started, so installation won't fail from locked files.
- **Official installer**: packaging via [mac-style-windows-installer](https://github.com/Lai-xuan/mac-style-windows-installer) is complete and in use, including the `.locked` file association/icon and an uninstaller.
- **Help**: added a Folder Guard section explaining what it is and how to use it.
- **Interface polish**: added show/hide password toggles to several password fields (decrypt, Folder Guard setup, etc.); reworked the Folder Guard list layout, fixing column widths and button alignment.

### Known limitations

- The Folder Guard right-click menu item requires a rebuilt Shell Extension (`FileLockerShellExtension.dll`). If you updated from an older version and don't see "Lock Folder" in the context menu, your system is likely still using the old registered menu — reinstalling the latest version (and restarting Explorer if needed) resolves this.
- Folder Guard's "double-click a locked folder to unlock directly" is an experimental feature, disabled by default: it was found to cause a full `explorer.exe` deadlock (requiring a reboot to clear) under certain conditions during testing. The code stays in the repo but isn't under active development for now.
- The update check requires reaching GitHub (`api.github.com`) and only works from an installed build (one that has `installer_config.json`); running from source shows no version info.
- The installer still isn't code-signed — Windows SmartScreen may warn when running the installer or an update package you just downloaded; click "More info" → "Run anyway" to continue.
- A lost password cannot be recovered — there is no backdoor. Keep your password and recovery key safe.
