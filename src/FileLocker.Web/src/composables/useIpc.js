// 對應架構審查（2026-07-27）：抽出「怎麼跟 C# 講話」這一層，跟 App.vue 裡的 messageHandlers
// 派送表分開——messageHandlers 本身已經是個經過驗證的深模組（見它上面的既有註解），
// 這裡只收斂「送出去」跟「送出去等一個回應」這兩件事的手刻 Promise 包裝，派送表維持原樣。
//
// pendingResolvers 用「回應訊息類型」當 key，不是用遞增 id 做請求關聯——這跟改動前的行為
// 完全一致：同一種回應類型同時間只會有一個等待中的請求，沒有這個假設就不需要對照表，
// 這裡刻意不做成更複雜的多重併發請求關聯機制，那是目前用不到的功能。
const pendingResolvers = {}

export function sendMessage(type, payload = {}) {
  window.chrome.webview.postMessage({ type, ...payload })
}

export function requestMessage(requestType, responseType, payload = {}) {
  return new Promise((resolve) => {
    pendingResolvers[responseType] = resolve
    sendMessage(requestType, payload)
  })
}

export function resolvePending(responseType, data) {
  pendingResolvers[responseType]?.(data)
  delete pendingResolvers[responseType]
}
