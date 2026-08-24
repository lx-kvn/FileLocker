// 側欄收合時，nav 項目的 hover/focus 提示框位置計算。純函式，不碰 DOM——呼叫端負責量測
// anchorRect（觸發提示的按鈕）跟 tooltipSize（提示框本身，通常是渲染後才量得到的實際尺寸），
// 這裡只負責算出「放在哪裡才不會被視窗邊緣裁掉」。
//
// 改用這個純函式取代原本的 CSS ::after 做法，是因為原本的 tooltip 是 nav 項目的偽元素，
// 而 .app-sidebar 容器本身有 overflow:hidden（收合/展開的寬度動畫需要），提示框只要往右
// 延伸出側欄本身的寬度，就會被自己的容器邊界直接裁掉——不是「貼近視窗邊緣才會裁到」，
// 而是原本的做法在收合寬度下（60px）幾乎必定被裁。這裡改成搭配 <Teleport to="body">
// 把提示框整個搬到側欄容器外面渲染，所以才需要自己算絕對座標，不能再依賴 CSS 的
// left: calc(100% + 10px) 相對定位。

// 預設放在錨點右側、垂直置中；如果右側放不下（例如視窗被拉得極窄），改放到錨點左側。
// 不管放哪一側，上下都要跟視窗邊緣留出 margin，不能讓提示框超出可視範圍。
export function computeTooltipPosition({
  anchorRect,
  tooltipSize,
  viewportWidth,
  viewportHeight,
  gap = 10,
  margin = 8,
}) {
  let left = anchorRect.right + gap
  const overflowsRight = left + tooltipSize.width + margin > viewportWidth
  if (overflowsRight) {
    left = anchorRect.left - gap - tooltipSize.width
  }
  // 不管左右哪一側，都不能讓提示框超出視窗左右邊界（例如視窗窄到兩側都放不下的極端情況）。
  left = Math.max(margin, Math.min(left, viewportWidth - tooltipSize.width - margin))

  let top = anchorRect.top + anchorRect.height / 2 - tooltipSize.height / 2
  top = Math.max(margin, Math.min(top, viewportHeight - tooltipSize.height - margin))

  return { top, left }
}
