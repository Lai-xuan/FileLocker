<script setup>
import { ref } from 'vue'

const path = ref('')
const password = ref('')
const hint = ref('')
const isEncrypting = ref(false)
const resultMessage = ref('')
const resultIsError = ref(false)

const isRunningInWebView2 = typeof window.chrome?.webview !== 'undefined'

if (isRunningInWebView2) {
  window.chrome.webview.addEventListener('message', (event) => {
    const data = event.data

    if (data.type === 'encryptResult') {
      isEncrypting.value = false
      if (data.success) {
        resultIsError.value = false
        resultMessage.value = `加密成功！指標檔位置：${data.lockedMarkerPath}`
      } else {
        resultIsError.value = true
        resultMessage.value = `加密失敗：${data.errorMessage}`
      }
    } else if (data.type === 'error') {
      isEncrypting.value = false
      resultIsError.value = true
      resultMessage.value = `發生錯誤：${data.message}`
    } else if (data.type === 'pathPicked') {
      path.value = data.path
    }
    // pathPickCancelled 不做任何事，使用者只是按了取消。
  })
}

function pickFile() {
  window.chrome.webview.postMessage({ type: 'pickFile' })
}

function pickFolder() {
  window.chrome.webview.postMessage({ type: 'pickFolder' })
}

function submitEncrypt() {
  if (!isRunningInWebView2) {
    resultIsError.value = true
    resultMessage.value = '目前是在一般瀏覽器裡執行，不是 WebView2，無法呼叫加密功能。'
    return
  }

  if (!path.value || !password.value) {
    resultIsError.value = true
    resultMessage.value = '請至少填寫路徑跟密碼。'
    return
  }

  isEncrypting.value = true
  resultMessage.value = ''
  window.chrome.webview.postMessage({
    type: 'encrypt',
    path: path.value,
    password: password.value,
    hint: hint.value
  })
}
</script>

<template>
  <div style="padding: 2rem; font-family: sans-serif; max-width: 480px;">
    <h1>加密檔案／資料夾</h1>

    <div style="margin-bottom: 1rem;">
      <label>檔案或資料夾路徑</label><br />
      <input v-model="path" placeholder="例如 D:\測試檔案.txt" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
      <div style="margin-top: 0.5rem;">
        <button @click="pickFile" type="button">選擇檔案</button>
        <button @click="pickFolder" type="button" style="margin-left: 0.5rem;">選擇資料夾</button>
      </div>
    </div>

    <div style="margin-bottom: 1rem;">
      <label>密碼</label><br />
      <input v-model="password" type="password" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
    </div>

    <div style="margin-bottom: 1rem;">
      <label>提示（可留空）</label><br />
      <input v-model="hint" style="width: 100%; padding: 0.5rem; box-sizing: border-box;" />
    </div>

    <button @click="submitEncrypt" :disabled="isEncrypting">
      {{ isEncrypting ? '加密中...' : '加密' }}
    </button>

    <p v-if="resultMessage" :style="{ color: resultIsError ? 'red' : 'green' }">
      {{ resultMessage }}
    </p>
  </div>
</template>