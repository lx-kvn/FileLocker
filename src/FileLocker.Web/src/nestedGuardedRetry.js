// 加密流程撞到「內含正在防護中的子資料夾」時，前端要做的判斷與字串處理。
//
// 抽成獨立純函式的理由跟 vaultListProjections.js／tooltipPosition.js 一致：這些判斷本身跟
// Vue 的響應式狀態、IPC 訊息格式都無關，抽出來才測得到。這條路徑過去曾經整條斷掉（引導只
// 掛在舊的一次到位加密流程上，而那條流程的入口函式已經沒有任何呼叫端，形成沒有入口的封閉
// 迴圈，實際效果是使用者只會看到一個錯誤訊息、拿不到「解鎖後重試」的引導），所以現在把判斷
// 條件固定在測試裡，避免同樣的斷裂再次發生而沒人發現。

/// 後端 ErrorCodes.FolderGuardContainsNestedGuarded 的值，前端只認這個字串。
export const NESTED_GUARDED_ERROR_CODE = 'FOLDER_GUARD_CONTAINS_NESTED_GUARDED'

/// 後端把多個被擋下來的資料夾路徑用 | 串成一個字串塞進 errorDetail（見 LockService
/// EncryptPendingAsync 的巢狀防護檢查），這裡拆回陣列。
export function parseNestedGuardedPaths(errorDetail) {
  return (errorDetail || '').split('|').filter(Boolean)
}

/// 彈窗文案只需要資料夾名稱，不需要讓使用者讀完整路徑。結尾可能帶分隔符（後端傳來的是
/// 原始路徑，不保證已經去掉），先去掉再取最後一層，否則會取到空字串。
export function formatNestedGuardedNames(paths) {
  return paths
    .map((path) => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop())
    .join('、')
}

/// 只在單一項目加密時提供「解鎖並重試」的引導——批次多筆的重試協調複雜度不成比例，
/// 直接照一般錯誤訊息處理即可（見資料夾防護規劃文件第 8 節）。
export function shouldOfferNestedGuardedRetry(errorCode, selectedPathCount) {
  return errorCode === NESTED_GUARDED_ERROR_CODE && selectedPathCount <= 1
}
