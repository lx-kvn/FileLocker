<script setup>
// 票根樣式的清單列（design-exploration/gui-styles-v2/13-sidebar-ticket-shell.html §「票根清單」，
// 定案文件 §3.4）。資料欄位、按鈕觸發的行為沿用 App.vue 原本「已加密清單」表格列一模一樣的
// 條件，外觀從表格列換成撕票造型的卡片。
//
// 撕開互動（點籤頭 → 撕一小角 → 驗證 → 通過才整張撕開飛走）：跟 mockup 原始做法（JS 執行期
// clone 整張 `.ticket` 出兩份 `.ticket__half--left/right`）不同，這裡用 Vue 慣用的做法簡化——
// 不複製兩份完整內容，改成直接在 `.ticket` 本身套用小幅度的 rotate/translate 抖動（撕線變紅、
// 圖示轉一下角度）表達「摸到了」的觸覺回饋，飛走＋下面補位交給 App.vue 那層的
// `<TransitionGroup>`（Vue 對「從 v-for 陣列移除」這件事本來就有內建的 leave 過場機制，不需要
// 手動量測高度、手動搬移其餘列的位置）。视覺上簡化了「兩個半張卡片各自飛開」的細節，但撕線
// 顏色／貫穿卡片全高的座標、驗證前先給即時回饋這幾個核心規格都照 mockup 定案版本做。
//
// 點籤頭只觸發密碼驗證路徑（跟點「解密」按鈕一樣），對應定案文件〈信封清單虛線的互動〉：
// 「不論從虛線或從解密按鈕觸發，驗證成功後撕開飛走的完整動畫都會播放；撕開的前導動作
// （撕一小角）只在直接點虛線時才有」。
import { ref, watch } from 'vue'
import { fileTypeVisual } from '../fileTypeVisuals.js'
import { nestedLockPreviewText } from '../vaultListProjections.js'

const props = defineProps({
  item: { type: Object, required: true },
  t: { type: Function, required: true },
  decrypting: { type: Boolean, default: false },
  // 驗證成功後、真正從清單陣列移除之前的短暫過場：撕開的視覺回饋播完，緊接著才由
  // App.vue 那層的 <TransitionGroup> 接手飛走＋下面補位（見上面的檔案開頭說明）。
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

const emit = defineEmits(['decrypt', 'decrypt-via-passkey', 'decrypt-via-recovery-key', 'delete'])

function visual() {
  return fileTypeVisual(props.item)
}

// 撕一小角：點籤頭當下先給即時觸覺回饋（apple-design「Respond on pointer-down」——不等驗證
// 彈窗真的跳出來才有反應），再送出密碼解密請求。如果使用者之後在密碼彈窗按了取消，
// decrypting 不會變成 true，這裡用一個逾時保底把「撕一小角」的狀態退回去，不用額外接一條
// App.vue 全域密碼彈窗的取消事件才能還原——真的驗證成功的話，decrypting 會在逾時之前變 true，
// 下面的 watch 接手維持撕開狀態直到這一列被移除。
const isPeeking = ref(false)
let peekResetTimer = null

function onSealClick() {
  isPeeking.value = true
  clearTimeout(peekResetTimer)
  peekResetTimer = setTimeout(() => {
    if (!props.decrypting) isPeeking.value = false
  }, 1500)
  emit('decrypt', props.item)
}

watch(() => props.decrypting, (decrypting) => {
  if (decrypting) {
    clearTimeout(peekResetTimer)
    isPeeking.value = true
  } else {
    // 驗證失敗／使用者取消都會讓 decrypting 從 true 掉回 false（見 App.vue 的
    // decryptingUuids），這裡直接還原，不用等逾時。驗證成功的情況不會走到這裡——
    // 這一列會直接從陣列裡被移除、整個元件卸載，不會有「decrypting 變回 false」這一刻。
    isPeeking.value = false
  }
}, { immediate: true }) // 掛載當下 decrypting 剛好是 true（例如頁面重新整理、解密其實還在
// 背景進行中）也要立刻反映撕開狀態，不用等下一次變化才補上。
</script>

<template>
  <div class="ticket" :class="{ 'is-peeking': isPeeking, 'is-tearing': tearing }">
    <button type="button" class="ticket__seal" :aria-label="t('list.decrypt')" :disabled="decrypting || tearing" @click="onSealClick">
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
      <div v-if="!item.markerFound" class="status-warning">{{ translateError(item.markerStatusCode, item.markerStatusDetail, item.markerStatusMessage) }}</div>
    </div>

    <span class="postmark-slot"></span>

    <div class="actions">
      <button type="button" data-action="decrypt" :disabled="decrypting || tearing" @click="emit('decrypt', item)">{{ t('list.decrypt') }}</button>
      <button v-if="item.passkeyEnabled" type="button" data-action="passkey" :disabled="decrypting || tearing" @click="emit('decrypt-via-passkey', item)">{{ t('decrypt.passkeyUnlock') }}</button>
      <button v-if="item.recoveryKeyEnabled" type="button" data-action="recovery-key" :disabled="decrypting || tearing" @click="emit('decrypt-via-recovery-key', item)">{{ t('decrypt.recoveryKeyUnlock') }}</button>
      <!-- 刪除鈕：mockup（13-sidebar-ticket-shell.html）沒有畫這顆按鈕，但原本表格版清單本來就有
           每列刪除的功能（App.vue 舊版的 row-delete-button），純粹重新蒙皮不能連功能一起丟掉，
           所以這裡照舊保留，只是外觀併進 actions 這排。 -->
      <button type="button" data-action="delete" class="ticket__delete" :aria-label="t('list.delete')" :title="t('list.delete')" @click="emit('delete', item)">
        <svg viewBox="0 0 24 24" fill="none"><path d="M5 7h14M10 11v6M14 11v6M7 7l1-3a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1l1 3M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
      </button>
    </div>
  </div>
</template>

<style scoped>
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
  /* 撕一小角：借用「撕開」動畫本身，只是幅度縮到很小＋更快的過場時間（160ms，不是完整
     撕開用的時長）——驗證通過後接著播的完整撕開感覺是同一個動作的延續，不是切成兩段
     不相干的東西（見 mockup 技術規格對應段落）。 */
  transition: border-color 150ms var(--ease-out, ease), transform 160ms var(--ease-out, ease);
}

.ticket:hover {
  border-color: var(--color-border-strong);
}

.ticket.is-peeking {
  transform: translateX(-1px) rotate(-0.35deg);
}

/* 撕開（驗證通過那一刻）：幅度比「撕一小角」大一截，過場也拉長一點，讓人看得出這是
   同一個撕開動作的延續、只是這次是真的撕開了。播完之後這一列就會從 vaultItems 陣列被
   移除，交給 App.vue 的 <TransitionGroup> 接手飛走，這裡不用自己處理位移出畫面。
   pointer-events:none 是防止這短短一瞬間使用者又點了一次按鈕。 */
.ticket.is-tearing {
  transform: translateX(4px) rotate(1.4deg);
  transition: transform 200ms var(--ease-out, ease), border-color 150ms var(--ease-out, ease);
  pointer-events: none;
}

.ticket.is-tearing .ticket__tear-line {
  border-color: var(--color-danger);
}

.ticket.is-tearing .ticket__icon {
  transform: rotate(8deg);
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
  transition: background-color 150ms ease;
}

.ticket__seal:hover:not(:disabled) {
  background: rgba(34, 34, 30, 0.035);
}

.ticket__seal:disabled {
  cursor: default;
}

.ticket__seal:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
}

.ticket__tear-line {
  position: absolute;
  left: 50%;
  top: 8px;
  bottom: 8px;
  width: 0;
  border-left: 1.5px dashed var(--color-border-strong);
  transition: border-color 160ms ease;
}

.ticket.is-peeking .ticket__tear-line {
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
  border: 1px solid var(--color-border);
  transition: transform 150ms var(--ease-out, ease);
}

.ticket.is-peeking .ticket__icon {
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
}

.actions button:hover:not(:disabled) {
  border-color: var(--color-accent);
  color: var(--color-accent);
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

.ticket__delete svg {
  width: 14px;
  height: 14px;
  display: block;
}
</style>
