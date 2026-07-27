# FileLocker v1.0.0

## 繁體中文

FileLocker 第一個正式版本。Windows 檔案／資料夾加密工具：檔案總管右鍵加密，內容集中存放到 Vault，原位置留下 `.locked` 指標檔；密碼、Passkey（Windows Hello）、恢復金鑰三種互相獨立的解鎖方式。

### 亮點

- **加密引擎**：Argon2id 金鑰衍生 + AES-256-GCM 串流分塊加密，大型檔案/資料夾不需要整份讀進記憶體。
- **三種解鎖方式**：密碼（必要）、Passkey（TPM 保護的裝置金鑰）、恢復金鑰（一次性顯示的備援代碼），三者互相獨立，各自只包裝內容金鑰,不影響彼此。
- **資料夾加密**：先封裝成 zip 再走同一套檔案加密流程，完整性以整包 AEAD 單元保證；巢狀 `.locked` 項目會被記錄並在刪除外層紀錄時擋下，避免使用者失去追蹤線索。
- **右鍵選單批次操作**：C++ COM Shell Extension 支援多選檔案/資料夾一次加密；App 啟動時自我檢查並修復右鍵選單登錄，不需要安裝程式介入。
- **CLI**：`--encrypt`／`--unlock`／`--unlock-recovery`／`--list`／`--delete`，支援批次操作與 `FILELOCKER_VAULT_PATH` 環境變數，方便無 GUI 環境使用。
- **關鍵操作驗證**：清除使用紀錄、停用保護機制、搬移 Vault 等破壞性操作可設定 Windows Hello 驗證門檻。
- **雲端同步**：Vault 位置可指向 OneDrive／Dropbox／Google Drive 等既有同步資料夾，同步軟體只會看到密文，達到零知識跨裝置備份。
- **GUI**：WebView2 + Vue 3 前端，macOS 風格無邊框視窗、深色模式、拖放檔案、加密精靈進度動畫。
- **多語言**：繁體中文／英文雙語介面，含後端錯誤代碼翻譯。
- **安全性強化**：明文安全清除（覆寫後刪除）、密碼錯誤指數退避鎖定、`.locked` 指標檔 HMAC-SHA256 簽章防竄改、Vault 設定檔 ACL 限制、Vault 相關檔案原子寫入。

### 已知限制

- 尚無正式安裝程式與數位簽章（技術路線已定案：沿用 [mac-style-windows-installer](https://github.com/Lai-xuan/mac-style-windows-installer)）。
- `.locked` 副檔名的檔案總管圖示關聯待安裝程式階段接入。
- 雲端同步情境僅完成自動化測試，跨裝置人工實測待使用者自行進行。
- CLI 尚未納入安裝包（獨立建置產物）；Passkey 因需要跳出系統 UI，設計上刻意不在 CLI 提供。
- 密碼遺失無法復原，沒有任何後門機制——請務必妥善保存密碼與恢復金鑰。

---

## English

The first stable release of FileLocker — a Windows file/folder encryption tool. Right-click to encrypt from File Explorer; content moves into a centrally managed Vault, leaving a `.locked` marker file behind. Three independent unlock methods: password, Windows Hello passkey, and recovery key.

### Highlights

- **Encryption engine**: Argon2id key derivation + chunked, streaming AES-256-GCM — large files and folders are never fully loaded into memory.
- **Three unlock methods**: password (required), passkey (TPM-backed device key), and a one-time-shown recovery key — each independently wraps the content key, so none of them affect the others.
- **Folder encryption**: folders are zipped and run through the same file-encryption pipeline, with integrity guaranteed as a single AEAD unit; nested `.locked` items are tracked and block deletion of the containing record so users don't lose track of what's inside.
- **Batch context-menu operations**: a C++ COM Shell Extension supports encrypting multiple files/folders at once; the app self-checks and repairs its context-menu registration on every launch, no installer step required.
- **CLI**: `--encrypt` / `--unlock` / `--unlock-recovery` / `--list` / `--delete`, with batch support and a `FILELOCKER_VAULT_PATH` environment variable for headless environments.
- **Critical action verification**: destructive operations (clearing history, disabling protection, moving the Vault) can require a Windows Hello check.
- **Cloud sync**: point the Vault at an existing OneDrive/Dropbox/Google Drive sync folder — the sync client only ever sees ciphertext, giving zero-knowledge cross-device backup.
- **GUI**: WebView2 + Vue 3 frontend, macOS-style borderless window, dark mode, drag & drop, and an animated encryption wizard.
- **Bilingual**: Traditional Chinese and English UI, including translated backend error codes.
- **Hardening**: secure plaintext erasure (overwrite then delete), exponential-backoff password lockout, HMAC-SHA256-signed `.locked` markers, ACL-restricted Vault config, atomic writes for all Vault-related files.

### Known limitations

- No installer or code signing yet (direction decided: reusing [mac-style-windows-installer](https://github.com/Lai-xuan/mac-style-windows-installer)).
- The `.locked` file-association icon isn't wired up yet — pending the installer stage.
- Cloud-sync scenarios have automated test coverage only; manual cross-device testing is still pending.
- The CLI isn't bundled into the installer yet (separate build artifact); passkey support is intentionally excluded from the CLI since it requires system UI.
- A lost password cannot be recovered — there is no backdoor. Keep your password and recovery key safe.
