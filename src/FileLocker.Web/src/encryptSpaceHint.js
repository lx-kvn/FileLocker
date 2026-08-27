// 「加密前顯示預估所需空間」在前端的呈現判斷（對應技術規格文件第 5 節）。
//
// 抽成純函式的理由跟 protectionTiers.js／nestedGuardedRetry.js 一致：這裡只有「多大才值得說」
// 跟「數字怎麼寫成人看得懂的樣子」兩件事，跟 Vue 的響應式狀態無關，抽出來才測得到。

const UNITS = [
  { limit: 1024 ** 3, suffix: 'GB' },
  { limit: 1024 ** 2, suffix: 'MB' },
  { limit: 1024, suffix: 'KB' },
]

/// 依大小自動選單位。固定用 GB 會讓小檔案顯示成「0.0 GB」，固定用 MB 會讓大資料夾顯示成
/// 「38912.0 MB」，兩種都是讀不出來的數字。
export function formatBytes(bytes) {
  for (const { limit, suffix } of UNITS) {
    if (bytes >= limit) {
      return `${(bytes / limit).toFixed(1)} ${suffix}`
    }
  }
  return `${bytes} B`
}

// 空間夠的情況下，多大才值得主動說一聲。規格第 5 節在意的是「非常大的資料夾（例如數十 GB）」
// 那種會讓人措手不及的量；門檻壓在 1 GB，比那個保守一些，但仍然遠高於日常加密幾份文件的量級
// ——每次加密都跳一行沒人要看的數字只會變成雜訊，久了連真的該看的那次也會被略過。
const NOTEWORTHY_BYTES = 1024 ** 3

/**
 * 回傳要顯示的提示，或 null（不顯示）。
 * @param {{ totalRequiredBytes: number, sufficient: boolean } | null | undefined} estimate
 */
export function encryptSpaceHintFor(estimate) {
  if (!estimate || estimate.totalRequiredBytes <= 0) {
    return null
  }

  // 空間不足一律提醒，不套門檻——量小但真的放不下，才是使用者最需要事先知道的那種情況。
  if (!estimate.sufficient) {
    return { level: 'warning', amount: formatBytes(estimate.totalRequiredBytes) }
  }

  return estimate.totalRequiredBytes >= NOTEWORTHY_BYTES
    ? { level: 'info', amount: formatBytes(estimate.totalRequiredBytes) }
    : null
}
