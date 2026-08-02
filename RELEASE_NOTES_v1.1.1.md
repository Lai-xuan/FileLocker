# FileLocker v1.1.1

## 繁體中文

修正資料夾防護在背景執行時的兩個崩潰/誤判問題。

### 亮點

- **資料夾防護：修正背景執行時右鍵完全無反應**：FileLocker 已經在背景執行時，右鍵「上鎖」／「解鎖」原本完全沒有反應——負責轉送這次動作的行程會在轉送完畢後嘗試釋放一個自己從未持有的系統鎖而當掉，現在已修正，並補上讓確認小視窗確實跳到最前面的處理（背景行程原本無法自行搶回前景焦點）。
- **資料夾防護：修正右鍵選單鎖定狀態誤判**：右鍵選單原本無法正確判斷資料夾是否已上鎖（位元遮罩算錯，永遠判定成「未鎖定」，導致解鎖選項不會出現），已修正，並改成單一來源、執行期讀取，避免之後又漂移出錯。

### 已知限制

- 資料夾防護的「雙擊已上鎖資料夾直接解鎖」仍是實驗性功能，預設關閉：實測曾經在特定情境下造成 `explorer.exe` 整個行程死結（需重開機才能解除），程式碼保留但暫不繼續開發測試。
- CLI 不涵蓋 Passkey（設計決定），未來若要支援應為獨立指令。
- 安裝程式仍未申請數位簽章，執行安裝檔或更新下載回來的安裝檔時，Windows SmartScreen 可能會跳出警告，點「其他資訊」→「仍要執行」即可繼續。
- 密碼遺失無法復原，沒有任何後門機制——請務必妥善保存密碼與恢復金鑰。

---

## English

Two Folder Guard crash/misdetection issues during background operation are fixed.

### Highlights

- **Folder Guard: fixed right-click doing nothing while running in the background**: right-click Lock/Unlock did nothing while FileLocker was already running in the background. The process that forwards the click to the running instance used to crash right after forwarding (releasing a system lock it never owned), and the confirmation window couldn't reliably grab foreground focus from a background process either — both are now fixed.
- **Folder Guard: fixed the context menu misreading lock state**: the context menu couldn't correctly tell whether a folder was already locked (a miscalculated bitmask always evaluated to "not locked," so "Unlock" never appeared) — fixed, and moved to a single source read at runtime so it can't drift out of sync again.

### Known limitations

- Folder Guard's "double-click a locked folder to unlock directly" is still an experimental feature, disabled by default: it was found to cause a full `explorer.exe` deadlock (requiring a reboot to clear) under certain conditions during testing. The code stays in the repo but isn't under active development for now.
- The CLI doesn't cover Passkey (a design decision); if support is added later it should be a separate command.
- The installer still isn't code-signed — Windows SmartScreen may warn when running the installer or an update package you just downloaded; click "More info" → "Run anyway" to continue.
- A lost password cannot be recovered — there is no backdoor. Keep your password and recovery key safe.
