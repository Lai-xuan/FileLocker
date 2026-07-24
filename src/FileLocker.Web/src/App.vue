<script setup>
import { ref, watch, computed } from 'vue'
import zhTW from './locales/zh-TW.json'
import en from './locales/en.json'

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

const activeTab = ref('encrypt')
const activeListSubTab = ref('files') // 'files' | 'history'

// ---- 設定頁籤 ----
const settingsVaultPath = ref('')
const settingsLanguage = ref('zh-TW')
const settingsTheme = ref('light')
const settingsSaveMessage = ref('')
const isChangingVaultPath = ref(false)

// ---- 加密頁籤 ----
const encryptPaths = ref([])
const encryptPassword = ref('')
const hint = ref('')
const enablePasskey = ref(false)
const enableRecoveryKey = ref(false)
const recoveryKeyDisplay = ref('') // 非空字串時顯示恢復金鑰彈窗
const recoveryKeySaveState = ref('') // '' | 'saved' | 'acknowledged'
const isEncrypting = ref(false)
const encryptBatchTotal = ref(0)
const encryptItemResults = ref([]) // 批次加密逐項回報的結果

// ---- 解密頁籤 ----
const decryptPath = ref('')
const decryptPassword = ref('')
const isDecrypting = ref(false)
const decryptResultMessage = ref('')
const decryptResultIsError = ref(false)
const decryptItemInfo = ref(null) // { uuid, originalName, hint, passkeyEnabled, recoveryKeyEnabled }

// ---- 已加密檔案子頁籤 ----
const vaultItems = ref([])
const isLoadingList = ref(false)
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

// 密碼輸入彈窗：取代原本用瀏覽器原生 prompt() 明碼輸入密碼的做法——prompt() 的輸入框不會把
// 打字內容用點點遮起來，旁邊有人看、或畫面被錄影/遠端連線時會直接看到密碼，這裡改用跟
// 其他表單一致的遮罩密碼欄位。
const passwordPromptContext = ref(null) // { mode: 'single' | 'batch', item或group, destinationDir }
const passwordPromptValue = ref('')

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
      encryptPaths.value = []
    } else if (data.type === 'decryptResult') {
      isDecrypting.value = false
      decryptResultIsError.value = !data.success
      decryptResultMessage.value = data.success
        ? t('decrypt.success', { path: data.restoredPath })
        : translateError(data.errorCode, data.errorDetail, t('decrypt.failed', { error: data.errorMessage }))
    } else if (data.type === 'decryptByUuidResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        alert(t('decrypt.success', { path: data.restoredPath }))
      } else {
        alert(translateError(data.errorCode, data.errorDetail, t('decrypt.failed', { error: data.errorMessage })))
      }
    } else if (data.type === 'decryptByPasskeyResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        alert(t('alert.passkeyDecryptSuccess', { path: data.restoredPath }))
      } else {
        alert(translateError(data.errorCode, data.errorDetail, t('alert.passkeyDecryptFailed', { error: data.errorMessage })))
      }
    } else if (data.type === 'decryptByRecoveryKeyResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        alert(t('alert.recoveryKeyDecryptSuccess', { path: data.restoredPath }))
      } else {
        alert(translateError(data.errorCode, data.errorDetail, t('alert.recoveryKeyDecryptFailed', { error: data.errorMessage })))
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
        alert(t('alert.batchUnlockPartial', { success: data.successCount, total: data.totalCount }))
      }
    } else if (data.type === 'saveRecoveryKeyToFileResult') {
      if (data.success) {
        recoveryKeySaveState.value = 'saved'
      } else if (!data.cancelled) {
        alert(t('alert.saveFileFailed', { error: data.errorMessage }))
      }
    } else if (data.type === 'inspectLockedFileResult') {
      decryptItemInfo.value = data.success
        ? { uuid: data.uuid, originalName: data.originalName, hint: data.hint, passkeyEnabled: data.passkeyEnabled, recoveryKeyEnabled: data.recoveryKeyEnabled }
        : null
    } else if (data.type === 'error') {
      isEncrypting.value = false
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
      isLoadingList.value = false
      vaultItems.value = data.items
    } else if (data.type === 'historyList') {
      isLoadingHistory.value = false
      historyItems.value = data.items
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
        alert(t('settings.vaultMoveFailed', { error: data.errorMessage }))
      }
    } else if (data.type === 'updateSettingResult') {
      settingsSaveMessage.value = t('settings.saved')
      setTimeout(() => { settingsSaveMessage.value = '' }, 2000)
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

function refreshList() {
  isLoadingList.value = true
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

function submitEncrypt() {
  if (encryptPaths.value.length === 0 || !encryptPassword.value) {
    encryptItemResults.value = [{ path: '', success: false, errorMessage: t('encrypt.needAtLeastOne'), note: '' }]
    return
  }
  isEncrypting.value = true
  encryptItemResults.value = []
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
function decryptFromList(item) {
  const restoreToOriginal = confirm(t('confirm.restoreToOriginal', { name: item.originalName, path: item.originalPath }))

  if (restoreToOriginal) {
    promptPasswordAndDecrypt(item, null)
  } else {
    pendingDecryptItem.value = item
    pendingDecryptMode.value = 'password'
    window.chrome.webview.postMessage({ type: 'pickFolder', purpose: 'decryptDestination' })
  }
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
function decryptFromListViaPasskey(item) {
  const restoreToOriginal = confirm(
    t('confirm.restoreToOriginal', { name: item.originalName, path: item.originalPath }) + t('confirm.passkeyNote')
  )

  if (restoreToOriginal) {
    startPasskeyDecrypt(item, null)
  } else {
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
function decryptFromListViaRecoveryKey(item) {
  const restoreToOriginal = confirm(t('confirm.restoreToOriginal', { name: item.originalName, path: item.originalPath }))

  if (restoreToOriginal) {
    openRecoveryKeyPrompt(item, null)
  } else {
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
    alert(t('recoveryKeyModal.copyFailed'))
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

function requestDelete(item) {
  if (!confirm(t('confirm.deleteWarning', { name: item.originalName }))) {
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
    alert(t('alert.deleteBlockedByNested', { count: data.nestedUuids.length }))
    return
  }
  alert(translateError(data.errorCode, null, t('alert.deleteFailed', { error: data.errorMessage })))
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
  <div style="padding: 2rem; font-family: sans-serif; max-width: 700px;">
    <div style="margin-bottom: 1.5rem;">
      <button @click="activeTab = 'encrypt'" :disabled="activeTab === 'encrypt'">{{ t('tab.encrypt') }}</button>
      <button @click="activeTab = 'decrypt'" :disabled="activeTab === 'decrypt'" style="margin-left: 0.5rem;">{{ t('tab.decrypt') }}</button>
      <button @click="activeTab = 'list'" :disabled="activeTab === 'list'" style="margin-left: 0.5rem;">{{ t('tab.list') }}</button>
      <button @click="activeTab = 'settings'" :disabled="activeTab === 'settings'" style="margin-left: 0.5rem;">{{ t('tab.settings') }}</button>
    </div>

    <div v-if="activeTab === 'encrypt'">
      <h1>{{ t('encrypt.title') }}</h1>
      <div style="margin-bottom: 1rem;">
        <label>{{ t('encrypt.itemsLabel') }}</label><br />
        <div style="margin-top: 0.5rem;">
          <button @click="pickFile" type="button">{{ t('encrypt.pickFiles') }}</button>
          <button @click="pickFolder" type="button" style="margin-left: 0.5rem;">{{ t('encrypt.pickFolder') }}</button>
        </div>
        <ul v-if="encryptPaths.length > 0" style="margin-top: 0.5rem; padding-left: 1.2rem;">
          <li v-for="(path, index) in encryptPaths" :key="path" style="margin-bottom: 0.25rem;">
            {{ path }}
            <button @click="removeEncryptPath(index)" type="button" style="margin-left: 0.5rem;">{{ t('encrypt.remove') }}</button>
          </li>
        </ul>
        <p v-else style="color: #999;">{{ t('encrypt.noItemsSelected') }}</p>
      </div>
      <div style="margin-bottom: 1rem;">
        <label>{{ t('encrypt.passwordLabel') }}</label><br />
        <input v-model="encryptPassword" type="password" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
      </div>
      <div style="margin-bottom: 1rem;">
        <label>{{ t('encrypt.hintLabel') }}</label><br />
        <input v-model="hint" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
      </div>
      <div style="margin-bottom: 1rem;">
        <label>
          <input type="checkbox" v-model="enablePasskey" :disabled="encryptPaths.length > 1" />
          {{ t('encrypt.passkeyLabel') }}
        </label>
        <p v-if="encryptPaths.length > 1" style="color: #999; font-size: 0.85em; margin: 0.25rem 0 0 1.5rem;">
          {{ t('encrypt.passkeyBatchDisabled') }}
        </p>
      </div>
      <div style="margin-bottom: 1rem;">
        <label>
          <input type="checkbox" v-model="enableRecoveryKey" :disabled="encryptPaths.length > 1" />
          {{ t('encrypt.recoveryKeyLabel') }}
        </label>
        <p v-if="encryptPaths.length > 1" style="color: #999; font-size: 0.85em; margin: 0.25rem 0 0 1.5rem;">
          {{ t('encrypt.recoveryKeyBatchDisabled') }}
        </p>
      </div>
      <button @click="submitEncrypt" :disabled="isEncrypting">
        {{ isEncrypting ? t('encrypt.encrypting', { current: encryptItemResults.length, total: encryptBatchTotal }) : t('encrypt.submit') }}
      </button>

      <div v-if="encryptItemResults.length > 0" style="margin-top: 1rem;">
        <div v-for="(item, index) in encryptItemResults" :key="index" :style="{ color: item.success ? 'green' : 'red', marginBottom: '0.25rem' }">
          <template v-if="item.path">{{ item.success ? '✅' : '❌' }} {{ item.path }}</template>
          <span v-if="item.errorMessage"> — {{ item.errorMessage }}</span>
          <span v-if="item.note"> — {{ item.note }}</span>
        </div>
      </div>
    </div>

    <div v-else-if="activeTab === 'decrypt'">
      <h1>{{ t('decrypt.title') }}</h1>
      <div style="margin-bottom: 1rem;">
        <label>{{ t('decrypt.lockedPathLabel') }}</label><br />
        <input v-model="decryptPath" :placeholder="t('decrypt.lockedPathPlaceholder')" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
        <div style="margin-top: 0.5rem;">
          <button @click="pickLockedFile" type="button">{{ t('decrypt.pickLockedFile') }}</button>
        </div>
      </div>
      <div style="margin-bottom: 1rem;">
        <label>{{ t('decrypt.passwordLabel') }}</label><br />
        <input v-model="decryptPassword" type="password" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
      </div>
      <button @click="submitDecrypt" :disabled="isDecrypting">
        {{ isDecrypting ? t('decrypt.decrypting') : t('decrypt.submit') }}
      </button>

      <div v-if="decryptItemInfo && (decryptItemInfo.passkeyEnabled || decryptItemInfo.recoveryKeyEnabled)" style="margin-top: 1rem;">
        <p style="color: #666;">{{ t('decrypt.altMethodsAvailable') }}</p>
        <button v-if="decryptItemInfo.passkeyEnabled" @click="decryptTabViaPasskey" type="button" :disabled="decryptingUuids.has(decryptItemInfo.uuid)">
          {{ t('decrypt.passkeyUnlock') }}
        </button>
        <button v-if="decryptItemInfo.recoveryKeyEnabled" @click="decryptTabViaRecoveryKey" type="button" style="margin-left: 0.5rem;">
          {{ t('decrypt.recoveryKeyUnlock') }}
        </button>
      </div>

      <p v-if="decryptResultMessage" :style="{ color: decryptResultIsError ? 'red' : 'green' }">
        {{ decryptResultMessage }}
      </p>
    </div>

    <div v-else-if="activeTab === 'list'">
      <h1>{{ t('list.title') }}</h1>

      <div style="margin-bottom: 1rem;">
        <button @click="activeListSubTab = 'files'" :disabled="activeListSubTab === 'files'">{{ t('list.subTabFiles') }}</button>
        <button @click="activeListSubTab = 'history'" :disabled="activeListSubTab === 'history'" style="margin-left: 0.5rem;">{{ t('list.subTabHistory') }}</button>
      </div>

      <div v-if="activeListSubTab === 'files'">
        <button @click="refreshList" :disabled="isLoadingList" style="margin-bottom: 1rem;">
          {{ isLoadingList ? t('list.loading') : t('list.refresh') }}
        </button>
        <p v-if="!isLoadingList && vaultItems.length === 0">{{ t('list.noItems') }}</p>

        <table v-if="vaultItems.length > 0" style="width: 100%; border-collapse: collapse;">
          <thead>
            <tr style="text-align: left; border-bottom: 1px solid #ccc;">
              <th style="padding: 0.5rem;">{{ t('list.colName') }}</th>
              <th style="padding: 0.5rem;">{{ t('list.colType') }}</th>
              <th style="padding: 0.5rem;">{{ t('list.colSize') }}</th>
              <th style="padding: 0.5rem;">{{ t('list.colHint') }}</th>
              <th style="padding: 0.5rem;">{{ t('list.colTime') }}</th>
              <th style="padding: 0.5rem;"></th>
            </tr>
          </thead>
          <tbody>
            <template v-for="group in groupedVaultItems" :key="group.isGroup ? group.batchId : group.item.uuid">
              <!-- 獨立項目（沒有 batchId）：跟之前一樣直接顯示一列。 -->
              <tr v-if="!group.isGroup" style="border-bottom: 1px solid #eee;">
                <td style="padding: 0.5rem;">
                  {{ group.item.originalName }}
                  <span v-if="group.item.hasNestedLocks" :title="t('list.nestedLockTitle')" style="color: orange;"> 🔒×{{ group.item.nestedLockCount }}</span>
                  <br v-if="!group.item.markerFound" />
                  <span v-if="!group.item.markerFound" style="color: #c00; font-size: 0.85em;">{{ t('list.markerMissing', { message: group.item.markerStatusMessage }) }}</span>
                </td>
                <td style="padding: 0.5rem;">{{ typeLabel(group.item.type) }}</td>
                <td style="padding: 0.5rem;">{{ formatSize(group.item.originalSizeBytes) }}</td>
                <td style="padding: 0.5rem;">{{ group.item.hint || t('list.hintNone') }}</td>
                <td style="padding: 0.5rem;">{{ formatDate(group.item.createdAtUtc) }}</td>
                <td style="padding: 0.5rem; white-space: nowrap;">
                  <button @click="decryptFromList(group.item)" type="button" :disabled="decryptingUuids.has(group.item.uuid)">
                    {{ decryptingUuids.has(group.item.uuid) ? t('list.decrypting') : t('list.decrypt') }}
                  </button>
                  <button
                    v-if="group.item.passkeyEnabled"
                    @click="decryptFromListViaPasskey(group.item)"
                    type="button"
                    :disabled="decryptingUuids.has(group.item.uuid)"
                    style="margin-left: 0.5rem;"
                  >
                    {{ t('decrypt.passkeyUnlock') }}
                  </button>
                  <button
                    v-if="group.item.recoveryKeyEnabled"
                    @click="decryptFromListViaRecoveryKey(group.item)"
                    type="button"
                    :disabled="decryptingUuids.has(group.item.uuid)"
                    style="margin-left: 0.5rem;"
                  >
                    {{ t('decrypt.recoveryKeyUnlock') }}
                  </button>
                  <button @click="requestDelete(group.item)" type="button" style="margin-left: 0.5rem;">{{ t('list.delete') }}</button>
                </td>
              </tr>

              <!-- 批次群組：一次選多個項目加密出來的，摺疊成一列，展開後每個項目維持獨立操作能力。 -->
              <template v-else>
                <tr style="border-bottom: 1px solid #eee; background: #f7f7f7;">
                  <td colspan="6" style="padding: 0.5rem;">
                    <button @click="toggleGroupExpanded(group.batchId)" type="button" style="margin-right: 0.5rem;">
                      {{ expandedGroups.has(group.batchId) ? '▼' : '▶' }}
                    </button>
                    {{ batchPreviewText(group.items) }}
                    <button
                      @click="decryptGroupViaPassword(group)"
                      type="button"
                      style="margin-left: 0.5rem;"
                      :disabled="decryptingBatchIds.has(group.batchId)"
                    >
                      {{ decryptingBatchIds.has(group.batchId) ? t('list.unlockAllInProgress') : t('list.unlockAll') }}
                    </button>
                  </td>
                </tr>
                <template v-if="expandedGroups.has(group.batchId)">
                  <tr v-for="item in group.items" :key="item.uuid" style="border-bottom: 1px solid #eee;">
                    <td style="padding: 0.5rem 0.5rem 0.5rem 2rem;">
                      {{ item.originalName }}
                      <span v-if="item.hasNestedLocks" :title="t('list.nestedLockTitle')" style="color: orange;"> 🔒×{{ item.nestedLockCount }}</span>
                      <br v-if="!item.markerFound" />
                      <span v-if="!item.markerFound" style="color: #c00; font-size: 0.85em;">{{ t('list.markerMissing', { message: item.markerStatusMessage }) }}</span>
                    </td>
                    <td style="padding: 0.5rem;">{{ typeLabel(item.type) }}</td>
                    <td style="padding: 0.5rem;">{{ formatSize(item.originalSizeBytes) }}</td>
                    <td style="padding: 0.5rem;">{{ item.hint || t('list.hintNone') }}</td>
                    <td style="padding: 0.5rem;">{{ formatDate(item.createdAtUtc) }}</td>
                    <td style="padding: 0.5rem; white-space: nowrap;">
                      <button @click="decryptFromList(item)" type="button" :disabled="decryptingUuids.has(item.uuid)">
                        {{ decryptingUuids.has(item.uuid) ? t('list.decrypting') : t('list.decrypt') }}
                      </button>
                      <button
                        v-if="item.passkeyEnabled"
                        @click="decryptFromListViaPasskey(item)"
                        type="button"
                        :disabled="decryptingUuids.has(item.uuid)"
                        style="margin-left: 0.5rem;"
                      >
                        {{ t('decrypt.passkeyUnlock') }}
                      </button>
                      <button
                        v-if="item.recoveryKeyEnabled"
                        @click="decryptFromListViaRecoveryKey(item)"
                        type="button"
                        :disabled="decryptingUuids.has(item.uuid)"
                        style="margin-left: 0.5rem;"
                      >
                        {{ t('decrypt.recoveryKeyUnlock') }}
                      </button>
                      <button @click="requestDelete(item)" type="button" style="margin-left: 0.5rem;">{{ t('list.delete') }}</button>
                    </td>
                  </tr>
                </template>
              </template>
            </template>
          </tbody>
        </table>
      </div>

      <div v-else>
        <button @click="refreshHistory" :disabled="isLoadingHistory" style="margin-bottom: 1rem;">
          {{ isLoadingHistory ? t('list.loading') : t('list.refresh') }}
        </button>
        <p v-if="!isLoadingHistory && historyItems.length === 0">{{ t('list.noHistory') }}</p>

        <table v-if="historyItems.length > 0" style="width: 100%; border-collapse: collapse;">
          <thead>
            <tr style="text-align: left; border-bottom: 1px solid #ccc;">
              <th style="padding: 0.5rem;">{{ t('list.colName') }}</th>
              <th style="padding: 0.5rem;">{{ t('list.historyColAction') }}</th>
              <th style="padding: 0.5rem;">{{ t('list.historyColTime') }}</th>
              <th style="padding: 0.5rem;">{{ t('list.historyColDetail') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(entry, index) in historyItems" :key="index" style="border-bottom: 1px solid #eee;">
              <td style="padding: 0.5rem;">{{ entry.originalName }}</td>
              <td style="padding: 0.5rem;">{{ actionLabel(entry.action) }}</td>
              <td style="padding: 0.5rem;">{{ formatDate(entry.timestampUtc) }}</td>
              <td style="padding: 0.5rem; font-size: 0.85em; color: #555;">{{ historyDetailText(entry) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <div v-else-if="activeTab === 'settings'">
      <h1>{{ t('settings.title') }}</h1>

      <div style="margin-bottom: 1.5rem;">
        <h3>{{ t('settings.vaultLocationTitle') }}</h3>
        <p style="font-family: monospace; background: #f0f0f0; padding: 0.5rem; word-break: break-all;">{{ settingsVaultPath }}</p>
        <button @click="pickVaultFolder" type="button" :disabled="isChangingVaultPath">
          {{ isChangingVaultPath ? t('settings.vaultMoving') : t('settings.vaultMove') }}
        </button>
        <p style="color: #666; font-size: 0.9em;">{{ t('settings.vaultMoveHint') }}</p>
      </div>

      <div style="margin-bottom: 1.5rem;">
        <h3>{{ t('settings.languageTitle') }}</h3>
        <select :value="settingsLanguage" @change="setLanguage($event.target.value)">
          <option value="zh-TW">繁體中文</option>
          <option value="en">English</option>
        </select>
      </div>

      <div style="margin-bottom: 1.5rem;">
        <h3>{{ t('settings.themeTitle') }}</h3>
        <button @click="setTheme('light')" type="button" :disabled="settingsTheme === 'light'">{{ t('settings.themeLight') }}</button>
        <button @click="setTheme('dark')" type="button" :disabled="settingsTheme === 'dark'" style="margin-left: 0.5rem;">{{ t('settings.themeDark') }}</button>
        <p style="color: #666; font-size: 0.9em;">{{ t('settings.themeHint') }}</p>
      </div>

      <p v-if="settingsSaveMessage" style="color: green;">{{ settingsSaveMessage }}</p>
    </div>
  </div>

  <!-- 恢復金鑰顯示彈窗：加密成功且開啟了恢復金鑰時跳出，強制使用者做選擇才能關閉。 -->
  <div v-if="recoveryKeyDisplay" style="position: fixed; inset: 0; background: rgba(0,0,0,0.5); display: flex; align-items: center; justify-content: center;">
    <div style="background: white; padding: 2rem; max-width: 480px; border-radius: 8px;">
      <h2 style="margin-top: 0;">{{ t('recoveryKeyModal.title') }}</h2>
      <p style="color: #c00;">{{ t('recoveryKeyModal.warning') }}</p>
      <div style="font-family: monospace; font-size: 1.1rem; background: #f0f0f0; padding: 1rem; border-radius: 4px; word-break: break-all; user-select: all;">
        {{ recoveryKeyDisplay }}
      </div>
      <div style="margin-top: 1rem; display: flex; gap: 0.5rem; flex-wrap: wrap;">
        <button @click="copyRecoveryKey" type="button">{{ t('recoveryKeyModal.copy') }}</button>
        <button @click="saveRecoveryKeyToFile" type="button">{{ t('recoveryKeyModal.saveToFile') }}</button>
        <button @click="acknowledgeRecoveryKey" type="button">{{ t('recoveryKeyModal.acknowledge') }}</button>
      </div>
      <p v-if="recoveryKeySaveState === 'saved'" style="color: green;">{{ t('recoveryKeyModal.savedNotice') }}</p>
      <p v-if="recoveryKeySaveState === 'copied'" style="color: green;">{{ t('recoveryKeyModal.copiedNotice') }}</p>
      <div style="margin-top: 1rem; text-align: right;">
        <button @click="closeRecoveryKeyDisplay" type="button" :disabled="!recoveryKeySaveState">
          {{ recoveryKeySaveState ? t('recoveryKeyModal.close') : t('recoveryKeyModal.closeDisabled') }}
        </button>
      </div>
    </div>
  </div>

  <!-- 密碼輸入彈窗：取代原本明碼顯示的 prompt()，用遮罩密碼欄位。 -->
  <div v-if="passwordPromptContext" style="position: fixed; inset: 0; background: rgba(0,0,0,0.5); display: flex; align-items: center; justify-content: center;">
    <div style="background: white; padding: 2rem; max-width: 480px; border-radius: 8px;">
      <h2 style="margin-top: 0;">{{ t('passwordPrompt.title') }}</h2>
      <p v-if="passwordPromptContext.mode === 'single'">{{ t('passwordPrompt.unlockSingle', { name: passwordPromptContext.item.originalName }) }}</p>
      <p v-else>{{ t('passwordPrompt.unlockBatch', { count: passwordPromptContext.group.items.length, preview: batchPreviewText(passwordPromptContext.group.items) }) }}</p>
      <input
        v-model="passwordPromptValue"
        type="password"
        style="width: 100%; padding: 0.5rem; box-sizing: border-box;"
        @keyup.enter="submitPasswordPrompt"
      />
      <div style="margin-top: 1rem; display: flex; justify-content: flex-end; gap: 0.5rem;">
        <button @click="cancelPasswordPrompt" type="button">{{ t('passwordPrompt.cancel') }}</button>
        <button @click="submitPasswordPrompt" type="button" :disabled="!passwordPromptValue">{{ t('passwordPrompt.unlock') }}</button>
      </div>
    </div>
  </div>

  <!-- 恢復金鑰輸入彈窗：清單頁按「恢復金鑰解鎖」後跳出。 -->
  <div v-if="recoveryKeyPromptItem" style="position: fixed; inset: 0; background: rgba(0,0,0,0.5); display: flex; align-items: center; justify-content: center;">
    <div style="background: white; padding: 2rem; max-width: 480px; border-radius: 8px;">
      <h2 style="margin-top: 0;">{{ t('recoveryKeyPrompt.title') }}</h2>
      <p>{{ t('recoveryKeyPrompt.unlock', { name: recoveryKeyPromptItem.originalName }) }}</p>
      <textarea
        v-model="recoveryKeyInputValue"
        rows="3"
        style="width: 100%; font-family: monospace; padding: 0.5rem; box-sizing: border-box;"
        :placeholder="t('recoveryKeyPrompt.placeholder')"
      ></textarea>
      <div style="margin-top: 1rem; display: flex; justify-content: flex-end; gap: 0.5rem;">
        <button @click="cancelRecoveryKeyPrompt" type="button">{{ t('recoveryKeyPrompt.cancel') }}</button>
        <button @click="submitRecoveryKeyDecrypt" type="button" :disabled="!recoveryKeyInputValue.trim()">{{ t('recoveryKeyPrompt.submit') }}</button>
      </div>
    </div>
  </div>
</template>