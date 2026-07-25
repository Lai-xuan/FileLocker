# CLAUDE.md

這份文件給 Claude Code 接手這個專案時看。完整技術規格在 `FileLocker_技術規格文件.md`（同目錄），這份文件是快速定位用的地圖，兩份一起讀。

## 專案是什麼

FileLocker：Windows 檔案/資料夾加密工具。使用者在檔案總管選取檔案或資料夾，右鍵加密，內容移到集中管理區（Vault），原位置留一個 `.locked` 指標檔；雙擊指標檔或在 App 裡操作可以解回原狀。支援密碼、Passkey（Windows Hello）、恢復金鑰三種獨立的解鎖方式。

GitHub: `https://github.com/Lai-xuan/FileLocker`（私有）

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
│   ├── FileLocker.Cli/            # CLI 原型
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

- **註解一律用繁體中文，說明「為什麼」不是「做了什麼」**——尤其是不直覺的決定（例如「這裡故意不用 X，因為 Y」），這是這個專案從頭到尾維持的風格，新增程式碼要照著做。
- **每個修正/決策留下理由**，不要只留下程式碼本身——之後回頭看才知道當初為什麼這樣做，避免被後面的人誤改回錯的版本。
- C# 端：`private static` 輔助方法搭配 XML doc 註解；例外處理優先接基底類別（例如 `CryptographicException` 而不是特定子類別），避免未來 .NET 版本更新後子類別改變導致漏接。
- Vue 端：`<script setup>` Composition API，所有文字一律走 `t('key')` 翻譯函式，不寫死中文或英文字串；新增文字要同時補 `zh-TW.json` 和 `en.json` 兩份。
- 兩邊都要維持前後端分離——UI 邏輯留在 Vue，商業邏輯/加密/檔案系統操作留在 C#。

## 目前狀態速覽

功能面（加解密、Vault、Metadata、安全機制、GUI、多語言）都已完成並通過測試。**還沒開始**：CLI 完整功能、Shell Extension 打包進安裝程式、雲端同步情境的跨裝置人工實測、正式安裝程式（技術路線已定案，還沒動工）。詳細清單見規格文件最後一節「開發進度」。

## 已知限制（不是要修的 bug，是刻意的取捨或技術限制）

- 密碼小視窗（`PasswordPromptWindow`）還是原生 WPF 標題列，沒有跟主視窗一起改成無邊框
- 主視窗拿掉原生框架後，沒有原生的最大化長大/縮小動畫（技術限制，真正解法需要換成 WebView2 的 Composition Controller 托管模式，是另一個量級的工程）
- 拖放檔案已經能動（用 WebView2 的 `postMessageWithAdditionalObjects` + `CoreWebView2File.Path`），不是走原生 WPF 拖放（試過會被 WebView2 攔死）
- 後端錯誤代碼系統涵蓋 `LockService` 的常見錯誤情境，但設定頁（搬移 Vault、存恢復金鑰檔案）的少數訊息還是固定繁體中文
- App 圖示跟 `.locked` 副檔名圖示都已經設計定案（黃銅色系，鑰匙孔/蠟封造型），但還沒接進 `.csproj` 或安裝程式的檔案關聯設定，也還沒匯出成 Windows 需要的 `.ico` 多解析度格式

## 這個對話的交接說明

這個專案先前在 Claude 一般對話介面裡開發，累積了大量逐步討論出來的設計決策（尤其是 GUI 那塊，經過很多輪根據實際截圖來回調整）。規格文件已經整理成不含歷史敘事的「目前狀態」寫法，直接照規格文件的內容認知現況即可，不需要去猜之前怎麼演變過來的。
