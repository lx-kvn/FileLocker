<script setup>
// 票根樣式的清單列（design-exploration/gui-styles-v2/13-sidebar-ticket-shell.html §「票根清單」，
// 定案文件 §3.4）。資料欄位、按鈕觸發的行為沿用 App.vue 原本「已加密清單」表格列一模一樣的
// 條件，外觀從表格列換成撕票造型的卡片。
//
// 撕開互動（點籤頭 → 撕一小角 → 驗證 → 通過才整張撕開飛走）：回饋指出先前這裡用「Vue 慣用
// 簡化」（單一元素小幅 rotate/translate 抖動）取代 mockup 的兩半 DOM clone 機制，結果支點、
// 撕開的點、停頓、時長全部對不上——mockup 精心調過這些細節，這裡改回照抄 mockup 的真實機制：
// `.ticket-stage` 底下疊三層——真正互動用的 `.ticket`，跟兩份純視覺的 `.ticket__half--left`／
// `--right`（用 clip-path 在撕線位置切開，各自只露出自己那一側，pointer-events:none 不接
// 任何互動）。平常只看得到 `.ticket`；peeking／tearing 狀態下 `.ticket` 淡出、兩個半邊淡入，
// 用 transform-origin: bottom left／bottom right（不是預設的置中）各自小幅度 translate+rotate，
// 看起來像真的從撕線裂開。裂線位置固定在 `.ticket__seal` 正中央（56px 寬、left:0，所以是
// 距左邊 28px），不像 mockup 用 JS 量測 DOM——這裡兩者都是靠 CSS 定死在同樣的座標，量測
// 沒有意義，直接寫死常數更直接。
//
// 完整時序（跟 mockup 的 playSequence 一致，只是「撕一小角、停等」那段從固定 1000ms 換成
// 真正的密碼驗證流程）：
//   點籤頭 → is-peeking（小幅撕開）→ decrypting 變 true 維持 → 後端驗證成功、tearing prop
//   變 true → is-tearing（撕線變紅、圖示轉角度，淡入兩個半邊）→ 下一輪 rAF → is-open（半邊
//   真的撐開到位）→ 停頓 TEAR_HOLD_MS 讓使用者看清楚裂開的樣子 → is-leaving（整個
//   `.ticket-stage` 飛走淡出）→ 飛走動畫播完才 emit torn-away，App.vue 收到才真的把這筆
//   從 vaultItems 陣列篩掉——這樣陣列真正異動、交給 App.vue 那層 `<TransitionGroup>` 接手
//   「其餘列往上補位」的那一刻，這一列早就已經完全飛出畫面、不會有兩段動畫互相打架。
//
// 點籤頭只觸發密碼驗證路徑（跟點「解密」按鈕一樣），對應定案文件〈信封清單虛線的互動〉：
// 「不論從虛線或從解密按鈕觸發，驗證成功後撕開飛走的完整動畫都會播放；撕開的前導動作
// （撕一小角）只在直接點虛線時才有」。
import { ref, watch, nextTick, onUnmounted } from 'vue'
import { fileTypeVisual } from '../fileTypeVisuals.js'
import { nestedLockPreviewText } from '../vaultListProjections.js'

const props = defineProps({
  item: { type: Object, required: true },
  t: { type: Function, required: true },
  decrypting: { type: Boolean, default: false },
  // 驗證成功後、真正從清單陣列移除之前：這個 prop 變 true 觸發下面完整的撕開＋飛走序列，
  // 序列播完會 emit torn-away，App.vue 收到才真的把這個項目從 vaultItems 篩掉（見上面
  // 檔案開頭的完整時序說明）。
  tearing: { type: Boolean, default: false },
  translateError: {
    type: Function,
    // 預設值只是保底（單獨測試這個元件時不用特別傳），正式接進 App.vue 時一定會傳入真正的
    // translateError（見 App.vue 既有同名函式，處理 errorCode/errorDetail 對照翻譯）。
    default: (code, detail, fallback) => fallback,
  },
  formatSize: {
    type: Function,
    default: (bytes) => {
      if (bytes < 1024) return `${bytes} B`
      if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
      return `${(bytes / 1024 / 1024).toFixed(1)} MB`
    },
  },
  formatDate: {
    type: Function,
    default: (isoString) => new Date(isoString).toLocaleString(),
  },
  typeLabel: {
    type: Function,
    default: (type, t) => (type === 'Folder' ? t('type.folder') : t('type.file')),
  },
})

const emit = defineEmits(['decrypt', 'decrypt-via-passkey', 'decrypt-via-recovery-key', 'delete', 'torn-away', 'go-to-original-location'])

function visual() {
  return fileTypeVisual(props.item)
}

// 撕線固定在 .ticket__seal（56px 寬、left:0）的正中央——兩份半邊 clip-path 用同一個常數，
// 不用量測 DOM（mockup 用 JS 量 seal.offsetLeft，但這裡座標本來就是 CSS 寫死的固定值，
// 量測沒有實質意義）。
const SEAM_X_PX = 28
function clipStyleFor(side) {
  return side === 'left'
    ? { clipPath: `inset(0 calc(100% - ${SEAM_X_PX}px) 0 0)` }
    : { clipPath: `inset(0 0 0 ${SEAM_X_PX}px)` }
}

// ---- 撕開狀態機：is-peeking → is-tearing → is-open → is-leaving（class 名稱、時序都照抄
// mockup 的 playSequence，只是「停等」那段從固定逾時換成真正的密碼驗證流程）。----
const isPeeking = ref(false)
const isTearing = ref(false)
const isOpen = ref(false)
const isLeaving = ref(false)

const TEAR_HOLD_MS = 550 // 裂開後停留一下，讓使用者看清楚裂開的樣子，再整個飛走——跟 mockup 完全一致

let peekResetTimer = null
let holdTimer = null
const rafIds = []

function clearAllTimers() {
  clearTimeout(peekResetTimer)
  clearTimeout(holdTimer)
  rafIds.forEach(cancelAnimationFrame)
  rafIds.length = 0
}

// 撕一小角：點籤頭當下先給即時觸覺回饋（apple-design「Respond on pointer-down」——不等驗證
// 彈窗真的跳出來才有反應），再送出密碼解密請求。如果使用者之後在密碼彈窗按了取消，
// decrypting 不會變成 true，這裡用一個逾時保底把「撕一小角」的狀態退回去，不用額外接一條
// App.vue 全域密碼彈窗的取消事件才能還原——真的驗證成功的話，decrypting 會在逾時之前變 true，
// 下面的 watch 接手維持撕開狀態直到 tearing prop 變 true 進入真正的撕開序列。
function onSealClick() {
  isPeeking.value = true
  clearTimeout(peekResetTimer)
  peekResetTimer = setTimeout(() => {
    if (!props.decrypting) isPeeking.value = false
  }, 1500)
  emit('decrypt', props.item)
}

watch(() => props.decrypting, (decrypting) => {
  if (isTearing.value) return // 已經進入真正的撕開序列，這個 watch 不用再管撕一小角的狀態
  if (decrypting) {
    clearTimeout(peekResetTimer)
    isPeeking.value = true
  } else {
    // 驗證失敗／使用者取消都會讓 decrypting 從 true 掉回 false（見 App.vue 的
    // decryptingUuids），這裡直接還原，不用等逾時。驗證成功的情況不會走到這裡——
    // decrypting 變 false 的同時 tearing 會變 true，上面的 return 已經先擋下了。
    isPeeking.value = false
  }
}, { immediate: true }) // 掛載當下 decrypting 剛好是 true（例如頁面重新整理、解密其實還在
// 背景進行中）也要立刻反映撕開狀態，不用等下一次變化才補上。

// tearing prop 變 true＝後端驗證成功——播完整的撕開＋停頓＋飛走序列，播完才 emit
// torn-away，讓 App.vue 真的把這個項目從陣列篩掉。
watch(() => props.tearing, (tearing) => {
  if (!tearing) return
  clearTimeout(peekResetTimer)
  isPeeking.value = false
  isTearing.value = true
  // 兩輪 rAF 才切到 is-open——比照 mockup：第一輪讓瀏覽器真的畫出 is-tearing 這個起點
  // （半邊淡入、還沒撐開），第二輪才觸發撐開的 transition，兩者要間隔至少一次繪製，
  // 不然會被瀏覽器合併成一次繪製直接跳過去看不到「先裂開、才撐開」這個分解動作。
  const raf1 = requestAnimationFrame(() => {
    const raf2 = requestAnimationFrame(() => {
      isOpen.value = true
      holdTimer = setTimeout(() => {
        isLeaving.value = true
      }, TEAR_HOLD_MS)
    })
    rafIds.push(raf2)
  })
  rafIds.push(raf1)
})

// 飛走動畫（.ticket-stage 的 transform 380ms／opacity 340ms，見下面 CSS）播完才真的
// emit——只在 propertyName 是兩者之中比較長的 transform 時才算數，避免 opacity 那個
// transitionend 先冒出來就提早觸發。
function onStageTransitionEnd(event) {
  if (!isLeaving.value) return
  if (event.target !== event.currentTarget) return
  if (event.propertyName !== 'transform') return
  emit('torn-away', props.item)
}

onUnmounted(clearAllTimers)
</script>

<template>
  <div class="ticket-wrap" :class="{ 'is-peeking': isPeeking, 'is-tearing': isTearing, 'is-open': isOpen, 'is-leaving': isLeaving }">
    <div class="ticket-stage" @transitionend="onStageTransitionEnd">
      <div class="ticket">
        <button type="button" class="ticket__seal" :aria-label="t('list.decrypt')" :disabled="decrypting || isTearing" @click="onSealClick">
          <span class="ticket__tear-line"></span>
          <span class="ticket__icon" :style="{ color: visual().color }">
            <svg v-if="visual().icon === 'folder'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7a2 2 0 0 1 2-2h4l2 2h6a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2Z"/></svg>
            <svg v-else-if="visual().icon === 'document'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h9l4 4v14H6z"/><path d="M14 3v5h5"/></svg>
            <svg v-else-if="visual().icon === 'archive'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2"/><path d="M4 15l4-4 4 4 4-6 4 6"/></svg>
            <svg v-else-if="visual().icon === 'certificate'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 15a3 3 0 0 0 3-3V6a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3Z"/><path d="M19 11a7 7 0 0 1-14 0M12 18v3"/></svg>
            <svg v-else-if="visual().icon === 'image'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="8.5" cy="10" r="1.5"/><path d="M21 16l-5-5-4 4-2-2-5 5"/></svg>
            <svg v-else-if="visual().icon === 'audio'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l11-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="17" cy="16" r="3"/></svg>
            <svg v-else-if="visual().icon === 'text'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h9l4 4v14H6z"/><path d="M9 13h6M9 17h6"/></svg>
            <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h9l4 4v14H6z"/></svg>
          </span>
        </button>

        <div class="info">
          <div class="name" :title="item.originalName">{{ item.originalName }}</div>
          <div class="meta">
            <span>{{ typeLabel(item.type, t) }} · {{ formatSize(item.originalSizeBytes) }}</span>
            <span v-if="item.hint">{{ item.hint }}</span>
            <span>{{ formatDate(item.createdAtUtc) }}</span>
          </div>
          <span v-if="item.hasNestedLocks" class="badge badge--nested-lock" :title="nestedLockPreviewText(item, t)">×{{ item.nestedLockCount }}</span>
          <div v-if="!item.markerFound" class="status-warning">
            {{ translateError(item.markerStatusCode, item.markerStatusDetail, item.markerStatusMessage) }}
            <!-- 只有 Standalone（獨立加密）項目才需要這顆按鈕——Vault 模式的指標檔找不到，
                 內容仍安全存在 Vault 裡，清單頁本來就能直接解密（見功能規劃 §1 表格），沒有
                 「使用者要自己去找檔案」這回事；Standalone 模式的密文本體就是那個找不到的
                 檔案，只能靠使用者自己找回來，「前往檔案原始位置」對這種情況才有實際幫助。 -->
            <button
              v-if="item.storageMode === 'Standalone'"
              type="button"
              class="status-warning__goto-link"
              @click="emit('go-to-original-location', item)"
            >{{ t('list.goToOriginalLocation') }}</button>
          </div>
        </div>

        <span class="postmark-slot"></span>

        <div class="actions">
          <button type="button" data-action="decrypt" :disabled="decrypting || isTearing" @click="emit('decrypt', item)">{{ t('list.decrypt') }}</button>
          <button v-if="item.passkeyEnabled" type="button" data-action="passkey" :disabled="decrypting || isTearing" @click="emit('decrypt-via-passkey', item)">{{ t('decrypt.passkeyUnlock') }}</button>
          <button v-if="item.recoveryKeyEnabled" type="button" data-action="recovery-key" :disabled="decrypting || isTearing" @click="emit('decrypt-via-recovery-key', item)">{{ t('decrypt.recoveryKeyUnlock') }}</button>
          <!-- 刪除鈕：mockup（13-sidebar-ticket-shell.html）沒有畫這顆按鈕，但原本表格版清單本來就有
               每列刪除的功能（App.vue 舊版的 row-delete-button），純粹重新蒙皮不能連功能一起丟掉，
               所以這裡照舊保留，只是外觀併進 actions 這排。 -->
          <button type="button" data-action="delete" class="ticket__delete" :aria-label="t('list.delete')" :title="t('list.delete')" @click="emit('delete', item)">
            <svg viewBox="0 0 24 24" fill="none"><path d="M5 7h14M10 11v6M14 11v6M7 7l1-3a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1l1 3M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
          </button>
        </div>
      </div>

      <!-- 純視覺的兩份半邊 clone——只在 peeking/tearing 期間淡入（見下面 CSS），永遠
           pointer-events:none，不接任何互動，內容跟上面 .ticket 完全一樣（clip-path 各自
           只露出撕線某一側），是撕開動畫「看起來真的裂成兩半」的唯一來源。 -->
      <template v-for="side in ['left', 'right']" :key="side">
        <div class="ticket__half" :class="`ticket__half--${side}`" :style="clipStyleFor(side)">
          <div class="ticket">
            <span class="ticket__seal">
              <span class="ticket__tear-line"></span>
              <span class="ticket__icon" :style="{ color: visual().color }">
                <svg v-if="visual().icon === 'folder'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7a2 2 0 0 1 2-2h4l2 2h6a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2Z"/></svg>
                <svg v-else-if="visual().icon === 'document'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h9l4 4v14H6z"/><path d="M14 3v5h5"/></svg>
                <svg v-else-if="visual().icon === 'archive'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="4" y="4" width="16" height="16" rx="2"/><path d="M4 15l4-4 4 4 4-6 4 6"/></svg>
                <svg v-else-if="visual().icon === 'certificate'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M12 15a3 3 0 0 0 3-3V6a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3Z"/><path d="M19 11a7 7 0 0 1-14 0M12 18v3"/></svg>
                <svg v-else-if="visual().icon === 'image'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="16" rx="2"/><circle cx="8.5" cy="10" r="1.5"/><path d="M21 16l-5-5-4 4-2-2-5 5"/></svg>
                <svg v-else-if="visual().icon === 'audio'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M9 18V5l11-2v13"/><circle cx="6" cy="18" r="3"/><circle cx="17" cy="16" r="3"/></svg>
                <svg v-else-if="visual().icon === 'text'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h9l4 4v14H6z"/><path d="M9 13h6M9 17h6"/></svg>
                <svg v-else viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3h9l4 4v14H6z"/></svg>
              </span>
            </span>
            <div class="info">
              <div class="name">{{ item.originalName }}</div>
              <div class="meta">
                <span>{{ typeLabel(item.type, t) }} · {{ formatSize(item.originalSizeBytes) }}</span>
                <span v-if="item.hint">{{ item.hint }}</span>
                <span>{{ formatDate(item.createdAtUtc) }}</span>
              </div>
            </div>
            <span class="postmark-slot"></span>
            <div class="actions">
              <button type="button" tabindex="-1">{{ t('list.decrypt') }}</button>
            </div>
          </div>
        </div>
      </template>
    </div>
  </div>
</template>

<style scoped>
.ticket-wrap {
  position: relative;
}

/* 回饋（使用者實測抓到）：撕開／撐開過程中兩個半邊（.ticket__half）因為旋轉角度，實際
   渲染範圍會比自己這一列的高度略高一點（撐開時往上下微幅超出），批次群組展開清單裡
   這一列跟上面那一列是普通 flex 手足，沒有明確 z-index 的情況下按照文件順序疊放，
   超出範圍的部分會被「畫面上排在後面、但 DOM 順序在前面」的上一列蓋住，看起來像是
   撕開動畫被上面的項目擋到一角。撕開／撐一角期間主動墊高自己的堆疊順序，確保這個過程
   的視覺一定蓋在其他靜止的手足上面，不管它是清單裡的第幾筆。 */
.ticket-wrap.is-peeking,
.ticket-wrap.is-tearing {
  z-index: 1;
}

.ticket-stage {
  position: relative;
  transition: transform 380ms var(--ease-out, ease), opacity 340ms ease;
}

.ticket-wrap.is-leaving .ticket-stage {
  transform: translateX(90px) rotate(2.5deg);
  opacity: 0;
}

.ticket {
  position: relative;
  background: var(--color-surface);
  border: 1px solid var(--color-border);
  border-radius: 10px;
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 10px 20px 10px 76px;
  min-height: 64px;
  transition: border-color 150ms var(--ease-out, ease), opacity 200ms ease;
}

.ticket-stage:hover .ticket {
  border-color: var(--color-border-strong);
}

/* peeking／tearing 期間，真正互動用的這張卡片淡出，讓底下的兩個半邊接手顯示——
   跟 mockup 完全一致（`.ticket-wrap.is-tearing .ticket, .ticket-wrap.is-peeking .ticket
   {opacity:0}`），不是只有完整撕開才切換，撕一小角那一刻其實也已經在用半邊機制、
   只是位移幅度很小。用 > 直接子層選擇器，不能只寫 `.ticket-wrap.is-tearing .ticket`
   （後代選擇器）——兩個半邊 `.ticket__half` 裡面也各自包了一份 `.ticket`（同一個 class
   名稱），後代選擇器會連它們一起選到、一起變不可見，整列會變成完全空白（真的撞到過這個
   問題：兩個半邊明明該顯示，卻因為共用 class 被同一條規則一起隱藏）。 */
.ticket-wrap.is-tearing > .ticket-stage > .ticket,
.ticket-wrap.is-peeking > .ticket-stage > .ticket {
  opacity: 0;
}

/* 兩份純視覺的半邊 clone——inset:0 疊在 .ticket-stage 同一個座標，用 clip-path
   （見 script 的 clipStyleFor）各自只露出撕線某一側，平常完全不可見、不接互動。 */
.ticket__half {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  gap: 16px;
  opacity: 0;
  pointer-events: none;
  transition: transform 420ms var(--ease-out, ease);
}

.ticket-wrap.is-tearing .ticket__half,
.ticket-wrap.is-peeking .ticket__half {
  opacity: 1;
}

/* 回饋抓到的問題：完整撕開時籤頭（左半邊那一小截）外框消失、顏色跟背景幾乎一樣分不出來——
   之前這裡把 border-color 設成 transparent，想讓兩個半邊看起來「本來就是同一張卡片裂開」，
   結果 --color-surface 本身跟頁面背景 --color-bg 的明暗差很小，少了邊框之後籤頭直接融進
   背景看不見。改回保留邊框，籤頭本身的底色也明確換成比 --color-surface 更深一階的
   --color-bg（這個 token 在深色/淺色模式下都確定比 --color-surface 深，不是碰運氣），
   撕開之後籤頭才有實在的存在感，不會像消失了一樣。 */
.ticket__half .ticket {
  width: 100%;
  background: var(--color-bg);
}

.ticket__half--left .ticket {
  border-color: var(--color-border-strong);
}

/* 支點：左半邊（撕線左側那一小截，貼著 seal）從自己的左下角轉開，右半邊（其餘
   絕大部分內容）從自己的右下角轉開——不是預設置中，撕開才會看起來像真的往兩邊裂。 */
.ticket__half--left {
  transform-origin: bottom left;
}

.ticket__half--right {
  transform-origin: bottom right;
}

/* 撕一小角：只借用撕開機制最前面一小段（幅度約完整撕開的四分之一、過場也更快
   160ms），驗證通過後接著播的完整撕開感覺是同一個動作的延續，不是切成兩段不相干
   的東西。 */
.ticket-wrap.is-peeking .ticket__half {
  transition: transform 160ms var(--ease-out, ease);
}

.ticket-wrap.is-peeking .ticket__half--left {
  transform: translateX(-0.75px) rotate(-0.35deg);
}

.ticket-wrap.is-peeking .ticket__half--right {
  transform: translateX(0.75px) rotate(0.3deg);
}

.ticket-wrap.is-tearing.is-open .ticket__half--left {
  transform: translateX(-9px) rotate(-3.5deg);
}

.ticket-wrap.is-tearing.is-open .ticket__half--right {
  transform: translateX(9px) rotate(2.5deg);
}

.ticket__seal {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 56px;
  display: flex;
  align-items: center;
  justify-content: center;
  appearance: none;
  border: none;
  background: none;
  padding: 0;
  border-radius: 8px;
  cursor: pointer;
  transition: background-color 150ms ease, transform 120ms ease-out;
}

button.ticket__seal:hover:not(:disabled) {
  background: rgba(34, 34, 30, 0.035);
}

/* UI/UX 走查：這顆按鈕（跟下面 .actions button／.ticket__delete）原本只有 :hover，
   點下去那一刻完全沒有回饋，要等非同步的解密回應回來才有動靜——按下瞬間就該有即時
   觸覺回饋（apple-design「Respond on pointer-down」），跟全站其他按鈕（.button）
   統一用 scale(0.97)。 */
button.ticket__seal:active:not(:disabled) {
  transform: scale(0.97);
}

button.ticket__seal:disabled {
  cursor: default;
}

button.ticket__seal:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
}

/* 撕線貫穿整個 seal（top:0;bottom:0，跟 seal 本身一樣頂到卡片上下邊緣）——讀起來才像
   真正的票根騎縫線貫穿整張票根，不是一小段浮在圖示旁邊、被內距框住的裝飾線（回饋抓到
   的問題：先前這裡用 top:8px;bottom:8px 內縮，看起來像浮在中間的短線）。 */
.ticket__tear-line {
  position: absolute;
  left: 50%;
  top: 0;
  bottom: 0;
  width: 0;
  border-left: 2px dashed var(--color-border-strong);
  transform: translateX(-50%);
  transition: border-color 160ms ease;
}

.ticket-wrap.is-peeking .ticket__tear-line,
.ticket-wrap.is-tearing .ticket__tear-line {
  border-color: var(--color-danger);
}

.ticket__icon {
  position: relative;
  z-index: 2;
  width: 32px;
  height: 32px;
  border-radius: 50%;
  background: var(--color-surface);
  display: flex;
  align-items: center;
  justify-content: center;
  /* 邊線用 currentColor（承接 .ticket__icon 上 :style 設的檔案類型色），不是固定灰色——
     圖示邊框顏色要跟著檔案類型變色，才是「印刷上去的徽章」而不是「隨便套一個外框」
     （回饋抓到的問題：先前這裡固定用 var(--color-border) 灰色，跟 mockup 對不上）。 */
  border: 1.6px solid currentColor;
  box-shadow: 0 1px 2px rgba(34, 34, 30, 0.08);
  transition: transform 150ms var(--ease-out, ease);
}

.ticket-wrap.is-peeking .ticket__icon {
  transform: rotate(-6deg);
}

.ticket__icon svg {
  width: 14px;
  height: 14px;
}

.info {
  flex: 1;
  min-width: 0;
}

.info .name {
  font-weight: 600;
  font-size: 13.5px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.meta {
  font-size: 11.5px;
  color: var(--color-text-secondary);
  margin-top: 3px;
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.badge--nested-lock {
  display: inline-block;
  margin-top: 4px;
  font-size: 11px;
  font-weight: 700;
  color: var(--color-accent-hover);
}

.status-warning {
  margin-top: 4px;
  font-size: 11.5px;
  color: var(--color-danger);
}

.status-warning__goto-link {
  display: inline;
  margin-left: 6px;
  padding: 0;
  border: none;
  background: none;
  font: inherit;
  font-size: inherit;
  color: var(--color-accent);
  text-decoration: underline;
  cursor: pointer;
}
.status-warning__goto-link:hover {
  color: var(--color-accent-hover);
}

.postmark-slot {
  width: 90px;
  flex-shrink: 0;
}

.actions {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  flex-shrink: 0;
  width: 180px;
}

.actions button {
  appearance: none;
  border: 1px solid var(--color-border-strong);
  background: var(--color-surface);
  color: var(--color-text);
  font: inherit;
  font-size: 12px;
  font-weight: 600;
  padding: 6px 10px;
  border-radius: var(--radius-sm);
  cursor: pointer;
  white-space: nowrap;
  transition: border-color 150ms ease, color 150ms ease, transform 120ms ease-out;
}

.actions button:hover:not(:disabled) {
  border-color: var(--color-accent);
  color: var(--color-accent);
}

/* UI/UX 走查：解密/Passkey/恢復金鑰這幾顆按鈕原本點下去沒有立即回饋，見上面
   .ticket__seal 的同一則說明。 */
.actions button:active:not(:disabled) {
  transform: scale(0.97);
}

.actions button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.ticket__delete {
  border-color: transparent !important;
  color: var(--color-text-tertiary);
  padding: 6px !important;
}

.ticket__delete:hover {
  color: var(--color-danger) !important;
  border-color: var(--color-danger) !important;
}

.ticket__delete:active {
  transform: scale(0.97);
}

.ticket__delete svg {
  width: 14px;
  height: 14px;
  display: block;
}

/* UI/UX 走查：撕開飛走是這輪動作幅度最大的動畫之一（撐開+90px 位移飛出畫面），跟
   EnvelopeEncrypt.vue 一樣原本沒有 prefers-reduced-motion 保護——只拿掉「移動很遠的
   距離」那一段（飛走），撕開本身的小幅度位移（撐開的 9px/3.5deg）保留，讓使用者還是
   看得出「撕開了」這個狀態變化，只是不會整列飛出畫面外。 */
@media (prefers-reduced-motion: reduce) {
  .ticket-wrap.is-leaving .ticket-stage {
    transition: opacity 200ms ease;
    transform: none;
  }
}
</style>
