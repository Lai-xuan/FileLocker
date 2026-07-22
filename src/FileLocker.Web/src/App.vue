<script setup>
import { ref, watch } from 'vue'

const activeTab = ref('encrypt')

// ---- 加密頁籤 ----
const encryptPath = ref('')
const encryptPassword = ref('')
const hint = ref('')
const isEncrypting = ref(false)
const encryptResultMessage = ref('')
const encryptResultIsError = ref(false)

// ---- 解密頁籤 ----
const decryptPath = ref('')
const decryptPassword = ref('')
const isDecrypting = ref(false)
const decryptResultMessage = ref('')
const decryptResultIsError = ref(false)

// ---- 清單頁籤 ----
const vaultItems = ref([])
const isLoadingList = ref(false)
const listErrorMessage = ref('')

const isRunningInWebView2 = typeof window.chrome?.webview !== 'undefined'

if (isRunningInWebView2) {
  window.chrome.webview.addEventListener('message', (event) => {
    const data = event.data

    if (data.type === 'encryptResult') {
      isEncrypting.value = false
      encryptResultIsError.value = !data.success
      encryptResultMessage.value = data.success
        ? `加密成功！指標檔位置：${data.lockedMarkerPath}`
        : `加密失敗：${data.errorMessage}`
    } else if (data.type === 'decryptResult') {
      isDecrypting.value = false
      decryptResultIsError.value = !data.success
      decryptResultMessage.value = data.success
        ? `解密成功！已還原至：${data.restoredPath}`
        : `解密失敗：${data.errorMessage}`
    } else if (data.type === 'error') {
      isEncrypting.value = false
      isDecrypting.value = false
      isLoadingList.value = false
      encryptResultIsError.value = true
      encryptResultMessage.value = `發生錯誤：${data.message}`
    } else if (data.type === 'pathPicked') {
      if (data.purpose === 'decryptPath') {
        decryptPath.value = data.path
      } else {
        encryptPath.value = data.path
      }
    } else if (data.type === 'vaultList') {
      isLoadingList.value = false
      vaultItems.value = data.items
    } else if (data.type === 'deleteRecordResult') {
      handleDeleteRecordResult(data)
    }
  })
}

// 切到清單頁籤時自動刷新一次。
watch(activeTab, (tab) => {
  if (tab === 'list') {
    refreshList()
  }
})

function refreshList() {
  isLoadingList.value = true
  listErrorMessage.value = ''
  window.chrome.webview.postMessage({ type: 'listVault' })
}

function pickFile() {
  window.chrome.webview.postMessage({ type: 'pickFile', purpose: 'encryptPath' })
}

function pickFolder() {
  window.chrome.webview.postMessage({ type: 'pickFolder' })
}

function pickLockedFile() {
  window.chrome.webview.postMessage({ type: 'pickFile', purpose: 'decryptPath' })
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
    hint: hint.value
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
    // 對應規格文件 3.2 節設計好的白話提示文案。
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
</script>

<template>
  <div style="padding: 2rem; font-family: sans-serif; max-width: 640px;">
    <div style="margin-bottom: 1.5rem;">
      <button @click="activeTab = 'encrypt'" :disabled="activeTab === 'encrypt'">加密</button>
      <button @click="activeTab = 'decrypt'" :disabled="activeTab === 'decrypt'" style="margin-left: 0.5rem;">解密</button>
      <button @click="activeTab = 'list'" :disabled="activeTab === 'list'" style="margin-left: 0.5rem;">已加密清單</button>
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
      <p v-if="decryptResultMessage" :style="{ color: decryptResultIsError ? 'red' : 'green' }">
        {{ decryptResultMessage }}
      </p>
    </div>

    <div v-else>
      <h1>已加密清單</h1>
      <button @click="refreshList" :disabled="isLoadingList" style="margin-bottom: 1rem;">
        {{ isLoadingList ? '載入中...' : '重新整理' }}
      </button>

      <p v-if="listErrorMessage" style="color: red;">{{ listErrorMessage }}</p>
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
            <td style="padding: 0.5rem;">
              <button @click="requestDelete(item)" type="button">刪除紀錄</button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>
</template>