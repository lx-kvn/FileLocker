// 破壞性動作要通過幾道關卡的統一判斷。
//
// 分層依據是「後果有多不可逆」，不是「動作名稱聽起來多嚴重」。改版前這兩件事是混在一起的：
// 「清除使用紀錄」聽起來很嚴重，所以被設成沒設定過關鍵操作驗證就整個功能鎖死、還要走三個步驟，
// 但它刪掉的只是本機的操作日誌，加密內容一個都沒少；反過來「永久刪除加密項目」是整個工具裡
// 唯一按下去內容就真的救不回來的動作，卻只要重新輸入一次密碼。輕重剛好顛倒。
//
// 四層的定義（完整說明見 docs/specs/features/通盤檢討_改善計畫.md 第 3 輪）：
//   T0 完全可逆、不掉東西        → 不用任何關卡（切換語言／主題、展開清單、前往資料夾）
//   T1 取回自己的內容            → 一次身分證明（解密、資料夾解鎖，走各自既有的驗證流程）
//   T2 影響範圍大但救得回來      → 已設定過關鍵操作驗證就要驗，加上明確確認
//   T3 內容永久消失              → 密碼 + 關鍵操作驗證（已設定過）+ 最終確認
//
// T0 跟 T1 不在這張表裡：T0 本來就不經過任何關卡，T1 的身分證明是各自流程內建的（解密要密碼、
// 資料夾解鎖要防護密碼），不是這裡這種「額外加一道門」的性質。

export const ACTION_TIERS = {
  /// 內容真的消失，沒有任何後門可以救回來——整個工具裡唯一的 T3。
  deleteRecord: 'T3',

  /// 刪的是本機操作日誌，加密內容不受影響。仍然保留驗證是因為它有隱私意義
  /// （日誌記載使用者操作過哪些檔案），但沒設定過驗證時退回確認彈窗，不再整個功能鎖死。
  clearHistory: 'T2',

  /// 可逆，但影響的是整個集中管理區的位置。
  moveVault: 'T2',
}

/// 認不得的動作一律當成 T3 處理：之後有人新增破壞性動作卻忘了加進對照表時，
/// 寧可多問幾道，也不要靜悄悄地一路放行。
const FALLBACK_TIER = 'T3'

/**
 * 回傳某個動作在目前設定下要通過哪些關卡。
 * @param {string} action ACTION_TIERS 的鍵
 * @param {boolean} criticalActionConfigured 使用者有沒有設定過「關鍵操作驗證」
 */
export function gatesFor(action, criticalActionConfigured) {
  const tier = ACTION_TIERS[action] || FALLBACK_TIER
  return {
    needsPassword: tier === 'T3',
    needsCriticalAction: (tier === 'T2' || tier === 'T3') && !!criticalActionConfigured,
    needsFinalConfirm: true,
  }
}
