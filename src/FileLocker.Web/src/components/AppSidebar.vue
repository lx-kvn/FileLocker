<script setup>
// 側欄殼子（design-exploration/gui-styles-v2/13-sidebar-ticket-shell.html 定案版本）——
// 取代原本 App.vue 頂部的水平分頁列（tab-bar）。這個階段只做殼子＋導覽，不含信封加密/
// 解密動畫，所以這裡刻意不知道 activeTab 底下更細的狀態（encryptStep 之類），只負責回報
// 使用者點了哪個 nav 項目，實際要切到哪個畫面由 App.vue 決定。
//
// 圖示全部照 GUI造型探索_定案文件 §3.3 的決定：跟 App.vue 既有 page-title 圖示共用同一份
// SVG path（加密／密碼庫／設定三個 1:1 照抄），只有「資料夾防護」是那輪造型探索新畫的
// 盾牌圖示，取代原本的資料夾圖示——這是唯一一個刻意換掉的圖示，不是漏改。
//
// t 用 prop 傳進來（不是 import App.vue 的全域 t()），比照 vaultListProjections.js 的作法：
// 讓這個元件維持純粹、不用真的初始化 i18n 也能單獨測試。
import { ref, nextTick, onMounted, onUnmounted, watch } from 'vue'

const props = defineProps({
  collapsed: { type: Boolean, default: false },
  active: { type: String, required: true },
  t: { type: Function, required: true },
})

const emit = defineEmits(['toggle-collapse', 'navigate'])

// 圖示直接在 template 裡用 v-if 切換（4 個固定項目，沒有多到需要動態 component 對照表），
// 這裡只列出 key／label，順序就是畫面上由上到下的順序。label 直接沿用原本頂部分頁列的
// tab.* 翻譯 key（"加密"／"資料夾防護"／"密碼庫"／"設定"）——文字內容跟原本分頁一模一樣，
// 沒必要為了側欄另外新增一組重複的翻譯字串。
const navItems = [
  { key: 'encrypt', labelKey: 'tab.encrypt' },
  { key: 'folderGuard', labelKey: 'tab.folderGuard' },
  { key: 'passwordLocker', labelKey: 'tab.passwordLocker' },
  { key: 'settings', labelKey: 'tab.settings' },
]

function onNavClick(key) {
  emit('navigate', key)
}

// ---- 會滑動的作用中背景色塊：比照原本頂部分頁列被移除前的 tab-bar__indicator 量測手法
// （見 App.vue 移植紀錄），量測目前作用中 nav 項目的實際位置/高度，讓色塊用 transform 滑
// 過去，而不是切換項目時背景色直接「跳」到新項目上——使用者要求過場要「邊移動邊變色」，
// 不是移動完才變色或一開始就先跳色，所以色塊本身的背景色轉場（background-color transition）
// 刻意設成跟位移同一個時長、同一條曲線，兩者同時進行，色塊還在半路上時顏色就已經在漸變了。
const navRefs = {}
function setNavRef(key, el) {
  if (el) navRefs[key] = el
}

const highlightStyle = ref({ transform: 'translateY(0px)', height: '0px', opacity: 0 })

function updateHighlight() {
  const el = navRefs[props.active]
  if (!el) {
    highlightStyle.value = { ...highlightStyle.value, opacity: 0 }
    return
  }
  highlightStyle.value = {
    transform: `translateY(${el.offsetTop}px)`,
    height: `${el.offsetHeight}px`,
    opacity: 1,
  }
}

watch(() => props.active, () => nextTick(updateHighlight))
// 收合/展開會改變每個按鈕的 padding、進而改變高度跟位置，色塊要跟著重新量測，不然收合完
// 色塊還停在展開時量到的舊座標上。
watch(() => props.collapsed, () => nextTick(updateHighlight))

function handleResize() {
  updateHighlight()
}

onMounted(() => {
  nextTick(updateHighlight)
  window.addEventListener('resize', handleResize)
})

onUnmounted(() => {
  window.removeEventListener('resize', handleResize)
})
</script>

<template>
  <aside class="app-sidebar" :class="{ 'is-collapsed': collapsed }">
    <button
      class="app-sidebar__collapse-btn"
      type="button"
      :aria-label="props.t(collapsed ? 'sidebar.expand' : 'sidebar.collapse')"
      @click="emit('toggle-collapse')"
    >
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M15 5 9 12l6 7"/></svg>
      <span class="label">{{ props.t('sidebar.collapse') }}</span>
    </button>

    <nav>
      <span class="app-sidebar__nav-highlight" :style="highlightStyle"></span>
      <button
        v-for="item in navItems"
        :key="item.key"
        :ref="(el) => setNavRef(item.key, el)"
        type="button"
        class="app-sidebar__nav-item"
        :class="{ 'is-active': active === item.key }"
        :data-label="props.t(item.labelKey)"
        @click="onNavClick(item.key)"
      >
        <svg v-if="item.key === 'encrypt'" viewBox="0 0 24 24" fill="none"><path d="M6 10V8a6 6 0 1 1 12 0v2" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/><rect x="4" y="10" width="16" height="11" rx="2.5" stroke="currentColor" stroke-width="1.8"/><circle cx="12" cy="15" r="1.6" fill="currentColor"/></svg>
        <svg v-else-if="item.key === 'folderGuard'" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round"><path d="M12 3 4 6v6c0 5 3.5 7.7 8 9 4.5-1.3 8-4 8-9V6l-8-3Z"/></svg>
        <svg v-else-if="item.key === 'passwordLocker'" viewBox="0 0 24 24" fill="none"><circle cx="8" cy="8" r="4.25" stroke="currentColor" stroke-width="1.8"/><path d="M11 11l9.5 9.5M16.5 15.5l3-3M19 18l2.5-2.5" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>
        <svg v-else viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="3" stroke="currentColor" stroke-width="1.7"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg>
        <span class="label">{{ props.t(item.labelKey) }}</span>
      </button>
    </nav>
  </aside>
</template>

<style scoped>
/* 側欄改成獨立浮動的面板（craft 走查回饋：不要緊貼視窗邊緣的直角矩形）——四邊都留出
   margin，四個角都是圓角，靠淡淡的陰影跟旁邊的主內容區分出「這是浮在上面的一塊材質」，
   不是版面本身天生就切成兩塊直角區域。 */
.app-sidebar {
  width: 200px;
  flex-shrink: 0;
  margin: 10px 0 10px 10px;
  border-radius: 16px;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  box-shadow: var(--shadow-sm);
  padding: 14px 10px;
  display: flex;
  flex-direction: column;
  gap: 2px;
  transition: width 220ms var(--ease-out);
  overflow: hidden;
}

.app-sidebar.is-collapsed {
  width: 60px;
  padding: 14px 8px;
}

.app-sidebar__collapse-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  border: 1px solid transparent;
  background: none;
  color: var(--color-text-tertiary);
  font: inherit;
  font-size: 12px;
  font-weight: 600;
  padding: 7px 8px;
  border-radius: var(--radius-sm);
  cursor: pointer;
  margin-bottom: 14px;
  transition: background-color 150ms ease, color 150ms ease;
  white-space: nowrap;
  width: 100%;
}

.app-sidebar__collapse-btn:hover {
  background: var(--color-border);
  color: var(--color-text);
}

.app-sidebar__collapse-btn svg {
  width: 15px;
  height: 15px;
  flex-shrink: 0;
  transition: transform 220ms var(--ease-out);
}

.app-sidebar.is-collapsed .app-sidebar__collapse-btn svg {
  transform: rotate(180deg);
}

.app-sidebar.is-collapsed .app-sidebar__collapse-btn {
  justify-content: center;
}

.app-sidebar__collapse-btn .label {
  overflow: hidden;
  text-overflow: ellipsis;
}

.app-sidebar.is-collapsed .app-sidebar__collapse-btn .label {
  display: none;
}

nav {
  position: relative;
}

/* 會滑動的作用中背景色塊——脫離文件流疊在 nav 項目後面（z-index:0，項目本身 z-index:1），
   位置/高度由 script 量測後透過 inline style 更新，位移用 transform（GPU 合成、不觸發
   layout），背景色用 background-color 轉場，兩者同一個 350ms 曲線同時跑，才會是「邊移動
   邊變色」而不是先跳色再滑，或滑完才變色。 */
.app-sidebar__nav-highlight {
  position: absolute;
  left: 0;
  right: 0;
  top: 0;
  border-radius: var(--radius-sm);
  background: var(--color-accent-soft);
  /* 回饋：選中項目跟背景色對比不夠，色塊邊緣加一圈比強調色再深一點的邊框，讓選中狀態
     的輪廓更明確，不只是靠底色的淡淡色差辨識。 */
  border: 1px solid var(--color-accent-hover);
  z-index: 0;
  pointer-events: none;
  /* 彈性曲線比照舊版頂部分頁列的滑動指示條（移植前就是這個數值，見 Phase 1 移植紀錄），
     位移／高度用會輕微過衝再回彈的曲線，不是單純減速停下——呼應 apple-design 的原則：
     動畫本身要有物理感，不是機械式的線性到達。background-color 維持平滑曲線，顏色本身
     過衝沒有意義（沒有「顏色的動能」這種東西）。 */
  transition: transform 380ms cubic-bezier(0.34, 1.56, 0.64, 1), height 380ms cubic-bezier(0.34, 1.56, 0.64, 1), background-color 350ms var(--ease-out), opacity 200ms ease;
}

@media (prefers-reduced-motion: reduce) {
  .app-sidebar__nav-highlight {
    transition: opacity 150ms ease;
  }
}

.app-sidebar__nav-item {
  appearance: none;
  border: none;
  width: 100%;
  text-align: left;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 8px 10px;
  border-radius: var(--radius-sm);
  background: none;
  color: var(--color-text-secondary);
  font: inherit;
  font-weight: 500;
  font-size: 13.5px;
  white-space: nowrap;
  position: relative;
  z-index: 1;
  cursor: pointer;
  /* 文字/圖示顏色跟著色塊同一個時長漸變，不是 class 一切就瞬間換色——色塊還在半路上滑的
     時候，文字顏色也正在同步往目標色淡入淡出，兩者看起來是同一個過場動作的兩個面向。 */
  transition: color 350ms var(--ease-out);
}

.app-sidebar__nav-item svg {
  width: 16px;
  height: 16px;
  stroke-width: 1.7;
  flex-shrink: 0;
}

.app-sidebar__nav-item.is-active {
  color: var(--color-accent);
  font-weight: 600;
}

.app-sidebar__nav-item:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: -4px;
  border-radius: var(--radius-sm);
}

.app-sidebar.is-collapsed .app-sidebar__nav-item {
  justify-content: center;
  padding: 9px 0;
}

.app-sidebar.is-collapsed .app-sidebar__nav-item .label {
  display: none;
}

.app-sidebar.is-collapsed .app-sidebar__nav-item::after {
  content: attr(data-label);
  position: absolute;
  left: calc(100% + 10px);
  top: 50%;
  transform: translateY(-50%) scale(0.96);
  background: var(--color-text);
  color: var(--color-surface);
  font-size: 12px;
  font-weight: 500;
  padding: 5px 9px;
  border-radius: 6px;
  white-space: nowrap;
  opacity: 0;
  pointer-events: none;
  transition: opacity 140ms ease, transform 140ms ease;
  z-index: 20;
}

.app-sidebar.is-collapsed .app-sidebar__nav-item:hover::after {
  opacity: 1;
  transform: translateY(-50%) scale(1);
}
</style>
