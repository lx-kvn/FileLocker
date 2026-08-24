<script setup>
// 信封加密流程（design-exploration/gui-styles-v2 定案文件 §1.6-1.10、技術規格 §1／§2.10）——
// 取代 App.vue 原本乾癟的表單精靈（encryptStep 1/2）。這個元件只負責「信封長什麼樣子、怎麼
// 動」，實際的檔案選取/密碼欄位資料、IPC 呼叫全部留在 App.vue（透過 props 傳進來、emit 事件
// 出去），比照 Phase 1 AppSidebar／TicketRow 的既有慣例——這個元件本身沒有 IPC、沒有檔案系統
// 概念，單獨測試不需要假造 window.chrome.webview。
//
// 時序常數跟座標數值直接照抄技術規格 §2.10 記錄的最終定案版本（不是兩份早期 mockup
// 8-envelope-assembled.html／12-file-tab-merged.html 裡已經被技術規格記錄推翻的做法）。
import { ref, computed, watch, onMounted, onUnmounted, nextTick } from 'vue'
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
  // 回饋：勾了恢復金鑰的話，App.vue 的恢復金鑰彈窗（全域 modal-overlay，不是這個元件內部
  // 的 sheet）會在 confirming 階段自動跳出來——要等使用者關掉那個彈窗，確認 sheet 的抽出
  // 動畫才能開始播，不能兩邊同時搶著出現。這個 prop 是 App.vue 那邊 recoveryKeyDisplay
  // 是否非空的即時鏡射，見 playFinalExitAndSeal。
  recoveryKeyModalOpen: { type: Boolean, default: false },
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

// 密碼欄位小眼睛顯示/隱藏——純展示層狀態，不透過 props 從 App.vue 傳進來（跟密碼本身
// 的值不一樣，這個狀態沒有任何一邊的業務邏輯需要知道）。
const showPassword = ref(false)
const showPasswordConfirm = ref(false)

// 兩次密碼不一致的提示（使用者回饋：原本只有按鈕變灰、沒有任何文字說明為什麼，猜不到
// 是密碼打錯）。用「確認密碼欄位離開過焦點」當觸發時機，不是每打一個字就即時比對——
// 使用者才剛開始打第二個密碼欄位，兩邊字數本來就會暫時不一樣，這是正常過程不是錯誤，
// 太早跳提示會很煩。一旦顯示過，之後改成一致要立刻消失（不用再次離開焦點才收回），
// 靠 computed 天生的響應式就能做到，不需要額外邏輯。
const passwordConfirmTouched = ref(false)
function onPasswordConfirmBlur() {
  passwordConfirmTouched.value = true
}
const passwordMismatch = computed(() =>
  passwordConfirmTouched.value && props.passwordConfirm.length > 0 && props.password !== props.passwordConfirm
)

const isDropping = ref(true)
const isOpen = ref(false)
const sheetPage = ref('picker') // 'picker' | 'password'
const sheetVisible = ref(false)
// 'hidden' | 'reveal' | 'settle' | 'fade-out' | 'morph-start' | 'fade-in' | 'retreat'
// 對應 mockup（13-sidebar-ticket-shell.html）的 .sheet--* 系列 class，命名跟狀態都刻意
// 照抄，不要自己發明新名字，方便之後對照 mockup 修改時不用先做名詞翻譯。
const sheetTransitionState = ref('hidden')

const pickerSheetEl = ref(null)
const passwordSheetEl = ref(null)
const confirmSheetEl = ref(null)
function sheetElFor(page) {
  if (page === 'picker') return pickerSheetEl.value
  if (page === 'password') return passwordSheetEl.value
  return confirmSheetEl.value
}
// 兩個 .sheet 各自根據「目前是不是這一頁」＋「共用的過場狀態」算出自己的 class——
// 兩張卡是互斥的（同時只有一張是 sheetPage 指到的那張），共用同一個 sheetTransitionState
// 沒問題，因為不是目前這頁的卡永遠落在第一個分支（sheet--hidden），不會被過場狀態誤套用。
function sheetClass(page) {
  const isActive = sheetPage.value === page && sheetVisible.value
  const state = sheetTransitionState.value
  return {
    'sheet--hidden': !isActive || state === 'hidden',
    'sheet--reveal': isActive && state === 'reveal',
    'sheet--settle': isActive && state === 'settle',
    'sheet--fade-out': isActive && state === 'fade-out',
    'sheet--morph-start': isActive && state === 'morph-start',
    'sheet--fade-in': isActive && state === 'fade-in',
    'sheet--retreat': isActive && state === 'retreat',
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
// CSS transition 要真的播出來，瀏覽器必須先把「起點」那個無 transition 的狀態畫出來一次，
// 才能認得出後面切到的 class 是「新的目標」而不是同一次繪製裡的最終結果——這裡對應 mockup
// 的 `void el.offsetWidth` 強制 reflow 那幾行（見技術規格 §2.10、13-sidebar-ticket-shell.html
// playSheetTwoPhaseEntrance／playSheetCrossfade）。Vue 的 class binding 改變不會同步反映到
// 畫面，要先 await nextTick() 讓「起點」那個 class 真的被瀏覽器畫過一次，再讀一次 offsetWidth
// 強制瀏覽器把這個瞬間的樣式算完、記下來，下一行才能安全切到會播 transition 的目標 class——
// 少了這兩步，兩次 class 變化有可能被瀏覽器合併成一次繪製，動畫直接跳過去看不到。
async function forceReflow(page) {
  await nextTick()
  const el = sheetElFor(page)
  if (el) void el.offsetWidth
}

// 世代編號：這個元件實例整體共用一個 key（不需要像技術規格那樣分三層 DOM 元素各自的
// generation，因為這裡沒有原本那套「開合三層各自獨立 class 切換」的 DOM 結構問題——Vue 的
// class binding 是宣告式的，狀態本身就是唯一事實來源，不會有「舊 class 沒清乾淨」的殘留，
// 需要世代編號防護的只有 setTimeout 鏈本身排定的「之後要做什麼」）。
const animKey = {}

// 兩段式進場（mockup playSheetTwoPhaseEntrance）：hidden → reveal（先滑出信封下緣，
// 完全清出畫布露臉）→ settle（再滑回來疊上信封）。這是「sheet 跳出來」這個手感的唯一
// 來源——之前這裡直接跳到定住的狀態，完全沒有滑出去那一段，是回饋抓到的問題。
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
      playSheetTwoPhaseEntrance('picker')
    })
  })
}

onMounted(playEntrance)
onUnmounted(() => {
  bumpGen(animKey) // 蓋掉任何還沒執行的 callback
  bumpGen(panelAnimKey)
  clearTimers()
})

// 兩張 Sheet 之間的「連貫翻頁」（mockup playSheetCrossfade）：選檔案→設密碼是同一段任務
// 的前後兩頁，不是各自獨立的物件，用原地交叉淡化＋縮放，不用兩段式抽出/收回（那個語言
// 代表「這個東西被收起來、換了一件不相干的事」）。MORPH_OUT_MS/MORPH_IN_MS 數值跟 mockup
// 完全一致（200ms 退場、260ms 進場，兩段各自的曲線也不同——退場一般 ease，進場強 ease-out）。
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

function goToPasswordPage() {
  if (props.paths.length === 0) return
  playSheetCrossfade('picker', 'password')
}

function goBackToPicker() {
  playSheetCrossfade('password', 'picker')
}

// 「選擇檔案」按鈕 ↔ 已選檔案清單：同一張 sheet 裡的兩個區塊，套跟上面 sheet 之間轉場
// 同一套縮放語言（mockup playPanelMorphSwap，共用 MORPH_OUT_MS/MORPH_IN_MS）——先把舊
// 區塊縮小淡出、真的從版面上消失（display:none）之後，才讓新區塊出現、從縮小狀態長出來，
// 避免兩個區塊同時佔用版面空間造成排版跳動。只在「有沒有檔案」這條邊界被跨越時才播（見
// watch(pickerHasFiles) 那段），清單裡增減筆數（一路都還在「有檔案」這個狀態內）不重播。
const emptyStateEl = ref(null)
const pickedWrapEl = ref(null)
const panelAnimKey = {}

function setPanelVisible(el, visible) {
  if (el) el.style.display = visible ? '' : 'none'
}

async function playPanelMorphSwap(fromEl, toEl) {
  const gen = bumpGen(panelAnimKey)
  if (fromEl) fromEl.classList.add('panel-morph-out')
  await new Promise((resolve) => after(MORPH_OUT_MS, resolve))
  if (!isCurrentGen(panelAnimKey, gen)) return
  if (fromEl) {
    fromEl.classList.remove('panel-morph-out')
    setPanelVisible(fromEl, false)
  }
  setPanelVisible(toEl, true)
  if (toEl) {
    toEl.classList.add('panel-morph-start')
    await nextTick()
    void toEl.offsetWidth // 強制 reflow，理由跟 forceReflow() 一樣
    if (!isCurrentGen(panelAnimKey, gen)) return
    toEl.classList.remove('panel-morph-start')
    toEl.classList.add('panel-morph-in')
  }
  after(MORPH_IN_MS, () => {
    if (!isCurrentGen(panelAnimKey, gen) || !toEl) return
    toEl.classList.remove('panel-morph-in')
  })
}

// 掛載當下（可能已經帶著預選的檔案，例如從檔案總管右鍵直接進來）要跟目前的 paths 同步，
// 不能觸發動畫——比照 mockup resetEncryptState 的做法，重置是瞬間完成，不播
// playPanelMorphSwap。之後 paths 才會跨界變化的才要播動畫，所以用一個獨立旗標記目前
// 「上一次算出來」的有無檔案狀態，而不是直接比較 watch 的 old/new 值（onMounted 跑的時候
// watch 的初始 old 值還沒意義）。
const pickerHadFiles = ref(props.paths.length > 0)
onMounted(() => {
  setPanelVisible(emptyStateEl.value, !pickerHadFiles.value)
  setPanelVisible(pickedWrapEl.value, pickerHadFiles.value)
})

watch(() => props.paths.length > 0, (hasFiles) => {
  if (hasFiles === pickerHadFiles.value) return
  pickerHadFiles.value = hasFiles
  if (hasFiles) {
    playPanelMorphSwap(emptyStateEl.value, pickedWrapEl.value)
  } else {
    playPanelMorphSwap(pickedWrapEl.value, emptyStateEl.value)
  }
})

// ---- 「這段任務真的結束了」的退場：技術規格 §2.10——這裡才用兩段式抽出/收回
// （sheet--reveal 完全滑出→sheet--retreat 收回並淡出），不是原地交叉淡化那種「翻頁」感，
// 兩者要明確區隔（回饋：sheet 的出場沒有套用抽出的動畫，原本誤用了跟頁面切換一樣的
// crossfade，這裡補上真正的抽出/收回）。退場播完才闔上信封、蓋章顯示檔名/郵戳/時間——
// 對應定案文件 §1.8「闔上信封、蓋章──闔上動畫播完才淡入檔名/郵戳/時間戳記」。
const showMailInfo = ref(false)
const mailTimestampText = ref('')

// 通用的兩段式抽出/收回退場——不管現在活躍的是哪一頁 sheet（sheetPage 不變，只是把它
// 播退場），reveal（完全滑出露出全貌）→ retreat（收回疊上信封並淡出）→ hidden 收尾。
// 密碼頁送出、確認/取消頁退場（不管是使用者按取消要重開、還是按確認後信封本身開始飛走）
// 都共用這一個函式，不用各自重寫一份幾乎一樣的邏輯。
async function playSheetTwoPhaseExit() {
  const gen = bumpGen(animKey)
  sheetTransitionState.value = 'reveal'
  await new Promise((resolve) => after(SHEET_PHASE_MS, resolve))
  if (!isCurrentGen(animKey, gen)) return
  sheetTransitionState.value = 'retreat'
  await new Promise((resolve) => after(SHEET_PHASE_MS, resolve))
  if (!isCurrentGen(animKey, gen)) return
  sheetVisible.value = false
  sheetTransitionState.value = 'hidden'
}

// 等某個 prop getter 變成 false（用在等 App.vue 那邊的全域彈窗真的關掉），watch 的
// stop handle 一拿到就馬上可能被呼叫，所以先存起來、resolve 之後再呼叫，不是定義完
// 立刻呼叫——避免 watch 內部還沒設好 stop 變數就想呼叫它的時序問題。
function waitForFalse(getter) {
  if (!getter()) return Promise.resolve()
  return new Promise((resolve) => {
    const stop = watch(getter, (val) => {
      if (!val) {
        stop()
        resolve()
      }
    })
  })
}

async function playFinalExitAndSeal() {
  await playSheetTwoPhaseExit() // 密碼頁退場
  const gen = bumpGen(animKey)
  isOpen.value = false
  mailTimestampText.value = new Date().toLocaleString()
  await new Promise((resolve) => after(FLAP_MS + 40, resolve))
  if (!isCurrentGen(animKey, gen)) return
  showMailInfo.value = true
  // 回饋：勾了恢復金鑰的話，這時候 App.vue 的恢復金鑰彈窗已經跳出來了（跟這裡的
  // showMailInfo 幾乎同時觸發，兩邊各自獨立收到自己那份 IPC 回應）——確認 sheet 的抽出
  // 動畫要等使用者關掉那個彈窗才能播，不能兩邊同時搶畫面。沒有勾恢復金鑰的話
  // recoveryKeyModalOpen 一直是 false，這裡等於立刻通過，走原本「停留一下才冒出來」的
  // 節奏（跟 mockup 恢復金鑰卡自動出現的節奏一致，定案文件 §5.2）。
  await waitForFalse(() => props.recoveryKeyModalOpen)
  if (!isCurrentGen(animKey, gen)) return
  await new Promise((resolve) => after(SETTLE_HOLD_MS, resolve))
  if (!isCurrentGen(animKey, gen)) return
  playSheetTwoPhaseEntrance('confirm')
}

// 使用者從「確認/取消」畫面按了取消：確認 sheet 先退場（抽出/收回），闔著蓋章的信封才
// 重新打開、回到密碼頁（定案文件 §1.8：取消後密碼欄位/勾選狀態保留，不清空，所以直接
// 回密碼頁而不是選檔頁）。
async function playReopenAfterCancel() {
  await playSheetTwoPhaseExit() // 確認 sheet 退場——回饋：按下確認/取消都要跑回去信封後面的動畫，不能瞬間消失
  const gen = bumpGen(animKey)
  showMailInfo.value = false
  isOpen.value = true
  after(FLAP_MS, () => {
    if (!isCurrentGen(animKey, gen)) return
    // 重新打開後 sheet 是「剛從信封裡再度冒出來」，跟第一次打開信封時同一種手感，
    // 套同一套兩段式進場（不是直接跳到定住的狀態）。
    playSheetTwoPhaseEntrance('password')
  })
}

// 使用者按確認、commit 真的成功、信封本身要開始飛走了——確認 sheet 要先完全收回退場，
// 飛走動畫才能開始播（回饋：原本兩個動畫是同時觸發的，看起來像 sheet 卡在半空中沒收好、
// 信封已經自己飛走了）。readyToFly 是這裡跟 template 的 .mailaway-rig.is-flying 之間的
// 開關——:class 同時看 phase==='flying' 跟這個旗標，兩者都成立才真的套用飛走的 class。
const readyToFly = ref(false)

async function playConfirmSheetExitForFlyAway() {
  readyToFly.value = false
  await playSheetTwoPhaseExit()
  readyToFly.value = true
}

watch(() => props.phase, (newPhase, oldPhase) => {
  if (newPhase === 'confirming' && oldPhase === 'processing') {
    playFinalExitAndSeal()
  } else if (newPhase === 'flying' && (oldPhase === 'committing' || oldPhase === 'confirming')) {
    playConfirmSheetExitForFlyAway()
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

// 回饋：飛走動畫播完後才該出現的東西（App.vue 的「要不要存進密碼庫」詢問）實際上提早
// 跳出來了——根本原因是 .mailaway-rig 同時有 rotate／translate／opacity 三個各自獨立
// 的 transition（見下面 CSS .mailaway-rig.is-flying），每一個播完都各自觸發一次
// transitionend、而且會冒泡到這裡，原本沒有篩選就直接 emit，撞到最短的 rotate（260ms）
// 播完那一刻就以為「動畫結束了」，這時 translate（220ms 延遲＋500ms＝720ms 才真的播完）
// 都還在飛。只在冒泡來源真的是這個元素本身（不是內層子元素自己的 transition 冒泡上來）、
// 且是三者之中最晚結束的 translate 播完時才真的 emit。
function onMailawayTransitionEnd(event) {
  if (event.target !== event.currentTarget) return
  if (props.phase !== 'flying') return
  if (event.propertyName !== 'translate') return
  emit('fly-away-complete')
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
    <div class="mailaway-rig" :class="{ 'is-dropping': isDropping, 'is-flying': phase === 'flying' && readyToFly }" @transitionend="onMailawayTransitionEnd">
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

    <!-- 頁三：確認/取消。回饋：這裡原本只是浮在闔上信封下方的一排裸按鈕，沒有 sheet——
         現在補上真正的 sheet，跟選檔案/設密碼共用同一套兩段式抽出/收回機制（見
         playFinalExitAndSeal／playReopenAfterCancel／playConfirmSheetExitForFlyAway）。 -->
    <div ref="confirmSheetEl" class="sheet sheet--confirm" :class="sheetClass('confirm')">
      <p class="confirm-summary">{{ pendingSummary }}</p>
      <div class="step2-actions">
        <button class="button button--secondary" type="button" data-action="cancel" :disabled="phase === 'committing'" @click="emit('cancel')">{{ t('encrypt.confirmSheetBack') }}</button>
        <button class="button button--primary" type="button" data-action="confirm" :disabled="phase === 'committing'" @click="emit('confirm')">{{ t('encrypt.confirmSheetConfirm') }}</button>
      </div>
    </div>

    <!-- 頁一：選檔案。「選擇檔案」空狀態／已選清單這兩塊不用 v-if/v-else 直接切換——那樣
         沒有任何轉場，兩塊一直都在 DOM 裡，用 ref + style.display 由 playPanelMorphSwap()
         手動控制可見度，才能在跨越「有沒有檔案」這條邊界時播放縮放淡化動畫（見上面
         playPanelMorphSwap 的說明）。 -->
    <div ref="pickerSheetEl" class="sheet sheet--picker" :class="[sheetClass('picker'), { 'has-files': paths.length > 0 }]">
      <div ref="emptyStateEl" class="sheet__empty-state">
        <button class="button button--primary" type="button" data-action="pick-file" @click="emit('pick-file')">{{ t('encrypt.pickFiles') }}</button>
        <button class="button button--secondary" type="button" data-action="pick-folder" @click="emit('pick-folder')">{{ t('encrypt.pickFolder') }}</button>
      </div>
      <div ref="pickedWrapEl" class="picked-list-frame">
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
    <div ref="passwordSheetEl" class="sheet sheet--password" :class="sheetClass('password')">
      <div class="step2-form">
        <!-- 回饋：密碼欄位漏掉了小眼睛顯示/隱藏切換——沿用 App.vue 全域的 .password-field／
             .password-field__toggle（App.vue 的 <style> 沒有 scoped，這裡直接共用同一套
             class 跟圖示，不用自己重畫一份），顯示狀態是這個元件自己的展示層概念，不用
             透過 props 從 App.vue 傳進來。 -->
        <div class="password-field">
          <input
            data-field="password"
            :type="showPassword ? 'text' : 'password'"
            :value="password"
            :placeholder="t('encrypt.passwordLabel')"
            @input="emit('update:password', $event.target.value)"
          />
          <button
            type="button"
            class="password-field__toggle"
            :aria-label="t(showPassword ? 'common.hidePassword' : 'common.showPassword')"
            @click="showPassword = !showPassword"
          >
            <svg v-if="showPassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
            <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
          </button>
        </div>
        <div class="password-field">
          <input
            data-field="passwordConfirm"
            :type="showPasswordConfirm ? 'text' : 'password'"
            :value="passwordConfirm"
            :placeholder="t('encrypt.passwordConfirmLabel')"
            @input="emit('update:passwordConfirm', $event.target.value)"
            @blur="onPasswordConfirmBlur"
          />
          <button
            type="button"
            class="password-field__toggle"
            :aria-label="t(showPasswordConfirm ? 'common.hidePassword' : 'common.showPassword')"
            @click="showPasswordConfirm = !showPasswordConfirm"
          >
            <svg v-if="showPasswordConfirm" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
            <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
          </button>
        </div>
        <p v-if="passwordMismatch" data-hint="password-mismatch" class="field-error-hint">{{ t('encrypt.passwordMismatch') }}</p>
        <input
          data-field="hint"
          type="text"
          :value="hint"
          :placeholder="t('encrypt.hintLabel')"
          @input="emit('update:hint', $event.target.value)"
        />
        <div class="checkbox-row">
          <label :class="{ 'is-disabled': disablePasskeyRecoveryKey }">
            <input type="checkbox" :checked="enablePasskey" :disabled="disablePasskeyRecoveryKey" @change="emit('update:enablePasskey', $event.target.checked)" />
            <img v-if="passkeyIconUrl" :src="passkeyIconUrl" alt="" />
            {{ t('encrypt.passkeyLabel') }}
          </label>
          <!-- 回饋：這裡要跟設定頁「i」提示圖示一樣的框框樣式，不是瀏覽器原生 title 那種
               陽春提示——沿用 App.vue 全域的 .info-tooltip 系列 class（App.vue 的 <style>
               沒有 scoped，這裡直接共用，不用自己重畫一份深色圓角泡泡）。回饋抓到的問題：
               這顆提示圖示原本放在 <label> 裡面，被停用的 label 有 opacity:0.5，這個
               opacity 會套用到整個子樹（就算子元素是 absolute 定位的泡泡也一樣被拖下水），
               泡泡因此看起來像半透明、後面文字會透出來——搬到 label 外面當手足元素，不再
               繼承那個透明度。 -->
          <span v-if="disablePasskeyRecoveryKey" class="info-tooltip" tabindex="0">
            <span class="info-tooltip__icon info-tooltip__icon--plain">?</span>
            <span class="info-tooltip__bubble">{{ t('encrypt.passkeyRecoveryKeyBatchDisabled') }}</span>
          </span>
        </div>
        <div class="checkbox-row">
          <label :class="{ 'is-disabled': disablePasskeyRecoveryKey }">
            <input type="checkbox" :checked="enableRecoveryKey" :disabled="disablePasskeyRecoveryKey" @change="emit('update:enableRecoveryKey', $event.target.checked)" />
            <img v-if="recoveryKeyIconUrl" :src="recoveryKeyIconUrl" alt="" />
            {{ t('encrypt.recoveryKeyLabel') }}
          </label>
          <span v-if="disablePasskeyRecoveryKey" class="info-tooltip" tabindex="0">
            <span class="info-tooltip__icon info-tooltip__icon--plain">?</span>
            <span class="info-tooltip__bubble">{{ t('encrypt.passkeyRecoveryKeyBatchDisabled') }}</span>
          </span>
        </div>
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
  transform: translate(-50%, 0);
  opacity: 1;
  /* 這個基底 transition 直接照抄 mockup 的 .sheet 預設值（見 13-sidebar-ticket-shell.html），
     只在沒有任何過場 class 匹配時當保底——正常情況下每個過場階段都有自己明確的 class
     （下面 --reveal/--settle/--fade-out/--morph-start/--fade-in/--retreat）指定要用的
     transition，不會真的落到這個保底值。 */
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1), opacity 160ms ease;
}

.sheet--hidden {
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 0);
  transition: none;
}

/* 兩段式進場／退場（mockup playSheetTwoPhaseEntrance／playSheetTwoPhaseExit）：
   進場＝hidden→reveal→settle（先滑出露臉，再收回疊上信封）；退場＝settle→reveal→retreat
   （反過來播，紙條塞回信封後面，退場最後淡出）。--reveal 是這兩段共用的中繼姿態——完全
   滑出信封 420px 畫布下緣之外（translateY 200px），不管是正要進場還是正要退場，路過這裡
   時視覺上都是同一個「完全清出畫布」的姿態。 */
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

/* 退場專用的第二段——跟 sheet--hidden 最終視覺結果一樣（位置歸零、opacity:0），但這裡
   要有轉場動畫（sheet--hidden 那個 transition:none 是刻意設計給「進場前瞬間重置起點」
   用的，不能拿來播退場，播退場會變成「滑下去之後直接瞬間消失」，沒有真的滑回去疊上
   信封）。退場播完之後 JS 會再切到 sheet--hidden 收尾，兩者最終視覺狀態相同，這個切換
   不會有任何可見的跳動。 */
.sheet--retreat {
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 0);
  transition: transform 280ms cubic-bezier(0.32, 0.72, 0, 1), opacity 200ms ease;
}

/* 兩張 Sheet 之間的「連貫翻頁」（mockup playSheetCrossfade）：不是每次換頁都要抽出/收回，
   那套語言代表「這個東西被收起來、換了一件不相干的事」——選檔案→設密碼是同一段連貫流程
   的前後兩頁，改用原地縮放淡化。退場／進場各自用不同的曲線：退場用一般 ease，進場用強
   ease-out cubic-bezier(0.23,1,0.32,1)，讓「長出來」那一下有俐落的起步感，不是兩段共用
   同一條曲線各退一半。 */
.sheet--fade-out {
  opacity: 0;
  pointer-events: none;
  transform: translate(-50%, 0) scale(0.88);
  transition: opacity 200ms ease, transform 200ms ease;
}

/* 進場前瞬間重置起點（縮小＋透明，無 transition）——對應 JS 裡 forceReflow() 之後才切到
   --fade-in，確保瀏覽器真的把這個起點畫過一次，接下來的 class 切換才會被認出是「新的
   過場目標」而真的播 transition，不會被合併成一次繪製直接跳過去看不到動畫。 */
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

/* 選檔案（已經有選檔案）／設密碼這兩個狀態統一撐到同一個高度——這兩者才是
   playSheetCrossfade 實際切換的前後兩個畫面，高度不一樣的話中途還是感覺得到「這張卡片
   突然變矮/變高」，不夠像同一塊東西在變形。一開始還沒選檔案、只有「選擇檔案」按鈕那個
   空狀態刻意不套這個最小高度——那個狀態本來就不會直接跟設密碼畫面交叉切換（要先選了
   檔案、「下一步」才會啟用），維持自己原本緊湊的高度就好。 */
.sheet--picker.has-files,
.sheet--password {
  min-height: 224px;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
}

/* 「選擇檔案」按鈕 ↔ 已選檔案清單這組切換（mockup playPanelMorphSwap）：套跟上面 Sheet
   之間轉場同一套縮放語言（0.88 縮放、退場 200ms 一般 ease、進場 260ms 強 ease-out），
   維持整體節奏一致，不是又另外發明一組數字。 */
.panel-morph-out {
  opacity: 0;
  transform: scale(0.88);
  transition: opacity 200ms ease, transform 200ms ease;
}

.panel-morph-start {
  opacity: 0;
  transform: scale(0.88);
  transition: none;
}

.panel-morph-in {
  opacity: 1;
  transform: scale(1);
  transition: opacity 260ms cubic-bezier(0.23, 1, 0.32, 1), transform 260ms cubic-bezier(0.23, 1, 0.32, 1);
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

/* 密碼欄位右邊留出小眼睛按鈕的空間——只套在 .password-field 包住的兩個密碼欄位，
   不影響提示文字欄位（那個沒有小眼睛，不需要留白）。 */
.step2-form .password-field input {
  padding-right: 2.4rem;
}

.step2-form input:focus {
  outline: none;
  border-color: #DCC289;
  box-shadow: 0 0 0 3px rgba(220, 194, 137, 0.35);
}

/* 兩次密碼不一致的提示文字——緊接在確認密碼欄位下面，用負的上邊距把它拉近欄位本身
   （.step2-form 的 flex gap 是給「不同欄位之間」用的間距，這裡要更貼近，不是另一個獨立欄位），
   下面接的提示（hint）欄位維持原本的 gap 不用跟著改。 */
.field-error-hint {
  margin: -4px 0 0;
  font-size: 12px;
  color: var(--color-danger);
  line-height: 1.4;
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

/* 提示框（.info-tooltip）跟勾選欄（<label>）並排的容器——回饋抓到的真正問題：提示圖示
   原本放在 <label> 裡面，被停用的 label 有 opacity:0.5，這個 opacity 會套用到整個子樹
   （就算子元素是 absolute 定位的泡泡也一樣被拖下水，opacity 造成的合成半透明不是
   z-index／堆疊順序能解決的事），泡泡因此看起來半透明、後面文字會透出來。把提示圖示
   移出 label、當它的手足元素放進這個 row 容器，就不會再繼承 label 的 opacity。 */
.checkbox-row {
  display: flex;
  align-items: center;
  gap: 6px;
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

/* 確認/取消 sheet 裡的摘要文字——沿用 .sheet 本身的置中/卡片樣式，這裡只補文字本身的
   排版，跟按鈕之間留白對齊 .step2-actions 上面那條分隔線的既有節奏。 */
.confirm-summary {
  margin: 4px 0 12px;
  font-size: 13px;
  color: var(--color-text);
  text-align: center;
  word-break: break-word;
}

/* UI/UX 走查：這個元件全身都是大幅度 3D 位移動畫（信封落下回彈、寄出飛走、sheet 滑出
   清出畫布），全站其他元件都有 prefers-reduced-motion 保護，這裡（跟 TicketRow.vue）
   是這輪新增動畫最多、幅度最大，卻唯二沒有這層保護的——大幅度移動正是這個媒體查詢
   要擋的前庭系統誘發暈眩來源。只拿掉「移動很遠的距離」這幾個（落下回彈、飛走、sheet
   完全滑出清出畫布），保留原地的縮放/淡化/圖示轉角度這種小幅度回饋——那些本身就在
   apple-design 建議保留的範圍內（"Keep opacity/color changes that aid comprehension"）。 */
@media (prefers-reduced-motion: reduce) {
  .mailaway-rig.is-dropping {
    animation: none;
  }
  .mailaway-rig.is-flying {
    transition: opacity 200ms ease;
    rotate: none;
    translate: none;
  }
  .sheet--reveal,
  .sheet--retreat {
    transition: opacity 200ms ease;
    transform: translate(-50%, 0) !important;
  }
}
</style>
