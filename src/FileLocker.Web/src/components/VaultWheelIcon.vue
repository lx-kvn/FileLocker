<script setup>
import { ref, watch } from 'vue'
import vaultWheelUrl from '../assets/Vault_Wheel.svg'

// 常態鎖定/解鎖用同一套顏色，靠角度＋透明度區分（定案文件〈清單常態圖示的狀態區分〉）。
// 這兩個數字是先給一個合理起手值，實際效果要用 run skill 截圖走查後再微調，不是定死的規格。
const IDLE_UNLOCKED_ANGLE_DEG = 16
const IDLE_UNLOCKED_OPACITY = 0.45

// 轉輪完整旋轉沿用 10-vault-door.html 已經驗證過的分段速度曲線與時長，不重新調參——
// 開鎖轉完停在 540deg（=360+180，90° 整數倍，配合轉輪四等分握把造型的對稱性）、
// 上鎖轉回 0deg，兩段「先加速、持續減速、到定位前衝過頭再彈回卡住」的手感細節見該檔案內註解。
const WHEEL_OPEN_MS = 650
const WHEEL_CLOSE_MS = 780

const props = defineProps({
  locked: { type: Boolean, required: true },
  size: { type: Number, default: 22 },
  // 金庫層的大轉輪只是儀式感道具，不代表任何鎖定狀態，開門/關門過程中顏色不該跟著
  // 「解鎖」變淡——只有清單列的小轉輪才需要靠角度/透明度區分常態鎖定/解鎖兩態。
  dimUnlocked: { type: Boolean, default: true }
})

// 本地顯示狀態跟 `locked` prop 初始同步，但動畫進行中刻意不跟著 prop 變動——
// `refreshFolderGuardList()` 會整批換掉 folderGuardItems 陣列，如果讓顯示狀態直接綁 prop，
// 播到一半的旋轉動畫會被這個資料刷新打斷，改成動畫播完當下才手動更新。
const displayLocked = ref(props.locked)
const isAnimating = ref(false)
const wiggling = ref(false)
const spinClass = ref('')
const wheelImgEl = ref(null)
let activeSpinTimeoutId = null
let activeSpinResolve = null
let activeSpinDirection = null

watch(
  () => props.locked,
  (value) => {
    if (!isAnimating.value) {
      displayLocked.value = value
    }
  }
)

function forceReflow() {
  // eslint-disable-next-line no-unused-expressions
  wheelImgEl.value?.offsetWidth
}

function wiggle() {
  if (isAnimating.value) {
    return
  }
  wiggling.value = false
  forceReflow()
  wiggling.value = true
  window.setTimeout(() => {
    wiggling.value = false
  }, 200)
}

function finishSpin(direction, resolve) {
  spinClass.value = ''
  displayLocked.value = direction === 'lock'
  isAnimating.value = false
  activeSpinTimeoutId = null
  activeSpinResolve = null
  activeSpinDirection = null
  resolve()
}

function spin(direction) {
  return new Promise((resolve) => {
    if (isAnimating.value) {
      resolve()
      return
    }
    isAnimating.value = true
    spinClass.value = ''
    forceReflow()
    const className = direction === 'unlock' ? 'is-spin-open' : 'is-spin-close'
    const duration = direction === 'unlock' ? WHEEL_OPEN_MS : WHEEL_CLOSE_MS
    spinClass.value = className
    activeSpinDirection = direction
    activeSpinResolve = resolve
    activeSpinTimeoutId = window.setTimeout(() => {
      finishSpin(direction, resolve)
    }, duration)
  })
}

// 提前結束正在播放的完整旋轉——使用者點金庫層背景要求「提前結束關閉動畫」時用（見
// VaultAddFolderOverlay），拿掉動畫 class 讓瀏覽器立刻跳到常態 CSS（是視覺上的瞬間跳格，
// 不是漸變收尾），跟 wiggle/spin 一樣的「動畫進行中忽略新指令」保護不受影響——這裡是
// 主動結束目前這一個，不是疊加新的一個。
function skipSpin() {
  if (!isAnimating.value || activeSpinTimeoutId === null) {
    return
  }
  window.clearTimeout(activeSpinTimeoutId)
  finishSpin(activeSpinDirection, activeSpinResolve)
}

defineExpose({ wiggle, spin, skipSpin })
</script>

<template>
  <span
    class="vault-wheel-icon"
    :class="{ 'is-locked': displayLocked, 'is-unlocked': !displayLocked && dimUnlocked, 'is-wiggling': wiggling }"
    :style="{ width: `${size}px`, height: `${size}px` }"
  >
    <img
      ref="wheelImgEl"
      :src="vaultWheelUrl"
      alt=""
      class="vault-wheel-icon__img"
      :class="spinClass"
      :style="{ '--idle-unlocked-angle': `${IDLE_UNLOCKED_ANGLE_DEG}deg` }"
    />
  </span>
</template>

<style scoped>
.vault-wheel-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.vault-wheel-icon__img {
  width: 100%;
  height: 100%;
  object-fit: contain;
  transform-origin: 50% 50%;
  transition: opacity 200ms var(--ease-out, ease);
  /* 預設（=金庫層大轉輪，dimUnlocked=false 時兩個狀態 class 都不會套用）：全彩、不偏轉角度。 */
  opacity: 1;
  transform: rotateZ(0deg);
}

.vault-wheel-icon.is-locked .vault-wheel-icon__img {
  opacity: 1;
  transform: rotateZ(0deg);
}

.vault-wheel-icon.is-unlocked .vault-wheel-icon__img {
  opacity: v-bind(IDLE_UNLOCKED_OPACITY);
  transform: rotateZ(var(--idle-unlocked-angle));
}

.vault-wheel-icon.is-wiggling .vault-wheel-icon__img {
  animation: vault-wheel-icon-wiggle 200ms ease-out;
}

@keyframes vault-wheel-icon-wiggle {
  0% { transform: rotateZ(0deg); }
  35% { transform: rotateZ(-10deg); }
  70% { transform: rotateZ(4deg); }
  100% { transform: rotateZ(0deg); }
}

/* 分段速度曲線跟接點百分比直接沿用 10-vault-door.html 量測驗證過的數值，不要憑印象重調。 */
@keyframes vault-wheel-icon-spin-open {
  0% { transform: rotateZ(0deg); animation-timing-function: cubic-bezier(0.18, 0.5, 0.32, 1); }
  85% { transform: rotateZ(550deg); animation-timing-function: cubic-bezier(0.3, 0.6, 0.5, 1); }
  100% { transform: rotateZ(540deg); }
}

@keyframes vault-wheel-icon-spin-close {
  0% { transform: rotateZ(540deg); animation-timing-function: cubic-bezier(0.18, 0.5, 0.32, 1); }
  90% { transform: rotateZ(-9deg); animation-timing-function: cubic-bezier(0.3, 0.6, 0.5, 1); }
  100% { transform: rotateZ(0deg); }
}

.vault-wheel-icon__img.is-spin-open {
  animation: vault-wheel-icon-spin-open 650ms both;
  opacity: 1;
}

.vault-wheel-icon__img.is-spin-close {
  animation: vault-wheel-icon-spin-close 780ms both;
  opacity: 1;
}

@media (prefers-reduced-motion: reduce) {
  .vault-wheel-icon.is-wiggling .vault-wheel-icon__img {
    animation: none;
    opacity: 0.7;
  }

  .vault-wheel-icon__img.is-spin-open,
  .vault-wheel-icon__img.is-spin-close {
    animation: none !important;
    transform: none !important;
    opacity: 0.7;
  }
}
</style>
