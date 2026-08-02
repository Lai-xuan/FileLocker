---
name: release
description: 依照這個專案實際的流程準備一次新版本發布——建置、測試、更新雙語 Release Notes、commit、打 tag，並提示需要在這個 repo 之外手動完成的安裝程式打包步驟。
---

# Release

這個 skill 是照 FileLocker 這個 repo 實際的發布慣例寫的，不是通用範本：

- Release Notes 是**單一檔案內雙語**（`RELEASE_NOTES_vX.Y.Z.md`，先 `## 繁體中文` 後 `## English`），不是分成 `README.md`／`README.zh-CN.md` 兩個檔案——比照 [`RELEASE_NOTES_v1.1.0.md`](../../../RELEASE_NOTES_v1.1.0.md) 的段落結構（亮點 + 已知限制，各自中英文對應）。
- 這個 repo 確實有在打 git tag（`v1.0.0`、`v1.1.0`），commit 訊息慣例是 `feat:`／`fix:`／`docs:`／`refactor:`／`style:` 開頭，但不是嚴格的 Conventional Commits 格式（後面接的是完整中文句子說明「為什麼」，不是簡短英文摘要）。
- **正式安裝程式的打包不在這個 repo 裡完成，但可以用 `mswi-cli` 自動化**——技術規格文件第 19 節說明是對接另一個獨立專案 [mac-style-windows-installer](https://github.com/Lai-xuan/mac-style-windows-installer)，這個 skill 負責 repo 內的部分（程式碼、測試、文件、tag）。打包本身現在透過該專案的 `mswi-cli pack --config installer/filelocker_installer.json` 完成，設定檔已經檢查進這個 repo（見步驟 8），不用再進另一個專案手動跑 GUI。上傳 GitHub Release 附件仍然是手動步驟。
- 這個環境沒有安裝 `gh` CLI，不要假設它存在——GitHub Release 草稿的步驟用網頁連結提示使用者手動做。

## 步驟

1. **確認工作目錄乾淨**：`git status --short`，如果有非預期的未追蹤/未提交檔案，先跟使用者確認要不要一併處理，不要悄悄略過。
2. **跑完整測試套件**：`dotnet test`。任何一個測試沒過就停下來，不要continue——回報給使用者，不要自己決定要不要跳過。
3. **Release 組態建置**：`dotnet build src/FileLocker.App/FileLocker.App.csproj -c Release`，確認 0 錯誤 0 警告；如果 `src/FileLocker.ShellExtension/*.cpp` 有變更過，提醒使用者（或直接執行，如果 VS Dev Shell 可用）用 CLAUDE.md 裡的 `cl` 指令重新編譯 x64 Shell Extension DLL——C# 建置不會自動重編 C++ 部分。
4. **決定版本號**：讀 `git tag` 列出目前最新的版本，跟使用者確認這次是 patch／minor／major，不要自己猜。
5. **產生 Release Notes 草稿**：`git log <上一個 tag>..HEAD --oneline` 整理這段期間的變更，依照 [`RELEASE_NOTES_v1.1.0.md`](../../../RELEASE_NOTES_v1.1.0.md) 的雙語段落結構寫成新的 `RELEASE_NOTES_vX.Y.Z.md`（亮點／已知限制，中文在前、英文在後，兩邊內容要對等——這個對話稽核過，這兩個檔案目前逐條對應，維持這個標準）。同時檢查 `README.md` 需不需要跟著更新（新功能通常要）。
6. **Commit**：訊息比照這個 repo 現有風格（`feat:`／`docs:` 等前綴 + 完整中文說明），文件變更可以跟程式碼變更分開兩個 commit（比照這個對話稍早的做法）。
7. **打 tag**：`git tag vX.Y.Z`，跟使用者確認要不要 push（tag 跟 commit 都是視覺化「發布」的動作，push 前一定要問，不要自動推）。
8. **打包安裝程式**：**開始之前先 Read `d:\Github\mac-style-windows-installer_專案\mac-style-windows-installer\CLI_USAGE.md`**——mswi-cli 用法隨時可能改版，使用者會持續把最新用法寫回這份文件，不要憑記憶或這份 skill 裡舊的範例假設。需要更完整脈絡（例如安裝路徑、跟 GUI 的對照）再讀同目錄下的 `使用說明書.md`。

   `dotnet publish src/FileLocker.App/FileLocker.App.csproj -c Release` 先確保 `publish/`（含 `cli/` 子資料夾）是最新的，再跑（以下是目前已知的範例，flag 名稱／JSON 欄位如果跟剛讀到的 `CLI_USAGE.md` 對不上，一律以文件為準）：
   ```
   mswi-cli pack --config installer/filelocker_installer.json --version X.Y.Z --exe-name FileLocker_vX.Y.Z_setup
   ```
   `--version`／`--exe-name` 覆蓋 JSON 裡的預設值，確保檔名跟版本號對得上這次發布，JSON 本身不用每次改。
   `installer/filelocker_installer.json` 裡的 `app_dir`／`png_icon`／`ico_icon` 目前是這台機器上的絕對路徑（`mswi-cli` 會把相對路徑誤判成相對於它自己的安裝目錄，不是相對於執行指令當下的工作目錄，只能用絕對路徑繞過），**在不同機器上執行前要先確認這幾個路徑仍然正確**。
   以下兩點是目前（`mswi-cli` 還裝在 `C:\Program Files` 底下時）的狀況，依 `CLI_USAGE.md` 當下內容判斷是否仍然成立，使用者提到之後會把工具搬到不需要提權的路徑，屆時可能已經過期：`mswi-cli` 找不到就用完整路徑 `C:\Program Files\mac-style-windows-installer\mswi-cli.exe`；這一步需要系統管理員權限（連暫存編譯檔案都要寫回安裝目錄），沒有提權就先把指令交給使用者在系統管理員終端機執行，不要跳過或悄悄失敗。
   編譯完成後，輸出在 `mswi-cli` 自己的 `dist\` 資料夾（例如 `C:\Program Files\mac-style-windows-installer\dist\FileLocker_vX.Y.Z_setup.exe`），複製一份到 `d:\Github\FileLocker_專案\vX.Y.Z\`（比照既有 v1.0.0／v1.1.0／v1.1.1 的擺放慣例）。
9. **建立 GitHub Release**：回到 GitHub 這個 repo 的 [Releases 頁面](https://github.com/Lai-xuan/FileLocker/releases) 手動建立 Release、貼上 Release Notes、上傳步驟 8 產生的安裝檔——這個環境沒有 `gh` CLI，不要嘗試自動化這一步。

## 不做的事

- 不自動 push（tag 或 commit）——一律先問。
- 不假設 `gh` CLI 存在。
- 不把 Release Notes 拆成分開的中英文檔案——這個 repo 的慣例是單一檔案。
