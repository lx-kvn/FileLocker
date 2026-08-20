// 票根清單每一列的圖示/識別色，依「副檔名/是不是資料夾」對應出來。純函式，不碰任何 Vue ref，
// 呼叫端（TicketRow.vue）把 { type, originalName } 傳進來就好，方便單獨測試。
//
// 顏色沿用 design-exploration/gui-styles-v2/13-sidebar-ticket-shell.html 裡手刻的範例配色
// （PDF 磚紅、資料夾黃銅、壓縮檔墨綠、憑證檔藍），但那份 mockup 全部寫死在畫面裡，沒有
// 「查不到副檔名對照」的 fallback（見該檔案 line 648-655 的 TODO 註解）。這裡補上 fallback：
// 查不到的一律當作泛用檔案圖示 + --color-text-tertiary（不用語意色，避免使用者誤以為是警告）。

const EXTENSION_VISUALS = {
  pdf: { icon: 'document', color: '#C1502F' },
  zip: { icon: 'archive', color: '#4F7A52' },
  '7z': { icon: 'archive', color: '#4F7A52' },
  rar: { icon: 'archive', color: '#4F7A52' },
  pfx: { icon: 'certificate', color: '#2B6CB0' },
  p12: { icon: 'certificate', color: '#2B6CB0' },
  cer: { icon: 'certificate', color: '#2B6CB0' },
  doc: { icon: 'document', color: '#2B5FA6' },
  docx: { icon: 'document', color: '#2B5FA6' },
  xls: { icon: 'document', color: '#1F7A4D' },
  xlsx: { icon: 'document', color: '#1F7A4D' },
  jpg: { icon: 'image', color: '#8A5CB0' },
  jpeg: { icon: 'image', color: '#8A5CB0' },
  png: { icon: 'image', color: '#8A5CB0' },
  gif: { icon: 'image', color: '#8A5CB0' },
  webp: { icon: 'image', color: '#8A5CB0' },
  // 音檔：獨立的青綠色，跟既有色票（PDF 磚紅、doc 藍、xls/zip 綠、圖片紫、憑證藍）
  // 拉開色相，同一份清單裡一眼能分辨出「這是音檔」。
  mp3: { icon: 'audio', color: '#1E8A8A' },
  wav: { icon: 'audio', color: '#1E8A8A' },
  m4a: { icon: 'audio', color: '#1E8A8A' },
  flac: { icon: 'audio', color: '#1E8A8A' },
  ogg: { icon: 'audio', color: '#1E8A8A' },
  // 純文字檔：跟 PDF／doc 同屬「文件」大類但不是同一種顏色——用比 PDF 磚紅更淡的赭橙，
  // 表達「也是文字內容，但不是正式文件格式」的降階觀感。
  txt: { icon: 'text', color: '#B8752E' },
  md: { icon: 'text', color: '#B8752E' },
  csv: { icon: 'text', color: '#B8752E' },
  log: { icon: 'text', color: '#B8752E' },
  json: { icon: 'text', color: '#B8752E' },
  xml: { icon: 'text', color: '#B8752E' },
}

const FOLDER_VISUAL = { icon: 'folder', color: 'var(--color-accent)' }
const FALLBACK_VISUAL = { icon: 'file', color: 'var(--color-text-tertiary)' }

function extensionOf(originalName) {
  const dotIndex = originalName.lastIndexOf('.')
  if (dotIndex <= 0 || dotIndex === originalName.length - 1) return ''
  return originalName.slice(dotIndex + 1).toLowerCase()
}

// item：至少要有 { type, originalName }。type === 'Folder' 直接走資料夾圖示，其餘一律
// 依副檔名查表，查不到就退回 fallback。
export function fileTypeVisual(item) {
  if (item.type === 'Folder') return FOLDER_VISUAL
  const ext = extensionOf(item.originalName || '')
  return EXTENSION_VISUALS[ext] || FALLBACK_VISUAL
}
