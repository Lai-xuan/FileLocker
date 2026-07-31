---
name: release
description: 依照這個專案實際的流程準備一次新版本發布——建置、測試、更新雙語 Release Notes、commit、打 tag，並提示需要在這個 repo 之外手動完成的安裝程式打包步驟。
---

# Release

這個 skill 是照 FileLocker 這個 repo 實際的發布慣例寫的，不是通用範本：

- Release Notes 是**單一檔案內雙語**（`RELEASE_NOTES_vX.Y.Z.md`，先 `## 繁體中文` 後 `## English`），不是分成 `README.md`／`README.zh-CN.md` 兩個檔案——比照 [`RELEASE_NOTES_v1.1.0.md`](../../../RELEASE_NOTES_v1.1.0.md) 的段落結構（亮點 + 已知限制，各自中英文對應）。
- 這個 repo 確實有在打 git tag（`v1.0.0`、`v1.1.0`），commit 訊息慣例是 `feat:`／`fix:`／`docs:`／`refactor:`／`style:` 開頭，但不是嚴格的 Conventional Commits 格式（後面接的是完整中文句子說明「為什麼」，不是簡短英文摘要）。
- **正式安裝程式的打包不在這個 repo 裡完成**——技術規格文件第 19 節說明是對接另一個獨立專案 [mac-style-windows-installer](https://github.com/Lai-xuan/mac-style-windows-installer)。這個 skill 負責 repo 內的部分（程式碼、測試、文件、tag），安裝檔打包跟上傳 GitHub Release 的附件，交給使用者在那個專案裡另外處理。
- 這個環境沒有安裝 `gh` CLI，不要假設它存在——GitHub Release 草稿的步驟用網頁連結提示使用者手動做。

## 步驟

1. **確認工作目錄乾淨**：`git status --short`，如果有非預期的未追蹤/未提交檔案，先跟使用者確認要不要一併處理，不要悄悄略過。
2. **跑完整測試套件**：`dotnet test`。任何一個測試沒過就停下來，不要continue——回報給使用者，不要自己決定要不要跳過。
3. **Release 組態建置**：`dotnet build src/FileLocker.App/FileLocker.App.csproj -c Release`，確認 0 錯誤 0 警告；如果 `src/FileLocker.ShellExtension/*.cpp` 有變更過，提醒使用者（或直接執行，如果 VS Dev Shell 可用）用 CLAUDE.md 裡的 `cl` 指令重新編譯 x64 Shell Extension DLL——C# 建置不會自動重編 C++ 部分。
4. **決定版本號**：讀 `git tag` 列出目前最新的版本，跟使用者確認這次是 patch／minor／major，不要自己猜。
5. **產生 Release Notes 草稿**：`git log <上一個 tag>..HEAD --oneline` 整理這段期間的變更，依照 [`RELEASE_NOTES_v1.1.0.md`](../../../RELEASE_NOTES_v1.1.0.md) 的雙語段落結構寫成新的 `RELEASE_NOTES_vX.Y.Z.md`（亮點／已知限制，中文在前、英文在後，兩邊內容要對等——這個對話稽核過，這兩個檔案目前逐條對應，維持這個標準）。同時檢查 `README.md` 需不需要跟著更新（新功能通常要）。
6. **Commit**：訊息比照這個 repo 現有風格（`feat:`／`docs:` 等前綴 + 完整中文說明），文件變更可以跟程式碼變更分開兩個 commit（比照這個對話稍早的做法）。
7. **打 tag**：`git tag vX.Y.Z`，跟使用者確認要不要 push（tag 跟 commit 都是視覺化「發布」的動作，push 前一定要問，不要自動推）。
8. **交接安裝程式打包**：明確告訴使用者接下來要去 mac-style-windows-installer 那個專案打包安裝檔，打包完成後回到 GitHub 這個 repo 的 [Releases 頁面](https://github.com/Lai-xuan/FileLocker/releases) 手動建立 Release、貼上 Release Notes、上傳安裝檔——這個環境沒有 `gh` CLI，不要嘗試自動化這一步。

## 不做的事

- 不自動 push（tag 或 commit）——一律先問。
- 不假設 `gh` CLI 存在。
- 不把 Release Notes 拆成分開的中英文檔案——這個 repo 的慣例是單一檔案。
