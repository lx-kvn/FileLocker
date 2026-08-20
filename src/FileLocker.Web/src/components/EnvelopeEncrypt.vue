<script setup>
// 信封加密流程（design-exploration/gui-styles-v2 定案文件 §1.6-1.10、技術規格 §1／§2.10）——
// 取代 App.vue 原本乾癟的表單精靈（encryptStep 1/2）。這個元件只負責「信封長什麼樣子、怎麼
// 動」，實際的檔案選取/密碼欄位資料、IPC 呼叫全部留在 App.vue（透過 props 傳進來、emit 事件
// 出去），比照 Phase 1 AppSidebar／TicketRow 的既有慣例——這個元件本身沒有 IPC、沒有檔案系統
// 概念，單獨測試不需要假造 window.chrome.webview。
//
// 時序常數跟座標數值直接照抄技術規格 §2.10 記錄的最終定案版本（不是兩份早期 mockup
// 8-envelope-assembled.html／12-file-tab-merged.html 裡已經被技術規格記錄推翻的做法）。
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import { bumpGen, isCurrentGen } from '../composables/useAnimGen.js'
import envelopeBodyEmptyUrl from '../assets/Envelope_Body_Empty.svg'
import envelopeBodyOneUrl from '../assets/Envelope_Body_One.svg'
import envelopeBodyTwoUrl from '../assets/Envelope_Body_Two.svg'
import envelopeBodyUrl from '../assets/Envelope_Body.svg'
import envelopeFlapUrl from '../assets/Envelope_Flap.svg'
import waxDripBackUrl from '../assets/Wax_Drip_Back.svg'
import envelopeWaxSealUrl from '../assets/Envelope_Wax_Seal.svg'
import postmarkNestedLockUrl from '../assets/Postmark_Nested_Lock.svg'

const props = defineProps({
  t: { type: Function, required: true },
  paths: { type: Array, required: true },
  password: { type: String, default: '' },
  passwordConfirm: { type: String, default: '' },
  hint: { type: String, default: '' },
  enablePasskey: { type: Boolean, default: false },
  enableRecoveryKey: { type: Boolean, default: false },
  disablePasskeyRecoveryKey: { type: Boolean, default: false },
  passkeyIconUrl: { type: String, default: '' },
  recoveryKeyIconUrl: { type: String, default: '' },
  // 'form'：使用者還在選檔案/填密碼；'processing'：encryptPending 進行中；
  // 'confirming'：pending 已完成，等使用者按確認/取消；'committing'：commitEncrypt 進行中；
  // 'flying'：commit 成功，播寄出動畫。App.vue 依 IPC 回應狀態決定目前是哪一階段。
  phase: { type: String, default: 'form' },
  progressPercent: { type: Number, default: 0 },
  pendingSummary: { type: String, default: null },
})

const emit = defineEmits([
  'pick-file', 'pick-folder', 'remove-path', 'clear-paths', 'drop',
  'update:password', 'update:passwordConfirm', 'update:hint',
  'update:enablePasskey', 'update:enableRecoveryKey',
  'submit', 'confirm', 'cancel', 'fly-away-complete',
])

// ---- 時序常數（技術規格 §2.10／§2.11，跟 mockup 完全一致的數值） ----
const DROP_MS = 820
const FLAP_MS = 420
const SETTLE_HOLD_MS = 500
const SHEET_PHASE_MS = 280

const isDropping = ref(true)
const isOpen = ref(false)
const sheetPage = ref('picker') // 'picker' | 'password'
const sheetVisible = ref(false)
const sheetTransitionState = ref('hidden') // 'hidden' | 'fade-out' | 'fade-in' | 'settled'

const timers = []
function after(ms, fn) {
  const id = setTimeout(fn, ms)
  timers.push(id)
  return id
}
function clearTimers() {
  timers.forEach(clearTimeout)
  timers.length = 0
}

// 世代編號：這個元件實例整體共用一個 key（不需要像技術規格那樣分三層 DOM 元素各自的
// generation，因為這裡沒有原本那套「開合三層各自獨立 class 切換」的 DOM 結構問題——Vue 的
// class binding 是宣告式的，狀態本身就是唯一事實來源，不會有「舊 class 沒清乾淨」的殘留，
// 需要世代編號防護的只有 setTimeout 鏈本身排定的「之後要做什麼」）。
const animKey = {}

function playEntrance() {
  const gen = bumpGen(animKey)
  isDropping.value = true
  isOpen.value = false
  after(DROP_MS + 20, () => {
    if (!isCurrentGen(animKey, gen)) return
    isDropping.value = false
    isOpen.value = true
    after(FLAP_MS + SETTLE_HOLD_MS, () => {
      if (!isCurrentGen(animKey, gen)) return
      sheetPage.value = 'picker'
      sheetVisible.value = true
      sheetTransitionState.value = 'settled'
    })
  })
}

onMounted(playEntrance)
onUnmounted(() => {
  bumpGen(animKey) // 蓋掉任何還沒執行的 callback
  clearTimers()
})

function goToPasswordPage() {
  if (props.paths.length === 0) return
  const gen = bumpGen(animKey)
  sheetTransitionState.value = 'fade-out'
  after(200, () => {
    if (!isCurrentGen(animKey, gen)) return
    sheetPage.value = 'password'
    sheetTransitionState.value = 'fade-in'
    after(SHEET_PHASE_MS, () => {
      if (!isCurrentGen(animKey, gen)) return
      sheetTransitionState.value = 'settled'
    })
  })
}

function goBackToPicker() {
  const gen = bumpGen(animKey)
  sheetTransitionState.value = 'fade-out'
  after(200, () => {
    if (!isCurrentGen(animKey, gen)) return
    sheetPage.value = 'picker'
    sheetTransitionState.value = 'fade-in'
    after(SHEET_PHASE_MS, () => {
      if (!isCurrentGen(animKey, gen)) return
      sheetTransitionState.value = 'settled'
    })
  })
}

// ---- 「這段任務真的結束了」的退場：技術規格 §2.10——這裡才用兩段式抽出/收回
// （sheet--reveal 完全滑出→sheet--retreat 收回並淡出），不是原地交叉淡化那種「翻頁」感，
// 兩者要明確區隔（回饋：sheet 的出場沒有套用抽出的動畫，原本誤用了跟頁面切換一樣的
// crossfade，這裡補上真正的抽出/收回）。退場播完才闔上信封、蓋章顯示檔名/郵戳/時間——
// 對應定案文件 §1.8「闔上信封、蓋章──闔上動畫播完才淡入檔名/郵戳/時間戳記」。
const showMailInfo = ref(false)
const mailTimestampText = ref('')

function playFinalExitAndSeal() {
  const gen = bumpGen(animKey)
  sheetTransitionState.value = 'reveal'
  after(SHEET_PHASE_MS, () => {
    if (!isCurrentGen(animKey, gen)) return
    sheetTransitionState.value = 'retreat'
    after(SHEET_PHASE_MS, () => {
      if (!isCurrentGen(animKey, gen)) return
      sheetVisible.value = false
      sheetTransitionState.value = 'hidden'
      isOpen.value = false
      mailTimestampText.value = new Date().toLocaleString()
      after(FLAP_MS + 40, () => {
        if (!isCurrentGen(animKey, gen)) return
        showMailInfo.value = true
      })
    })
  })
}

// 使用者從「確認/取消」畫面按了取消：闔著蓋章的信封要重新打開，回到密碼頁（定案文件
// §1.8：取消後密碼欄位/勾選狀態保留，不清空，所以直接回密碼頁而不是選檔頁）。
function playReopenAfterCancel() {
  const gen = bumpGen(animKey)
  showMailInfo.value = false
  isOpen.value = true
  after(FLAP_MS, () => {
    if (!isCurrentGen(animKey, gen)) return
    sheetPage.value = 'password'
    sheetVisible.value = true
    sheetTransitionState.value = 'settled'
  })
}

watch(() => props.phase, (newPhase, oldPhase) => {
  if (newPhase === 'confirming' && oldPhase === 'processing') {
    playFinalExitAndSeal()
  } else if (newPhase === 'form' && (oldPhase === 'confirming' || oldPhase === 'committing')) {
    playReopenAfterCancel()
  }
})

// ---- 拖曳懸停預覽張數（定案文件 §1.6「拖曳檔案懸停在信封上時的回饋」）：懸停中先讓使用者
// 看到「放開後會怎樣」的誠實預覽（信封本體圖示切成對應張數、提示文字隱藏、外圍浮起陰影），
// 不是統一先顯示 1 張再等放開修正。用 dataTransfer.items（不是 .files——.files 只有在 drop
// 那一刻才有內容，dragenter/dragover 階段讀不到）在懸停中就能拿到正確的項目數。 ----
const isDragHovering = ref(false)
const dragHoverCount = ref(0)

function countDraggedFiles(dataTransfer) {
  if (!dataTransfer?.items) return 0
  let count = 0
  for (const item of dataTransfer.items) {
    if (item.kind === 'file') count++
  }
  return count
}

function onDragEnter(event) {
  isDragHovering.value = true
  dragHoverCount.value = countDraggedFiles(event.dataTransfer)
}

function onDragOver(event) {
  // dragover 會持續觸發，每次都重新讀一次張數——理論上懸停中途項目數不會變，
  // 但重新讀取的成本很低，這樣寫比另外判斷「要不要更新」簡單、也不會有漏更新的風險。
  isDragHovering.value = true
  dragHoverCount.value = countDraggedFiles(event.dataTransfer)
}

function onDragLeave() {
  isDragHovering.value = false
  dragHoverCount.value = 0
}

function onDrop(event) {
  isDragHovering.value = false
  dragHoverCount.value = 0
  emit('drop', event)
}

const bodyImageUrl = computed(() => {
  // 懸停中：用拖曳項目的即時數量預覽；沒在懸停：用已經選定的檔案數量。
  const count = isDragHovering.value ? dragHoverCount.value : props.paths.length
  if (count <= 0) return envelopeBodyEmptyUrl
  if (count === 1) return envelopeBodyOneUrl
  if (count === 2) return envelopeBodyTwoUrl
  return envelopeBodyUrl
})

const progressScale = computed(() => Math.max(0, Math.min(100, props.progressPercent)) / 100)

const nextDisabled = computed(() => props.paths.length === 0)
const submitDisabled = computed(() => props.phase === 'processing' || !props.password || props.password !== props.passwordConfirm)
</script>

<template>
  <div
    class="envelope-outer"
    :class="{ 'is-open': isOpen, 'is-closed': !isOpen, 'is-flying': phase === 'flying', 'show-mail-info': showMailInfo }"
  >
    <div class="mailaway-rig" :class="{ 'is-dropping': isDropping, 'is-flying': phase === 'flying' }" @transitionend="phase === 'flying' && emit('fly-away-complete')">
      <div
        class="envelope-canvas"
        :class="{ 'is-drag-hovering': isDragHovering }"
        @dragenter.prevent="onDragEnter"
        @dragover.prevent="onDragOver"
        @dragleave.prevent="onDragLeave"
        @drop.prevent="onDrop"
      >
        <img class="envelope-canvas__body" :src="bodyImageUrl" alt="" />
        <div class="flap-group">
          <div class="wax-drip-back"><img :src="waxDripBackUrl" alt="" /></div>
          <img class="flap-group__flap" :src="envelopeFlapUrl" alt="" />
          <div class="wax-seal"><img :src="envelopeWaxSealUrl" alt="" /></div>
        </div>
        <p class="dropzone-hint" :class="{ 'is-hidden': isDragHovering }">{{ t('encrypt.dropHint') }}</p>
        <!-- 闔上蓋章後的檔名／郵戳／加密時間（定案文件 §1.8、mockup 13-sidebar-ticket-shell）：
             左邊檔名標籤、右邊郵戳圖示+時間戳記，疊在信封本體上，不是另外浮出一張卡片——
             confirming／committing／flying 這三個階段信封本身都是闔著的，蓋章內容全程留著。 -->
        <div class="mail-filename" :title="pendingSummary">{{ pendingSummary }}</div>
        <div class="mail-postmark">
          <img :src="postmarkNestedLockUrl" alt="" />
          <span class="mail-timestamp">{{ mailTimestampText }}</span>
        </div>
      </div>
    </div>

    <!-- 確認/取消：pending 完成，等使用者確認才真的 finalize。不是浮動卡片，是疊在信封蓋章
         畫面下方的一排按鈕，維持「信封本身就是內容」的觀感，不是內容被另一張卡片蓋住。 -->
    <div class="mail-confirm-actions" :class="{ 'is-hidden': phase !== 'confirming' && phase !== 'committing' }">
      <button class="button button--secondary" type="button" data-action="cancel" :disabled="phase === 'committing'" @click="emit('cancel')">{{ t('encrypt.envelopeCancel') }}</button>
      <button class="button button--primary" type="button" data-action="confirm" :disabled="phase === 'committing'" @click="emit('confirm')">{{ t('encrypt.envelopeConfirm') }}</button>
    </div>

    <!-- 頁一：選檔案 -->
    <div
      class="sheet sheet--picker"
      :class="{
        'sheet--hidden': !sheetVisible || sheetPage !== 'picker',
        'sheet--fade-out': sheetVisible && sheetPage === 'picker' && sheetTransitionState === 'fade-out',
        'sheet--fade-in': sheetVisible && sheetPage === 'picker' && sheetTransitionState === 'fade-in',
      }"
    >
      <div v-if="paths.length === 0" class="sheet__empty-state">
        <button class="button button--primary" type="button" data-action="pick-file" @click="emit('pick-file')">{{ t('encrypt.pickFiles') }}</button>
        <button class="button button--secondary" type="button" data-action="pick-folder" @click="emit('pick-folder')">{{ t('encrypt.pickFolder') }}</button>
      </div>
      <div v-else class="picked-list-frame">
        <ul class="picked-list">
          <li v-for="(path, index) in paths" :key="path">
            <span :title="path">{{ path }}</span>
            <button type="button" @click="emit('remove-path', index)">{{ t('encrypt.remove') }}</button>
          </li>
        </ul>
        <button class="link-more" type="button" @click="emit('pick-file')">{{ t('encrypt.pickFiles') }}</button>
      </div>
      <div class="add-file-actions">
        <button class="button button--secondary" type="button" data-action="cancel" @click="emit('cancel')">{{ t('encrypt.envelopeCancel') }}</button>
        <button class="button button--primary" type="button" data-action="next" :disabled="nextDisabled" @click="goToPasswordPage">{{ t('encrypt.next') }}</button>
      </div>
    </div>

    <!-- 頁二：密碼／Passkey／恢復金鑰 -->
    <div
      class="sheet sheet--password"
      :class="{
        'sheet--hidden': !sheetVisible || sheetPage !== 'password',
        'sheet--fade-out': sheetVisible && sheetPage === 'password' && sheetTransitionState === 'fade-out',
        'sheet--fade-in': sheetVisible && sheetPage === 'password' && sheetTransitionState === 'fade-in',
        'sheet--reveal': sheetVisible && sheetPage === 'password' && sheetTransitionState === 'reveal',
        'sheet--retreat': sheetVisible && sheetPage === 'password' && sheetTransitionState === 'retreat',
      }"
    >
      <div class="step2-form">
        <input
          data-field="password"
          type="password"
          :value="password"
          :placeholder="t('encrypt.passwordLabel')"
          @input="emit('update:password', $event.target.value)"
        />
        <input
          data-field="passwordConfirm"
          type="password"
          :value="passwordConfirm"
          :placeholder="t('encrypt.passwordConfirmLabel')"
          @input="emit('update:passwordConfirm', $event.target.value)"
        />
        <input
          data-field="hint"
          type="text"
          :value="hint"
          :placeholder="t('encrypt.hintLabel')"
          @input="emit('update:hint', $event.target.value)"
        />
        <label :class="{ 'is-disabled': disablePasskeyRecoveryKey }">
          <input type="checkbox" :checked="enablePasskey" :disabled="disablePasskeyRecoveryKey" @change="emit('update:enablePasskey', $event.target.checked)" />
          <img v-if="passkeyIconUrl" :src="passkeyIconUrl" alt="" />
          {{ t('encrypt.passkeyLabel') }}
        </label>
        <label :class="{ 'is-disabled': disablePasskeyRecoveryKey }">
          <input type="checkbox" :checked="enableRecoveryKey" :disabled="disablePasskeyRecoveryKey" @change="emit('update:enableRecoveryKey', $event.target.checked)" />
          <img v-if="recoveryKeyIconUrl" :src="recoveryKeyIconUrl" alt="" />
          {{ t('encrypt.recoveryKeyLabel') }}
        </label>
      </div>

      <div v-if="phase === 'processing'" class="progress-bar" role="progressbar" :aria-valuenow="Math.round(progressPercent)">
        <div class="progress-bar__fill" :style="{ transform: `scaleX(${progressScale})` }"></div>
      </div>

      <div class="step2-actions">
        <button class="button button--secondary" type="button" :disabled="phase === 'processing'" @click="goBackToPicker">{{ t('encrypt.back') }}</button>
        <button class="button button--primary" type="button" data-action="submit" :disabled="submitDisabled" @click="emit('submit')">
          {{ phase === 'processing' ? `${t('encrypt.submit')}... ${Math.round(progressPercent)}%` : t('encrypt.submit') }}
        </button>
      </div>
    </div>

    <!-- 確認/取消：pending 完成，等使用者確認才真的 finalize -->
  </div>
</template>

<style scoped>
.envelope-outer {
  position: relative;
  width: 420px;
  margin: 0 auto;
  overflow: visible;
  perspective: 1600px;
}

.envelope-outer.is-flying {
  perspective: 3200px;
}

.mailaway-rig {
  position: relative;
  z-index: 1;
  width: 420px;
  transform-origin: 50% 50%;
  transform-style: preserve-3d;
  will-change: transform, filter, opacity;
}

@keyframes envelope-drop-bounce {
  0% { transform: translateY(-420px); }
  55% { transform: translateY(0); }
  72% { transform: translateY(-16px); }
  86% { transform: translateY(5px); }
  100% { transform: translateY(0); }
}

.mailaway-rig.is-dropping {
  animation: envelope-drop-bounce 820ms cubic-bezier(0.34, 1.28, 0.64, 1) both;
}

.mailaway-rig.is-flying {
  transition:
    rotate 260ms var(--ease-out),
    translate 500ms var(--ease-inout, cubic-bezier(0.77, 0, 0.175, 1)) 220ms,
    opacity 460ms linear 240ms;
  rotate: x 35deg;
  translate: 0 -130px -260px;
  opacity: 0;
}

.envelope-canvas {
  position: relative;
  width: 420px;
  height: 420px;
  transform-style: preserve-3d;
  transition: filter 160ms ease;
}

/* 定案文件 §1.6：拖曳懸停時信封外圍加一圈偏下方的陰影，只給 Y 軸正值位移，讓信封看起來
   準備接收、微微浮起——不是隨機加陰影，X 軸刻意維持 0，方向感要對得上「準備接住」的意涵。 */
.envelope-canvas.is-drag-hovering {
  filter: drop-shadow(0 22px 30px rgba(34, 34, 30, 0.4));
}

.envelope-canvas__body {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.flap-group {
  position: absolute;
  left: 0;
  top: -17.927%;
  width: 100%;
  height: 100%;
  transform-style: preserve-3d;
  transform-origin: 50% 50%;
  transition: transform 420ms var(--ease-inout, cubic-bezier(0.77, 0, 0.175, 1));
  will-change: transform;
}

.envelope-outer.is-open .flap-group { transform: rotateX(0deg); }
.envelope-outer.is-closed .flap-group { transform: rotateX(-180deg); }

.flap-group__flap {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.wax-drip-back {
  position: absolute;
  left: 50%;
  top: 27.53%;
  width: 44px;
  height: 44px;
  transform: translate(-50%, -50%);
  transition: opacity 30ms linear 195ms;
  pointer-events: none;
}
.envelope-outer.is-open .wax-drip-back { opacity: 1; }
.envelope-outer.is-closed .wax-drip-back { opacity: 0; }
.wax-drip-back img { width: 100%; height: 100%; }

.wax-seal {
  position: absolute;
  left: 50%;
  top: 29.5%;
  width: 42px;
  height: 42px;
  transform: translate(-50%, -50%) rotateX(180deg);
  transition: opacity 30ms linear 195ms;
  pointer-events: none;
}
.envelope-outer.is-closed .wax-seal { opacity: 1; }
.envelope-outer.is-open .wax-seal { opacity: 0; }
.wax-seal img { width: 100%; height: 100%; }

.dropzone-hint {
  position: absolute;
  left: 50%;
  top: 43%;
  transform: translate(-50%, -50%);
  margin: 0;
  font-size: 11.5px;
  color: var(--color-text-secondary);
  white-space: nowrap;
  opacity: 1;
  transition: opacity 160ms ease 420ms;
}
.envelope-outer.is-closed .dropzone-hint { opacity: 0; transition: opacity 100ms ease; }
/* 懸停拖放中這句提示是多餘的（使用者已經在拖了，不需要再提醒「拖到這裡」）——拿掉，
   讓視線集中在正在切換的信封本體圖示跟浮起陰影上。回饋：懸停這一刻文字要立刻消失，
   不能還播一段淡出動畫（懸停中的畫面切換講求即時，跟開合信封那種有敘事節奏的動畫不同），
   所以這裡明確蓋掉繼承來的 transition，不是單純調快時長。 */
.dropzone-hint.is-hidden {
  opacity: 0;
  transition: none;
}

.sheet {
  position: absolute;
  left: 50%;
  top: 245px;
  z-index: 2;
  width: 268px;
  box-sizing: border-box;
  background: var(--color-surface);
  border-radius: 9px;
  box-shadow: 0 4px 10px rgba(34, 34, 30, 0.16);
  padding: 12px 14px 13px;
  transform: translate(-50%, 0) scale(1);
  opacity: 1;
  transition: opacity 200ms ease, transform 200ms ease;
}

.sheet--hidden {
  opacity: 0;
  pointer-events: none;
  transition: none;
}

.sheet--fade-out {
  opacity: 0;
  transform: translate(-50%, 0) scale(0.88);
  transition: opacity 200ms ease, transform 200ms ease;
}

.sheet--fade-in {
  opacity: 1;
  transform: translate(-50%, 0) scale(1);
  transition: opacity 260ms cubic-bezier(0.23, 1, 0.32, 1), transform 260ms cubic-bezier(0.23, 1, 0.32, 1);
}

/* 兩段式抽出/收回——只在「這段任務真的結束了」時播（技術規格 §2.10），跟上面
   fade-out/fade-in 那組「翻頁」語意分開：抽出先完全滑出露出全貌，收回再滑回疊上信封範圍
   並淡出，不是單純縮放淡化。 */
.sheet--reveal {
  opacity: 1;
  transform: translate(-50%, 200px) scale(1);
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1), opacity 160ms ease;
}

.sheet--retreat {
  opacity: 0;
  transform: translate(-50%, 0) scale(1);
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1), opacity 200ms ease;
}

.picked-list-frame {
  border: 1px solid var(--color-border);
  border-radius: 8px;
  padding: 6px;
  margin-bottom: 8px;
  background: var(--color-bg);
}

.picked-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: 112px;
  overflow-y: auto;
}

.picked-list li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 6px;
  padding: 6px 10px;
  font-size: 12px;
  color: var(--color-text);
}

.picked-list li span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.picked-list li button {
  border: none;
  background: none;
  color: var(--color-danger);
  font: inherit;
  font-size: 11px;
  cursor: pointer;
  flex-shrink: 0;
}

.link-more {
  border: none;
  background: none;
  color: var(--color-accent);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
  padding: 4px 0;
}

.sheet__empty-state {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.add-file-actions,
.step2-actions {
  display: flex;
  justify-content: center;
  gap: 8px;
  padding-top: 12px;
  margin-top: 8px;
  border-top: 1px solid var(--color-border);
}

.step2-form {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.step2-form input[type="password"],
.step2-form input[type="text"] {
  width: 100%;
  box-sizing: border-box;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  padding: 8px 10px;
  font: inherit;
  font-size: 13px;
  background: var(--color-surface);
  color: var(--color-text);
}

.step2-form input:focus {
  outline: none;
  border-color: #DCC289;
  box-shadow: 0 0 0 3px rgba(220, 194, 137, 0.35);
}

.step2-form label {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12.5px;
  color: var(--color-text);
}

.step2-form label.is-disabled {
  opacity: 0.5;
}

.step2-form label img {
  width: 16px;
  height: 16px;
}

.progress-bar {
  height: 4px;
  background: var(--color-border);
  border-radius: 2px;
  overflow: hidden;
  margin-top: 10px;
}

.progress-bar__fill {
  height: 100%;
  width: 100%;
  background: var(--color-accent);
  transform-origin: left;
  transition: transform 150ms linear;
}

/* 定案文件 §1.8／mockup 13-sidebar-ticket-shell：檔名／郵戳／時間戳記闔上蓋章之後才淡入，
   數值（right%／top%／字級）直接沿用 mockup 已經針對 420px 畫布定案的座標，不用另外量測。 */
.mail-filename,
.mail-postmark {
  position: absolute;
  top: 58%;
  transform: translateY(-50%);
  opacity: 0;
  transition: opacity 260ms var(--ease-out);
  pointer-events: none;
}
.envelope-outer.show-mail-info .mail-filename,
.envelope-outer.show-mail-info .mail-postmark {
  opacity: 1;
}

.mail-filename {
  right: 63%;
  background: #FFFAF0;
  border: 1px solid var(--color-accent);
  border-radius: 4px;
  padding: 2px 7px 3.5px;
  font-size: 10px;
  font-weight: 600;
  color: #6B5527;
  white-space: nowrap;
  max-width: 26%;
  overflow: hidden;
  text-overflow: ellipsis;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.06);
}

.mail-postmark {
  right: 19%;
  width: 64px;
  height: 64px;
}

.mail-postmark img {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  object-fit: contain;
}

.mail-postmark .mail-timestamp {
  position: absolute;
  left: 68%;
  top: 59%;
  font-size: 6px;
  font-weight: 700;
  letter-spacing: -0.3px;
  color: #8A6A1F;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

/* 確認/取消：不是浮動卡片，是一排疊在信封蓋章畫面下方的按鈕——跟 .sheet 用同一個水平
   置中邏輯，但不套用卡片背景/陰影，維持「信封本身就是內容」的觀感。 */
.mail-confirm-actions {
  position: absolute;
  left: 50%;
  top: 340px;
  /* .mailaway-rig 自己是 z-index:1，這裡沒有明確蓋過去的話，按鈕會被信封圖層擋住點不到
     （已經真的踩過這個坑——截圖驗證時按鈕點擊被信封本體圖片攔截）。 */
  z-index: 3;
  transform: translateX(-50%);
  display: flex;
  gap: 8px;
  opacity: 1;
  transition: opacity 200ms ease;
}

.mail-confirm-actions.is-hidden {
  opacity: 0;
  pointer-events: none;
}
</style>
