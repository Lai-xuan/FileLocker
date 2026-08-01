# CLAUDE.md
完整技術規格在 `FileLocker_技術規格文件.md`（同目錄）


## 語言
用使用者寫訊息的語言回覆。這個專案的主要開發者用中文——除非對方特別要求，否則一律用中文回覆。所有使用者看得到的文字都要同時準備中文與英文兩個版本。

## 專案是什麼

FileLocker：Windows 檔案/資料夾加密工具。使用者在檔案總管選取檔案或資料夾，右鍵加密，內容移到集中管理區（Vault），原位置留一個 `.locked` 指標檔；雙擊指標檔或在 App 裡操作可以解回原狀。支援密碼、Passkey（Windows Hello）、恢復金鑰三種獨立的解鎖方式。

另設有不使用加密功能，僅透過 Windows 存取權限限制資料夾的 `資料夾保護功能` ，詳情請見技術規格文件。

GitHub: `https://github.com/Lai-xuan/FileLocker`（公開的Repo）

## 技術棧

- **後端**：C#/.NET 10，獨立的 Class Library（`FileLocker.Core`）+ WPF 宿主（`FileLocker.App`）
- **前端**：Vue 3（Composition API）+ Vite，透過 WebView2 呈現，跟後端用 `postMessage`／`postMessageWithAdditionalObjects` 溝通
- **Shell Extension**：C++ COM `IContextMenu`，獨立元件，只負責右鍵選單跟把選取路徑轉交給主程式
- **加密演算法**：Argon2id 金鑰衍生 + AES-256-GCM

## 專案結構

```
FileLocker/
├── FileLocker.slnx
├── src/
│   ├── FileLocker.Core/          # 核心邏輯（加解密、Vault、Metadata、安全機制）
│   ├── FileLocker.App/            # WPF 宿主（視窗、WebView2、單一執行個體、拖放）
│   ├── FileLocker.Cli/            # CLI（隨安裝程式一起發布，加入系統 PATH，見規格文件第 19 節）
│   ├── FileLocker.Web/            # Vue 3 + Vite 前端
│   │   └── src/
│   │       ├── App.vue            # 目前是單一大檔案，沒有拆元件
│   │       ├── locales/           # zh-TW.json、en.json
│   │       └── assets/            # 使用者自製的 SVG 圖示
│   └── FileLocker.ShellExtension/ # C++ COM Shell Extension
└── tests/FileLocker.Core.Tests/   # xUnit 測試
```

## 建置與測試指令

```bash
# 後端測試
dotnet test

# 前端開發伺服器（App.xaml.cs 的 Debug 建置會連到 http://localhost:5173）
cd src/FileLocker.Web
npm run dev

# 跑整個 App（另開一個終端機，跟 npm run dev 同時跑）
dotnet run --project src/FileLocker.App

# Shell Extension 編譯（VS Developer Command Prompt）
cl /LD /EHsc /utf-8 dllmain.cpp /Fe:FileLockerShellExtension.dll /link /DEF:FileLockerShellExtension.def
```

## 程式碼慣例

- **註解使用用繁體中文，說明「為什麼」不是「做了什麼」**——尤其是不直覺的決定（例如「不使用 X，因為 Y」），保持這種寫法。
- **每個修正/決策留下理由**，不要只留下程式碼本身——之後回頭看才知道當初為什麼這樣做，避免被後面的人誤改回錯的版本。
- C# 端：`private static` 輔助方法搭配 XML doc 註解；例外處理優先接基底類別（例如 `CryptographicException` 而不是特定子類別），避免未來 .NET 版本更新後子類別改變導致漏接。
- Vue 端：`<script setup>` Composition API，所有文字一律走 `t('key')` 翻譯函式，不寫死中文或英文字串；新增文字要同時補 `zh-TW.json` 和 `en.json` 兩份。
- 兩邊都要維持前後端分離——UI 邏輯留在 Vue，商業邏輯/加密/檔案系統操作留在 C#。

## 撰寫技術規格文件之規範

- **語言使用繁體中文，已知限制與待辦事項分為兩個項目，放在文件的最末尾。**
- **詳盡說明使用到的規格，以及為何，若是有反直覺的決議請註記。**
- **語氣使用專業、非日常口語化用法之「紀錄式」敘述法。**
- **不使用「刻意不...」此類語法，採用「不使用 X ，因為 Y 」此類語法。**
- **如果待辦清單內的事項完成了則改列至「已完成之待辦」項目，而不是將待辦清單內的文字劃線表示已解決。**


## UI 變更注意事項
標題列、彈窗、對話框的排版每次改動後：(1) 檢查新增的控制項有沒有意外繼承到全域規則（例如 `width: 100%`）、(2) 互動控制項（下拉選單、按鈕）不能放進視窗拖曳區內、(3) 確認在支援的最窄視窗寬度下文字不會被截斷、(4) 截圖或描述排版結果後再回報完成，不要沒看過畫面就宣告做完。

## 建置與驗證
Commit 前一律先跑過完整測試套件（目前 181 個測試都要過）。部署到 `Program Files` 需要提權的 shell——如果拿不到提權權限，就先建置到本機暫存資料夾，把確切的手動提權指令交給使用者執行，不要悄悄跳過驗證步驟。

## 已知的坑
單一執行個體的 Mutex 處理路徑是右鍵/上鎖這類進入點過去真的出過當機事故的地方——新增任何啟動路徑時，要處理「Mutex 已經被別的執行個體持有」的情況，並呼叫 `SetForegroundWindow`（或等效的前景焦點搶奪機制）把既有視窗搶到最前面，而不是直接結束或讓例外把行程弄崩潰。


## 目前狀態速覽

見規格文件最後一節「開發進度」。

## 已知限制（非 bug，為取捨或技術限制）

- 拖放檔案已經能動（用 WebView2 的 `postMessageWithAdditionalObjects` + `CoreWebView2File.Path`），不是走原生 WPF 拖放（試過會被 WebView2 攔死）
- 後端錯誤代碼系統涵蓋 `LockService` 的常見錯誤情境，但設定頁（搬移 Vault、存恢復金鑰檔案）的少數訊息還是固定繁體中文
