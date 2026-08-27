<script setup>
// 資訊圖示（小圓圈裡一個 i 或 ?）加上滑鼠移上去／鍵盤 focus 才出現的說明泡泡。
//
// 泡泡用 <Teleport to="body"> 搬到外面渲染、自己算絕對座標，不是用 CSS 的
// bottom: calc(100% + 8px) 相對定位——這跟 AppSidebar.vue 的提示框是同一個理由：只要祖先
// 元素有 overflow（不是 visible），泡泡超出那個容器邊界的部分就會被直接裁掉。信封加密的
// 密碼卡片在視窗變矮時需要能捲動（見 EnvelopeEncrypt.vue 的 .sheet），一有 overflow，
// 原本往上開的泡泡在捲到底時上緣就會被切掉一截（實測 600px 視窗高度下被裁掉 19px）。
//
// 位置計算是 tooltipPosition.js 的純函式，元件本身只負責量測與渲染，方便單獨測試。
import { ref, nextTick, onMounted, onUnmounted } from 'vue'
import { computeStackedTooltipPosition } from '../tooltipPosition.js'

const props = defineProps({
  text: { type: String, required: true },
  // 圖示裡顯示的字。'i' 用斜體襯線（像印刷體的資訊符號），'?' 用正體——同一顆殼子放問號時
  // 斜體會顯得歪斜、像排版錯誤，不是同一種符號的視覺語言。
  symbol: { type: String, default: 'i' },
  // 泡泡寬度。預設值配合信封卡片的內容寬度；放在寬鬆版面時呼叫端可以自己放寬。
  width: { type: Number, default: 240 },
})

const anchorEl = ref(null)
const bubbleEl = ref(null)
const open = ref(false)
const style = ref({ top: '0px', left: '0px', opacity: 0 })

async function show() {
  open.value = true
  await nextTick()
  reposition()
}

function hide() {
  open.value = false
  style.value = { ...style.value, opacity: 0 }
}

function reposition() {
  if (!anchorEl.value || !bubbleEl.value) return
  const anchorRect = anchorEl.value.getBoundingClientRect()
  const size = bubbleEl.value.getBoundingClientRect()
  const pos = computeStackedTooltipPosition({
    anchorRect,
    tooltipSize: { width: size.width, height: size.height },
    viewportWidth: window.innerWidth,
    viewportHeight: window.innerHeight,
  })
  style.value = { top: `${pos.top}px`, left: `${pos.left}px`, opacity: 1 }
}

// 鍵盤 focus 也要能叫出說明（無障礙需求），但滑鼠點擊產生的 focus 不用重複跳一次——
// 用 :focus-visible 判斷這次 focus 是不是鍵盤導覽觸發的。比照 AppSidebar.vue 的既有作法。
function onFocus(event) {
  if (event.target.matches(':focus-visible')) show()
}

// 泡泡已經脫離原本的容器，捲動時不會跟著跑——顯示期間跟著重算位置。用 capture 監聽是因為
// 真正在捲的是卡片自己，不是 window，事件不會冒泡到 window 上。
function onAnyScroll() {
  if (open.value) reposition()
}

onMounted(() => {
  window.addEventListener('scroll', onAnyScroll, true)
  window.addEventListener('resize', onAnyScroll)
})

onUnmounted(() => {
  window.removeEventListener('scroll', onAnyScroll, true)
  window.removeEventListener('resize', onAnyScroll)
})
</script>

<template>
  <span
    ref="anchorEl"
    class="info-tooltip"
    tabindex="0"
    @mouseenter="show"
    @mouseleave="hide"
    @focus="onFocus"
    @blur="hide"
  >
    <span
      class="info-tooltip__icon"
      :class="{ 'info-tooltip__icon--plain': symbol !== 'i' }"
    >{{ symbol }}</span>

    <Teleport to="body">
      <span
        v-if="open"
        ref="bubbleEl"
        class="info-tooltip__bubble info-tooltip__bubble--floating"
        :style="{ ...style, width: `${width}px` }"
        role="tooltip"
      >{{ text }}</span>
    </Teleport>
  </span>
</template>

<style scoped>
/* 圖示本身沿用 App.vue 全域的 .info-tooltip__icon 樣式（那份 <style> 沒有 scoped），
   這裡只補 teleport 之後泡泡需要的定位相關規則——全域那份是 position:absolute 相對於
   圖示，搬到 body 之後要改成 fixed 加自己算的座標。 */
.info-tooltip__bubble--floating {
  position: fixed;
  bottom: auto;
  left: auto;
  transform: none;
  pointer-events: none;
  z-index: 300;
  transition: opacity var(--duration-fast, 150ms) var(--ease-out, ease);
}
</style>
