// 動畫安全重觸發：世代編號機制（GUI造型探索_技術規格.md §2.12）。
//
// 問題：信封開合／Sheet 進退場這類動畫用 setTimeout 鏈分段驅動（例如「先加 class A，420ms 後
// 移除 class A、加上 class B」），如果使用者在動畫播到一半就快速重新觸發（例如按了取消又立刻
// 重新開啟），舊的 setTimeout callback 仍然會在之後某個時間點執行，跟新一輪動畫的狀態互相
// 干擾，導致畫面停在不一致的中間狀態（技術規格記錄過兩個實際案例：取消後重開信封還是開著飛
// 下來、蠟封在飛下來過程中跑到封口下面）。
//
// 做法：每個會播動畫的物件（不一定是 DOM 元素，任何一個 JS 物件都能當 key）各自維護一個遞增
// 計數器。每次重新觸發動畫就把計數器加一，取得這一輪的「世代編號」；所有排定的 setTimeout
// callback 執行前先確認自己拿到的世代編號還是不是這個物件當下最新的一份，不是的話就直接不做
// 任何事（也不 reject/報錯，安全地當沒發生過）。
//
// 用 WeakMap 而不是在每個物件上掛屬性：不用去污染呼叫端的物件本身，物件被回收時這裡的紀錄也
// 自動一起消失，不需要手動清理。
const generations = new WeakMap()

// 宣告「這是最新的一輪」，回傳這一輪的世代編號。呼叫時機：動畫一開始播放的當下，或需要瞬間
// 重置狀態時（瞬間重置本身也算宣告了新的一輪，蓋掉任何舊動畫殘留的 callback）。
export function bumpGen(key) {
  const next = (generations.get(key) || 0) + 1
  generations.set(key, next)
  return next
}

// 排定的 callback 執行前呼叫，確認拿到的世代編號還是不是目前最新的一份。
export function isCurrentGen(key, gen) {
  return generations.get(key) === gen
}
