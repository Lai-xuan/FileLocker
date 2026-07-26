// 對應架構審查（2026-07-27）：這幾個函式本來就是純函式（吃資料吐資料，不碰任何 Vue ref），
// 只是先前定義在 App.vue 的 <script setup> 裡，想單獨測試就得掛載整個元件、假造
// window.chrome.webview。搬到這裡之後維持純函式的形狀——連翻譯函式 t 都當參數傳入，
// 不從任何全域狀態抓，呼叫端（App.vue）想測試時可以傳一個假的 t 進來，不需要真的
// 初始化 i18n。

// 把清單裡帶有相同 batchId 的項目摺疊成一組，沒有 batchId 的維持獨立顯示。
// 分組本身完全在前端做——後端只負責在每個項目上帶 batchId，分不分組、怎麼呈現都是畫面的事。
export function groupVaultItems(items) {
  const groups = new Map()
  const standalone = []

  for (const item of items) {
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
  for (const [batchId, groupItems] of groups) {
    result.push({ isGroup: true, batchId, items: groupItems })
  }

  result.sort((a, b) => {
    const latest = (entry) => entry.isGroup
      ? Math.max(...entry.items.map((i) => new Date(i.createdAtUtc).getTime()))
      : new Date(entry.item.createdAtUtc).getTime()
    return latest(b) - latest(a)
  })

  return result
}

export function batchPreviewText(items, t) {
  const names = items.map((i) => i.originalName)
  if (names.length <= 2) {
    return names.join('、')
  }
  return names.slice(0, 2).join('、') + t('batchPreview.suffix', { count: names.length })
}

// 巢狀鎖定圖示的 tooltip：列出裡面實際包含哪些檔案，查不到任何名稱（例如巢狀項目後來
// 也被刪除了）就退回通用文字，不留空白 tooltip。
export function nestedLockPreviewText(item, t) {
  const names = item.nestedLockItemNames || []
  if (names.length === 0) {
    return t('list.nestedLockTitle')
  }
  const preview = names.length <= 2
    ? names.join('、')
    : names.slice(0, 2).join('、') + t('batchPreview.suffix', { count: names.length })
  return t('list.nestedLockPreview', { preview })
}
