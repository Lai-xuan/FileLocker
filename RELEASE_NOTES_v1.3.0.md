# FileLocker v1.3.0

## 繁體中文

新增資料夾防護閒置自動重新上鎖、CLI 靜默批次模式，以及背景模式（系統匣常駐＋跟隨 Windows 啟動）。

### 亮點

- **資料夾防護：新增閒置逾時自動重新上鎖**
  - 解鎖之後如果忘記手動重新上鎖，預設 15 分鐘（可調整）閒置後會自動重新上鎖，避免防護形同虛設。啟動時會補跑一次（涵蓋上次關閉前忘記重新上鎖、重開機後才發現已經過期的情境），之後每 60 秒輪詢一次。
- **CLI：新增靜默批次模式**
  - 給指令碼／排程工作用的非互動模式：新增 `--password-stdin`（從標準輸入讀密碼，避免密碼留在 shell 歷史紀錄）、`--password-file`、`--recovery-key`、`--hint`、`--yes`（略過確認提示）幾個旗標，搭配結構化的結束碼（0 成功／1 用法錯誤／2 批次部分或全部失敗／3 使用者取消），方便呼叫端判斷執行結果。
- **新增背景模式（系統匣常駐＋跟隨 Windows 啟動）**
  - 「關閉視窗後留在系統匣」與「跟著 Windows 啟動」是兩個獨立開關，切換任一個都會立刻生效，不用重開 App。
  - 系統匣右鍵選單改用自製的圓角彈出視窗（原本用 Windows 內建元件套用圓角時，角落會有殘影），外觀更貼近現代系統匣應用程式。
  - 修正已經在系統匣裡的 FileLocker 被再次雙擊執行檔時，視窗不會跳到前景的問題。
- **文件**：新增密碼庫（Password Locker）功能的完整規劃文件與相關術語定義（`FileLocker_密碼庫_功能規劃.md`、`CONTEXT.md`、`docs/adr/`），這是下一階段要實作的功能，這次發布本身不包含任何密碼庫的實際功能。

### 已知限制

- 背景模式（系統匣常駐）下，主視窗與系統匣選單的實際彈出位置有時不如預期（可能出現在螢幕角落而非預期位置），不影響功能本身可以正常開啟與操作，僅彈出位置不理想；根因排查中。
- 資料夾防護的「使用 .lockfolder 開啟上鎖資料夾」預設關閉，開啟後 `.lockfolder` 標記檔會讓資料夾在「依檔案類型分組」檢視下跟真正的資料夾分開排列，這是接受的設計取捨，不是 bug。
- 安裝程式仍未申請數位簽章，執行安裝檔或更新下載回來的安裝檔時，Windows SmartScreen 可能會跳出警告，點「其他資訊」→「仍要執行」即可繼續。
- 軟體更新檢查需要能連上 GitHub（`api.github.com`），且僅支援透過正式安裝版比對版本；直接以原始碼執行的開發版不會顯示版本資訊。
- 密碼遺失無法復原，沒有任何後門機制——請務必妥善保存密碼與恢復金鑰。

---

## English

Added Folder Guard idle-timeout auto-relock, a CLI silent batch mode, and background mode (minimize to tray + launch at startup).

### Highlights

- **Folder Guard: idle-timeout auto-relock**
  - If you forget to manually relock a folder after unlocking it, it now relocks itself automatically after 15 minutes of idle time by default (configurable), so protection doesn't quietly lapse. A catch-up pass runs on startup (covering the case where the app was closed before the timeout fired), and a timer polls every 60 seconds afterward.
- **CLI: silent batch mode**
  - A non-interactive mode for scripts and scheduled jobs: new `--password-stdin` (read the password from standard input, keeping it out of shell history), `--password-file`, `--recovery-key`, `--hint`, and `--yes` (skip confirmation prompts) flags, paired with structured exit codes (0 success / 1 usage error / 2 partial or total batch failure / 3 cancelled) so callers can act on the result programmatically.
- **New: background mode (minimize to tray + launch at startup)**
  - "Stay in the system tray when the window closes" and "Launch at Windows startup" are two independent toggles; flipping either takes effect immediately without restarting the app.
  - The tray icon's right-click menu now uses a custom rounded popup window instead of the built-in Windows control (which showed visual ghosting artifacts around the rounded corners), giving it a look closer to modern tray applications.
  - Fixed an issue where double-clicking the executable while FileLocker was already running in the tray would not bring the window to the foreground.
- **Docs**: added a complete planning document and terminology for the upcoming Password Locker feature (`FileLocker_密碼庫_功能規劃.md`, `CONTEXT.md`, `docs/adr/`) — this is planning for the next phase of work; this release does not include any actual Password Locker functionality.

### Known limitations

- In background mode, the main window and tray menu don't always pop up exactly where expected (occasionally in a screen corner) — this doesn't affect the app opening or working normally, only where it visually appears; the root cause is still being investigated.
- Folder Guard's "Open locked folders with a .lockfolder file" is disabled by default; when enabled, the `.lockfolder` marker file sorts separately from the real folder under Explorer's "group by file type" view — this is an accepted design trade-off, not a bug.
- The installer still isn't code-signed — Windows SmartScreen may warn when running the installer or an update package you just downloaded; click "More info" → "Run anyway" to continue.
- The update check requires reaching GitHub (`api.github.com`) and only works from an installed build; running from source shows no version info.
- A lost password cannot be recovered — there is no backdoor. Keep your password and recovery key safe.
