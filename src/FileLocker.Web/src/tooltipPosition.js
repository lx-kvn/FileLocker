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

// 疊在錨點正上方（放不下就翻到下方）、水平置中的提示框位置計算。資訊圖示（.info-tooltip）
// 用的是這一種擺法。
//
// 跟上面那個「放在錨點側邊」的是兩個函式而不是同一個加參數：側邊那個的取捨是左右擇一、
// 垂直置中，這個是上下擇一、水平置中，硬共用只會變成一堆互斥的 if。兩者真正共同的部分
// 只有「不能超出視窗邊界」這個夾限，那本來就只有兩行。
//
// 需要自己算絕對座標的理由跟側欄那個相同：提示框搭配 <Teleport to="body"> 渲染，脫離了
// 原本容器的裁切範圍，所以不能再依賴 CSS 的 bottom: calc(100% + 8px) 相對定位。會需要
// teleport 是因為信封加密的密碼卡片在視窗變矮時要能捲動（見 EnvelopeEncrypt.vue 的
// .sheet），而一旦有了 overflow，卡片內任何超出邊界的絕對定位元素都會被裁掉。
export function computeStackedTooltipPosition({
  anchorRect,
  tooltipSize,
  viewportWidth,
  viewportHeight,
  gap = 8,
  margin = 8,
}) {
  const above = anchorRect.top - gap - tooltipSize.height;
  const below = anchorRect.bottom + gap;

  // 預設往上開；上面放不下才翻到下面。兩邊都放不下時（提示框比視窗還高）就貼齊上緣，
  // 至少讓開頭讀得到，而不是整塊飄到視窗外面。
  let top = above >= margin ? above : below;
  top = Math.max(margin, Math.min(top, viewportHeight - tooltipSize.height - margin));

  const anchorCenterX = anchorRect.left + anchorRect.width / 2;
  let left = anchorCenterX - tooltipSize.width / 2;
  left = Math.max(margin, Math.min(left, viewportWidth - tooltipSize.width - margin));

  return { top, left };
}
