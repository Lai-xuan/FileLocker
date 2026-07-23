<script setup>
import { ref, watch } from 'vue'

const activeTab = ref('encrypt')
const activeListSubTab = ref('files') // 'files' | 'history'

// ---- 設定頁籤 ----
const settingsVaultPath = ref('')
const settingsLanguage = ref('zh-TW')
const settingsTheme = ref('light')
const settingsSaveMessage = ref('')
const isChangingVaultPath = ref(false)

// ---- 加密頁籤 ----
const encryptPath = ref('')
const encryptPassword = ref('')
const hint = ref('')
const enablePasskey = ref(false)
const enableRecoveryKey = ref(false)
const recoveryKeyDisplay = ref('') // 非空字串時顯示恢復金鑰彈窗
const recoveryKeySaveState = ref('') // '' | 'saved' | 'acknowledged'
const isEncrypting = ref(false)
const encryptResultMessage = ref('')
const encryptResultIsError = ref(false)

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

const isRunningInWebView2 = typeof window.chrome?.webview !== 'undefined'

if (isRunningInWebView2) {
  window.chrome.webview.addEventListener('message', (event) => {
    const data = event.data

    if (data.type === 'encryptResult') {
      isEncrypting.value = false
      encryptResultIsError.value = !data.success
      if (data.success) {
        // errorMessage 在成功時如果有值，代表加密內容已經安全寫入，只是收尾清除原始檔案時出了狀況，
        // 是提醒使用者手動處理，不是加密真的失敗。
        let message = data.errorMessage
          ? `加密成功，但有提醒：${data.errorMessage}`
          : `加密成功！指標檔位置：${data.lockedMarkerPath}`

        // 使用者勾了要開 Passkey，但實際上沒有真的啟用成功（裝置不支援、驗證中途取消都有可能），
        // 一定要講清楚，不能讓使用者誤以為多了一層保護但其實沒有。
        if (data.passkeyRequested && !data.passkeyEnabled) {
          message += '\n\n⚠️ Passkey 快速解鎖沒有成功啟用（可能是這台裝置不支援，或驗證過程被取消），目前只有密碼保護這個項目。'
        } else if (data.passkeyEnabled) {
          message += '\n\n✅ Passkey 快速解鎖已啟用。'
        }

        encryptResultMessage.value = message

        if (data.recoveryKey) {
          recoveryKeyDisplay.value = data.recoveryKey
          recoveryKeySaveState.value = ''
        }
      } else {
        encryptResultMessage.value = `加密失敗：${data.errorMessage}`
      }
    } else if (data.type === 'decryptResult') {
      isDecrypting.value = false
      decryptResultIsError.value = !data.success
      decryptResultMessage.value = data.success
        ? `解密成功！已還原至：${data.restoredPath}`
        : `解密失敗：${data.errorMessage}`
    } else if (data.type === 'decryptByUuidResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        alert(`解密成功！已還原至：${data.restoredPath}`)
      } else {
        alert(`解密失敗：${data.errorMessage}`)
      }
    } else if (data.type === 'decryptByPasskeyResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        alert(`Passkey 解密成功！已還原至：${data.restoredPath}`)
      } else {
        alert(`Passkey 解密失敗：${data.errorMessage}`)
      }
    } else if (data.type === 'decryptByRecoveryKeyResult') {
      decryptingUuids.value.delete(data.uuid)
      if (data.success) {
        vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
        alert(`恢復金鑰解密成功！已還原至：${data.restoredPath}`)
      } else {
        alert(`恢復金鑰解密失敗：${data.errorMessage}`)
      }
    } else if (data.type === 'saveRecoveryKeyToFileResult') {
      if (data.success) {
        recoveryKeySaveState.value = 'saved'
      } else if (!data.cancelled) {
        alert(`存檔失敗：${data.errorMessage}`)
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
      encryptResultIsError.value = true
      encryptResultMessage.value = `發生錯誤：${data.message}`
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
        encryptPath.value = data.path
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
      settingsTheme.value = data.theme
    } else if (data.type === 'changeVaultPathResult') {
      isChangingVaultPath.value = false
      if (data.success) {
        settingsVaultPath.value = data.newPath
        settingsSaveMessage.value = '已完成搬移！請重新啟動 FileLocker 讓變更生效。'
      } else {
        alert(`搬移失敗：${data.errorMessage}`)
      }
    } else if (data.type === 'updateSettingResult') {
      settingsSaveMessage.value = '已儲存。'
      setTimeout(() => { settingsSaveMessage.value = '' }, 2000)
    }
  })
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

function pickVaultFolder() {
  window.chrome.webview.postMessage({ type: 'pickVaultFolder' })
}

function setLanguage(value) {
  settingsLanguage.value = value
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
  if (!encryptPath.value || !encryptPassword.value) {
    encryptResultIsError.value = true
    encryptResultMessage.value = '請至少填寫路徑跟密碼。'
    return
  }
  isEncrypting.value = true
  encryptResultMessage.value = ''
  window.chrome.webview.postMessage({
    type: 'encrypt',
    path: encryptPath.value,
    password: encryptPassword.value,
    hint: hint.value,
    enablePasskey: enablePasskey.value,
    enableRecoveryKey: enableRecoveryKey.value
  })
}

function submitDecrypt() {
  if (!decryptPath.value || !decryptPassword.value) {
    decryptResultIsError.value = true
    decryptResultMessage.value = '請至少填寫 .locked 檔案路徑跟密碼。'
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
  const restoreToOriginal = confirm(
    `要把「${item.originalName}」還原到原始位置嗎？\n\n原始位置：${item.originalPath}\n\n` +
    `按「確定」還原到原始位置；按「取消」則自己選擇要存到哪裡。`
  )

  if (restoreToOriginal) {
    promptPasswordAndDecrypt(item, null)
  } else {
    pendingDecryptItem.value = item
    pendingDecryptMode.value = 'password'
    window.chrome.webview.postMessage({ type: 'pickFolder', purpose: 'decryptDestination' })
  }
}

// 密碼輸入還是先用原生 prompt（之後有專門的密碼小視窗時可以換掉這裡）；destinationDir 為 null 代表還原到原始位置。
function promptPasswordAndDecrypt(item, destinationDir) {
  const password = prompt(`輸入「${item.originalName}」的密碼：`)
  if (password === null || password === '') {
    return
  }
  decryptingUuids.value.add(item.uuid)
  window.chrome.webview.postMessage({ type: 'decryptByUuid', uuid: item.uuid, password, destinationDir })
}

// 清單頁用 Passkey 解密：一樣先問還原到原始位置、還是自己選地方存，不需要輸入密碼，
// 選完之後直接觸發 Windows Hello 驗證。
function decryptFromListViaPasskey(item) {
  const restoreToOriginal = confirm(
    `要把「${item.originalName}」還原到原始位置嗎？\n\n原始位置：${item.originalPath}\n\n` +
    `按「確定」還原到原始位置；按「取消」則自己選擇要存到哪裡。\n\n（接下來會跳出 Windows Hello 驗證，不需要輸入密碼）`
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
  const restoreToOriginal = confirm(
    `要把「${item.originalName}」還原到原始位置嗎？\n\n原始位置：${item.originalPath}\n\n` +
    `按「確定」還原到原始位置；按「取消」則自己選擇要存到哪裡。`
  )

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
    await navigator.clipboard.writeText(recoveryKeyDisplay.value)
    recoveryKeySaveState.value = recoveryKeySaveState.value || 'copied'
  } catch {
    alert('複製失敗，請手動選取文字複製。')
  }
}

function saveRecoveryKeyToFile() {
  window.chrome.webview.postMessage({
    type: 'saveRecoveryKeyToFile',
    content: `FileLocker 恢復金鑰\n\n${recoveryKeyDisplay.value}\n\n請妥善保管這組恢復金鑰，任何拿到它的人都能解密對應的內容，等同於你的密碼。`,
    suggestedFileName: 'FileLocker-恢復金鑰.txt'
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
  if (!confirm(`確定要刪除「${item.originalName}」這筆加密紀錄嗎？這個動作沒辦法復原。`)) {
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
    alert(
      `這個資料夾裡面還鎖著 ${data.nestedUuids.length} 個東西沒拿出來，直接刪除的話它們會一起消失、永遠打不開。` +
      `請先把這個資料夾解鎖，確認裡面的東西都處理好之後，再回來刪除。`
    )
    return
  }
  alert(`刪除失敗：${data.errorMessage}`)
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function formatDate(isoString) {
  return new Date(isoString).toLocaleString('zh-TW')
}

function typeLabel(type) {
  return type === 'Folder' ? '資料夾' : '檔案'
}

function actionLabel(action) {
  return { Encrypted: '加密', Decrypted: '解密', RecordDeleted: '刪除紀錄' }[action] || action
}

function unlockMethodLabel(method) {
  return { password: '密碼', passkey: 'Passkey', recoveryKey: '恢復金鑰' }[method] || '未知'
}

function historyDetailText(entry) {
  if (entry.action === 'Encrypted') {
    const parts = []
    if (entry.sourcePath) parts.push(`來源：${entry.sourcePath}`)
    parts.push(`Passkey：${entry.passkeyEnabled ? '已啟用' : '未啟用'}`)
    parts.push(`恢復金鑰：${entry.recoveryKeyEnabled ? '已啟用' : '未啟用'}`)
    return parts.join('｜')
  }
  if (entry.action === 'Decrypted') {
    const parts = [`解鎖方式：${unlockMethodLabel(entry.unlockMethod)}`]
    if (entry.restoredPath) parts.push(`還原至：${entry.restoredPath}`)
    return parts.join('｜')
  }
  return entry.detail || ''
}
</script>

<template>
  <div style="padding: 2rem; font-family: sans-serif; max-width: 700px;">
    <div style="margin-bottom: 1.5rem;">
      <button @click="activeTab = 'encrypt'" :disabled="activeTab === 'encrypt'">加密</button>
      <button @click="activeTab = 'decrypt'" :disabled="activeTab === 'decrypt'" style="margin-left: 0.5rem;">解密</button>
      <button @click="activeTab = 'list'" :disabled="activeTab === 'list'" style="margin-left: 0.5rem;">已加密清單</button>
      <button @click="activeTab = 'settings'" :disabled="activeTab === 'settings'" style="margin-left: 0.5rem;">設定</button>
    </div>

    <div v-if="activeTab === 'encrypt'">
      <h1>加密檔案／資料夾</h1>
      <div style="margin-bottom: 1rem;">
        <label>檔案或資料夾路徑</label><br />
        <input v-model="encryptPath" placeholder="例如 D:\測試檔案.txt" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
        <div style="margin-top: 0.5rem;">
          <button @click="pickFile" type="button">選擇檔案</button>
          <button @click="pickFolder" type="button" style="margin-left: 0.5rem;">選擇資料夾</button>
        </div>
      </div>
      <div style="margin-bottom: 1rem;">
        <label>密碼</label><br />
        <input v-model="encryptPassword" type="password" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
      </div>
      <div style="margin-bottom: 1rem;">
        <label>提示（可留空）</label><br />
        <input v-model="hint" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
      </div>
      <div style="margin-bottom: 1rem;">
        <label>
          <input type="checkbox" v-model="enablePasskey" />
          開啟以 Passkey 快速解鎖（用這台裝置的 Windows Hello 額外解鎖，密碼仍然可以照舊使用）
        </label>
      </div>
      <div style="margin-bottom: 1rem;">
        <label>
          <input type="checkbox" v-model="enableRecoveryKey" />
          開啟恢復金鑰備援（產生一組一次性顯示的恢復碼，密碼忘記時可以用它解鎖，需要自己妥善保管）
        </label>
      </div>
      <button @click="submitEncrypt" :disabled="isEncrypting">
        {{ isEncrypting ? '加密中...' : '加密' }}
      </button>
      <p v-if="encryptResultMessage" :style="{ color: encryptResultIsError ? 'red' : 'green' }">
        {{ encryptResultMessage }}
      </p>
    </div>

    <div v-else-if="activeTab === 'decrypt'">
      <h1>解密</h1>
      <div style="margin-bottom: 1rem;">
        <label>.locked 檔案路徑</label><br />
        <input v-model="decryptPath" placeholder="例如 D:\測試檔案.locked" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
        <div style="margin-top: 0.5rem;">
          <button @click="pickLockedFile" type="button">選擇 .locked 檔案</button>
        </div>
      </div>
      <div style="margin-bottom: 1rem;">
        <label>密碼</label><br />
        <input v-model="decryptPassword" type="password" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
      </div>
      <button @click="submitDecrypt" :disabled="isDecrypting">
        {{ isDecrypting ? '解密中...' : '解密' }}
      </button>

      <div v-if="decryptItemInfo && (decryptItemInfo.passkeyEnabled || decryptItemInfo.recoveryKeyEnabled)" style="margin-top: 1rem;">
        <p style="color: #666;">這個項目也可以用下面的方式解鎖，不用輸入密碼：</p>
        <button v-if="decryptItemInfo.passkeyEnabled" @click="decryptTabViaPasskey" type="button" :disabled="decryptingUuids.has(decryptItemInfo.uuid)">
          🔑 Passkey 解鎖
        </button>
        <button v-if="decryptItemInfo.recoveryKeyEnabled" @click="decryptTabViaRecoveryKey" type="button" style="margin-left: 0.5rem;">
          🔐 恢復金鑰解鎖
        </button>
      </div>

      <p v-if="decryptResultMessage" :style="{ color: decryptResultIsError ? 'red' : 'green' }">
        {{ decryptResultMessage }}
      </p>
    </div>

    <div v-else-if="activeTab === 'list'">
      <h1>已加密清單</h1>

      <div style="margin-bottom: 1rem;">
        <button @click="activeListSubTab = 'files'" :disabled="activeListSubTab === 'files'">已加密檔案</button>
        <button @click="activeListSubTab = 'history'" :disabled="activeListSubTab === 'history'" style="margin-left: 0.5rem;">使用紀錄</button>
      </div>

      <div v-if="activeListSubTab === 'files'">
        <button @click="refreshList" :disabled="isLoadingList" style="margin-bottom: 1rem;">
          {{ isLoadingList ? '載入中...' : '重新整理' }}
        </button>
        <p v-if="!isLoadingList && vaultItems.length === 0">目前沒有任何加密項目。</p>

        <table v-if="vaultItems.length > 0" style="width: 100%; border-collapse: collapse;">
          <thead>
            <tr style="text-align: left; border-bottom: 1px solid #ccc;">
              <th style="padding: 0.5rem;">名稱</th>
              <th style="padding: 0.5rem;">型別</th>
              <th style="padding: 0.5rem;">大小</th>
              <th style="padding: 0.5rem;">提示</th>
              <th style="padding: 0.5rem;">加密時間</th>
              <th style="padding: 0.5rem;"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="item in vaultItems" :key="item.uuid" style="border-bottom: 1px solid #eee;">
              <td style="padding: 0.5rem;">
                {{ item.originalName }}
                <span v-if="item.hasNestedLocks" title="裡面還有其他鎖定項目" style="color: orange;"> 🔒×{{ item.nestedLockCount }}</span>
                <br v-if="!item.markerFound" />
                <span v-if="!item.markerFound" style="color: #c00; font-size: 0.85em;">⚠️ 已移動或找不到（{{ item.markerStatusMessage }}）</span>
              </td>
              <td style="padding: 0.5rem;">{{ typeLabel(item.type) }}</td>
              <td style="padding: 0.5rem;">{{ formatSize(item.originalSizeBytes) }}</td>
              <td style="padding: 0.5rem;">{{ item.hint || '（無）' }}</td>
              <td style="padding: 0.5rem;">{{ formatDate(item.createdAtUtc) }}</td>
              <td style="padding: 0.5rem; white-space: nowrap;">
                <button @click="decryptFromList(item)" type="button" :disabled="decryptingUuids.has(item.uuid)">
                  {{ decryptingUuids.has(item.uuid) ? '解密中...' : '解密' }}
                </button>
                <button
                  v-if="item.passkeyEnabled"
                  @click="decryptFromListViaPasskey(item)"
                  type="button"
                  :disabled="decryptingUuids.has(item.uuid)"
                  style="margin-left: 0.5rem;"
                >
                  🔑 Passkey 解鎖
                </button>
                <button
                  v-if="item.recoveryKeyEnabled"
                  @click="decryptFromListViaRecoveryKey(item)"
                  type="button"
                  :disabled="decryptingUuids.has(item.uuid)"
                  style="margin-left: 0.5rem;"
                >
                  🔐 恢復金鑰解鎖
                </button>
                <button @click="requestDelete(item)" type="button" style="margin-left: 0.5rem;">刪除紀錄</button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <div v-else>
        <button @click="refreshHistory" :disabled="isLoadingHistory" style="margin-bottom: 1rem;">
          {{ isLoadingHistory ? '載入中...' : '重新整理' }}
        </button>
        <p v-if="!isLoadingHistory && historyItems.length === 0">目前沒有任何操作紀錄。</p>

        <table v-if="historyItems.length > 0" style="width: 100%; border-collapse: collapse;">
          <thead>
            <tr style="text-align: left; border-bottom: 1px solid #ccc;">
              <th style="padding: 0.5rem;">名稱</th>
              <th style="padding: 0.5rem;">動作</th>
              <th style="padding: 0.5rem;">時間</th>
              <th style="padding: 0.5rem;">詳細資訊</th>
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
      <h1>設定</h1>

      <div style="margin-bottom: 1.5rem;">
        <h3>已加密檔案集中位置（Vault）</h3>
        <p style="font-family: monospace; background: #f0f0f0; padding: 0.5rem; word-break: break-all;">{{ settingsVaultPath }}</p>
        <button @click="pickVaultFolder" type="button" :disabled="isChangingVaultPath">
          {{ isChangingVaultPath ? '搬移中...' : '搬移到新位置...' }}
        </button>
        <p style="color: #666; font-size: 0.9em;">選一個空資料夾，會把目前所有已加密的內容搬過去，搬移完成後需要重新啟動 FileLocker 才會生效。</p>
      </div>

      <div style="margin-bottom: 1.5rem;">
        <h3>語言</h3>
        <select :value="settingsLanguage" @change="setLanguage($event.target.value)">
          <option value="zh-TW">繁體中文</option>
        </select>
      </div>

      <div style="margin-bottom: 1.5rem;">
        <h3>主題</h3>
        <button @click="setTheme('light')" type="button" :disabled="settingsTheme === 'light'">☀️ 亮色</button>
        <button @click="setTheme('dark')" type="button" :disabled="settingsTheme === 'dark'" style="margin-left: 0.5rem;">🌙 深色</button>
        <p style="color: #666; font-size: 0.9em;">目前只會記住選擇，畫面實際套用主題的部分要等整體視覺設計階段才會實作。</p>
      </div>

      <p v-if="settingsSaveMessage" style="color: green;">{{ settingsSaveMessage }}</p>
    </div>
  </div>

  <!-- 恢復金鑰顯示彈窗：加密成功且開啟了恢復金鑰時跳出，強制使用者做選擇才能關閉。 -->
  <div v-if="recoveryKeyDisplay" style="position: fixed; inset: 0; background: rgba(0,0,0,0.5); display: flex; align-items: center; justify-content: center;">
    <div style="background: white; padding: 2rem; max-width: 480px; border-radius: 8px;">
      <h2 style="margin-top: 0;">你的恢復金鑰</h2>
      <p style="color: #c00;">
        這是唯一一次顯示這組恢復金鑰，FileLocker 不會保留任何副本。請務必存好或抄下來——
        任何拿到它的人都能解密對應的內容，等同於你的密碼，要用一樣謹慎的態度保管。
      </p>
      <div style="font-family: monospace; font-size: 1.1rem; background: #f0f0f0; padding: 1rem; border-radius: 4px; word-break: break-all; user-select: all;">
        {{ recoveryKeyDisplay }}
      </div>
      <div style="margin-top: 1rem; display: flex; gap: 0.5rem; flex-wrap: wrap;">
        <button @click="copyRecoveryKey" type="button">複製</button>
        <button @click="saveRecoveryKeyToFile" type="button">存成檔案</button>
        <button @click="acknowledgeRecoveryKey" type="button">我已經抄下來了</button>
      </div>
      <p v-if="recoveryKeySaveState === 'saved'" style="color: green;">已存成檔案。</p>
      <p v-if="recoveryKeySaveState === 'copied'" style="color: green;">已複製到剪貼簿。</p>
      <div style="margin-top: 1rem; text-align: right;">
        <button @click="closeRecoveryKeyDisplay" type="button" :disabled="!recoveryKeySaveState">
          {{ recoveryKeySaveState ? '關閉' : '請先複製、存檔，或確認已抄下來' }}
        </button>
      </div>
    </div>
  </div>

  <!-- 恢復金鑰輸入彈窗：清單頁按「恢復金鑰解鎖」後跳出。 -->
  <div v-if="recoveryKeyPromptItem" style="position: fixed; inset: 0; background: rgba(0,0,0,0.5); display: flex; align-items: center; justify-content: center;">
    <div style="background: white; padding: 2rem; max-width: 480px; border-radius: 8px;">
      <h2 style="margin-top: 0;">輸入恢復金鑰</h2>
      <p>解鎖「{{ recoveryKeyPromptItem.originalName }}」</p>
      <textarea
        v-model="recoveryKeyInputValue"
        rows="3"
        style="width: 100%; font-family: monospace; padding: 0.5rem; box-sizing: border-box;"
        placeholder="貼上或輸入恢復金鑰"
      ></textarea>
      <div style="margin-top: 1rem; display: flex; justify-content: flex-end; gap: 0.5rem;">
        <button @click="cancelRecoveryKeyPrompt" type="button">取消</button>
        <button @click="submitRecoveryKeyDecrypt" type="button" :disabled="!recoveryKeyInputValue.trim()">解鎖</button>
      </div>
    </div>
  </div>
</template>