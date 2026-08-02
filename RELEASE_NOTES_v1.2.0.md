# FileLocker v1.2.0

## 繁體中文

重做資料夾防護的「雙擊上鎖資料夾直接解鎖」機制，脫離實驗性階段；連帶調整介面文字與排版。

### 亮點

- **資料夾防護：「使用 .lockfolder 開啟上鎖資料夾」重新設計並脫離實驗性階段**
  - v1.1.0 推出時的技術路線是自訂 Shell Namespace Extension（`IShellFolder` + `desktop.ini` 的 `CLSID2`），實測會造成 `explorer.exe` 整個行程死結，需要重開機才能解除，因此當時預設關閉、暫緩投入。這次改用跟 `.locked` 相同、已證明穩定的檔案關聯機制：開啟這個進階選項後，鎖定資料夾時會在同一層額外建立一個 `.lockfolder` 標記檔，雙擊它會直接跳出解鎖確認視窗，不需要先開啟 FileLocker，也不會先看到 Windows 原生的「存取被拒」畫面。
  - 資料夾本身完全不受影響，全程維持完整的 ACL 拒絕權限保護，不像先前的技術路線需要為了讓資料夾可瀏覽而降低保護強度。
  - 解鎖成功後會自動用檔案總管開啟該資料夾，不用再手動導覽過去。
  - `.lockfolder` 換上專屬圖示（不再借用主程式圖示）。
  - 這個選項預設仍是關閉狀態，需要在資料夾防護設定頁手動開啟；已知取捨是 `.lockfolder` 標記檔會讓資料夾在檔案總管「依檔案類型分組」檢視下跟真正的資料夾分開排列。
- **介面文字與排版**：進階開關的說明提示（滑鼠移過「i」圖示跳出的內容）原本是一整段擠在窄欄位裡的長句，這次拆成前言＋條列重點並加寬欄位，不再是一大片文字牆；「使用說明」彈窗的資料夾防護章節同樣補上這個選項的說明，並拆成多個段落，不再是擠成一團的長句。
- **打包設定**：安裝程式圖示改用正式品牌圖示（`icon.png`／`icon.ico`／`Locked_File.ico`），取代先前打包時暫用的圖示。
- **技術規格文件**：依專案撰寫規範重新整理，已知限制與待辦事項統一移到文件最末尾兩個獨立章節。

### 已知限制

- 資料夾防護的「使用 .lockfolder 開啟上鎖資料夾」預設關閉，開啟後 `.lockfolder` 標記檔會讓資料夾在「依檔案類型分組」檢視下跟真正的資料夾分開排列，這是接受的設計取捨，不是 bug。
- 安裝程式仍未申請數位簽章，執行安裝檔或更新下載回來的安裝檔時，Windows SmartScreen 可能會跳出警告，點「其他資訊」→「仍要執行」即可繼續。
- 軟體更新檢查需要能連上 GitHub（`api.github.com`），且僅支援透過正式安裝版比對版本；直接以原始碼執行的開發版不會顯示版本資訊。
- 密碼遺失無法復原，沒有任何後門機制——請務必妥善保存密碼與恢復金鑰。

---

## English

Reworked Folder Guard's "double-click to unlock a locked folder" mechanism and moved it out of the experimental stage; interface text and layout were also refined.

### Highlights

- **Folder Guard: "Open locked folders with a .lockfolder file" redesigned and no longer experimental**
  - The v1.1.0 implementation used a custom Shell Namespace Extension (`IShellFolder` + `desktop.ini`'s `CLSID2`), which was found to cause a full `explorer.exe` deadlock requiring a reboot to clear — so it shipped disabled by default and development was paused. This release switches to the same file-association mechanism already proven stable for `.locked`: with this advanced option enabled, locking a folder also creates a companion `.lockfolder` marker file next to it — double-clicking that file pops up the unlock confirmation directly, without opening FileLocker first or hitting Windows' native "Access Denied" screen.
  - The folder itself is completely unaffected and keeps full ACL deny-rule protection throughout — unlike the previous approach, which had to weaken protection to keep the folder browsable.
  - The folder now opens automatically in File Explorer once unlocked, no manual navigation needed.
  - `.lockfolder` now has its own dedicated icon instead of borrowing the main app's icon.
  - The option is still disabled by default and must be turned on from the Folder Guard settings page. Known trade-off: the `.lockfolder` marker file sorts separately from the real folder under Explorer's "group by file type" view.
- **Interface text and layout**: the advanced toggle's info tooltip (hovering the "i" icon) used to cram one long sentence into a narrow box; it's now split into an intro line plus bullet points in a wider box, no longer a wall of text. The Folder Guard section of the in-app Help dialog was likewise updated to cover this option and broken into multiple paragraphs instead of one dense block.
- **Packaging**: the installer now uses the official brand icons (`icon.png` / `icon.ico` / `Locked_File.ico`) instead of the placeholder icons used in earlier packaging.
- **Technical spec document**: restructured per the project's documentation conventions, with known limitations and to-do items consolidated into two dedicated sections at the end of the document.

### Known limitations

- Folder Guard's "Open locked folders with a .lockfolder file" is disabled by default; when enabled, the `.lockfolder` marker file sorts separately from the real folder under Explorer's "group by file type" view — this is an accepted design trade-off, not a bug.
- The installer still isn't code-signed — Windows SmartScreen may warn when running the installer or an update package you just downloaded; click "More info" → "Run anyway" to continue.
- The update check requires reaching GitHub (`api.github.com`) and only works from an installed build; running from source shows no version info.
- A lost password cannot be recovered — there is no backdoor. Keep your password and recovery key safe.
