<script setup>
import { ref } from 'vue'
import vaultFrameUrl from '../assets/Vault_Frame.svg'
import vaultDoorUrl from '../assets/Vault_Door_Slab.svg'
import VaultWheelIcon from './VaultWheelIcon.vue'

// 對應 .door 的 CSS transition 時長，跟 10-vault-door.html 一致——轉輪的旋轉時長由
// VaultWheelIcon 自己的 spin() 負責，這裡只需要知道門扇這一段要等多久。
const DOOR_MS = 500

const emit = defineEmits(['cancel'])

const wheelRef = ref(null)
const doorOpen = ref(false)

// 完整流程：轉輪轉完才開門（接力時序），resolve 時門已經完全打開，呼叫端才可以跳出
// Windows 選資料夾對話框（定案文件〈新增資料夾的開門儀式〉步驟 3-4）。
async function playOpen() {
  await wheelRef.value?.spin('unlock')
  doorOpen.value = true
  await new Promise((resolve) => window.setTimeout(resolve, DOOR_MS))
}

// 門先關上，關完轉盤才轉回去（接力反向），resolve 時整套動畫播完，呼叫端才可以讓懸浮層消失
// （定案文件步驟 6-7：選定或取消都走這同一套收場）。
async function playClose() {
  doorOpen.value = false
  await new Promise((resolve) => window.setTimeout(resolve, DOOR_MS))
  await wheelRef.value?.spin('lock')
}

// 點擊金庫圖示以外的背景＝立即取消，交給呼叫端（App.vue）處理——呼叫端會直接把
// `folderGuardOverlayVisible` 設回 false，整個元件連同還沒播完的轉盤/門扇動畫一起被
// v-if 移除，不會像過去那樣還要播完一整段關門動畫才收場。疊層本身的模糊/透明度淡出
// （見下方 `.vault-overlay-leave-*`）仍然會播，但那只是很快的一層背景過場，不是這裡指的
// 「關閉動畫」（那是指立體的轉盤/門扇選繹）。
function onBackdropClick() {
  emit('cancel')
}

defineExpose({ playOpen, playClose })
</script>

<template>
  <div class="vault-add-overlay" @click.self="onBackdropClick">
    <div class="vault-add-overlay__scene" :class="{ 'is-open': doorOpen }">
      <img class="vault-add-overlay__frame" :src="vaultFrameUrl" alt="" />
      <div class="vault-add-overlay__door">
        <img class="vault-add-overlay__door-img" :src="vaultDoorUrl" alt="" />
        <div class="vault-add-overlay__wheel-slot">
          <VaultWheelIcon ref="wheelRef" :locked="true" :size="140" :dim-unlocked="false" />
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.vault-add-overlay {
  position: fixed;
  inset: 0;
  z-index: 260;
  display: flex;
  align-items: center;
  justify-content: center;
  /* 只要模糊，不要額外疊一層灰色調——讓背景維持原本的顏色，只是模糊，不是「變暗變灰」。 */
  background: transparent;
  backdrop-filter: blur(10px);
  -webkit-backdrop-filter: blur(10px);
}

.vault-add-overlay__scene {
  position: relative;
  width: 320px;
  height: 280px;
  perspective: 1000px;
}

.vault-add-overlay__frame {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.vault-add-overlay__door {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  transform-style: preserve-3d;
  transform-origin: 21% 50%;
  transition: transform 500ms var(--ease-inout, cubic-bezier(0.77, 0, 0.175, 1));
}

.vault-add-overlay__door-img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.vault-add-overlay__wheel-slot {
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
}

.vault-add-overlay__scene.is-open .vault-add-overlay__door {
  transform: rotateY(-70deg);
}

/* 疊層本身的「materialize」進出場（apple-design〈12. Materials & depth〉：模糊材質要讓
   blur 半徑跟著漸變，不能只做 opacity 淡入淡出）。這裡的 class 名稱由 App.vue 那層
   `<Transition name="vault-overlay">` 套用，寫在這個元件自己的 scoped 樣式裡——Vue scoped
   CSS 對元件根節點（`.vault-add-overlay`）本身有效，父層套用的過場 class 一樣吃得到。
   離場（不論是自然關閉還是點外面立即取消觸發的移除）用比進場快的時長，跟 `.modal-leave-active`
   的既有慣例一致。 */
.vault-overlay-enter-active {
  transition: opacity var(--duration-base, 200ms) var(--ease-out, ease),
    backdrop-filter var(--duration-base, 200ms) var(--ease-out, ease),
    -webkit-backdrop-filter var(--duration-base, 200ms) var(--ease-out, ease);
}

.vault-overlay-leave-active {
  transition: opacity var(--duration-fast, 150ms) var(--ease-out, ease),
    backdrop-filter var(--duration-fast, 150ms) var(--ease-out, ease),
    -webkit-backdrop-filter var(--duration-fast, 150ms) var(--ease-out, ease);
}

.vault-overlay-enter-from,
.vault-overlay-leave-to {
  opacity: 0;
  backdrop-filter: blur(0px);
  -webkit-backdrop-filter: blur(0px);
}

@media (prefers-reduced-transparency: reduce) {
  .vault-add-overlay {
    backdrop-filter: none;
    -webkit-backdrop-filter: none;
    background: rgba(20, 22, 28, 0.85);
  }
}

@media (prefers-reduced-motion: reduce) {
  .vault-add-overlay__door {
    transition: opacity 200ms ease;
  }

  .vault-add-overlay__scene:not(.is-open) .vault-add-overlay__door {
    opacity: 1;
    transform: none;
  }

  .vault-add-overlay__scene.is-open .vault-add-overlay__door {
    opacity: 0;
    transform: none;
  }
}
</style>
