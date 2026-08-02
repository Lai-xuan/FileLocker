# FileLocker v1.1.2

## 繁體中文

CLI 正式隨安裝程式一起發布並加入系統 PATH。

### 亮點

- **CLI 隨裝發布（全新）**：`FileLocker.Cli` 現在會一起打包進安裝內容（獨立的 `cli/` 子資料夾，不跟 GUI 混在同一層），並透過安裝程式加入系統 PATH，安裝完成後可以直接在任何終端機打 `FileLocker.Cli --encrypt`／`--list` 等指令，不需要自己找路徑。

### 已知限制

- 資料夾防護的「雙擊已上鎖資料夾直接解鎖」仍是實驗性功能，預設關閉：實測曾經在特定情境下造成 `explorer.exe` 整個行程死結（需重開機才能解除），程式碼保留但暫不繼續開發測試。
- CLI 不涵蓋 Passkey（設計決定），未來若要支援應為獨立指令。
- 安裝程式仍未申請數位簽章，執行安裝檔或更新下載回來的安裝檔時，Windows SmartScreen 可能會跳出警告，點「其他資訊」→「仍要執行」即可繼續。
- 密碼遺失無法復原，沒有任何後門機制——請務必妥善保存密碼與恢復金鑰。

---

## English

The CLI now ships with the installer and is added to the system PATH.

### Highlights

- **CLI ships with the installer (new)**: `FileLocker.Cli` is now packaged into the installer content (its own `cli/` subfolder, kept separate from the GUI) and added to the system PATH by the installer — after installing, you can run `FileLocker.Cli --encrypt` / `--list` etc. from any terminal without hunting for the path yourself.

### Known limitations

- Folder Guard's "double-click a locked folder to unlock directly" is still an experimental feature, disabled by default: it was found to cause a full `explorer.exe` deadlock (requiring a reboot to clear) under certain conditions during testing. The code stays in the repo but isn't under active development for now.
- The CLI doesn't cover Passkey (a design decision); if support is added later it should be a separate command.
- The installer still isn't code-signed — Windows SmartScreen may warn when running the installer or an update package you just downloaded; click "More info" → "Run anyway" to continue.
- A lost password cannot be recovered — there is no backdoor. Keep your password and recovery key safe.
