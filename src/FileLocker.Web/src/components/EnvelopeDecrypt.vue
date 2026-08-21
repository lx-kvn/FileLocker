<script setup>
// 獨立解密流程（信封＋Sheet，design-exploration/gui-styles-v2 定案文件 §1.11、
// 13-sidebar-ticket-shell.html 830-893 行 HTML／1366-1525 行 JS）——取代 App.vue 原本
// 「解密」分頁的乾癟表單（路徑輸入框＋密碼欄）。跟 EnvelopeEncrypt.vue 同樣的分工：這個
// 元件只管信封長什麼樣、怎麼動，IPC 呼叫／驗證結果全部留在 App.vue（透過 props 傳進來、
// emit 事件出去），單獨測試不需要假造 window.chrome.webview。
//
// 跟加密流程的信封最大的不同：這個信封落下回彈後停在闔著的狀態，不會自動打開封口——
// 檔名／郵戳／時間戳記從一開始就常駐顯示（不用等蓋章動畫），因為這份東西「本來就已經是
// 加密好的」，不是「剛加密完才蓋章」。驗證成功之後才會打開封口、抽出選存檔位置的 sheet。
import { ref, computed, watch, onMounted, onUnmounted, nextTick } from 'vue'
import { bumpGen, isCurrentGen } from '../composables/useAnimGen.js'
import envelopeBodyUrl from '../assets/Envelope_Body.svg'
import envelopeFlapUrl from '../assets/Envelope_Flap.svg'
import waxDripBackUrl from '../assets/Wax_Drip_Back.svg'
import envelopeWaxSealUrl from '../assets/Envelope_Wax_Seal.svg'
import postmarkNestedLockUrl from '../assets/Postmark_Nested_Lock.svg'

const props = defineProps({
  t: { type: Function, required: true },
  originalName: { type: String, default: '' },
  createdAtUtc: { type: String, default: null },
  passkeyEnabled: { type: Boolean, default: false },
  recoveryKeyEnabled: { type: Boolean, default: false },
  passkeyIconUrl: { type: String, default: '' },
  recoveryKeyIconUrl: { type: String, default: '' },
  // { status: 'idle' | 'verifying' | 'success' | 'failed', message? }——App.vue 依 IPC 回應
  // 更新，元件自己在送出當下已經先本地切到 'verifying' 給即時回饋（apple-design「Respond on
  // pointer-down, not on release」），這個 prop 主要是拿來接收非同步驗證結果。
  verifyState: { type: Object, default: () => ({ status: 'idle' }) },
  // { status: 'idle' | 'restoring' | 'success' | 'failed', restoredPath? }
  commitState: { type: Object, default: () => ({ status: 'idle' }) },
})

const emit = defineEmits([
  'submit-password', 'verify-passkey', 'submit-recovery-key',
  'pick-destination', 'cancel', 'done',
])

// ---- 時序常數：跟 EnvelopeEncrypt.vue 完全一致的數值（技術規格 §2.10／§2.11） ----
const DROP_MS = 820
const SETTLE_HOLD_MS = 500
const SHEET_PHASE_MS = 280

const isDropping = ref(true)
// 這個流程的信封落地後永遠停在闔著，只有驗證成功才會打開——不像加密流程有 isOpen 一路
// 隨落地/收尾動畫變化，這裡拆成獨立的 ref 更直白：envelopeOpen 只在「驗證成功→選存檔位置」
// 這段才會是 true。
const envelopeOpen = ref(false)

const sheetPage = ref('verify') // 'verify' | 'destination'
const sheetVisible = ref(false)
const sheetTransitionState = ref('hidden') // 跟 EnvelopeEncrypt.vue 同一套命名，直接照抄

const verifySheetEl = ref(null)
const destinationSheetEl = ref(null)
function sheetElFor(page) {
  return page === 'verify' ? verifySheetEl.value : destinationSheetEl.value
}
function sheetClass(page) {
  const isActive = sheetPage.value === page && sheetVisible.value
  const state = sheetTransitionState.value
  return {
    'sheet--hidden': !isActive || state === 'hidden',
    'sheet--reveal': isActive && state === 'reveal',
    'sheet--settle': isActive && state === 'settle',
    'sheet--retreat': isActive && state === 'retreat',
    'sheet--fade-out': isActive && state === 'fade-out',
    'sheet--morph-start': isActive && state === 'morph-start',
    'sheet--fade-in': isActive && state === 'fade-in',
  }
}

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
// 強制 reflow，理由跟 EnvelopeEncrypt.vue 的 forceReflow 完全一樣：class 切換要先讓瀏覽器
// 真的畫過一次「起點」，下一次切換才會被認出是新的過場目標、真的播 transition。
async function forceReflow(page) {
  await nextTick()
  const el = sheetElFor(page)
  if (el) void el.offsetWidth
}

const animKey = {}

async function playSheetTwoPhaseEntrance(page) {
  const gen = bumpGen(animKey)
  sheetPage.value = page
  sheetTransitionState.value = 'hidden'
  sheetVisible.value = true
  await forceReflow(page)
  if (!isCurrentGen(animKey, gen)) return
  sheetTransitionState.value = 'reveal'
  after(SHEET_PHASE_MS, () => {
    if (!isCurrentGen(animKey, gen)) return
    sheetTransitionState.value = 'settle'
  })
}

// 驗證 sheet → 選存檔位置 sheet：回饋（走查後修正）改成跟 EnvelopeEncrypt.vue 的
// playSheetCrossfade 同一套「原地縮放淡化」（不是抽出/收回）——這兩張卡是同一段連貫任務
// 的前後兩步（驗證完、接著選地方存），不是「這個東西被收起來、換了一件不相干的事」，用
// 抽出/收回那套語言反而讓人以為又要重新跑一次進場儀式。MORPH_OUT_MS/MORPH_IN_MS 數值跟
// EnvelopeEncrypt.vue 完全一致，不要自己發明新數字。
const MORPH_OUT_MS = 200
const MORPH_IN_MS = 260

async function playSheetCrossfade(fromPage, toPage) {
  const gen = bumpGen(animKey)
  sheetTransitionState.value = 'fade-out'
  await new Promise((resolve) => after(MORPH_OUT_MS, resolve))
  if (!isCurrentGen(animKey, gen)) return
  sheetPage.value = toPage
  sheetTransitionState.value = 'morph-start'
  await forceReflow(toPage)
  if (!isCurrentGen(animKey, gen)) return
  sheetTransitionState.value = 'fade-in'
  after(MORPH_IN_MS, () => {
    if (!isCurrentGen(animKey, gen)) return
    sheetTransitionState.value = 'settle'
  })
}

// 落下回彈，但停在闔著（不像加密流程接著打開封口）——對應定案文件 §1.11「回彈後停在關閉態」。
function playEntrance() {
  const gen = bumpGen(animKey)
  isDropping.value = true
  after(DROP_MS + 20, () => {
    if (!isCurrentGen(animKey, gen)) return
    isDropping.value = false
    after(SETTLE_HOLD_MS, () => {
      if (!isCurrentGen(animKey, gen)) return
      playSheetTwoPhaseEntrance('verify')
      if (props.passkeyEnabled) {
        startPasskeyVerify()
      }
    })
  })
}

onMounted(playEntrance)
onUnmounted(() => {
  bumpGen(animKey)
  clearTimers()
})

// ---- 密碼／Passkey／恢復金鑰輸入頁 ----
const passwordInput = ref('')
const recoveryKeyInput = ref('')
const recoveryKeyPageActive = ref(false) // 同一張卡片內部左右滑動翻頁，不是抽出新卡片
const verifying = ref(false)
const verifySucceeded = ref(false)
const passkeyHint = ref('')

function submitPassword() {
  if (!passwordInput.value.trim() || verifying.value) return
  verifying.value = true
  passkeyHint.value = ''
  emit('submit-password', passwordInput.value)
}

function startPasskeyVerify() {
  if (verifying.value) return
  verifying.value = true
  passkeyHint.value = ''
  emit('verify-passkey')
}

function goToRecoveryKeyPage() {
  recoveryKeyPageActive.value = true
}
function backToPasswordPage() {
  recoveryKeyPageActive.value = false
}
function submitRecoveryKey() {
  if (!recoveryKeyInput.value.trim() || verifying.value) return
  verifying.value = true
  passkeyHint.value = ''
  emit('submit-recovery-key', recoveryKeyInput.value)
}

// 驗證成功後：打勾停 SETTLE_HOLD_MS → 信封打開 → sheet 原地縮放淡化切到選存檔位置頁
// （見 playSheetCrossfade 的說明）。信封打開跟 sheet 切頁同時播——蠟封／封口在畫布上緣
// （top 27-30%），sheet 卡片本身貼在畫布下半部（top:245px），視覺上不會互相遮擋，兩個
// 動作同時發生沒有問題。
async function playVerifiedThenOpenEnvelope() {
  const gen = bumpGen(animKey)
  await new Promise((resolve) => after(SETTLE_HOLD_MS, resolve))
  if (!isCurrentGen(animKey, gen)) return
  verifySucceeded.value = false
  envelopeOpen.value = true
  playSheetCrossfade('verify', 'destination')
}

watch(() => props.verifyState, (state) => {
  if (!state) return
  if (state.status === 'success') {
    verifying.value = false
    verifySucceeded.value = true
    playVerifiedThenOpenEnvelope()
  } else if (state.status === 'failed') {
    verifying.value = false
    verifySucceeded.value = false
    // 只有 Passkey 自動觸發失敗才在 sheet 上顯示提示文字（不自動重試、不跳錯誤 toast，
    // 使用者可以自己選重試或改用其他方式）；密碼／恢復金鑰輸入錯誤走 App.vue 既有的
    // toast 錯誤機制，不在這裡重複顯示。
    if (state.message) {
      passkeyHint.value = state.message
    }
  }
}, { deep: true })

// ---- 選存檔位置 sheet ----
const destinationDone = ref(false)

function pickDestination() {
  emit('pick-destination')
}

watch(() => props.commitState, (state) => {
  if (!state) return
  if (state.status === 'success') {
    destinationDone.value = true
    after(SETTLE_HOLD_MS + 400, () => emit('done'))
  }
}, { deep: true })

function cancelAtDestination() {
  emit('cancel')
}

const createdAtDisplay = computed(() => {
  if (!props.createdAtUtc) return ''
  const date = new Date(props.createdAtUtc)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleString()
})
</script>

<template>
  <div class="envelope-outer decrypt-envelope" :class="{ 'is-open': envelopeOpen, 'is-closed': !envelopeOpen }">
    <div class="mailaway-rig" :class="{ 'is-dropping': isDropping }">
      <div class="envelope-canvas">
        <img class="envelope-canvas__body" :src="envelopeBodyUrl" alt="" />
        <div class="flap-group">
          <div class="wax-drip-back"><img :src="waxDripBackUrl" alt="" /></div>
          <img class="flap-group__flap" :src="envelopeFlapUrl" alt="" />
          <div class="wax-seal"><img :src="envelopeWaxSealUrl" alt="" /></div>
        </div>
        <div class="mail-filename" :title="originalName">{{ originalName }}</div>
        <div class="mail-postmark">
          <img :src="postmarkNestedLockUrl" alt="" />
          <span class="mail-timestamp">{{ createdAtDisplay }}</span>
        </div>
      </div>
    </div>

    <!-- 驗證 sheet：page1 密碼／Passkey／恢復金鑰入口，page2 恢復金鑰輸入
         （同一張卡片內部左右滑動翻頁，不是抽出新的一張，見定案文件 §1.11） -->
    <div ref="verifySheetEl" class="sheet decrypt-sheet" :class="[sheetClass('verify'), { 'decrypt-sheet--page2': recoveryKeyPageActive }]">
      <div v-if="!verifying && !verifySucceeded" class="decrypt-sheet__pages">
        <div class="decrypt-sheet__page">
          <input
            v-model="passwordInput"
            type="password"
            :placeholder="t('decrypt.enterPassword')"
            @keydown.enter="submitPassword"
          />
          <button class="button button--primary decrypt-sheet__submit" type="button" @click="submitPassword">{{ t('decrypt.unlock') }}</button>
          <button v-if="passkeyEnabled" class="decrypt-sheet__alt-btn" type="button" @click="startPasskeyVerify">
            <img v-if="passkeyIconUrl" :src="passkeyIconUrl" alt="" />
            {{ t('decrypt.usePasskey') }}
          </button>
          <button v-if="recoveryKeyEnabled" class="decrypt-sheet__alt-btn" type="button" @click="goToRecoveryKeyPage">
            <img v-if="recoveryKeyIconUrl" :src="recoveryKeyIconUrl" alt="" />
            {{ t('decrypt.useRecoveryKey') }}
          </button>
          <p v-if="passkeyHint" class="decrypt-sheet__hint">{{ passkeyHint }}</p>
        </div>
        <!-- 回饋：這一頁沒開恢復金鑰時也一直待在 DOM 裡（只是被 translateX 移出可視範圍），
             flex row 預設 align-items:stretch 會讓沒開恢復金鑰的 page1 被這頁的高度拉伸，
             sheet 下緣因此多出一大塊空白——沒開恢復金鑰的話這頁本來就用不到，直接 v-if 拿掉，
             不讓它參與高度計算。 -->
        <div v-if="recoveryKeyEnabled" class="decrypt-sheet__page">
          <input
            v-model="recoveryKeyInput"
            type="text"
            :placeholder="t('decrypt.enterRecoveryKey')"
            @keydown.enter="submitRecoveryKey"
          />
          <button class="button button--primary decrypt-sheet__submit" type="button" @click="submitRecoveryKey">{{ t('decrypt.unlock') }}</button>
          <button class="decrypt-sheet__link" type="button" @click="backToPasswordPage">{{ t('decrypt.back') }}</button>
        </div>
      </div>
      <div v-else-if="verifying" class="decrypt-sheet__status">
        <div class="spinner"></div>
        <span>{{ t('decrypt.verifying') }}</span>
      </div>
      <div v-else class="decrypt-sheet__status">
        <div class="check-mark">✓</div>
        <span>{{ t('decrypt.verified') }}</span>
      </div>
    </div>

    <!-- 存檔位置 sheet：驗證成功、信封打開後才會抽出 -->
    <div ref="destinationSheetEl" class="sheet destination-sheet" :class="sheetClass('destination')">
      <div v-if="!destinationDone" class="destination-sheet__body">
        <button class="button button--primary" type="button" :disabled="commitState.status === 'restoring'" @click="pickDestination">{{ t('decrypt.pickDestination') }}</button>
        <button class="decrypt-sheet__link" type="button" :disabled="commitState.status === 'restoring'" @click="cancelAtDestination">{{ t('decrypt.cancel') }}</button>
      </div>
      <div v-else class="destination-sheet__success">{{ t('decrypt.restoredSuccess') }}</div>
    </div>
  </div>
</template>

<style scoped>
/* 信封本體／flap-group／蠟封／sheet 基底樣式跟 EnvelopeEncrypt.vue 完全一致（技術規格
   §2.10 定案的座標／時序數值），這裡照抄一份，不是共用同一個檔案——這兩個元件雖然視覺
   語言相同，但狀態機跟互動邏輯差異夠大（這裡沒有拖放、沒有多步驟表單、多了兩頁翻頁跟
   驗證中/成功狀態層），拆成獨立元件比硬共用一個巨大的通用元件容易維護。 */
.envelope-outer {
  position: relative;
  width: 420px;
  margin: 0 auto;
  overflow: visible;
  perspective: 1600px;
}

.mailaway-rig {
  position: relative;
  z-index: 1;
  width: 420px;
  transform-origin: 50% 50%;
  transform-style: preserve-3d;
  will-change: transform;
}

@keyframes decrypt-envelope-drop-bounce {
  0% { transform: translateY(-420px); }
  55% { transform: translateY(0); }
  72% { transform: translateY(-16px); }
  86% { transform: translateY(5px); }
  100% { transform: translateY(0); }
}

.mailaway-rig.is-dropping {
  animation: decrypt-envelope-drop-bounce 820ms cubic-bezier(0.34, 1.28, 0.64, 1) both;
}

.envelope-canvas {
  position: relative;
  width: 420px;
  height: 420px;
  transform-style: preserve-3d;
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

/* 跟加密流程的信封最大差異：檔名／郵戳從一開始就常駐顯示（opacity:1、沒有 transition），
   不用等蓋章動畫——這份東西本來就已經是加密好的，不是剛加密完才需要蓋章的儀式感
   （對應定案文件 §1.11、mockup .envelope-outer.decrypt-envelope 覆蓋規則）。 */
.mail-filename,
.mail-postmark {
  position: absolute;
  top: 58%;
  transform: translateY(-50%);
  opacity: 1;
  pointer-events: none;
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
  transform: translate(-50%, 0);
  opacity: 1;
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1), opacity 160ms ease;
}

.sheet--hidden {
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 0);
  transition: none;
}

.sheet--reveal {
  opacity: 1;
  pointer-events: none;
  transform: translate(-50%, 200px);
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1), opacity 160ms ease;
}

.sheet--settle {
  opacity: 1;
  pointer-events: auto;
  transform: translate(-50%, 0);
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1);
}

.sheet--retreat {
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 0);
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1), opacity 200ms ease;
}

/* 驗證 sheet → 選存檔位置 sheet 之間的「原地縮放淡化」（見 playSheetCrossfade 的說明）——
   跟 EnvelopeEncrypt.vue 的 .sheet--fade-out／--morph-start／--fade-in 完全一致的數值，
   不要自己發明新的。 */
.sheet--fade-out {
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 0) scale(0.88);
  transition: opacity 200ms ease, transform 200ms ease;
}
.sheet--morph-start {
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 0) scale(0.88);
  transition: none;
}
.sheet--fade-in {
  opacity: 1;
  pointer-events: auto;
  transform: translate(-50%, 0) scale(1);
  transition: opacity 260ms cubic-bezier(0.23, 1, 0.32, 1), transform 260ms cubic-bezier(0.23, 1, 0.32, 1);
}

/* 同一張卡片內部用 translateX 左右滑動切到恢復金鑰輸入頁（不是抽出新卡片）——這是同一張
   紙卡「翻頁」，比淡出淡入更貼近「翻到另一面內容」的直覺（定案文件 §1.11）。overflow:hidden
   放在 .decrypt-sheet__pages 這一層（緊貼實際內容寬度），不是 .decrypt-sheet 本身——
   .decrypt-sheet 是 border-box、含左右 padding，套在那一層寬度會算錯，導致翻頁時看得到
   下一頁的文字穿幫（GUI造型探索_技術規格.md §1.11 記錄過這個坑）。 */
.decrypt-sheet {
  width: 268px;
  overflow: hidden;
}
.decrypt-sheet__pages {
  display: flex;
  width: 240px;
  overflow: hidden;
  transition: transform 280ms var(--ease-out, cubic-bezier(0.23, 1, 0.32, 1));
}
.decrypt-sheet__pages .decrypt-sheet__page {
  width: 240px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.decrypt-sheet--page2 .decrypt-sheet__pages {
  transform: translateX(-240px);
}

.decrypt-sheet input[type="password"],
.decrypt-sheet input[type="text"] {
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
.decrypt-sheet input:focus {
  outline: none;
  border-color: #DCC289;
  box-shadow: 0 0 0 3px rgba(220, 194, 137, 0.35);
}

.decrypt-sheet__alt-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  width: 100%;
  border: 1px solid var(--color-border);
  border-radius: 6px;
  padding: 7px 10px;
  background: var(--color-bg);
  color: var(--color-text);
  font: inherit;
  font-size: 12.5px;
  cursor: pointer;
  transition: background-color 150ms ease, transform 100ms ease-out;
}
.decrypt-sheet__alt-btn:hover { background: var(--color-border); }
.decrypt-sheet__alt-btn:active { transform: scale(0.97); }
.decrypt-sheet__alt-btn img { width: 16px; height: 16px; }

.decrypt-sheet__link {
  border: none;
  background: none;
  color: var(--color-text-secondary);
  font: inherit;
  font-size: 12px;
  cursor: pointer;
  text-align: center;
  padding: 4px 0;
}

.decrypt-sheet__submit { width: 100%; }

.decrypt-sheet__hint {
  font-size: 11px;
  color: var(--color-danger);
  margin: 0;
  line-height: 1.5;
}

.decrypt-sheet__status {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  padding: 14px 0;
  text-align: center;
  font-size: 12.5px;
  color: var(--color-text);
}

.spinner {
  width: 26px;
  height: 26px;
  border-radius: 50%;
  border: 2.5px solid var(--color-border);
  border-top-color: var(--color-accent);
  animation: decrypt-spinner-spin 800ms linear infinite;
}
@keyframes decrypt-spinner-spin {
  to { transform: rotate(360deg); }
}

.check-mark {
  width: 30px;
  height: 30px;
  border-radius: 50%;
  background: #1F5C34;
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 16px;
  font-weight: 700;
}

.destination-sheet {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  width: 220px;
}
.destination-sheet__body {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  width: 100%;
}
.destination-sheet__body .button { width: 100%; }
.destination-sheet__success {
  font-size: 12.5px;
  color: #1F5C34;
  font-weight: 600;
}

@media (prefers-reduced-motion: reduce) {
  .mailaway-rig.is-dropping {
    animation: none;
  }
  .sheet--reveal,
  .sheet--retreat {
    transition: opacity 200ms ease;
    transform: translate(-50%, 0) !important;
  }
  .spinner {
    animation-duration: 1400ms;
  }
}
</style>
