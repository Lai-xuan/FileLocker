<script setup>
import { ref, watch, computed, nextTick, onMounted, onUnmounted } from 'vue'
import '@fontsource/ibm-plex-sans/400.css'
import '@fontsource/ibm-plex-sans/500.css'
import '@fontsource/ibm-plex-sans/600.css'
import '@fontsource/ibm-plex-mono/400.css'
import '@fontsource/ibm-plex-mono/500.css'
import zhTW from './locales/zh-TW.json'
import en from './locales/en.json'
import lockedWaxSealUrl from './assets/Locked_Wax_Seal.svg'
import passkeyBlackUrl from './assets/Passkey_Black.svg'
import passkeyWhiteUrl from './assets/Passkey_White.svg'
import recoveryKeyBlackUrl from './assets/Recovery_Key_Black.svg'
import recoveryKeyWhiteUrl from './assets/Recovery_Key_White.svg'
import lightModeBlackUrl from './assets/Light_Mode_Black.svg'
import lightModeWhiteUrl from './assets/Light_Mode_White.svg'
import darkModeBlackUrl from './assets/Dark_Mode_Black.svg'
import darkModeWhiteUrl from './assets/Dark_Mode_White.svg'

// ---- 多語言：目前支援繁體中文／英文，語言包放在 locales/ 底下的 JSON 檔。
// t() 找不到對應的語言檔或找不到 key 時，會退回繁體中文，再找不到就直接顯示 key 本身
// （方便開發時發現漏翻的字串）。{name} 這種花括號佔位符用來塞動態內容。
const locales = { 'zh-TW': zhTW, en }
const currentLocale = ref('zh-TW')

function t(key, params) {
  let text = locales[currentLocale.value]?.[key] ?? locales['zh-TW'][key] ?? key
  if (params) {
    for (const [paramKey, value] of Object.entries(params)) {
      text = text.replaceAll(`{${paramKey}}`, value)
    }
  }
  return text
}

// 後端（C#）失敗結果目前有兩種：新的走 errorCode／errorDetail（例如密碼錯誤、找不到紀錄這些
// 常見情境，能完整翻譯），舊的／少數還沒涵蓋到的邊界情況只有固定繁體中文的 errorMessage
// （例如搬移 Vault、存恢復金鑰檔案失敗這些）。這個函式統一處理：有 errorCode 且查得到翻譯就用
// 翻譯後的文字，查不到（或根本沒有 errorCode）就退回原本的繁體中文 errorMessage，不會讓使用者
// 看到「錯誤代碼」這種內部識別字串。
function translateError(errorCode, errorDetail, fallbackMessage) {
  if (!errorCode) {
    return fallbackMessage
  }
  let detail = errorDetail
  if (errorCode === 'LOCKED_OUT' && errorDetail) {
    detail = formatRemainingTime(parseInt(errorDetail, 10))
  }
  const key = `error.${errorCode}`
  const translated = t(key, { detail })
  return translated !== key ? translated : fallbackMessage
}

// 鎖定剩餘時間的格式，跟 LockService.FormatRemaining 的邏輯對應，但這裡依目前語言決定用詞
// （後端只給原始秒數，格式化交給前端才能配合語言顯示）。
function formatRemainingTime(seconds) {
  if (currentLocale.value === 'en') {
    return seconds >= 60 ? `${Math.ceil(seconds / 60)} minute(s)` : `${seconds} second(s)`
  }
  return seconds >= 60 ? `${Math.ceil(seconds / 60)} 分鐘` : `${seconds} 秒`
}

// ---- 自訂通知（取代原生 alert()）：原生對話框在桌面應用程式裡會顯示「localhost:5173 說」
// 這種瀏覽器痕跡，看起來完全不像原生軟體。改用畫面右下角的通知卡片，跟其他 UI 一致。
const toasts = ref([])
function showToast(message, kind = 'error') {
  const id = `${Date.now()}-${Math.random()}`
  toasts.value.push({ id, message, kind })
  setTimeout(() => {
    toasts.value = toasts.value.filter((toast) => toast.id !== id)
  }, 6000)
}
function dismissToast(id) {
  toasts.value = toasts.value.filter((toast) => toast.id !== id)
}

// ---- 自訂確認對話框（取代原生 confirm()）：同樣的理由，換成跟其他彈窗一致的樣式。
// askConfirm 回傳一個 Promise，呼叫端用 await 取得使用者按了「確定」還是「取消」。
// 只適合真正的二選一（做／不做同一件事），例如永久刪除——「取消」在這種情境下就是
// 單純不做任何事，語意上站得住腳。
const confirmDialogState = ref(null) // { message, confirmLabel, cancelLabel, variant, resolve }
function askConfirm(message, options = {}) {
  return new Promise((resolve) => {
    confirmDialogState.value = {
      message,
      confirmLabel: options.confirmLabel || t('confirmDialog.defaultConfirm'),
      cancelLabel: options.cancelLabel || t('passwordPrompt.cancel'),
      variant: options.variant || 'default',
      resolve
    }
  })
}
function resolveConfirmDialog(result) {
  confirmDialogState.value?.resolve(result)
  confirmDialogState.value = null
}

// ---- 自訂三選一對話框：用在「還原到原始位置」還是「自己選位置」這種情境——
// 這種情境本質上不是「做／不做同一件事」，硬套用確定/取消的語意會讓「取消」變成
// 實際上觸發了另一個動作（跳出資料夾選擇器），使用者會搞不清楚「取消」到底取消了什麼。
// 改成兩個各自標示清楚意圖的按鈕；真正的取消（不做任何事）是點背景或按 Esc，
// 回傳 null，呼叫端據此判斷什麼都不做。
const choiceDialogState = ref(null) // { message, choices: [{ value, label, variant }], resolve }
function askChoice(message, choices) {
  return new Promise((resolve) => {
    choiceDialogState.value = { message, choices, resolve }
  })
}
function resolveChoiceDialog(value) {
  choiceDialogState.value?.resolve(value)
  choiceDialogState.value = null
}

const activeTab = ref('encrypt')
const activeListSubTab = ref('files') // 'files' | 'history'

// ---- 頁籤下方會滑動的指示條：量測目前作用中頁籤按鈕的實際位置/寬度，讓指示條動畫過去，
// 而不是每個按鈕各自套用固定的底線樣式（那樣切換時只會「跳」過去，沒有滑動的感覺）。
const tabBarRefs = {}
function setTabRef(key, el) {
  if (el) {
    tabBarRefs[key] = el
  }
}

const tabIndicatorStyle = ref({ transform: 'translateX(0px)', width: '0px' })

function updateTabIndicator() {
  const el = tabBarRefs[activeTab.value]
  if (!el) {
    return
  }
  tabIndicatorStyle.value = {
    transform: `translateX(${el.offsetLeft}px)`,
    width: `${el.offsetWidth}px`
  }
}

watch(activeTab, () => nextTick(updateTabIndicator))

// 視窗縮放（尤其這個 App 可以自由調整大小）會改變按鈕實際寬度，指示條要跟著重新對齊，
// 不然縮放後位置會跟按鈕對不上。
function handleWindowResize() {
  updateTabIndicator()
}

// Esc 關閉目前開啟的彈窗——照優先權由上而下檢查哪個彈窗開著就關掉哪個，正常情況下同時間
// 只會有一個開著。恢復金鑰顯示彈窗刻意不放進來：那個彈窗本來就設計成要強制使用者先複製、
// 存檔，或確認已經抄下來才能關閉，Esc 不該是繞過這個安全機制的後門。
function handleGlobalKeydown(event) {
  if (event.key !== 'Escape') {
    return
  }
  if (confirmDialogState.value) {
    resolveConfirmDialog(false)
  } else if (choiceDialogState.value) {
    resolveChoiceDialog(null)
  } else if (passwordPromptContext.value) {
    cancelPasswordPrompt()
  } else if (recoveryKeyPromptItem.value) {
    cancelRecoveryKeyPrompt()
  } else if (isHelpOpen.value) {
    isHelpOpen.value = false
  }
}

onMounted(() => {
  nextTick(updateTabIndicator)
  window.addEventListener('resize', handleWindowResize)
  window.addEventListener('keydown', handleGlobalKeydown)
})

onUnmounted(() => {
  window.removeEventListener('resize', handleWindowResize)
  window.removeEventListener('keydown', handleGlobalKeydown)
})

// ---- 自訂標題列：視窗是不是最大化狀態（由 C# 那邊在視窗狀態改變時通知）----
const isWindowMaximized = ref(false)

function minimizeWindow() {
  window.chrome.webview.postMessage({ type: 'windowMinimize' })
}

function toggleMaximizeWindow() {
  window.chrome.webview.postMessage({ type: 'windowMaximizeToggle' })
}

function closeWindow() {
  window.chrome.webview.postMessage({ type: 'windowClose' })
}

// ---- 設定頁籤 ----
const settingsVaultPath = ref('')
const settingsLanguage = ref('zh-TW')
const settingsTheme = ref('light')

// 主題按鈕的圖示要跟著目前的主題換黑白版本——淺色背景配黑色線條、深色背景配白色線條，
// 不是照哪顆按鈕決定，是照「畫面現在是亮色還是深色」決定，兩顆按鈕的圖示會一起切換。
const lightModeIconUrl = computed(() => settingsTheme.value === 'dark' ? lightModeWhiteUrl : lightModeBlackUrl)
const darkModeIconUrl = computed(() => settingsTheme.value === 'dark' ? darkModeWhiteUrl : darkModeBlackUrl)
const passkeyIconUrl = computed(() => settingsTheme.value === 'dark' ? passkeyWhiteUrl : passkeyBlackUrl)
const recoveryKeyIconUrl = computed(() => settingsTheme.value === 'dark' ? recoveryKeyWhiteUrl : recoveryKeyBlackUrl)
const settingsSaveMessage = ref('')
const isChangingVaultPath = ref(false)

// ---- 加密頁籤 ----
const encryptPaths = ref([])
const isDraggingFile = ref(false) // 拖著檔案進入視窗範圍時為 true，見 MainWindow.xaml.cs 的拖放事件說明
const encryptPassword = ref('')
const hint = ref('')
const enablePasskey = ref(false)
const enableRecoveryKey = ref(false)
const recoveryKeyDisplay = ref('') // 非空字串時顯示恢復金鑰彈窗
const recoveryKeySaveState = ref('') // '' | 'saved' | 'acknowledged'
const isEncrypting = ref(false)

// ---- 加密進度條：不是真正的加解密進度回報（那需要深入 ChunkedCipher 的每個區塊往外送
// 訊息，工程量大很多），是依項目數量／檔案大小預估一個合理的耗時，跑一個前快後慢的動畫，
// 實際完成時直接補到 100%——只是體驗用的視覺回饋，不是精確的進度。 ----
const encryptProgressPercent = ref(0)
// 目前是「壓縮中」還是「加密中」——只有批次裡有資料夾項目時才會用到 compressing 這個階段，
// 純檔案批次會直接維持在 encrypting，不會多顯示一個用不到的階段。
const encryptPhaseLabel = ref('encrypting')
let progressAnimationFrame = null
let progressStartedAt = 0
let progressEstimatedDurationMs = 0
let progressCompressionMs = 0
let pathSizesResolve = null

function requestPathSizes(paths) {
  return new Promise((resolve) => {
    pathSizesResolve = resolve
    window.chrome.webview.postMessage({ type: 'getPathSizes', paths })
  })
}

// 粗略假設本機加密大概每秒能處理 80MB（含 Argon2 延展、串流加解密、安全清除原始檔案這些
// 疊加起來的體感速度，不是精確測出來的吞吐量，這裡只追求「數量級大致合理」，不是準確計時）。
const ESTIMATED_BYTES_PER_MS = (80 * 1024 * 1024) / 1000

// 資料夾項目的預估時間裡，抓 30% 算成「壓縮」階段、其餘算「加密」階段——資料夾加密的實際
// 流程是先打包成 zip 再加密那個 zip（見規格文件 3.2 節），這裡的比例一樣是粗略假設
// （壓縮通常比完整的加解密快一些），不是量測出來的精確數字。
const FOLDER_COMPRESSION_SHARE = 0.3

function estimateEncryptPhases(itemCount, items) {
  const baseMs = 500 // 每次加密固定會有的開銷（Argon2 金鑰衍生、寫檔案），不太隨大小變化
  const perItemMs = 200 // 項目數量本身的額外負擔（愈多檔案，即使都很小，逐一處理也要時間）
  let totalMs = baseMs + perItemMs * itemCount
  let compressionMs = 0

  for (const item of items) {
    const itemMs = item.bytes / ESTIMATED_BYTES_PER_MS
    totalMs += itemMs
    if (item.isFolder) {
      compressionMs += itemMs * FOLDER_COMPRESSION_SHARE
    }
  }

  totalMs = Math.max(700, totalMs)
  // 壓縮階段最多只能佔掉「總時間扣掉一點緩衝」，不能整個估算時間都花在壓縮上，
  // 不然畫面會顯示「壓縮中」一路跑到接近完成，看起來像壓縮跟加密根本沒有分開。
  compressionMs = Math.min(compressionMs, Math.max(0, totalMs - 200))

  return { totalMs, compressionMs }
}

function startFakeProgress(itemCount, items) {
  cancelFakeProgress()
  encryptProgressPercent.value = 0

  const hasFolder = items.some((item) => item.isFolder)
  const { totalMs, compressionMs } = estimateEncryptPhases(itemCount, items)
  progressStartedAt = performance.now()
  progressEstimatedDurationMs = totalMs
  progressCompressionMs = compressionMs
  encryptPhaseLabel.value = hasFolder && compressionMs > 0 ? 'compressing' : 'encrypting'

  const tick = (now) => {
    const elapsed = now - progressStartedAt
    const t = Math.min(1, elapsed / progressEstimatedDurationMs)
    // 前快後慢的緩動曲線——一開始跑得比較快，愈接近預估時間愈慢。故意只逼近 92%，
    // 不會自己衝到 100%：真正的完成要等後端回報，避免進度條在實際做完之前就宣告結束，
    // 跟接下來冒出來的結果訊息對不上會很奇怪。
    const eased = 1 - Math.pow(1 - t, 2.2)
    encryptProgressPercent.value = Math.min(92, eased * 92)
    encryptPhaseLabel.value = (hasFolder && elapsed < progressCompressionMs) ? 'compressing' : 'encrypting'
    if (t < 1) {
      progressAnimationFrame = requestAnimationFrame(tick)
    }
  }
  progressAnimationFrame = requestAnimationFrame(tick)
}

function cancelFakeProgress() {
  if (progressAnimationFrame !== null) {
    cancelAnimationFrame(progressAnimationFrame)
    progressAnimationFrame = null
  }
}

function finishFakeProgress() {
  cancelFakeProgress()
  encryptProgressPercent.value = 100
  setTimeout(() => { encryptProgressPercent.value = 0 }, 350)
}
const encryptBatchTotal = ref(0)
const encryptItemResults = ref([]) // 批次加密逐項回報的結果

// ---- 解密頁籤 ----
const decryptPath = ref('')

// 路徑不管是透過選檔對話框變更、還是使用者直接在欄位裡手動打字/清空，都要讓「其他解鎖方式」
// 那組資訊（Passkey／恢復金鑰按鈕）跟著失效——不這樣做的話，使用者選過一個有開 Passkey 的
// 檔案、後來手動把路徑改成別的檔案，畫面還是會殘留著指向舊檔案 UUID 的 Passkey 按鈕，
// 按下去會操作到錯的項目。選檔對話框流程本身之後會再打一次 inspectLockedFile 拿到新資訊、
// 重新填回去，這裡先清空不會跟那個流程衝突（清空在前，非同步回應在後）。
watch(decryptPath, () => {
  decryptItemInfo.value = null
})
const decryptPassword = ref('')
const isDecrypting = ref(false)
const decryptResultMessage = ref('')
const decryptResultIsError = ref(false)
const decryptItemInfo = ref(null) // { uuid, originalName, hint, passkeyEnabled, recoveryKeyEnabled }

// ---- 已加密檔案子頁籤 ----
const vaultItems = ref([])
const isLoadingList = ref(false)
// 使用者停在清單頁時，背景 watcher 偵測到 Vault 有變化就把這個設成 true，只顯示「有更新」
// 提示、不強制整包刷新畫面——vaultList 是整包覆蓋（見下面 vaultList 處理），靜默自動刷新
// 會讓使用者正在互動的項目突然消失或位移，體驗比多一個小提示更糟。
const vaultListStale = ref(false)
const decryptingUuids = ref(new Set())
const expandedGroups = ref(new Set())
const decryptingBatchIds = ref(new Set())

// ---- 使用紀錄子頁籤 ----
const historyItems = ref([])
const isLoadingHistory = ref(false)

// 清單解密：選了自訂位置時，暫存「正在處理哪一筆、要用密碼還是 Passkey」，等資料夾選好之後接著跳下一步。
const pendingDecryptItem = ref(null)
const pendingDecryptMode = ref('password')

// 恢復金鑰解鎖：暫存正在處理哪一筆，等使用者輸入恢復金鑰。
const recoveryKeyPromptItem = ref(null)
const recoveryKeyPromptDestination = ref(null)
const recoveryKeyPromptMarkerPath = ref(null)
const recoveryKeyInputValue = ref('')
const recoveryKeyInputRef = ref(null)

watch(recoveryKeyPromptItem, (item) => {
  if (item) {
    nextTick(() => recoveryKeyInputRef.value?.focus())
  }
})

// 使用說明彈窗：內容比其他彈窗長很多，需要能捲動，用獨立的 modal--help 樣式處理。
const isHelpOpen = ref(false)

// 密碼輸入彈窗：取代原本用瀏覽器原生 prompt() 明碼輸入密碼的做法——prompt() 的輸入框不會把
// 打字內容用點點遮起來，旁邊有人看、或畫面被錄影/遠端連線時會直接看到密碼，這裡改用跟
// 其他表單一致的遮罩密碼欄位。
const passwordPromptContext = ref(null) // { mode: 'single' | 'batch', item或group, destinationDir }
const passwordPromptValue = ref('')
const passwordPromptInputRef = ref(null)

// 原生的 autofocus 屬性對 Vue 動態插入的元素不可靠——瀏覽器通常只在「這個元素是網頁一開始
// 載入時就存在」的情況下才會處理 autofocus，像這種用 v-if 動態生成的彈窗，瀏覽器常常不會
// 主動聚焦，使用者按下「還原到原始位置」之後鍵盤輸入不會自動跳進密碼欄位就是這個原因。
// 改成手動在彈窗真的顯示出來之後（nextTick，等 DOM 更新完成）呼叫 .focus()。
watch(passwordPromptContext, (context) => {
  if (context) {
    nextTick(() => passwordPromptInputRef.value?.focus())
  }
})

const isRunningInWebView2 = typeof window.chrome?.webview !== 'undefined'

if (isRunningInWebView2) {
  window.chrome.webview.addEventListener('message', (event) => {
    const data = event.data

    if (data.type === 'encryptBatchStarted') {
      encryptBatchTotal.value = data.totalCount
      encryptItemResults.value = []
    } else if (data.type === 'encryptItemResult') {
      let note = ''
      if (data.passkeyRequested && !data.passkeyEnabled) {
        note = t('note.passkeyNotEnabled')
      } else if (data.passkeyEnabled) {
        note = t('note.passkeyEnabled')
      }
      encryptItemResults.value.push({
        path: data.path,
        success: data.success,
        errorMessage: translateError(data.errorCode, data.errorDetail, data.errorMessage),
        note
      })
      if (data.recoveryKey) {
        recoveryKeyDisplay.value = data.recoveryKey
        recoveryKeySaveState.value = ''
      }
    } else if (data.type === 'encryptBatchDone') {
      isEncrypting.value = false
      finishFakeProgress()
      encryptPaths.value = []
      // 密碼是敏感資料，不管這次成功還是失敗，都不該一直留在欄位裡——失敗的話重新輸入
      // 一次不是很大的負擔，但讓密碼長時間留在畫面上是不必要的風險。提示文字不算敏感資料，
      // 但同一批既然結束了，一起清掉、準備接下一批比較乾淨。
      encryptPassword.value = ''
      hint.value = ''
    } else if (data.type === 'decryptResult') {
      isDecrypting.value = false
      decryptResultIsError.value = !data.success
      decryptResultMessage.value = data.success
        ? t('decrypt.success', { path: data.restoredPath })
        : translateError(data.errorCode, data.errorDetail, t('decrypt.failed', { error: data.errorMessage }))
      // 密碼一律清掉。路徑跟「其他解鎖方式」資訊只有失敗時才留著——失敗通常是密碼打錯，
      // 使用者想對同一個檔案重新輸入密碼，這種情況下路徑欄位跟 Passkey/恢復金鑰按鈕都還有效，
      // 留著方便直接重試。成功的話這個項目已經解密消失了，路徑跟按鈕都該一起清掉，
      // 不然會誤導使用者以為還能對一個已經不存在的東西重試。
      decryptPassword.value = ''
      if (data.success) {
        decryptPath.value = ''
        decryptItemInfo.value = null
      }
    } else if (data.type === 'decryptByUuidResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        showToast(t('decrypt.success', { path: data.restoredPath }), 'success')
      } else {
        showToast(translateError(data.errorCode, data.errorDetail, t('decrypt.failed', { error: data.errorMessage })))
      }
    } else if (data.type === 'decryptByPasskeyResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        // 這則訊息是「已加密清單頁」跟「解密頁籤」的 Passkey 按鈕共用的，成功後兩邊各自
        // 該清掉的殘留資訊都要處理——清單頁清 vaultItems（上面那行），解密頁籤清路徑欄位
        // 跟「其他解鎖方式」按鈕，只有這次成功的項目剛好就是解密頁籤正在顯示的那個才清，
        // 用 uuid 比對確保不會誤清到不相關的狀態。
        if (decryptItemInfo.value?.uuid === data.uuid) {
          decryptPath.value = ''
          decryptItemInfo.value = null
        }
        showToast(t('alert.passkeyDecryptSuccess', { path: data.restoredPath }), 'success')
      } else {
        showToast(translateError(data.errorCode, data.errorDetail, t('alert.passkeyDecryptFailed', { error: data.errorMessage })))
      }
    } else if (data.type === 'decryptByRecoveryKeyResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        if (decryptItemInfo.value?.uuid === data.uuid) {
          decryptPath.value = ''
          decryptItemInfo.value = null
        }
        showToast(t('alert.recoveryKeyDecryptSuccess', { path: data.restoredPath }), 'success')
      } else {
        showToast(translateError(data.errorCode, data.errorDetail, t('alert.recoveryKeyDecryptFailed', { error: data.errorMessage })))
      }
    } else if (data.type === 'decryptBatchStarted') {
      // totalCount 目前先不用另外存，逐項回報時直接從 vaultItems 篩掉即可。
    } else if (data.type === 'decryptBatchItemResult') {
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
      }
    } else if (data.type === 'decryptBatchDone') {
      // 找出這批是哪個 batchId（此時對應項目如果全部成功，vaultItems 裡已經不會再有它們了）。
      for (const batchId of decryptingBatchIds.value) {
        const stillHasItems = vaultItems.value.some((item) => item.batchId === batchId)
        if (!stillHasItems) {
          decryptingBatchIds.value.delete(batchId)
        }
      }
      decryptingBatchIds.value.clear()
      if (data.successCount < data.totalCount) {
        showToast(t('alert.batchUnlockPartial', { success: data.successCount, total: data.totalCount }))
      }
    } else if (data.type === 'pathSizesResult') {
      pathSizesResolve?.(data.items)
      pathSizesResolve = null
    } else if (data.type === 'saveRecoveryKeyToFileResult') {
      if (data.success) {
        recoveryKeySaveState.value = 'saved'
      } else if (!data.cancelled) {
        showToast(t('alert.saveFileFailed', { error: data.errorMessage }))
      }
    } else if (data.type === 'inspectLockedFileResult') {
      decryptItemInfo.value = data.success
        ? { uuid: data.uuid, originalName: data.originalName, hint: data.hint, passkeyEnabled: data.passkeyEnabled, recoveryKeyEnabled: data.recoveryKeyEnabled }
        : null
    } else if (data.type === 'error') {
      isEncrypting.value = false
      cancelFakeProgress()
      encryptProgressPercent.value = 0
      isDecrypting.value = false
      isLoadingList.value = false
      isLoadingHistory.value = false
      encryptItemResults.value.push({ path: '', success: false, errorMessage: t('alert.genericError', { message: data.message }), note: '' })
    } else if (data.type === 'pathPicked') {
      if (data.purpose === 'decryptPath') {
        decryptPath.value = data.path
        decryptItemInfo.value = null
        window.chrome.webview.postMessage({ type: 'inspectLockedFile', path: data.path })
      } else if (data.purpose === 'decryptDestination') {
        const item = pendingDecryptItem.value
        const mode = pendingDecryptMode.value
        pendingDecryptItem.value = null
        if (item) {
          if (mode === 'passkey') {
            startPasskeyDecrypt(item, data.path)
          } else if (mode === 'recoveryKey') {
            openRecoveryKeyPrompt(item, data.path)
          } else {
            promptPasswordAndDecrypt(item, data.path)
          }
        }
      } else if (data.purpose === 'vaultFolder') {
        isChangingVaultPath.value = true
        window.chrome.webview.postMessage({ type: 'changeVaultPath', newPath: data.path })
      } else {
        // 資料夾選擇（單選）走這裡，加到清單裡而不是取代整份清單。
        if (!encryptPaths.value.includes(data.path)) {
          encryptPaths.value.push(data.path)
        }
      }
    } else if (data.type === 'pathsPicked') {
      // 加密頁籤的「選擇檔案」允許多選，選完的路徑合併進現有清單（去除重複）。
      for (const path of data.paths) {
        if (!encryptPaths.value.includes(path)) {
          encryptPaths.value.push(path)
        }
      }
    } else if (data.type === 'vaultList') {
      applyAfterMinSkeletonDuration(listLoadStartedAt, () => {
        isLoadingList.value = false
        vaultItems.value = data.items
      })
    } else if (data.type === 'vaultChanged') {
      // 使用者不在清單頁的話什麼都不用做——之後切換分頁時，既有的 watch(activeTab)/
      // watch(activeListSubTab) 邏輯自然會呼叫 refreshList() 拿到最新資料。
      if (activeTab.value === 'list' && activeListSubTab.value === 'files') {
        vaultListStale.value = true
      }
    } else if (data.type === 'historyList') {
      applyAfterMinSkeletonDuration(historyLoadStartedAt, () => {
        isLoadingHistory.value = false
        historyItems.value = data.items
      })
    } else if (data.type === 'deleteRecordResult') {
      handleDeleteRecordResult(data)
    } else if (data.type === 'pathPickCancelled') {
      // 使用者在「自己選地方存」流程中途按了取消，把暫存的項目清掉，避免下次選檔誤觸發解密。
      if (data.purpose === 'decryptDestination') {
        pendingDecryptItem.value = null
      }
    } else if (data.type === 'settingsResult') {
      settingsVaultPath.value = data.vaultPath
      settingsLanguage.value = data.language
      currentLocale.value = data.language
      settingsTheme.value = data.theme
    } else if (data.type === 'changeVaultPathResult') {
      isChangingVaultPath.value = false
      if (data.success) {
        settingsVaultPath.value = data.newPath
        settingsSaveMessage.value = t('settings.vaultMoveSuccess')
      } else {
        showToast(t('settings.vaultMoveFailed', { error: data.errorMessage }))
      }
    } else if (data.type === 'updateSettingResult') {
      settingsSaveMessage.value = t('settings.saved')
      setTimeout(() => { settingsSaveMessage.value = '' }, 2000)
    } else if (data.type === 'windowStateChanged') {
      isWindowMaximized.value = data.isMaximized
    } else if (data.type === 'filesDropped') {
      // 拖放進來的檔案：合併進現有清單（去除重複），不是整份取代——使用者可能已經選了
      // 一些東西，拖放應該是「再加一些」，不是「重新開始」。這則訊息現在來自
      // HandleFilesDroppedFromWebView（見 handleFileDrop 函式），不是原生 WPF 拖放。
      activeTab.value = 'encrypt'
      for (const path of data.paths) {
        if (!encryptPaths.value.includes(path)) {
          encryptPaths.value.push(path)
        }
      }
    } else if (data.type === 'initialPaths') {
      // 從 Shell Extension 右鍵選單過來的路徑清單，切到加密頁籤、整份清單都帶進去。
      activeTab.value = 'encrypt'
      if (data.paths && data.paths.length > 0) {
        encryptPaths.value = [...data.paths]
      }
    }
  })

  // 監聽器掛好之後才要一次設定值（尤其是語言），不要等到使用者自己點進「設定」頁籤才套用——
  // 不然使用者明明上次選了英文，重開 App 卻會先看到繁體中文，要點進設定頁才切回來，體驗很怪。
  window.chrome.webview.postMessage({ type: 'getSettings' })
}

watch(activeTab, (tab) => {
  if (tab === 'list') {
    refreshList()
  } else if (tab === 'settings') {
    window.chrome.webview.postMessage({ type: 'getSettings' })
  }
})

watch(activeListSubTab, (subTab) => {
  if (subTab === 'files') {
    refreshList()
  } else {
    refreshHistory()
  }
})

// 骨架畫面最短顯示時間：資料回來得太快時（例如本機讀取幾乎瞬間完成），骨架只閃現幾毫秒
// 反而像個畫面雜訊、不是有意義的載入提示。這裡保證骨架至少完整顯示過一次呼吸閃爍週期，
// 資料本身跟 isLoadingList 一起延後套用（不能只延後 isLoadingList，vaultItems 只要一有內容，
// 真正的表格就會不管 isLoadingList 直接蓋過骨架顯示，兩個要綁在一起延後才有效）。
const MIN_SKELETON_DURATION_MS = 300
let listLoadStartedAt = 0
let historyLoadStartedAt = 0

function applyAfterMinSkeletonDuration(startedAt, applyFn) {
  const elapsed = Date.now() - startedAt
  const remaining = Math.max(0, MIN_SKELETON_DURATION_MS - elapsed)
  setTimeout(applyFn, remaining)
}

function refreshList() {
  isLoadingList.value = true
  vaultListStale.value = false
  listLoadStartedAt = Date.now()
  window.chrome.webview.postMessage({ type: 'listVault' })
}

// 把清單裡帶有相同 batchId 的項目摺疊成一組，沒有 batchId 的維持獨立顯示。
// 分組本身完全在前端做——後端只負責在每個項目上帶 batchId，分不分組、怎麼呈現都是畫面的事。
const groupedVaultItems = computed(() => {
  const groups = new Map()
  const standalone = []

  for (const item of vaultItems.value) {
    if (item.batchId) {
      if (!groups.has(item.batchId)) {
        groups.set(item.batchId, [])
      }
      groups.get(item.batchId).push(item)
    } else {
      standalone.push(item)
    }
  }

  const result = []
  for (const item of standalone) {
    result.push({ isGroup: false, item })
  }
  for (const [batchId, items] of groups) {
    result.push({ isGroup: true, batchId, items })
  }

  result.sort((a, b) => {
    const latest = (entry) => entry.isGroup
      ? Math.max(...entry.items.map((i) => new Date(i.createdAtUtc).getTime()))
      : new Date(entry.item.createdAtUtc).getTime()
    return latest(b) - latest(a)
  })

  return result
})

function batchPreviewText(items) {
  const names = items.map((i) => i.originalName)
  if (names.length <= 2) {
    return names.join('、')
  }
  return names.slice(0, 2).join('、') + t('batchPreview.suffix', { count: names.length })
}

function toggleGroupExpanded(batchId) {
  if (expandedGroups.value.has(batchId)) {
    expandedGroups.value.delete(batchId)
  } else {
    expandedGroups.value.add(batchId)
  }
}

function decryptGroupViaPassword(group) {
  passwordPromptContext.value = { mode: 'batch', group }
  passwordPromptValue.value = ''
}

function refreshHistory() {
  isLoadingHistory.value = true
  historyLoadStartedAt = Date.now()
  window.chrome.webview.postMessage({ type: 'listHistory' })
}

function pickFile() {
  window.chrome.webview.postMessage({ type: 'pickFile', purpose: 'encryptPath' })
}

function pickFolder() {
  window.chrome.webview.postMessage({ type: 'pickFolder' })
}

function removeEncryptPath(index) {
  encryptPaths.value.splice(index, 1)
}

/// 拖放檔案：一般的 postMessage 只能傳可以轉成 JSON 的資料，瀏覽器沙盒化的 File 物件本身
/// 沒有真正的磁碟路徑可以序列化。WebView2 專門為此開了 postMessageWithAdditionalObjects
/// 這個管道，讓我們可以把 File 物件原封不動連同訊息一起送到 C# 那邊，C# 端會收到對應的
/// CoreWebView2File，讀 .Path 屬性就是真正路徑——見 MainWindow.xaml.cs 的
/// HandleFilesDroppedFromWebView 說明。
function handleFileDrop(event) {
  isDraggingFile.value = false
  const files = event.dataTransfer?.files
  if (!files || files.length === 0) {
    return
  }
  if (!window.chrome?.webview?.postMessageWithAdditionalObjects) {
    return
  }
  window.chrome.webview.postMessageWithAdditionalObjects({ type: 'filesDroppedFromWebView' }, files)
}

function pickVaultFolder() {
  window.chrome.webview.postMessage({ type: 'pickVaultFolder' })
}

function setLanguage(value) {
  settingsLanguage.value = value
  currentLocale.value = value
  window.chrome.webview.postMessage({ type: 'updateSetting', key: 'language', value })
}

function setTheme(value) {
  settingsTheme.value = value
  window.chrome.webview.postMessage({ type: 'updateSetting', key: 'theme', value })
}

function pickLockedFile() {
  window.chrome.webview.postMessage({ type: 'pickFile', purpose: 'decryptPath' })
}

// 「解密」頁籤：直接用 .locked 檔案目前所在的資料夾當還原位置，跟密碼路徑行為一致，不用額外問。
function decryptTabViaPasskey() {
  if (!decryptItemInfo.value) return
  decryptingUuids.value.add(decryptItemInfo.value.uuid)
  window.chrome.webview.postMessage({
    type: 'decryptByPasskey',
    uuid: decryptItemInfo.value.uuid,
    markerPath: decryptPath.value
  })
}

function decryptTabViaRecoveryKey() {
  if (!decryptItemInfo.value) return
  openRecoveryKeyPrompt(
    { uuid: decryptItemInfo.value.uuid, originalName: decryptItemInfo.value.originalName },
    null
  )
  recoveryKeyPromptMarkerPath.value = decryptPath.value
}

async function submitEncrypt() {
  if (encryptPaths.value.length === 0 || !encryptPassword.value) {
    encryptItemResults.value = [{ path: '', success: false, errorMessage: t('encrypt.needAtLeastOne'), note: '' }]
    return
  }
  isEncrypting.value = true
  encryptItemResults.value = []

  const sizeItems = await requestPathSizes(encryptPaths.value)
  startFakeProgress(encryptPaths.value.length, sizeItems)

  // 多個項目時，Passkey／恢復金鑰在畫面上已經鎖住不能勾，這裡再保險一次，不管前端狀態怎樣都不送出去。
  const isBatch = encryptPaths.value.length > 1
  window.chrome.webview.postMessage({
    type: 'encrypt',
    paths: encryptPaths.value,
    password: encryptPassword.value,
    hint: hint.value,
    enablePasskey: isBatch ? false : enablePasskey.value,
    enableRecoveryKey: isBatch ? false : enableRecoveryKey.value
  })
}

function submitDecrypt() {
  if (!decryptPath.value || !decryptPassword.value) {
    decryptResultIsError.value = true
    decryptResultMessage.value = t('decrypt.needPathAndPassword')
    return
  }
  isDecrypting.value = true
  decryptResultMessage.value = ''
  window.chrome.webview.postMessage({
    type: 'decrypt',
    path: decryptPath.value,
    password: decryptPassword.value
  })
}

// 清單頁用密碼解密：先問要還原到原始位置、還是自己選地方存。
async function decryptFromList(item) {
  const choice = await askChoice(
    t('confirm.restoreLocationQuestion', { name: item.originalName, path: item.originalPath }),
    [
      { value: 'original', label: t('choice.restoreToOriginal') },
      { value: 'custom', label: t('choice.chooseLocation') }
    ]
  )

  if (choice === 'original') {
    promptPasswordAndDecrypt(item, null)
  } else if (choice === 'custom') {
    pendingDecryptItem.value = item
    pendingDecryptMode.value = 'password'
    window.chrome.webview.postMessage({ type: 'pickFolder', purpose: 'decryptDestination' })
  }
  // choice 是 null 代表點了背景或按 Esc，真正的取消，什麼都不做。
}

// destinationDir 為 null 代表還原到原始位置。
function promptPasswordAndDecrypt(item, destinationDir) {
  passwordPromptContext.value = { mode: 'single', item, destinationDir }
  passwordPromptValue.value = ''
}

function submitPasswordPrompt() {
  const ctx = passwordPromptContext.value
  const password = passwordPromptValue.value
  if (!ctx || !password) {
    return
  }
  passwordPromptContext.value = null

  if (ctx.mode === 'batch') {
    decryptingBatchIds.value.add(ctx.group.batchId)
    window.chrome.webview.postMessage({
      type: 'decryptBatch',
      uuids: ctx.group.items.map((i) => i.uuid),
      password
    })
  } else {
    decryptingUuids.value.add(ctx.item.uuid)
    window.chrome.webview.postMessage({ type: 'decryptByUuid', uuid: ctx.item.uuid, password, destinationDir: ctx.destinationDir })
  }
}

function cancelPasswordPrompt() {
  passwordPromptContext.value = null
}

// 清單頁用 Passkey 解密：一樣先問還原到原始位置、還是自己選地方存，不需要輸入密碼，
// 選完之後直接觸發 Windows Hello 驗證。
async function decryptFromListViaPasskey(item) {
  const choice = await askChoice(
    t('confirm.restoreLocationQuestion', { name: item.originalName, path: item.originalPath }) + t('confirm.passkeyNote'),
    [
      { value: 'original', label: t('choice.restoreToOriginal') },
      { value: 'custom', label: t('choice.chooseLocation') }
    ]
  )

  if (choice === 'original') {
    startPasskeyDecrypt(item, null)
  } else if (choice === 'custom') {
    pendingDecryptItem.value = item
    pendingDecryptMode.value = 'passkey'
    window.chrome.webview.postMessage({ type: 'pickFolder', purpose: 'decryptDestination' })
  }
}

function startPasskeyDecrypt(item, destinationDir) {
  decryptingUuids.value.add(item.uuid)
  window.chrome.webview.postMessage({ type: 'decryptByPasskey', uuid: item.uuid, destinationDir })
}

// 清單頁用恢復金鑰解密：一樣先問還原到原始位置、還是自己選地方存，接著跳出輸入恢復金鑰的畫面。
async function decryptFromListViaRecoveryKey(item) {
  const choice = await askChoice(
    t('confirm.restoreLocationQuestion', { name: item.originalName, path: item.originalPath }),
    [
      { value: 'original', label: t('choice.restoreToOriginal') },
      { value: 'custom', label: t('choice.chooseLocation') }
    ]
  )

  if (choice === 'original') {
    openRecoveryKeyPrompt(item, null)
  } else if (choice === 'custom') {
    pendingDecryptItem.value = item
    pendingDecryptMode.value = 'recoveryKey'
    window.chrome.webview.postMessage({ type: 'pickFolder', purpose: 'decryptDestination' })
  }
}

function openRecoveryKeyPrompt(item, destinationDir) {
  recoveryKeyPromptItem.value = item
  recoveryKeyPromptDestination.value = destinationDir
  recoveryKeyPromptMarkerPath.value = null
  recoveryKeyInputValue.value = ''
}

function submitRecoveryKeyDecrypt() {
  const item = recoveryKeyPromptItem.value
  if (!item || !recoveryKeyInputValue.value.trim()) {
    return
  }
  decryptingUuids.value.add(item.uuid)
  window.chrome.webview.postMessage({
    type: 'decryptByRecoveryKey',
    uuid: item.uuid,
    recoveryKey: recoveryKeyInputValue.value.trim(),
    destinationDir: recoveryKeyPromptDestination.value,
    markerPath: recoveryKeyPromptMarkerPath.value
  })
  recoveryKeyPromptItem.value = null
  recoveryKeyPromptMarkerPath.value = null
}

function cancelRecoveryKeyPrompt() {
  recoveryKeyPromptItem.value = null
  recoveryKeyPromptMarkerPath.value = null
}

// 恢復金鑰顯示畫面：複製到剪貼簿。
async function copyRecoveryKey() {
  try {
    const copiedValue = recoveryKeyDisplay.value
    await navigator.clipboard.writeText(copiedValue)
    recoveryKeySaveState.value = recoveryKeySaveState.value || 'copied'

    // 恢復金鑰等同密碼，留在剪貼簿裡風險不小（Windows 剪貼簿歷史紀錄會保留好幾筆之前複製過的內容，
    // 甚至可能跨裝置同步）。比照密碼管理工具的慣例，過一段時間自動清空——但只有在剪貼簿裡還是
    // 我們剛剛複製的這份內容時才清，避免蓋掉使用者後來自己複製的別的東西。
    setTimeout(async () => {
      try {
        const current = await navigator.clipboard.readText()
        if (current === copiedValue) {
          await navigator.clipboard.writeText('')
        }
      } catch {
        // 讀取剪貼簿失敗（例如視窗失去焦點時瀏覽器會擋）就算了，不強求。
      }
    }, 45000)
  } catch {
    showToast(t('recoveryKeyModal.copyFailed'))
  }
}

function saveRecoveryKeyToFile() {
  window.chrome.webview.postMessage({
    type: 'saveRecoveryKeyToFile',
    content: t('recoveryKeyModal.fileContent', { key: recoveryKeyDisplay.value }),
    suggestedFileName: t('recoveryKeyModal.suggestedFileName')
  })
}

function acknowledgeRecoveryKey() {
  recoveryKeySaveState.value = 'acknowledged'
}

function closeRecoveryKeyDisplay() {
  recoveryKeyDisplay.value = ''
  recoveryKeySaveState.value = ''
}

async function requestDelete(item) {
  const confirmed = await askConfirm(t('confirm.deleteWarning', { name: item.originalName }), {
    confirmLabel: t('list.delete'),
    variant: 'danger'
  })
  if (!confirmed) {
    return
  }
  window.chrome.webview.postMessage({ type: 'deleteRecord', uuid: item.uuid })
}

function handleDeleteRecordResult(data) {
  if (data.success) {
    vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
    return
  }
  if (data.blockedByNestedLocks) {
    showToast(t('alert.deleteBlockedByNested', { count: data.nestedUuids.length }))
    return
  }
  showToast(translateError(data.errorCode, null, t('alert.deleteFailed', { error: data.errorMessage })))
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function formatDate(isoString) {
  return new Date(isoString).toLocaleString(currentLocale.value === 'en' ? 'en-US' : 'zh-TW')
}

function typeLabel(type) {
  return type === 'Folder' ? t('type.folder') : t('type.file')
}

function actionLabel(action) {
  return t(`action.${action}`) !== `action.${action}` ? t(`action.${action}`) : action
}

function unlockMethodLabel(method) {
  return { password: t('unlockMethod.password'), passkey: t('unlockMethod.passkey'), recoveryKey: t('unlockMethod.recoveryKey') }[method] || t('unlockMethod.unknown')
}

function historyDetailText(entry) {
  if (entry.action === 'Encrypted') {
    const parts = []
    if (entry.sourcePath) parts.push(t('historyDetail.source', { path: entry.sourcePath }))
    parts.push(t('historyDetail.passkeyStatus', { status: entry.passkeyEnabled ? t('historyDetail.enabled') : t('historyDetail.disabled') }))
    parts.push(t('historyDetail.recoveryKeyStatus', { status: entry.recoveryKeyEnabled ? t('historyDetail.enabled') : t('historyDetail.disabled') }))
    return parts.join('｜')
  }
  if (entry.action === 'Decrypted') {
    const parts = [t('historyDetail.unlockMethod', { method: unlockMethodLabel(entry.unlockMethod) })]
    if (entry.restoredPath) parts.push(t('historyDetail.restoredTo', { path: entry.restoredPath }))
    return parts.join('｜')
  }
  return entry.detail || ''
}
</script>

<template>
  <div class="app" :class="{ 'app--dark': settingsTheme === 'dark' }">
    <!-- 自訂標題列：整條都是可拖曳區域（app-region: drag），交給作業系統的視窗管理員
         原生處理拖曳，所以能得到 Aero Snap、雙擊最大化、右鍵系統選單這些原生行為。
         三顆按鈕本身標記成 no-drag，否則點下去只會開始拖視窗、按不到按鈕。 -->
    <header class="title-bar">
      <div class="traffic-lights">
        <button
          class="traffic-light traffic-light--close"
          type="button"
          :title="t('window.close')"
          :aria-label="t('window.close')"
          @click="closeWindow"
        >
          <svg viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M3.5 3.5l5 5M8.5 3.5l-5 5" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>
        </button>
        <button
          class="traffic-light traffic-light--minimize"
          type="button"
          :title="t('window.minimize')"
          :aria-label="t('window.minimize')"
          @click="minimizeWindow"
        >
          <svg viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M3 6h6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>
        </button>
        <button
          class="traffic-light traffic-light--maximize"
          type="button"
          :title="isWindowMaximized ? t('window.restore') : t('window.maximize')"
          :aria-label="isWindowMaximized ? t('window.restore') : t('window.maximize')"
          @click="toggleMaximizeWindow"
        >
          <svg v-if="!isWindowMaximized" viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M4 4h4v4z" fill="currentColor"/><path d="M8 8H4V4z" fill="currentColor" opacity="0"/><path d="M3.6 3.6h4.8v4.8z" fill="currentColor"/></svg>
          <svg v-else viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M3.2 6.4h5.6M6.4 3.2v5.6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" opacity="0"/><path d="M3.5 5.2h3.3v3.3zM5.2 3.5h3.3v3.3z" fill="currentColor"/></svg>
        </button>
      </div>
      <span class="title-bar__title">FileLocker</span>
    </header>

    <nav class="tab-bar">
      <button :ref="(el) => setTabRef('encrypt', el)" class="tab-bar__item" :class="{ 'is-active': activeTab === 'encrypt' }" @click="activeTab = 'encrypt'">{{ t('tab.encrypt') }}</button>
      <button :ref="(el) => setTabRef('decrypt', el)" class="tab-bar__item" :class="{ 'is-active': activeTab === 'decrypt' }" @click="activeTab = 'decrypt'">{{ t('tab.decrypt') }}</button>
      <button :ref="(el) => setTabRef('list', el)" class="tab-bar__item" :class="{ 'is-active': activeTab === 'list' }" @click="activeTab = 'list'">{{ t('tab.list') }}</button>
      <button :ref="(el) => setTabRef('settings', el)" class="tab-bar__item" :class="{ 'is-active': activeTab === 'settings' }" @click="activeTab = 'settings'">{{ t('tab.settings') }}</button>
      <span class="tab-bar__indicator" :style="tabIndicatorStyle"></span>
    </nav>

    <div class="page-wrapper">
      <main class="page" :class="{ 'page--wide': activeTab === 'list' }">
        <div v-if="activeTab === 'encrypt'">
          <h1 class="page-title">
            <svg class="page-title__icon" viewBox="0 0 24 24" fill="none"><path d="M6 10V8a6 6 0 1 1 12 0v2" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/><rect x="4" y="10" width="16" height="11" rx="2.5" stroke="currentColor" stroke-width="1.8"/><circle cx="12" cy="15" r="1.6" fill="currentColor"/></svg>
            {{ t('encrypt.title') }}
          </h1>

          <div class="field">
            <label class="field__label">{{ t('encrypt.itemsLabel') }}</label>
            <div class="button-row">
              <button class="button button--secondary" @click="pickFile" type="button">{{ t('encrypt.pickFiles') }}</button>
              <button class="button button--secondary" @click="pickFolder" type="button">{{ t('encrypt.pickFolder') }}</button>
            </div>
            <div
              v-if="encryptPaths.length === 0"
              class="dropzone"
              :class="{ 'is-dragging': isDraggingFile }"
              @dragover.prevent="isDraggingFile = true"
              @dragleave.prevent="isDraggingFile = false"
              @drop.prevent="handleFileDrop"
            >
              <svg class="dropzone__icon" viewBox="0 0 24 24" fill="none"><path d="M12 4v11m0-11 4 4m-4-4-4 4" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><path d="M4 16v2.5A1.5 1.5 0 0 0 5.5 20h13a1.5 1.5 0 0 0 1.5-1.5V16" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>
              <p class="dropzone__text">{{ t('encrypt.dropHint') }}</p>
            </div>
            <ul v-else class="item-list">
              <li v-for="(path, index) in encryptPaths" :key="path" class="item-list__row">
                <span class="item-list__path" :title="path">{{ path }}</span>
                <button class="link-button" @click="removeEncryptPath(index)" type="button">{{ t('encrypt.remove') }}</button>
              </li>
            </ul>
          </div>

          <div class="field">
            <label class="field__label">{{ t('encrypt.passwordLabel') }}</label>
            <input v-model="encryptPassword" type="password" class="text-input" />
          </div>

          <div class="field">
            <label class="field__label">{{ t('encrypt.hintLabel') }}</label>
            <input v-model="hint" class="text-input" />
          </div>

          <div class="field">
            <label class="checkbox-field" :class="{ 'is-disabled': encryptPaths.length > 1 }">
              <input type="checkbox" v-model="enablePasskey" :disabled="encryptPaths.length > 1" />
              <img :src="passkeyIconUrl" alt="" class="checkbox-field__icon" />
              <span>{{ t('encrypt.passkeyLabel') }}</span>
              <span class="info-tooltip" tabindex="0">
                <span class="info-tooltip__icon">i</span>
                <span class="info-tooltip__bubble">{{ t('encrypt.passkeyLabelDetail') }}</span>
              </span>
            </label>
            <p v-if="encryptPaths.length > 1" class="hint-text hint-text--indented">
              {{ t('encrypt.passkeyBatchDisabled') }}
            </p>
          </div>

          <div class="field">
            <label class="checkbox-field" :class="{ 'is-disabled': encryptPaths.length > 1 }">
              <input type="checkbox" v-model="enableRecoveryKey" :disabled="encryptPaths.length > 1" />
              <img :src="recoveryKeyIconUrl" alt="" class="checkbox-field__icon" />
              <span>{{ t('encrypt.recoveryKeyLabel') }}</span>
              <span class="info-tooltip" tabindex="0">
                <span class="info-tooltip__icon">i</span>
                <span class="info-tooltip__bubble">{{ t('encrypt.recoveryKeyLabelDetail') }}</span>
              </span>
            </label>
            <p v-if="encryptPaths.length > 1" class="hint-text hint-text--indented">
              {{ t('encrypt.recoveryKeyBatchDisabled') }}
            </p>
          </div>

          <button class="button button--primary" @click="submitEncrypt" :disabled="isEncrypting">
            {{ isEncrypting
              ? t(encryptPhaseLabel === 'compressing' ? 'encrypt.compressing' : 'encrypt.encrypting', { current: encryptItemResults.length, total: encryptBatchTotal })
              : t('encrypt.submit') }}
          </button>

          <div v-if="isEncrypting" class="progress-bar" role="progressbar" :aria-valuenow="Math.round(encryptProgressPercent)" aria-valuemin="0" aria-valuemax="100">
            <div class="progress-bar__fill" :style="{ width: encryptProgressPercent + '%' }"></div>
          </div>

          <TransitionGroup name="result-row" tag="div" class="result-list">
            <div v-for="(item, index) in encryptItemResults" :key="index" class="result-row" :class="item.success ? 'result-row--success' : 'result-row--error'">
              <span class="result-row__icon">{{ item.success ? '✓' : '✕' }}</span>
              <span>
                <template v-if="item.path">{{ item.path }}</template>
                <span v-if="item.errorMessage"> — {{ item.errorMessage }}</span>
                <span v-if="item.note"> — {{ item.note }}</span>
              </span>
            </div>
          </TransitionGroup>
        </div>

        <div v-else-if="activeTab === 'decrypt'">
          <h1 class="page-title">
            <svg class="page-title__icon" viewBox="0 0 24 24" fill="none"><path d="M6 10V8a6 6 0 0 1 11.2-3" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/><rect x="4" y="10" width="16" height="11" rx="2.5" stroke="currentColor" stroke-width="1.8"/><circle cx="12" cy="15" r="1.6" fill="currentColor"/></svg>
            {{ t('decrypt.title') }}
          </h1>

          <div class="field">
            <label class="field__label">{{ t('decrypt.lockedPathLabel') }}</label>
            <input v-model="decryptPath" :placeholder="t('decrypt.lockedPathPlaceholder')" class="text-input text-input--mono" />
            <div class="button-row">
              <button class="button button--secondary" @click="pickLockedFile" type="button">{{ t('decrypt.pickLockedFile') }}</button>
            </div>
          </div>

          <div class="field">
            <label class="field__label">{{ t('decrypt.passwordLabel') }}</label>
            <input v-model="decryptPassword" type="password" class="text-input" />
          </div>

          <button class="button button--primary" @click="submitDecrypt" :disabled="isDecrypting">
            {{ isDecrypting ? t('decrypt.decrypting') : t('decrypt.submit') }}
          </button>

          <div v-if="decryptItemInfo && (decryptItemInfo.passkeyEnabled || decryptItemInfo.recoveryKeyEnabled)" class="alt-methods">
            <p class="alt-methods__label">{{ t('decrypt.altMethodsAvailable') }}</p>
            <div class="button-row">
              <button v-if="decryptItemInfo.passkeyEnabled" class="button button--secondary" @click="decryptTabViaPasskey" type="button" :disabled="decryptingUuids.has(decryptItemInfo.uuid)">
                <img :src="passkeyIconUrl" alt="" class="button__icon" />
                {{ t('decrypt.passkeyUnlock') }}
              </button>
              <button v-if="decryptItemInfo.recoveryKeyEnabled" class="button button--secondary" @click="decryptTabViaRecoveryKey" type="button">
                <img :src="recoveryKeyIconUrl" alt="" class="button__icon" />
                {{ t('decrypt.recoveryKeyUnlock') }}
              </button>
            </div>
          </div>

          <p v-if="decryptResultMessage" class="status-message" :class="decryptResultIsError ? 'status-message--error' : 'status-message--success'">
            {{ decryptResultMessage }}
          </p>
        </div>

        <div v-else-if="activeTab === 'list'">
          <h1 class="page-title">
            <svg class="page-title__icon" viewBox="0 0 24 24" fill="none"><path d="M4 6h16M4 12h16M4 18h10" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>
            {{ t('list.title') }}
          </h1>

          <div class="sub-tab-bar">
            <button class="sub-tab-bar__item" :class="{ 'is-active': activeListSubTab === 'files' }" @click="activeListSubTab = 'files'">{{ t('list.subTabFiles') }}</button>
            <button class="sub-tab-bar__item" :class="{ 'is-active': activeListSubTab === 'history' }" @click="activeListSubTab = 'history'">{{ t('list.subTabHistory') }}</button>
          </div>

          <div v-if="activeListSubTab === 'files'">
            <div v-if="vaultListStale" class="update-banner" @click="refreshList">
              {{ t('list.updateAvailable') }}
            </div>
            <button class="button button--secondary refresh-button" @click="refreshList" :disabled="isLoadingList">
              {{ isLoadingList ? t('list.loading') : t('list.refresh') }}
            </button>
            <div v-if="!isLoadingList && vaultItems.length === 0" class="empty-state-block">
              <svg class="empty-state-block__icon" viewBox="0 0 24 24" fill="none"><rect x="4" y="10" width="16" height="11" rx="2.5" stroke="currentColor" stroke-width="1.6"/><path d="M8 10V8a4 4 0 1 1 8 0v2" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>
              <p class="empty-state-block__text">{{ t('list.noItems') }}</p>
            </div>

            <!-- 骨架畫面：第一次載入、還沒有任何資料時顯示，用灰色色塊模擬表格結構，資料回來
                 之前先讓畫面「看起來已經有東西」，感覺是漸漸浮現，不是空白一段時間後憑空跳出來。
                 已經有資料、只是重新整理的情況不顯示骨架——那樣每次按重新整理畫面都閃一下，
                 反而干擾，直接讓舊資料留著，等新資料回來再替換就好。 -->
            <div v-if="isLoadingList && vaultItems.length === 0" class="table-scroll">
              <table class="table table--auto">
                <thead>
                  <tr>
                    <th>{{ t('list.colName') }}</th>
                    <th>{{ t('list.colType') }}</th>
                    <th>{{ t('list.colSize') }}</th>
                    <th>{{ t('list.colHint') }}</th>
                    <th>{{ t('list.colTime') }}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="n in 8" :key="n">
                    <td><span class="skeleton-block" style="width: 70%;"></span></td>
                    <td><span class="skeleton-block" style="width: 50%;"></span></td>
                    <td><span class="skeleton-block" style="width: 40%;"></span></td>
                    <td><span class="skeleton-block" style="width: 30%;"></span></td>
                    <td><span class="skeleton-block" style="width: 60%;"></span></td>
                    <td><span class="skeleton-block" style="width: 80%;"></span></td>
                  </tr>
                </tbody>
              </table>
            </div>

            <div v-if="vaultItems.length > 0" class="table-scroll">
              <table class="table table--auto">
                <thead>
                  <tr>
                    <th>{{ t('list.colName') }}</th>
                    <th>{{ t('list.colType') }}</th>
                    <th>{{ t('list.colSize') }}</th>
                    <th>{{ t('list.colHint') }}</th>
                    <th>{{ t('list.colTime') }}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <template v-for="group in groupedVaultItems" :key="group.isGroup ? group.batchId : group.item.uuid">
                    <!-- 獨立項目（沒有 batchId）：跟之前一樣直接顯示一列。 -->
                    <tr v-if="!group.isGroup">
                      <td>
                        <div class="cell-name" :title="group.item.originalName">{{ group.item.originalName }}</div>
                        <span v-if="group.item.hasNestedLocks" class="badge" :title="t('list.nestedLockTitle')">🔒 ×{{ group.item.nestedLockCount }}</span>
                        <div v-if="!group.item.markerFound" class="status-warning">{{ t('list.markerMissing', { message: group.item.markerStatusMessage }) }}</div>
                      </td>
                      <td>{{ typeLabel(group.item.type) }}</td>
                      <td>{{ formatSize(group.item.originalSizeBytes) }}</td>
                      <td>{{ group.item.hint || t('list.hintNone') }}</td>
                      <td>{{ formatDate(group.item.createdAtUtc) }}</td>
                      <td>
                        <div class="table__actions">
                          <button class="button button--tiny" @click="decryptFromList(group.item)" type="button" :disabled="decryptingUuids.has(group.item.uuid)">
                            {{ decryptingUuids.has(group.item.uuid) ? t('list.decrypting') : t('list.decrypt') }}
                          </button>
                          <button
                            v-if="group.item.passkeyEnabled"
                            class="button button--tiny"
                            @click="decryptFromListViaPasskey(group.item)"
                            type="button"
                            :disabled="decryptingUuids.has(group.item.uuid)"
                          >
                            <img :src="passkeyIconUrl" alt="" class="button__icon" />
                            {{ t('decrypt.passkeyUnlock') }}
                          </button>
                          <button
                            v-if="group.item.recoveryKeyEnabled"
                            class="button button--tiny"
                            @click="decryptFromListViaRecoveryKey(group.item)"
                            type="button"
                            :disabled="decryptingUuids.has(group.item.uuid)"
                          >
                            <img :src="recoveryKeyIconUrl" alt="" class="button__icon" />
                            {{ t('decrypt.recoveryKeyUnlock') }}
                          </button>
                          <button class="link-button link-button--danger" @click="requestDelete(group.item)" type="button">{{ t('list.delete') }}</button>
                        </div>
                      </td>
                    </tr>

                    <!-- 批次群組：一次選多個項目加密出來的，摺疊成一列，展開後每個項目維持獨立操作能力。 -->
                    <template v-else>
                      <tr class="group-row">
                        <td colspan="6">
                          <div class="group-row__inner">
                            <button class="group-row__toggle" @click="toggleGroupExpanded(group.batchId)" type="button">
                              <span class="group-row__chevron" :class="{ 'is-expanded': expandedGroups.has(group.batchId) }">▸</span>
                              {{ batchPreviewText(group.items) }}
                            </button>
                            <button
                              class="button button--tiny"
                              @click="decryptGroupViaPassword(group)"
                              type="button"
                              :disabled="decryptingBatchIds.has(group.batchId)"
                            >
                              {{ decryptingBatchIds.has(group.batchId) ? t('list.unlockAllInProgress') : t('list.unlockAll') }}
                            </button>
                          </div>
                        </td>
                      </tr>
                      <template v-if="expandedGroups.has(group.batchId)">
                        <tr v-for="item in group.items" :key="item.uuid" class="table__row--nested">
                          <td>
                            <div class="cell-name" :title="item.originalName">{{ item.originalName }}</div>
                            <span v-if="item.hasNestedLocks" class="badge" :title="t('list.nestedLockTitle')">🔒 ×{{ item.nestedLockCount }}</span>
                            <div v-if="!item.markerFound" class="status-warning">{{ t('list.markerMissing', { message: item.markerStatusMessage }) }}</div>
                          </td>
                          <td>{{ typeLabel(item.type) }}</td>
                          <td>{{ formatSize(item.originalSizeBytes) }}</td>
                          <td>{{ item.hint || t('list.hintNone') }}</td>
                          <td>{{ formatDate(item.createdAtUtc) }}</td>
                          <td>
                            <div class="table__actions">
                              <button class="button button--tiny" @click="decryptFromList(item)" type="button" :disabled="decryptingUuids.has(item.uuid)">
                                {{ decryptingUuids.has(item.uuid) ? t('list.decrypting') : t('list.decrypt') }}
                              </button>
                              <button
                                v-if="item.passkeyEnabled"
                                class="button button--tiny"
                                @click="decryptFromListViaPasskey(item)"
                                type="button"
                                :disabled="decryptingUuids.has(item.uuid)"
                              >
                                <img :src="passkeyIconUrl" alt="" class="button__icon" />
                                {{ t('decrypt.passkeyUnlock') }}
                              </button>
                              <button
                                v-if="item.recoveryKeyEnabled"
                                class="button button--tiny"
                                @click="decryptFromListViaRecoveryKey(item)"
                                type="button"
                                :disabled="decryptingUuids.has(item.uuid)"
                              >
                                <img :src="recoveryKeyIconUrl" alt="" class="button__icon" />
                                {{ t('decrypt.recoveryKeyUnlock') }}
                              </button>
                              <button class="link-button link-button--danger" @click="requestDelete(item)" type="button">{{ t('list.delete') }}</button>
                            </div>
                          </td>
                        </tr>
                      </template>
                    </template>
                  </template>
                </tbody>
              </table>
            </div>
          </div>

          <div v-else>
            <button class="button button--secondary refresh-button" @click="refreshHistory" :disabled="isLoadingHistory">
              {{ isLoadingHistory ? t('list.loading') : t('list.refresh') }}
            </button>
              <div v-if="!isLoadingHistory && historyItems.length === 0" class="empty-state-block">
            <svg class="empty-state-block__icon" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="8.5" stroke="currentColor" stroke-width="1.6"/><path d="M12 7.5V12l3 2" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            <p class="empty-state-block__text">{{ t('list.noHistory') }}</p>
          </div>

            <div v-if="isLoadingHistory && historyItems.length === 0" class="table-scroll">
              <table class="table">
                <colgroup>
                  <col style="width: 24%;" />
                  <col style="width: 12%;" />
                  <col style="width: 16%;" />
                  <col style="width: 48%;" />
                </colgroup>
                <thead>
                  <tr>
                    <th>{{ t('list.colName') }}</th>
                    <th>{{ t('list.historyColAction') }}</th>
                    <th>{{ t('list.historyColTime') }}</th>
                    <th>{{ t('list.historyColDetail') }}</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="n in 8" :key="n">
                    <td><span class="skeleton-block" style="width: 65%;"></span></td>
                    <td><span class="skeleton-block" style="width: 45%;"></span></td>
                    <td><span class="skeleton-block" style="width: 55%;"></span></td>
                    <td><span class="skeleton-block" style="width: 85%;"></span></td>
                  </tr>
                </tbody>
              </table>
            </div>

            <div v-if="historyItems.length > 0" class="table-scroll">
              <table class="table">
                <colgroup>
                  <col style="width: 24%;" />
                  <col style="width: 12%;" />
                  <col style="width: 16%;" />
                  <col style="width: 48%;" />
                </colgroup>
                <thead>
                  <tr>
                    <th>{{ t('list.colName') }}</th>
                    <th>{{ t('list.historyColAction') }}</th>
                    <th>{{ t('list.historyColTime') }}</th>
                    <th>{{ t('list.historyColDetail') }}</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(entry, index) in historyItems" :key="index">
                    <td class="table__wrap-cell" :title="entry.originalName">{{ entry.originalName }}</td>
                    <td>{{ actionLabel(entry.action) }}</td>
                    <td>{{ formatDate(entry.timestampUtc) }}</td>
                    <td class="table__detail-cell table__wrap-cell" :title="historyDetailText(entry)">{{ historyDetailText(entry) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </div>

        <div v-else-if="activeTab === 'settings'">
          <h1 class="page-title">
            <svg class="page-title__icon" viewBox="0 0 24 24" fill="none"><path d="M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8Z" stroke="currentColor" stroke-width="1.8"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>
            {{ t('settings.title') }}
          </h1>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.vaultLocationTitle') }}</h3>
            <p class="vault-path-display" :title="settingsVaultPath">{{ settingsVaultPath }}</p>
            <button class="button button--secondary" @click="pickVaultFolder" type="button" :disabled="isChangingVaultPath">
              {{ isChangingVaultPath ? t('settings.vaultMoving') : t('settings.vaultMove') }}
            </button>
            <p class="hint-text">{{ t('settings.vaultMoveHint') }}</p>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.languageTitle') }}</h3>
            <select class="select-input" :value="settingsLanguage" @change="setLanguage($event.target.value)">
              <option value="zh-TW">繁體中文</option>
              <option value="en">English</option>
            </select>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.themeTitle') }}</h3>
            <div class="button-row">
              <button class="button button--secondary" @click="setTheme('light')" type="button" :disabled="settingsTheme === 'light'">
                <img :src="lightModeIconUrl" alt="" class="button__icon" />
                {{ t('settings.themeLight') }}
              </button>
              <button class="button button--secondary" @click="setTheme('dark')" type="button" :disabled="settingsTheme === 'dark'">
                <img :src="darkModeIconUrl" alt="" class="button__icon" />
                {{ t('settings.themeDark') }}
              </button>
            </div>
            <p class="hint-text">{{ t('settings.themeHint') }}</p>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.helpTitle') }}</h3>
            <button class="button button--secondary" @click="isHelpOpen = true" type="button">{{ t('settings.helpButton') }}</button>
          </section>

          <p v-if="settingsSaveMessage" class="status-message status-message--success">{{ settingsSaveMessage }}</p>
        </div>
      </main>
    </div>

    <!-- 通知（取代原生 alert()） -->
    <div class="toast-stack">
      <TransitionGroup name="toast">
        <div v-for="toast in toasts" :key="toast.id" class="toast" :class="`toast--${toast.kind}`" @click="dismissToast(toast.id)">
          <svg v-if="toast.kind === 'success'" class="toast__icon" viewBox="0 0 20 20" fill="none"><circle cx="10" cy="10" r="8.5" stroke="currentColor" stroke-width="1.6"/><path d="M6.5 10.2l2.2 2.2 4.8-5" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>
          <svg v-else class="toast__icon" viewBox="0 0 20 20" fill="none"><circle cx="10" cy="10" r="8.5" stroke="currentColor" stroke-width="1.6"/><path d="M10 6v5" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/><circle cx="10" cy="13.8" r="1" fill="currentColor"/></svg>
          <span>{{ toast.message }}</span>
        </div>
      </TransitionGroup>
    </div>

    <!-- 確認對話框（取代原生 confirm()）：只用在真正的二選一（做／不做同一件事）。 -->
    <Transition name="modal">
      <div v-if="confirmDialogState" class="modal-overlay" @click.self="resolveConfirmDialog(false)">
        <div class="modal">
          <p class="modal__message">{{ confirmDialogState.message }}</p>
          <div class="modal__footer">
            <button class="button button--secondary" @click="resolveConfirmDialog(false)" type="button">{{ confirmDialogState.cancelLabel }}</button>
            <button
              class="button"
              :class="confirmDialogState.variant === 'danger' ? 'button--danger' : 'button--primary'"
              @click="resolveConfirmDialog(true)"
              type="button"
            >
              {{ confirmDialogState.confirmLabel }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 三選一對話框：用在「還原到原始位置」還是「自己選位置」這種情境，兩個按鈕各自標示
         清楚意圖，不套用確定/取消的語意。點背景關閉等同真正的取消，什麼都不做。 -->
    <Transition name="modal">
      <div v-if="choiceDialogState" class="modal-overlay" @click.self="resolveChoiceDialog(null)">
        <div class="modal">
          <p class="modal__message">{{ choiceDialogState.message }}</p>
          <div class="modal__footer modal__footer--stacked">
            <button
              v-for="choice in choiceDialogState.choices"
              :key="choice.value"
              class="button button--secondary"
              @click="resolveChoiceDialog(choice.value)"
              type="button"
            >
              {{ choice.label }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 使用說明彈窗：內容比較長，用可以捲動的樣式處理。 -->
    <Transition name="modal">
      <div v-if="isHelpOpen" class="modal-overlay" @click.self="isHelpOpen = false">
        <div class="modal modal--help">
          <h2 class="modal__title">{{ t('help.title') }}</h2>
          <div class="modal--help__body">
            <section class="modal--help__section">
              <h3>{{ t('help.basicsTitle') }}</h3>
              <p>{{ t('help.basicsBody') }}</p>
            </section>
            <section class="modal--help__section">
              <h3>{{ t('help.howItWorksTitle') }}</h3>
              <p>{{ t('help.howItWorksBody') }}</p>
            </section>
            <section class="modal--help__section">
              <h3>{{ t('help.precautionsTitle') }}</h3>
              <p>{{ t('help.precautionsBody') }}</p>
            </section>
          </div>
          <div class="modal__footer modal__footer--center">
            <button class="button button--primary" @click="isHelpOpen = false" type="button">{{ t('recoveryKeyModal.close') }}</button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 恢復金鑰顯示彈窗：加密成功且開啟了恢復金鑰時跳出，強制使用者做選擇才能關閉。
         這是整個 App 裡刻意做出視覺差異的一個畫面——風險最高、最需要使用者專注的一刻，
         用類似「封印/證書」的處理讓它明顯跟其他畫面不一樣。 -->
    <Transition name="modal">
      <div v-if="recoveryKeyDisplay" class="modal-overlay">
        <div class="modal modal--signature">
          <img :src="lockedWaxSealUrl" alt="" class="modal--signature__seal" />
          <h2 class="modal__title">{{ t('recoveryKeyModal.title') }}</h2>
          <p class="modal--signature__warning">{{ t('recoveryKeyModal.warning') }}</p>
          <div class="recovery-key-display" tabindex="0">{{ recoveryKeyDisplay }}</div>
          <div class="modal__actions modal__actions--wrap">
            <button class="button button--secondary" @click="copyRecoveryKey" type="button">{{ t('recoveryKeyModal.copy') }}</button>
            <button class="button button--secondary" @click="saveRecoveryKeyToFile" type="button">{{ t('recoveryKeyModal.saveToFile') }}</button>
            <button class="button button--secondary" @click="acknowledgeRecoveryKey" type="button">{{ t('recoveryKeyModal.acknowledge') }}</button>
          </div>
          <p v-if="recoveryKeySaveState === 'saved'" class="status-message status-message--success">{{ t('recoveryKeyModal.savedNotice') }}</p>
          <p v-if="recoveryKeySaveState === 'copied'" class="status-message status-message--success">{{ t('recoveryKeyModal.copiedNotice') }}</p>
          <div class="modal__footer modal__footer--center">
            <button class="button button--primary" @click="closeRecoveryKeyDisplay" type="button" :disabled="!recoveryKeySaveState">
              {{ recoveryKeySaveState ? t('recoveryKeyModal.close') : t('recoveryKeyModal.closeDisabled') }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 密碼輸入彈窗：取代原本明碼顯示的 prompt()，用遮罩密碼欄位。 -->
    <Transition name="modal">
      <div v-if="passwordPromptContext" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('passwordPrompt.title') }}</h2>
          <p v-if="passwordPromptContext.mode === 'single'" class="modal__subtitle">{{ t('passwordPrompt.unlockSingle', { name: passwordPromptContext.item.originalName }) }}</p>
          <p v-else class="modal__subtitle">{{ t('passwordPrompt.unlockBatch', { count: passwordPromptContext.group.items.length, preview: batchPreviewText(passwordPromptContext.group.items) }) }}</p>
          <input
            ref="passwordPromptInputRef"
            v-model="passwordPromptValue"
            type="password"
            class="text-input"
            @keyup.enter="submitPasswordPrompt"
          />
          <div class="modal__footer">
            <button class="button button--secondary" @click="cancelPasswordPrompt" type="button">{{ t('passwordPrompt.cancel') }}</button>
            <button class="button button--primary" @click="submitPasswordPrompt" type="button" :disabled="!passwordPromptValue">{{ t('passwordPrompt.unlock') }}</button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 恢復金鑰輸入彈窗：清單頁按「恢復金鑰解鎖」後跳出。 -->
    <Transition name="modal">
      <div v-if="recoveryKeyPromptItem" class="modal-overlay">
        <div class="modal">
          <h2 class="modal__title">{{ t('recoveryKeyPrompt.title') }}</h2>
          <p class="modal__subtitle">{{ t('recoveryKeyPrompt.unlock', { name: recoveryKeyPromptItem.originalName }) }}</p>
          <textarea
            ref="recoveryKeyInputRef"
            v-model="recoveryKeyInputValue"
            rows="3"
            class="text-input text-input--mono"
            :placeholder="t('recoveryKeyPrompt.placeholder')"
          ></textarea>
          <div class="modal__footer">
            <button class="button button--secondary" @click="cancelRecoveryKeyPrompt" type="button">{{ t('recoveryKeyPrompt.cancel') }}</button>
            <button class="button button--primary" @click="submitRecoveryKeyDecrypt" type="button" :disabled="!recoveryKeyInputValue.trim()">{{ t('recoveryKeyPrompt.submit') }}</button>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>

<style>
:root {
  /* ---- 色彩：扣著「鎖與鑰匙」這個主題發想 ---- */
  --color-bg: #EDEEF2;
  --color-surface: #FFFFFF;
  --color-border: #E1E4EA;
  --color-border-strong: #C9CDD6;
  --color-text: #1B1E24;
  --color-text-secondary: #454A54;
  --color-text-tertiary: #6B707A;
  --color-accent: #A8770F;
  --color-accent-hover: #8C630C;
  --color-accent-soft: #FBF2DE;
  --color-accent-border: #E4C77E;
  --color-success: #2E7D4F;
  --color-success-soft: #E7F4EC;
  --color-danger: #B14328;
  --color-danger-soft: #FBEBE6;

  --font-ui: 'IBM Plex Sans', -apple-system, 'Segoe UI', sans-serif;
  --font-mono: 'IBM Plex Mono', 'Cascadia Code', 'Consolas', monospace;

  --radius-sm: 6px;
  --radius-md: 10px;
  --radius-lg: 16px;

  /* ---- 陰影：用來做出真正的層次深度，取代單薄的 1px 邊框 ---- */
  --shadow-xs: 0 1px 2px rgba(20, 22, 30, 0.05);
  --shadow-sm: 0 1px 3px rgba(20, 22, 30, 0.04), 0 8px 20px rgba(20, 22, 30, 0.06);
  --shadow-md: 0 4px 10px rgba(20, 22, 30, 0.06), 0 16px 32px rgba(20, 22, 30, 0.08);
  --shadow-modal: 0 24px 64px rgba(20, 22, 30, 0.28), 0 2px 8px rgba(20, 22, 30, 0.12);

  /* ---- 動效：進場用 ease-out（不用內建的弱曲線），離場更快 ---- */
  --ease-out: cubic-bezier(0.23, 1, 0.32, 1);
  --duration-fast: 150ms;
  --duration-base: 200ms;
}

/* ---- 深色模式：色彩變數整組覆蓋，其他所有樣式規則都直接沿用同一套 var()，不用另外寫
   一份深色專用的樣式。強調色（黃銅）在深色背景上調亮一點，不然對比度不夠、看起來髒髒的。 ---- */
.app--dark {
  --color-bg: #1C1D21;
  --color-surface: #232428;
  --color-border: #34363C;
  --color-border-strong: #454850;
  --color-text: #ECEDEF;
  --color-text-secondary: #B0B4BC;
  --color-text-tertiary: #82868F;
  --color-accent: #D9A83B;
  --color-accent-hover: #E8B94F;
  --color-accent-soft: #3A3220;
  --color-accent-border: #6B5726;
  --color-success: #4EAE76;
  --color-success-soft: #1E3327;
  --color-danger: #E17153;
  --color-danger-soft: #3A2620;
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
}

.app {
  font-family: var(--font-ui);
  color: var(--color-text);
  background: var(--color-surface);
  /* 改成固定滿版高度的 flex 直向排列，標題列跟頁籤列是不會縮的固定項目，
     只有底下的內容區（.page-wrapper）自己捲動——不然內容一多，整個文件（含標題列、
     三顆視窗控制按鈕）會一起被捲走，使用者往下滑就看不到、按不到那些按鈕了。 */
  height: 100vh;
  display: flex;
  flex-direction: column;
  font-size: 14px;
  line-height: 1.55;
  -webkit-font-smoothing: antialiased;
  overflow-x: hidden;
  text-align: left;
}

/* Vite 專案範本預設的 style.css 會設定 #app { text-align: center } 跟 h1 的字級/顏色，
   跟這裡的設計系統直接衝突（文字被強制置中、標題顏色被蓋掉）。正解是把那份 import 從
   main.js 移除；這幾條是防禦性覆蓋，確保就算它還在也不會影響畫面。 */
#app {
  max-width: none;
  margin: 0;
  padding: 0;
  text-align: left;
}

.app h1,
.app h2,
.app h3 {
  color: var(--color-text);
  font-size: inherit;
  line-height: inherit;
}

/* ---- 自訂標題列（macOS 風格三顆按鈕）----
   整條標題列標記成可拖曳區域，由作業系統原生處理拖曳；按鈕本身要標記成 no-drag，
   不然滑鼠按下去只會開始拖動視窗、永遠按不到按鈕。 */
.title-bar {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  height: 38px;
  flex-shrink: 0;
  padding: 0 0.85rem;
  background: var(--color-surface);
  app-region: drag;
  -webkit-app-region: drag;
  user-select: none;
  position: relative;
  z-index: 2;
}

.traffic-lights {
  display: flex;
  align-items: center;
  gap: 8px;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}

.traffic-light {
  appearance: none;
  width: 12px;
  height: 12px;
  padding: 0;
  border: none;
  border-radius: 50%;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  /* 符號平常隱形，游標移到整組按鈕上才浮現——這是 macOS 的作法，
     沒有互動時三顆燈維持乾淨的純色圓點。 */
  color: rgba(0, 0, 0, 0);
  transition: color var(--duration-fast) ease, filter var(--duration-fast) ease;
}

.traffic-light--close {
  background: #FF5F57;
}

.traffic-light--minimize {
  background: #FEBC2E;
}

.traffic-light--maximize {
  background: #28C840;
}

.traffic-lights:hover .traffic-light {
  color: rgba(0, 0, 0, 0.55);
}

.traffic-light:hover {
  filter: brightness(0.92);
}

.traffic-light:active {
  filter: brightness(0.82);
}

.traffic-light__glyph {
  width: 12px;
  height: 12px;
  display: block;
}

.title-bar__title {
  font-size: 0.8rem;
  font-weight: 500;
  color: var(--color-text-tertiary);
  letter-spacing: 0.01em;
}

/* ---- 頁籤列 ---- */
.tab-bar {
  display: flex;
  gap: 0.25rem;
  padding: 0 2rem;
  flex-shrink: 0;
  background: var(--color-surface);
  border-bottom: 1px solid var(--color-border);
  position: relative;
  z-index: 1;
}

/* .tab-bar__indicator 是 position: absolute，相對於這個 position: relative 的 .tab-bar 定位——
   量測到的 offsetLeft/offsetWidth 也是相對 .tab-bar 本身（含 padding），兩者座標系一致，
   不需要額外做 padding 偏移計算。 */

.tab-bar__item {
  appearance: none;
  border: none;
  background: none;
  font-family: inherit;
  font-size: 0.9rem;
  font-weight: 500;
  color: var(--color-text-secondary);
  padding: 0.9rem 0.75rem;
  cursor: pointer;
  transition: color var(--duration-fast) ease;
}

.tab-bar__item:hover:not(.is-active) {
  color: var(--color-text);
}

.tab-bar__item.is-active {
  color: var(--color-accent);
}

.tab-bar__indicator {
  position: absolute;
  bottom: 0;
  left: 0;
  height: 2px;
  background: var(--color-accent);
  border-radius: 1px;
  transition: transform 380ms cubic-bezier(0.34, 1.56, 0.64, 1), width 380ms cubic-bezier(0.34, 1.56, 0.64, 1);
  will-change: transform, width;
}

@media (prefers-reduced-motion: reduce) {
  .tab-bar__indicator {
    transition: none;
  }
}

/* ---- 主要內容區：貼齊視窗邊緣、只留內距，整個視窗是同一個表面——不做「卡片飄浮在
     留白背景中」那種網頁排版。內容本身的分組靠留白節奏跟局部的陰影/分隔線，不靠外層包一個框。 ---- */
.page-wrapper {
  display: flex;
  justify-content: center;
  flex: 1;
  overflow-y: auto;
}

.page {
  max-width: 760px;
  width: 100%;
  padding: 2rem 2.5rem 3rem;
  text-align: left;
  transition: max-width var(--duration-base) var(--ease-out);
}

/* 表單類頁面（加密／解密／設定）刻意維持適中寬度——密碼欄位、勾選項這種內容，
   拉滿整個視窗寬度只會讓每一行變得又長又空洞，讀起來反而更費力，不是每個頁面都適合
   隨視窗寬度伸展。已加密清單頁的表格則相反：資料列多一點空間才讀得舒服，
   讓它隨視窗寬度伸展，最大化時能看到更多內容而不是兩側留白。 */
.page--wide {
  max-width: 1180px;
}

.page-title {
  display: flex;
  align-items: center;
  gap: 0.55rem;
  font-size: 1.375rem;
  font-weight: 600;
  /* 大字級收緊字距、壓低行高（Apple 的字體排版原則：tracking 與 leading 都是隨字級調整的，
     不是所有尺寸共用一個值）。 */
  letter-spacing: -0.02em;
  line-height: 1.2;
  margin: 0 0 1.75rem;
  color: var(--color-text);
  opacity: 1;
  text-align: left;
}

.page-title__icon {
  width: 22px;
  height: 22px;
  color: var(--color-accent);
  flex-shrink: 0;
}

/* ---- 表單欄位 ---- */
.field {
  margin-bottom: 1.375rem;
  text-align: left;
}

.field__label {
  display: block;
  font-size: 0.825rem;
  font-weight: 500;
  color: var(--color-text-secondary);
  margin-bottom: 0.4rem;
}

.text-input,
.select-input {
  width: 100%;
  font-family: inherit;
  font-size: 0.9rem;
  color: var(--color-text);
  background: var(--color-surface);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-sm);
  padding: 0.55rem 0.7rem;
  transition: border-color var(--duration-fast) ease, box-shadow var(--duration-fast) ease;
}

.text-input--mono {
  font-family: var(--font-mono);
  font-size: 0.85rem;
}

.text-input:focus,
.select-input:focus {
  outline: none;
  border-color: var(--color-accent);
  box-shadow: 0 0 0 3px var(--color-accent-soft);
}

textarea.text-input {
  resize: vertical;
}

.select-input {
  width: auto;
  min-width: 200px;
}

.checkbox-field {
  display: flex;
  align-items: flex-start;
  gap: 0.55rem;
  font-size: 0.875rem;
  color: var(--color-text);
  cursor: pointer;
  line-height: 1.65;
  line-break: strict;
  text-wrap: pretty;
}

.checkbox-field.is-disabled {
  color: var(--color-text-tertiary);
  cursor: not-allowed;
}

.checkbox-field input {
  margin-top: 0.2rem;
  accent-color: var(--color-accent);
}

.checkbox-field__icon {
  width: 16px;
  height: 16px;
  margin-top: 0.15rem;
  flex-shrink: 0;
}

/* 資訊提示框：把原本一長串塞在勾選項後面的說明文字收起來，滑鼠移過去（或鍵盤 focus）
   才顯示，平常畫面乾淨很多。tabindex="0" 讓鍵盤使用者也能用 Tab 鍵觸發，不是只有滑鼠。 */
.info-tooltip {
  position: relative;
  display: inline-flex;
  align-items: center;
  margin-top: 0.15rem;
  outline: none;
}

.info-tooltip__icon {
  width: 15px;
  height: 15px;
  border-radius: 50%;
  background: var(--color-border-strong);
  color: var(--color-surface);
  font-size: 0.68rem;
  font-style: italic;
  font-family: Georgia, 'Times New Roman', serif;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: help;
  flex-shrink: 0;
  transition: background-color var(--duration-fast) ease;
}

.info-tooltip:hover .info-tooltip__icon,
.info-tooltip:focus-visible .info-tooltip__icon {
  background: var(--color-accent);
}

.info-tooltip__bubble {
  position: absolute;
  bottom: calc(100% + 8px);
  left: 50%;
  transform: translateX(-50%) translateY(4px);
  width: 260px;
  background: var(--color-text);
  color: var(--color-surface);
  font-size: 0.78rem;
  font-weight: 400;
  line-height: 1.6;
  padding: 0.6rem 0.75rem;
  border-radius: var(--radius-sm);
  box-shadow: var(--shadow-md);
  opacity: 0;
  pointer-events: none;
  transition: opacity var(--duration-fast) ease, transform var(--duration-fast) ease;
  z-index: 20;
  text-align: left;
  line-break: strict;
  text-wrap: pretty;
}

.info-tooltip:hover .info-tooltip__bubble,
.info-tooltip:focus-visible .info-tooltip__bubble {
  opacity: 1;
  transform: translateX(-50%) translateY(0);
}

@media (prefers-reduced-motion: reduce) {
  .info-tooltip__bubble {
    transition: none;
  }
}

.hint-text {
  font-size: 0.8rem;
  line-height: 1.7;
  color: var(--color-text-tertiary);
  margin: 0.4rem 0 0;
  line-break: strict;
  text-wrap: pretty;
}

.hint-text--indented {
  margin-left: 1.65rem;
}

/* ---- 按鈕：所有可點擊元素都要有按下去的回饋（Emil Kowalski 的設計原則） ---- */
.button-row {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  margin-top: 0.5rem;
}

.button {
  appearance: none;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  font-family: inherit;
  font-size: 0.875rem;
  font-weight: 500;
  border-radius: var(--radius-sm);
  padding: 0.55rem 1rem;
  cursor: pointer;
  white-space: nowrap;
  transition: background-color var(--duration-fast) ease, border-color var(--duration-fast) ease,
    opacity var(--duration-fast) ease, transform 160ms var(--ease-out);
  border: 1px solid transparent;
}

.button:active:not(:disabled) {
  transform: scale(0.97);
}

.button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.button--primary {
  background: var(--color-accent);
  color: #FFFFFF;
  box-shadow: var(--shadow-xs);
}

/* 這個上邊距只有加密/解密頁籤最下面那顆獨立的送出按鈕需要（跟上面的欄位拉開距離），
   彈窗裡的按鈕不該受影響——原本寫在 .button--primary 基礎樣式裡，導致任何地方的主要按鈕
   都跟著多了這段留白，跟旁邊的次要按鈕高度對不齊，這裡收斂成只在真正需要的地方套用。 */
.page > div > .button--primary {
  margin-top: 0.25rem;
}

.button--primary:hover:not(:disabled) {
  background: var(--color-accent-hover);
}

.button--danger {
  background: var(--color-danger);
  color: #FFFFFF;
}

.button--danger:hover:not(:disabled) {
  background: #96351f;
}

.button--secondary {
  background: var(--color-surface);
  color: var(--color-text);
  border-color: var(--color-border-strong);
}

.button--secondary:hover:not(:disabled) {
  border-color: var(--color-accent);
  color: var(--color-accent);
}

.button__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.button--tiny {
  font-size: 0.78rem;
  padding: 0.32rem 0.6rem;
  background: var(--color-surface);
  color: var(--color-text-secondary);
  border-color: var(--color-border);
}

.button--tiny:hover:not(:disabled) {
  border-color: var(--color-accent);
  color: var(--color-accent);
}

.link-button {
  appearance: none;
  border: none;
  background: none;
  font-family: inherit;
  font-size: 0.8rem;
  color: var(--color-text-tertiary);
  cursor: pointer;
  padding: 0.2rem 0.4rem;
  text-decoration: underline;
  text-underline-offset: 2px;
  transition: color var(--duration-fast) ease;
}

.link-button:hover {
  color: var(--color-text-secondary);
}

.link-button--danger {
  color: var(--color-danger);
  opacity: 0.75;
}

.link-button--danger:hover {
  opacity: 1;
}

/* ---- 加密項目清單／結果 ---- */
.item-list {
  list-style: none;
  margin: 0.6rem 0 0;
  padding: 0;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  overflow: hidden;
}

.item-list__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.55rem 0.75rem;
  border-bottom: 1px solid var(--color-border);
  background: var(--color-surface);
}

.item-list__row:last-child {
  border-bottom: none;
}

.item-list__path {
  font-family: var(--font-mono);
  font-size: 0.8rem;
  /* 長路徑截斷成一行、用刪節號收尾，滑鼠移上去（title 屬性）看完整內容——比整段自動換行
     更乾淨，尤其在表格列裡，換行會讓每一列的高度參差不齊，看起來很亂。 */
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  min-width: 0;
  flex: 1 1 auto;
  cursor: default;
}

.empty-state {
  color: var(--color-text-tertiary);
  font-size: 0.85rem;
  margin: 0.6rem 0 0;
}

/* 拖放區：加密頁籤還沒選任何項目時顯示，本身也是拖放檔案的目標區域，
   拖著檔案進入視窗時（isDraggingFile）邊框跟背景會亮起來給明確的視覺回饋。 */
.dropzone {
  margin-top: 0.6rem;
  padding: 2.5rem 1.5rem;
  border: 1.5px dashed var(--color-border-strong);
  border-radius: var(--radius-md);
  text-align: center;
  transition: border-color var(--duration-fast) ease, background-color var(--duration-fast) ease;
}

.dropzone.is-dragging {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
}

.dropzone__icon {
  width: 32px;
  height: 32px;
  color: var(--color-text-tertiary);
  margin-bottom: 0.6rem;
  transition: color var(--duration-fast) ease;
}

.dropzone.is-dragging .dropzone__icon {
  color: var(--color-accent);
}

.dropzone__text {
  font-size: 0.85rem;
  color: var(--color-text-tertiary);
  margin: 0;
  line-break: strict;
  text-wrap: pretty;
}

/* 清單類頁面（已加密清單／使用紀錄）的空狀態：不是拖放目標，單純告知「目前沒有內容」，
   用置中的圖示＋文字取代原本一行孤零零的灰字。 */
.empty-state-block {
  padding: 3rem 1rem;
  text-align: center;
}

.empty-state-block__icon {
  width: 36px;
  height: 36px;
  color: var(--color-text-tertiary);
  margin-bottom: 0.75rem;
}

.empty-state-block__text {
  font-size: 0.85rem;
  color: var(--color-text-tertiary);
  margin: 0;
}

/* 進度條：不是精確反映後端真實進度，是依項目數量/檔案大小估算出的視覺回饋（見
   estimateEncryptDurationMs 說明）。用 width 過渡而不是重新畫整條，過渡時間刻意設短
   （tick 頻率本來就高，這裡的 transition 只是讓每一格 requestAnimationFrame 之間的
   width 變化不要看起來是硬切），真正的節奏由 JS 那邊的緩動函式控制。 */
.progress-bar {
  margin-top: 0.6rem;
  height: 4px;
  border-radius: 2px;
  background: var(--color-border);
  overflow: hidden;
}

.progress-bar__fill {
  height: 100%;
  background: var(--color-accent);
  border-radius: 2px;
  transition: width 80ms linear;
}

@media (prefers-reduced-motion: reduce) {
  .progress-bar__fill {
    transition: none;
  }
}

.result-list {
  margin-top: 1.25rem;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.result-row {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  font-size: 0.85rem;
  padding: 0.5rem 0.7rem;
  border-radius: var(--radius-sm);
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

.result-row-enter-from {
  opacity: 0;
  transform: translateY(-4px) scale(0.98);
}

.result-row--success {
  background: var(--color-success-soft);
  color: var(--color-success);
}

.result-row--error {
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

.result-row__icon {
  font-weight: 600;
}

/* ---- 解密頁籤 ---- */
.alt-methods {
  margin-top: 1.25rem;
  padding-top: 1.25rem;
  border-top: 1px solid var(--color-border);
}

.alt-methods__label {
  font-size: 0.85rem;
  color: var(--color-text-secondary);
  margin: 0 0 0.5rem;
}

.status-message {
  font-size: 0.875rem;
  margin-top: 1rem;
  padding: 0.6rem 0.8rem;
  border-radius: var(--radius-sm);
}

.status-message--success {
  background: var(--color-success-soft);
  color: var(--color-success);
}

.status-message--error {
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

/* ---- 已加密清單子頁籤 ---- */
.sub-tab-bar {
  display: flex;
  gap: 0.5rem;
  margin-bottom: 1.25rem;
}

.sub-tab-bar__item {
  appearance: none;
  font-family: inherit;
  font-size: 0.82rem;
  font-weight: 500;
  border: 1px solid var(--color-border-strong);
  background: var(--color-surface);
  color: var(--color-text-secondary);
  border-radius: 999px;
  padding: 0.35rem 0.85rem;
  cursor: pointer;
  transition: background-color var(--duration-fast) ease, border-color var(--duration-fast) ease, color var(--duration-fast) ease;
}

.sub-tab-bar__item.is-active {
  background: var(--color-accent-soft);
  border-color: var(--color-accent-border);
  color: var(--color-accent);
}

.refresh-button {
  margin-bottom: 1rem;
}

.update-banner {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  margin-bottom: 0.75rem;
  padding: 0.5rem 0.85rem;
  border-radius: var(--radius-sm);
  background: var(--color-accent-soft);
  border: 1px solid var(--color-accent-border);
  color: var(--color-accent);
  font-size: 0.85rem;
  cursor: pointer;
  transition: background-color var(--duration-fast) ease;
}

/* ---- 表格：外框用陰影而不是描邊，橫向內容過長時整個表格區域自己捲動，
     不會把整個視窗撐爆（對應「使用紀錄會炸到畫面外面」這個問題）。 ---- */
/* 骨架畫面：灰色色塊模擬表格結構，資料還沒回來之前先讓畫面「看起來已經有東西」，
   微微的呼吸閃爍暗示「還在等」，比空白畫面或純文字「載入中」更平順。 */
.skeleton-block {
  display: inline-block;
  height: 0.85rem;
  border-radius: 4px;
  background: var(--color-border);
  animation: skeleton-breathe 1.4s ease-in-out infinite;
}

@keyframes skeleton-breathe {
  0%, 100% { opacity: 0.6; }
  50% { opacity: 1; }
}

@media (prefers-reduced-motion: reduce) {
  .skeleton-block {
    animation: none;
    opacity: 0.8;
  }
}

.table-scroll {
  overflow-x: auto;
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-xs);
}

.table {
  width: 100%;
  min-width: 560px;
  border-collapse: collapse;
  font-size: 0.85rem;
  background: var(--color-surface);
}

.table--auto td:last-child {
  width: 1%;
  white-space: nowrap;
}

.table:not(.table--auto) {
  table-layout: fixed;
}

.table th {
  text-align: left;
  font-weight: 500;
  color: var(--color-text-tertiary);
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  padding: 0.65rem 0.85rem;
  border-bottom: 1px solid var(--color-border);
}

.table td {
  padding: 0.7rem 0.85rem;
  border-bottom: 1px solid var(--color-border);
  vertical-align: top;
}

.table tbody tr:last-child td {
  border-bottom: none;
}

/* 表格列進場動畫：只有列真正被插入 DOM 的那一刻才會播放（CSS animation 的天生行為，
   Vue 靠 :key 重複使用既有節點時不會重新觸發），資料更新但列本來就存在的情況不會
   每次都跳一次動畫，避免常常重新整理的頁面看久了膩。依序的 nth-child 延遲做出
   逐一浮現的感覺，超過第 5 列之後統一延遲，不無限累加下去。
   刻意只用 opacity、不帶 translateY 位移——原本帶位移時，動畫過程中 .page-wrapper
   那層 overflow-y: auto 會短暫判斷內容高度增加，跳出捲軸、動畫結束又消失，很干擾。
   純 opacity 變化不影響版面高度計算，不會有這個副作用。 */
@keyframes table-row-in {
  from { opacity: 0; }
  to { opacity: 1; }
}

.table tbody tr {
  animation: table-row-in 280ms var(--ease-out) backwards;
}

.table tbody tr:nth-child(1) { animation-delay: 0ms; }
.table tbody tr:nth-child(2) { animation-delay: 35ms; }
.table tbody tr:nth-child(3) { animation-delay: 70ms; }
.table tbody tr:nth-child(4) { animation-delay: 105ms; }
.table tbody tr:nth-child(5) { animation-delay: 140ms; }
.table tbody tr:nth-child(n+6) { animation-delay: 175ms; }

@media (prefers-reduced-motion: reduce) {
  .table tbody tr {
    animation: none;
  }
}

.table tbody tr:hover td {
  background: var(--color-bg);
}

/* 詳細資訊這種可能很長的欄位要能換行，不能無限撐開表格寬度——這是「使用紀錄爆版」的根本原因：
   之前沒有這個規則，長路徑會強迫整個表格（進而整個視窗）變寬。 */
.table__wrap-cell {
  max-width: 320px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
}

.table__row--nested td {
  padding-left: 2rem;
  background: #FCFCFD;
}

/* 按鈕列不能直接把 display:flex 放在 <td> 上——Chromium 對 flex 化的表格儲存格處理
   跟一般儲存格不同，會導致這個儲存格沒有跟著整列一起撐滿高度，hover 變色只蓋到一半，
   這正是「hover 沒有整塊區域變色」的成因。改成 <td> 裡包一層 div 做 flex，<td> 本身維持
   預設的 table-cell 顯示方式，高度就會跟其他儲存格一致。 */
.table__actions {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 0.4rem;
}

/* 直向堆疊時，按鈕內容統一靠左對齊，視覺上才會像一組整齊的清單而不是散落的方塊。 */
.table__actions .button {
  justify-content: flex-start;
}

.table__actions .link-button {
  text-align: right;
  padding-top: 0.15rem;
}

.table__detail-cell {
  color: var(--color-text-secondary);
  font-size: 0.8rem;
  max-width: 420px;
}

.cell-name {
  font-weight: 500;
  max-width: 280px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
}

.badge {
  display: inline-block;
  font-size: 0.75rem;
  color: var(--color-accent);
  margin-top: 0.15rem;
}

.status-warning {
  font-size: 0.78rem;
  color: var(--color-danger);
  margin-top: 0.2rem;
}

.group-row td {
  background: var(--color-accent-soft);
  padding: 0;
}

.group-row__inner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.7rem 0.85rem;
  flex-wrap: wrap;
}

.group-row__toggle {
  appearance: none;
  border: none;
  background: none;
  font-family: inherit;
  font-size: 0.85rem;
  font-weight: 500;
  color: var(--color-text);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  text-align: left;
}

.group-row__chevron {
  display: inline-block;
  transition: transform var(--duration-fast) var(--ease-out);
  color: var(--color-accent);
  flex-shrink: 0;
}

.group-row__chevron.is-expanded {
  transform: rotate(90deg);
}

/* ---- 設定頁籤 ---- */
.settings-section {
  margin-bottom: 1.75rem;
  padding-bottom: 1.75rem;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
}

.settings-section:last-of-type {
  border-bottom: none;
}

.settings-section__title {
  font-size: 0.95rem;
  font-weight: 600;
  line-height: 1.4;
  margin: 0 0 0.65rem;
  color: var(--color-text);
}

.vault-path-display {
  font-family: var(--font-mono);
  font-size: 0.8rem;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  padding: 0.6rem 0.75rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
  margin: 0 0 0.65rem;
}

/* ---- 通知（取代原生 alert）---- */
.toast-stack {
  position: fixed;
  right: 1.5rem;
  bottom: 1.5rem;
  display: flex;
  flex-direction: column-reverse;
  gap: 0.5rem;
  z-index: 200;
  max-width: 360px;
}

.toast {
  display: flex;
  align-items: flex-start;
  gap: 0.55rem;
  font-size: 0.85rem;
  padding: 0.7rem 0.9rem;
  border-radius: var(--radius-sm);
  box-shadow: var(--shadow-md);
  cursor: pointer;
  background: var(--color-surface);
  color: var(--color-text);
  border-left: 3px solid var(--color-danger);
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

.toast__icon {
  width: 17px;
  height: 17px;
  flex-shrink: 0;
  margin-top: 0.05rem;
  color: var(--color-danger);
}

.toast--success {
  border-left-color: var(--color-success);
}

.toast--success .toast__icon {
  color: var(--color-success);
}

.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateX(16px) scale(0.97);
}

/* ---- 彈窗（含確認對話框） ---- */
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(20, 22, 28, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 1.5rem;
  z-index: 100;
  transition: opacity var(--duration-base) ease;
}

.modal {
  font-family: var(--font-ui);
  background: var(--color-surface);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-modal);
  padding: 1.75rem 2rem;
  max-width: 480px;
  width: 100%;
  text-align: left;
  transform-origin: center;
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

/* 彈窗進出場：從 scale(0.95) 進場，不是從 0——真實世界不會有東西憑空從無變有；
   離場比進場快，符合「系統回應要快、使用者決策時可以慢」的原則。 */
.modal-enter-from .modal,
.modal-leave-to .modal {
  transform: scale(0.95);
  opacity: 0;
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
}

.modal-leave-active {
  transition-duration: var(--duration-fast);
}

.modal-leave-active .modal {
  transition-duration: var(--duration-fast);
}

.modal__title {
  font-size: 1.125rem;
  font-weight: 600;
  letter-spacing: -0.015em;
  line-height: 1.3;
  margin: 0 0 0.5rem;
  color: var(--color-text);
}

.modal__subtitle {
  font-size: 0.875rem;
  color: var(--color-text-secondary);
  margin: 0 0 0.75rem;
}

.modal__message {
  font-size: 0.9rem;
  line-height: 1.75;
  white-space: pre-line;
  text-align: left;
  margin: 0;
  /* 中文斷行處理：strict 讓標點遵守禁則（句號、逗號不會被丟到行首），
     pretty 讓瀏覽器平衡整段的斷行、避免最後一行只剩一兩個字。 */
  line-break: strict;
  text-wrap: pretty;
  word-break: normal;
  overflow-wrap: break-word;
}

.modal__footer {
  margin-top: 1.25rem;
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}

/* 用 flex:1 平分寬度，保證兩顆按鈕完全等寬——min-width 只是設下限，
   文字或圖示內容一多就會把其中一顆撐開，不夠可靠。 */
.modal__footer:not(.modal__footer--stacked) .button {
  flex: 1 1 0;
  min-width: 0;
}

.modal__footer--center {
  justify-content: flex-end;
}

.modal__footer--center .button,
.modal__footer.modal__footer--center .button {
  flex: initial;
}

/* 三選一對話框的按鈕：直向堆疊、各自撐滿寬度，比照 macOS 動作表（action sheet）的慣例——
   每個選項都是清楚標示意圖的完整一列，不是擠在同一行的兩顆按鈕。 */
.modal__footer--stacked {
  flex-direction: column;
  align-items: stretch;
}

.modal__footer--stacked .button {
  justify-content: center;
}

.modal__actions {
  margin-top: 1rem;
  display: flex;
  gap: 0.5rem;
}

.modal__actions--wrap {
  flex-wrap: wrap;
}

/* ---- 恢復金鑰彈窗：整個 App 的簽名元素，刻意跟其他畫面拉開視覺差異 ---- */
.modal--signature {
  max-width: 520px;
  text-align: left;
  border: 1px solid var(--color-accent-border);
  overflow: visible;
  position: relative;
  /* 上方留出蠟封的高度，讓標題從封印下方開始，構圖才不會擠在一起。 */
  padding-top: 5rem;
}

/* 標題是這個畫面的主角之一，字級要撐得起這個時刻的份量——照 Apple 的字體排版原則，
   階層是「字級＋字重＋行高」一起決定的，不是只靠放大字級。 */
.modal--signature .modal__title {
  font-size: 1.5rem;
  letter-spacing: -0.02em;
  line-height: 1.25;
  margin-bottom: 0.75rem;
}

.modal--signature__seal {
  width: 132px;
  height: 132px;
  position: absolute;
  top: -44px;
  left: -34px;
  filter: drop-shadow(0 10px 22px rgba(20, 22, 30, 0.34));
  pointer-events: none;
}

.modal--signature__warning {
  font-size: 0.825rem;
  line-height: 1.7;
  color: var(--color-danger);
  text-align: left;
  background: var(--color-danger-soft);
  border-radius: var(--radius-sm);
  padding: 0.75rem 0.9rem;
  margin: 0 0 1rem;
  line-break: strict;
  text-wrap: pretty;
}

/* ---- 使用說明彈窗：內容比其他彈窗長很多，固定高度、內容區自己捲動。 ---- */
.modal--help {
  max-width: 560px;
  display: flex;
  flex-direction: column;
  max-height: min(600px, 80vh);
}

.modal--help__body {
  overflow-y: auto;
  margin: 0.5rem -0.5rem 0;
  padding: 0 0.5rem;
}

.modal--help__section {
  margin-bottom: 1.5rem;
}

.modal--help__section:last-child {
  margin-bottom: 0;
}

.modal--help__section h3 {
  font-size: 0.95rem;
  font-weight: 600;
  color: var(--color-accent);
  margin: 0 0 0.5rem;
}

.modal--help__section p {
  font-size: 0.85rem;
  line-height: 1.75;
  color: var(--color-text);
  margin: 0;
  white-space: pre-line;
  line-break: strict;
  text-wrap: pretty;
}

.recovery-key-display {
  font-family: var(--font-mono);
  font-size: 1.15rem;
  font-weight: 500;
  letter-spacing: 0.04em;
  color: var(--color-text);
  background: var(--color-accent-soft);
  border: 1px dashed var(--color-accent-border);
  border-radius: var(--radius-md);
  padding: 1.1rem;
  word-break: break-all;
  user-select: all;
  cursor: text;
}

.recovery-key-display:focus {
  outline: none;
  border-style: solid;
  border-color: var(--color-accent);
}

/* 尊重系統的「減少動態效果」偏好設定：保留能幫助理解的透明度變化，
   去掉位移、縮放這類會造成前庭不適的動態。 */
@media (prefers-reduced-motion: reduce) {
  .button:active:not(:disabled) {
    transform: none;
  }

  .modal-enter-from .modal,
  .modal-leave-to .modal {
    transform: none;
  }

  .toast-enter-from,
  .toast-leave-to {
    transform: none;
  }

  .result-row-enter-from {
    transform: none;
  }

  .group-row__chevron {
    transition: none;
  }
}
</style>