<script setup>
import { computed, ref, watch } from 'vue'
// 轉盤素材拆成三層獨立圖層（使用者提供）：top／bottom 是固定不動的外框／底盤，只有 middle
// （轉盤本體）接手原本整張圖在轉的旋轉動畫——疊放順序 top 最上層、middle 居中、bottom 最
// 底層，用 z-index 排，不是 DOM 順序（三層都是 position:absolute，DOM 順序本身不影響）。
//
// 兩組素材：金庫層的大轉輪（VaultAddFolderOverlay，dimUnlocked=false）跟清單列的小轉輪
// （資料夾防護清單，dimUnlocked=true 預設）用的是使用者分開設計的兩套圖，不是同一張圖
// 縮放——清單版本（Vault_Wheel_List_*）線條在縮小尺寸下比較清楚。用既有的 dimUnlocked
// 這個 prop 分流（它本來就是「這是清單小轉輪還是金庫大轉輪」的既有判斷依據，不用另外
// 加一個新 prop），金庫門上的轉輪維持原本素材不變。
import vaultWheelTopUrl from '../assets/Vault_Wheel_top.svg'
import vaultWheelMiddleUrl from '../assets/Vault_Wheel_middle.svg'
import vaultWheelBottomUrl from '../assets/Vault_Wheel_bottom.svg'
import vaultWheelListTopUrl from '../assets/Vault_Wheel_List_top.svg'
import vaultWheelListMiddleUrl from '../assets/Vault_Wheel_List_middle.svg'
import vaultWheelListBottomUrl from '../assets/Vault_Wheel_List_bottom.svg'
// 清單小轉輪專用的背景襯底（使用者自己畫的，取代先前 CSS 深灰方塊的試做版）——疊在 bottom
// 圖層下面、z-index 最低，不參與旋轉動畫（跟 top/bottom 一樣是固定不動的靜態層，只有
// middle 那層會轉）。金庫層大轉輪（dimUnlocked=false）不用這個背景，維持原本鏤空的樣子。
import vaultWheelBackgroundUrl from '../assets/Vault_Wheel_Background.svg'

// 常態鎖定/解鎖用同一套顏色，靠角度＋透明度區分（定案文件〈清單常態圖示的狀態區分〉）。
// 這兩個數字是先給一個合理起手值，實際效果要用 run skill 截圖走查後再微調，不是定死的規格。
const IDLE_UNLOCKED_ANGLE_DEG = 16
const IDLE_UNLOCKED_OPACITY = 0.45

// 轉輪完整旋轉的分段速度曲線與時長沿用 10-vault-door.html 已經驗證過的數值，不重新調參，
// 「先加速、持續減速、到定位前衝過頭再彈回卡住」的手感細節見該檔案內註解——這裡只調整了
// 最終停等角度（跟該檔案不同）。
//
// 修正記錄：原本開鎖停在 540deg（=360+180，90° 整數倍），是配合舊版轉盤四握把、四等分
// 造型的對稱性算的——90° 整數倍的角度，看起來會跟 0 度一模一樣。使用者換上新的轉盤素材
// （Vault_Wheel_middle.svg）之後，這個假設不再成立：新造型是五輻條，每 72 度才重複一次
// 圖案，540 度沒對齊在這個週期上（540 mod 72 = 36，不是整數倍）。動畫播完的瞬間
// finishSpin() 會把播動畫用的 class 整個拿掉、退回寫死的靜止樣式（見下面 keyframes 下方
// spin()／finishSpin() 的說明），這一拿掉就會露餡：540 度跟退回去的 0 度看起來根本不是
// 同一個角度，使用者看到的就是「轉到底突然喀一下跳回原位」。
// 改成 504deg（=72×7）——跟新轉盤的對稱週期對齊，504 度看起來會跟 0 度一模一樣，class
// 被拿掉的那一刻視覺上完全無縫，不會再跳動。換過轉盤造型如果對稱週期改變，這個數字要
// 跟著重算（一律取「72 的整數倍」這個規則本身不變，72 這個數字本身才是跟著轉盤造型變的）。
const WHEEL_OPEN_MS = 650
const WHEEL_CLOSE_MS = 780

const props = defineProps({
  locked: { type: Boolean, required: true },
  size: { type: Number, default: 22 },
  // 金庫層的大轉輪只是儀式感道具，不代表任何鎖定狀態，開門/關門過程中顏色不該跟著
  // 「解鎖」變淡——只有清單列的小轉輪才需要靠角度/透明度區分常態鎖定/解鎖兩態。
  dimUnlocked: { type: Boolean, default: true }
})

const wheelTopUrl = computed(() => props.dimUnlocked ? vaultWheelListTopUrl : vaultWheelTopUrl)
const wheelMiddleUrl = computed(() => props.dimUnlocked ? vaultWheelListMiddleUrl : vaultWheelMiddleUrl)
const wheelBottomUrl = computed(() => props.dimUnlocked ? vaultWheelListBottomUrl : vaultWheelBottomUrl)

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
    <img v-if="dimUnlocked" :src="vaultWheelBackgroundUrl" alt="" class="vault-wheel-icon__layer vault-wheel-icon__layer--background" />
    <img :src="wheelBottomUrl" alt="" class="vault-wheel-icon__layer vault-wheel-icon__layer--bottom" />
    <img
      ref="wheelImgEl"
      :src="wheelMiddleUrl"
      alt=""
      class="vault-wheel-icon__layer vault-wheel-icon__layer--middle"
      :class="spinClass"
      :style="{ '--idle-unlocked-angle': `${IDLE_UNLOCKED_ANGLE_DEG}deg` }"
    />
    <img :src="wheelTopUrl" alt="" class="vault-wheel-icon__layer vault-wheel-icon__layer--top" />
  </span>
</template>

<style scoped>
.vault-wheel-icon {
  position: relative;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  /* 這顆 span 本身還是「行內」層級的元素（inline-flex 只改 flex 版面配置，不會讓它變成
     block），放進表格儲存格時預設用 vertical-align: baseline——對齊文字基線，不是儲存格
     的置中位置。資料夾防護清單用 `<td>` 的 vertical-align:middle 把整格內容置中，那一層
     只管「整行內容」在儲存格裡的位置，這顆圖示自己在那行內容裡還會依基線再偏一次，兩層
     疊起來就是實測量到的「圖示比文字/按鈕高了快 3px」。這裡蓋掉基線對齊、改置中，跟外層
     儲存格的置中方式一致，兩層疊加後才會真的對齊，不是只解決其中一層。 */
  vertical-align: middle;
}

/* 三層都疊在同一個 position:relative 容器裡、彼此絕對定位重疊——z-index 決定疊放順序
   （top 最上層、bottom 最底層），跟 DOM 順序無關，寫在同一個地方比較不容易搞混。 */
.vault-wheel-icon__layer {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.vault-wheel-icon__layer--background { z-index: 0; }
.vault-wheel-icon__layer--bottom { z-index: 1; }
.vault-wheel-icon__layer--middle { z-index: 2; }
.vault-wheel-icon__layer--top { z-index: 3; }

/* 回饋：解鎖時只有中間那層（轉盤本體）變淺，外框／底盤兩層顏色沒有一起變淺，看起來
   像只變了一半——清單版素材（Vault_Wheel_List_*）的外框／底盤本身也帶顏色（不是像舊版
   那樣純線稿框），旋轉角度這個視覺語言維持只給 middle（外框/底盤本來就不轉，轉了才奇怪），
   但常態鎖定/解鎖的透明度改成三層一起變，才會是「整顆圖示一起變淺」而不是只有中間亮。 */
.vault-wheel-icon__layer--top,
.vault-wheel-icon__layer--bottom {
  transition: opacity 200ms var(--ease-out, ease);
  opacity: 1;
}

.vault-wheel-icon__layer--middle {
  transform-origin: 50% 50%;
  transition: opacity 200ms var(--ease-out, ease);
  /* 預設（=金庫層大轉輪，dimUnlocked=false 時兩個狀態 class 都不會套用）：全彩、不偏轉角度。 */
  opacity: 1;
  transform: rotateZ(0deg);
}

.vault-wheel-icon.is-locked .vault-wheel-icon__layer--middle {
  opacity: 1;
  transform: rotateZ(0deg);
}

.vault-wheel-icon.is-unlocked .vault-wheel-icon__layer--top,
.vault-wheel-icon.is-unlocked .vault-wheel-icon__layer--bottom {
  opacity: v-bind(IDLE_UNLOCKED_OPACITY);
}

.vault-wheel-icon.is-unlocked .vault-wheel-icon__layer--middle {
  opacity: v-bind(IDLE_UNLOCKED_OPACITY);
  transform: rotateZ(var(--idle-unlocked-angle));
}

.vault-wheel-icon.is-wiggling .vault-wheel-icon__layer--middle {
  animation: vault-wheel-icon-wiggle 200ms ease-out;
}

@keyframes vault-wheel-icon-wiggle {
  0% { transform: rotateZ(0deg); }
  35% { transform: rotateZ(-10deg); }
  70% { transform: rotateZ(4deg); }
  100% { transform: rotateZ(0deg); }
}

/* 分段速度曲線跟接點百分比直接沿用 10-vault-door.html 量測驗證過的數值，不要憑印象重調——
   停等角度（100% 那一格）跟過頭再彈回的過頭量（85%/90% 那一格）已經改成對齊新轉盤的 72°
   對稱週期，見上面 WHEEL_OPEN_MS 旁邊的修正記錄。過頭量本身（+10°／-9°）是動畫播放中途
   的暫態，不需要也對齊 72°，只有 100% 落地那一格需要。 */
@keyframes vault-wheel-icon-spin-open {
  0% { transform: rotateZ(0deg); animation-timing-function: cubic-bezier(0.18, 0.5, 0.32, 1); }
  85% { transform: rotateZ(514deg); animation-timing-function: cubic-bezier(0.3, 0.6, 0.5, 1); }
  100% { transform: rotateZ(504deg); }
}

@keyframes vault-wheel-icon-spin-close {
  0% { transform: rotateZ(504deg); animation-timing-function: cubic-bezier(0.18, 0.5, 0.32, 1); }
  90% { transform: rotateZ(-9deg); animation-timing-function: cubic-bezier(0.3, 0.6, 0.5, 1); }
  100% { transform: rotateZ(0deg); }
}

.vault-wheel-icon__layer--middle.is-spin-open {
  animation: vault-wheel-icon-spin-open 650ms both;
  opacity: 1;
}

.vault-wheel-icon__layer--middle.is-spin-close {
  animation: vault-wheel-icon-spin-close 780ms both;
  opacity: 1;
}

@media (prefers-reduced-motion: reduce) {
  .vault-wheel-icon.is-wiggling .vault-wheel-icon__layer--middle {
    animation: none;
    opacity: 0.7;
  }

  .vault-wheel-icon__layer--middle.is-spin-open,
  .vault-wheel-icon__layer--middle.is-spin-close {
    animation: none !important;
    transform: none !important;
    opacity: 0.7;
  }
}
</style>
