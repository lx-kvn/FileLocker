<script setup>
// 密碼庫清單頁的「筆記本」視覺——只負責呈現跟局部動畫狀態，所有實際資料（清單本身、
// 搜尋/排序結果、密碼是否已顯示、TOTP 是否已顯示、多選狀態……）都由 App.vue 透過 props
// 傳進來，使用者操作一律用 emit 丟回去，跟 EnvelopeEncrypt.vue／AppSidebar.vue 同一種
// 分工：邏輯留在 App.vue，元件只管畫面。排版數值原本照抄
// design-exploration/gui-styles-v2/11-notebook-password-locker.html、14-notebook-in-shell.html
// 的量測結果，但那兩份 mockup 用的是固定 px——這份正式元件改用 CSS container query 單位
// （cqw），理由見下方 <style> 開頭註解。
import { ref, computed, watch, onMounted, onUnmounted } from 'vue'
import notebookBodyUrl from '../assets/Notebook_Body.svg'
import notebookBodyDarkUrl from '../assets/Notebook_Body_Drack.svg' // 使用者原始命名如此，不是 Dark 的筆誤
import notebookTabWebsiteUrl from '../assets/Notebook_Tab_Website.svg'
import notebookTabFileUrl from '../assets/Notebook_Tab_EncryptedFile.svg'
import notebookPocketWatchUrl from '../assets/Notebook_Pocket_Watch.svg'
import { totpSecondsRemaining } from '../totp.js'

const props = defineProps({
  websiteItems: { type: Array, default: () => [] },
  fileItems: { type: Array, default: () => [] },
  activeCategory: { type: String, default: 'website' }, // 'website' | 'file'
  searchQuery: { type: String, default: '' },
  selectedIds: { type: Set, default: () => new Set() },
  visibleIds: { type: Set, default: () => new Set() },
  revealedPasswords: { type: Object, default: () => ({}) },
  revealedTotps: { type: Object, default: () => ({}) },
  usernameVisibleIds: { type: Set, default: () => new Set() },
  revealedUsernames: { type: Object, default: () => ({}) },
  sortMode: { type: String, default: 'alphabetical' },
  isDark: { type: Boolean, default: false },
  hasSelection: { type: Boolean, default: false },
  selectedCount: { type: Number, default: 0 },
  isLoading: { type: Boolean, default: false },
  // 跟既有 passwordLockerDisplayTitle 同簽章，直接把 App.vue 那個純函式傳進來用，不在這裡
  // 重寫一次「從 associatedDomains 組標題摘要」的邏輯——單一真相來源留在呼叫端。
  displayTitleFn: { type: Function, required: true },
  t: { type: Function, required: true },
})

const emit = defineEmits([
  'update:search',
  'update:sort',
  'select-category',
  'toggle-select',
  'toggle-password',
  'toggle-username',
  'toggle-totp',
  'copy-password',
  'edit',
  'add',
  'associate',
  'refresh',
  'cancel-selection',
  'delete-selected',
])

const notebookBodySrc = computed(() => (props.isDark ? notebookBodyDarkUrl : notebookBodyUrl))

const activeItems = computed(() => (props.activeCategory === 'file' ? props.fileItems : props.websiteItems))

// 真分頁——一頁固定額度的行數，超過就翻頁，不是把全部項目一次塞進畫布讓它溢出印刷線範圍
// 外面（這是實測回報的「密碼數量到一定數量就會換頁的功能沒有做」問題）。13 這個數字是
// entry-list 從第 2 條線開始，扣掉底部要留給分頁器（.pager，跟 mockup 一樣定位在 top:86%）
// 的空間後，量出來還放得下、視覺上不會太擁擠的行數，純粹前端分頁，不是後端 API 分頁
// （見規劃：後端 listPasswordLocker 本來就是一次回傳全部項目，見 App.vue 的
// passwordLockerWebsiteItems/FileItems computed，這裡只是把已經拿到的完整陣列切片顯示）。
const ROWS_PER_PAGE = 13
const currentPage = ref(1)

// 切換分類／搜尋條件改變，或清單本身筆數變動（例如換頁排序、刪除項目）時，如果現在停留的
// 頁碼已經超出新的總頁數，要退回最後一頁，不能讓畫面停在一頁空白——同時，只要「篩選/分類
// 條件」本身改變（不是同一份清單只是筆數微調），就該直接跳回第 1 頁，不然使用者切換分類
// 後還停在第 87 個項目在的那一頁的體感很怪。
watch(() => [props.activeCategory, props.searchQuery], () => {
  currentPage.value = 1
})

const totalPages = computed(() => Math.max(1, Math.ceil(activeItems.value.length / ROWS_PER_PAGE)))

watch(totalPages, (pages) => {
  if (currentPage.value > pages) currentPage.value = pages
})

const pagedItems = computed(() => {
  const start = (currentPage.value - 1) * ROWS_PER_PAGE
  return activeItems.value.slice(start, start + ROWS_PER_PAGE)
})

function goToPrevPage() {
  if (currentPage.value > 1) currentPage.value -= 1
}
function goToNextPage() {
  if (currentPage.value < totalPages.value) currentPage.value += 1
}

// 元件自己的一顆每秒 tick，只用來讓懷錶扇形（.totp-pie）平滑消耗——跟 App.vue 那顆負責
// 「重新抓取驗證碼本身」的計時器（passwordLockerTotpRefreshTimer）是分開的兩件事：
// 驗證碼字串變動要靠父層重新 reveal，扇形的視覺消耗則是元件自己就能算,不需要每秒都
// 麻煩父層重新傳一次 props。
const nowTick = ref(Date.now())
let tickTimer = null
onMounted(() => {
  tickTimer = setInterval(() => { nowTick.value = Date.now() }, 1000)
})
onUnmounted(() => {
  if (tickTimer) clearInterval(tickTimer)
})

// 扇形代表「已經消耗掉多少」，從 12 點鐘方向開始，順時針把扇形吃掉——跟一般計時器/
// Google Authenticator 的慣例一致：一開始是滿的實心圓，隨時間過去，缺口從 12 點鐘
// 順時針長出來，缺口越來越大，剩下的實心部分越來越小，最後整圈消耗完。
// conic-gradient 的角度預設就是「從 12 點鐘開始、順時針遞增」，所以缺口（transparent）
// 用 elapsed（已消耗比例，0→1）算，剩下的實色部分自然會用順時針的方向縮小，不是逆時針。
function totpPieRatio(period) {
  const remaining = totpSecondsRemaining(period, nowTick.value)
  return 1 - remaining / period // elapsed
}

// 改成剩下 1/3 圈就變警告色，不是固定秒數——固定 5 秒對短週期（例如某些 TOTP 設定用
// 15 秒週期）警告時間太短，幾乎跟過期同時發生；改成比例門檻，不管週期長短都是「剩最後
// 三分之一」才轉色，跟扇形本身「剩多少畫多少」的比例語言一致，不是另外訂一套獨立的
// 秒數邏輯。這裡不能再沿用 App.vue totpRingStyle() 的 5 秒常數了（那是固定秒數的
// 舊邏輯，這裡刻意改成比例制，兩邊不用同一個判斷方式）。
const TOTP_WARNING_RATIO_THRESHOLD = 1 / 3
function isTotpWarning(period) {
  return totpSecondsRemaining(period, nowTick.value) / period <= TOTP_WARNING_RATIO_THRESHOLD
}

function formatTotpCode(code) {
  if (!code || code.length < 4) return code || ''
  const mid = Math.ceil(code.length / 2)
  return `${code.slice(0, mid)} ${code.slice(mid)}`
}

function onSelectCategory(category) {
  emit('select-category', category)
}
</script>

<template>
  <div class="notebook-outer">
    <img class="notebook-body" :src="notebookBodySrc" alt="" />

    <!-- 直接照抄 14-notebook-in-shell.html 已經驗證過的結構：標題＋工具列都在同一個
         .page-header 裡用文件流排列（標題 margin-bottom 帶出間距，工具列緊接在後面），
         不是像上一版那樣另外用 position:absolute 自己算工具列要放第幾條線——mockup 這樣
         排本來就對得整整齊齊，是我自己另外重新計算位置時算錯/累積誤差，不是這個排版方式
         本身有問題。第一行（搜尋＋新增/關聯/重新整理，選取模式換成取消/刪除選取）欄位跟
         mockup 一模一樣；mockup 沒有排序下拉，這裡多一行放排序，是唯一跟 mockup 不同的地方
         （mockup 的清單沒有排序功能）。 -->
    <div class="page-header">
      <h1 class="nb-title">{{ t('tab.passwordLocker') }}</h1>
      <div class="toolbar">
        <input
          class="search-box"
          :value="searchQuery"
          @input="$emit('update:search', $event.target.value)"
          :placeholder="t('passwordLocker.searchPlaceholder')"
          :title="t('passwordLocker.searchPlaceholder')"
        />
        <template v-if="!hasSelection">
          <button class="toolbar-btn toolbar-btn--add primary" type="button" @click="$emit('add')">{{ t('passwordLocker.addButton') }}</button>
          <button class="toolbar-btn toolbar-btn--associate" type="button" @click="$emit('associate')">{{ t('passwordLocker.associateButton') }}</button>
          <button class="toolbar-btn toolbar-btn--refresh" type="button" :disabled="isLoading" @click="$emit('refresh')">
            {{ isLoading ? t('list.loading') : t('list.refresh') }}
          </button>
        </template>
        <template v-else>
          <button class="toolbar-btn toolbar-btn--cancel-selection" type="button" @click="$emit('cancel-selection')">{{ t('passwordLocker.cancelSelectionButton') }}</button>
          <button class="toolbar-btn toolbar-btn--delete-selected danger" type="button" @click="$emit('delete-selected')">
            {{ t('passwordLocker.deleteSelectedButton') }} ({{ selectedCount }})
          </button>
        </template>
      </div>
    </div>

    <!-- 排序下拉獨立定位在 entry-list 上方那條保留給它的印刷線正中間（跟 entry-row 用同一個
         height:var(--nb-line-h)+align-items:center 置中規則），不是掛在 .page-header 底下
         用文件流的 margin-top 隨便估一個位置——之前那樣做，位置跟線對不準。 -->
    <div class="toolbar toolbar--secondary">
      <select
        class="toolbar-btn sort-select"
        :value="sortMode"
        @change="$emit('update:sort', $event.target.value)"
      >
        <option value="alphabetical">{{ t('passwordLocker.sortAlphabetical') }}</option>
        <option value="time">{{ t('passwordLocker.sortTime') }}</option>
      </select>
    </div>

    <div v-if="activeItems.length === 0" class="empty-state-block empty-state-block--notebook">
      <p class="empty-state-block__text">{{ searchQuery ? t('passwordLocker.noSearchResults') : t('passwordLocker.noItems') }}</p>
    </div>

    <div v-else class="entry-list">
      <div v-for="item in pagedItems" :key="item.id" class="entry-row">
        <span class="entry-select checkbox-ring">
          <input
            type="checkbox"
            :checked="selectedIds.has(item.id)"
            @change="$emit('toggle-select', item.id)"
          />
        </span>

        <div class="entry-main">
          <span class="entry-cat" :class="item.category === 'EncryptedFile' ? 'file' : 'website'"></span>
          <span class="entry-title" :class="{ 'text-strikethrough': item.sourceDeleted }">{{ displayTitleFn(item) }}</span>
        </div>

        <!-- 密碼欄位本身可以直接點擊複製，不用先按眼睛顯示明文再手動選字複製——這是換皮前
             舊表格既有的「複製」按鈕功能（見 App.vue ensurePasswordLockerVerified 的
             type:'copy' 分支），mockup 排版沒有另外畫一顆複製按鈕，改成沿用 TOTP 驗證碼
             欄位「點文字本身＝複製」的既有慣例，不是遺漏。 -->
        <div class="entry-secret">
          <span
            v-if="!(visibleIds.has(item.id) && revealedPasswords[item.id])"
            class="dots"
            role="button"
            tabindex="0"
            :title="t('passwordLocker.copy')"
            @click="$emit('copy-password', item)"
            @keydown.enter="$emit('copy-password', item)"
          >••••••••</span>
          <span
            v-else
            class="revealed"
            role="button"
            tabindex="0"
            :title="t('passwordLocker.copy')"
            @click="$emit('copy-password', item)"
            @keydown.enter="$emit('copy-password', item)"
          >{{ revealedPasswords[item.id] }}</span>
          <button
            type="button"
            class="eye-btn"
            :aria-label="t(visibleIds.has(item.id) ? 'passwordLocker.hide' : 'passwordLocker.show')"
            @click="$emit('toggle-password', item)"
          >
            <span class="icon-box">
              <svg v-if="visibleIds.has(item.id)" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
              <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </span>
          </button>
        </div>

        <div class="totp-code">{{ item.hasTotp && revealedTotps[item.id] ? formatTotpCode(revealedTotps[item.id].code) : '' }}</div>

        <div class="totp-slot">
          <div v-if="item.hasTotp && revealedTotps[item.id]" class="totp-badge">
            <img :src="notebookPocketWatchUrl" alt="" />
            <div
              class="totp-pie"
              :class="{ 'is-warning': isTotpWarning(revealedTotps[item.id].period) }"
              :style="{ '--totp-ratio': totpPieRatio(revealedTotps[item.id].period) }"
            ></div>
          </div>
          <!-- 還沒顯示驗證碼之前用「閉眼」圖示，不是懷錶素材本身——懷錶只在已經顯示、
               扇形在跑的時候才出現；未顯示狀態沿用密碼欄位「閉眼＝目前是隱藏的」同一套
               慣例語彙，讓使用者一眼看出這是「按了才會顯示」的東西，不是裝飾用的錶。 -->
          <button
            v-else-if="item.hasTotp"
            type="button"
            class="totp-reveal-btn"
            :aria-label="t('passwordLocker.totpShowButton')"
            @click="$emit('toggle-totp', item)"
          >
            <span class="icon-box">
              <svg viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </span>
          </button>
        </div>

        <div class="entry-actions">
          <button type="button" :aria-label="t('passwordLocker.editButton')" @click="$emit('edit', item)">
            <span class="icon-box">
              <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8"><path d="M12 20h9"/><path d="M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z"/></svg>
            </span>
          </button>
        </div>
      </div>
    </div>

    <div v-if="totalPages > 1" class="pager">
      <button type="button" :disabled="currentPage <= 1" @click="goToPrevPage">{{ t('passwordLocker.pagerPrev') }}</button>
      <span class="pager-num">{{ currentPage }} / {{ totalPages }}</span>
      <button type="button" :disabled="currentPage >= totalPages" @click="goToNextPage">{{ t('passwordLocker.pagerNext') }}</button>
    </div>

    <div class="cat-tabs">
      <button
        type="button"
        class="cat-tab cat-tab--website"
        :class="activeCategory === 'website' ? 'is-active' : 'is-inactive'"
        @click="onSelectCategory('website')"
      >
        <div class="cat-tab-shape"><img :src="notebookTabWebsiteUrl" alt="" /></div>
        <span class="tab-label">{{ t('passwordLocker.groupWebsite') }}</span>
      </button>
      <button
        type="button"
        class="cat-tab cat-tab--file"
        :class="activeCategory === 'file' ? 'is-active' : 'is-inactive'"
        @click="onSelectCategory('file')"
      >
        <div class="cat-tab-shape"><img :src="notebookTabFileUrl" alt="" /></div>
        <span class="tab-label">{{ t('passwordLocker.groupEncryptedFile') }}</span>
      </button>
    </div>
  </div>
</template>

<style scoped>
/* 排版數值的單位選擇：整個 .notebook-outer 是靠 `width:min(760px,100%)` 縮放的正方形容器，
   裡面的筆記本內頁圖（Notebook_Body.svg）用 object-fit:contain 會跟著容器等比縮放，但如果
   內容文字/座標繼續寫死 px，容器縮小時圖跟著縮小、文字位置卻不會跟著縮小，兩者就會對不上
   （這是實測在真正的 App 視窗裡發現的問題——視窗窄一點，畫布跟著縮小，清單就飄到內頁的
   橫線外面）。改用 CSS container query 單位 cqw（容器寬度的 1%）取代所有原本量測出來的 px
   數字：`.notebook-outer` 設 container-type:inline-size 之後，1cqw 永遠等於容器目前實際寬度
   的 1%，所有位置／字級／間距都用 cqw 表示，容器縮放時全部跟著同比例縮放，不會再跑位。
   換算方式：原始量測基準是 760px 寬的畫布（跟 14-notebook-in-shell.html mockup 一致），
   1cqw = 7.6px（760÷100），所以「原本的 px 數字 ÷ 7.6」就是換算後的 cqw 數字——下面每個
   數值旁的註解都留著原始 px 供之後比對，不要因為看不到 px 就以為數字是隨便訂的。 */
/* 縮放邏輯不再用 calc(100vh - N px) 猜視窗高度——猜了兩輪，一次太保守（畫布縮太小，下面
   留一大截空白）、一次不夠保守（.page 還是跑出捲軸），兩次都錯，因為「上面到底有多少像素
   的 chrome」本來就不該用猜的。改成父層（App.vue 的 .notebook-scale-area，flex:1 撐滿
   .password-locker-tab 扣掉標題/說明文字後的剩餘高度）直接把「真正能用的高度」透過
   flex stretch 交給這個元素：width 改成 auto，height 吃父層給的 100%，aspect-ratio:1
   讓瀏覽器從「拿到的高度」反推出對應的正方形寬度，max-width 才是唯一的上限（760px 或
   可用寬度，取小者）。不管視窗多高/多矮，這個元素永遠剛好等於瀏覽器實際量出來的可用空間，
   不會超出、也不會留白，不需要每次改動又要重新校正一個魔術數字。 */
/* 拿掉原本 760px 的寬度上限——所有排版數值都是 cqw（容器寬度的比例），放大容器不會讓
   文字跟素材比例跑掉，之前設 760px 上限只是延續 mockup 原始參考尺寸的慣性，沒有實際
   技術理由。使用者反映字太小看得辛苦，拿掉上限＋讓密碼庫分頁改用 .page--wide（見 App.vue）
   後，畫布能吃到的實際空間變大，讓 aspect-ratio 從「真正可用的高度」反推出更大的正方形。 */
.notebook-outer {
  position: relative; width: auto; height: 100%; max-width: 100%; aspect-ratio: 1;
  container-type: inline-size;
}
.notebook-body { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; }

/* --nb-ink／--nb-ink-soft／--nb-line／--paper 這幾個 token 從第一輪換皮開始就只在
   scoped style 裡用 var(--x, 淺色預設值) 的寫法，一直沒有真的在任何地方定義過這幾個
   CSS 變數——深色模式一直是靠 fallback 值撐著，也就是說深色模式下的文字/背景其實
   從來沒有真的變過色，只是剛好淺色配色在淺色背景上看得清楚，沒被發現。這裡補上深色
   模式的實際定義，數字沿用 14-notebook-in-shell.html mockup 當初就設計好、只是沒有
   真的接上的深色版本。App.vue 的深色模式開關是在根層 .app 元素加 app--dark class
   （不是 data-theme 屬性，跟 mockup 原本假設的機制不同），所以這裡對應改用
   .app--dark 當作外層選擇器，不是 html[data-theme="dark"]。 */
.app--dark .notebook-outer {
  --nb-ink: #EFE9D6; --nb-ink-soft: #B9AF8F; --nb-line: #4A4230; --paper: #242119;
}

/* 印刷格線的精確座標——直接從 Notebook_Body.svg 原始檔量出來（每一條橫線是一個獨立
   <g transform="matrix(1,0,0,1,x,y)"><path d="M...L..."/></g>，y 平移量決定那條線的高度），
   不是憑截圖用眼睛猜的：viewBox 是 1024×1024 的正方形，量到的線距（相鄰兩條線 translateY
   的差）固定是 35.81212 個 svg 單位，第一條線的絕對 Y 是 233.09696。因為畫布容器也是正方形
   （aspect-ratio:1），svg 單位可以直接線性換算成 cqw：cqw = svg單位 ÷ 1024 × 100。
   --nb-line-h 就是這個線距，--nb-line-1 是第一條線的位置，兩者是所有「要跟印刷線對齊」的
   元素（工具列、entry-row）共同的量測基準，不要各自重複硬算一次。 */
.notebook-outer { --nb-line-h: 3.4973cqw; --nb-line-1: 22.7634cqw; }

/* 這輪整段照抄 14-notebook-in-shell.html 已經視覺驗證過的數字（換算成 cqw，理由見上面的
   換算說明），不再自己重新湊排版——標題／工具列都是文件流排列（.page-header 只釘 top:13.5%
   一個定位點，裡面東西照順序疊下去），不是分開兩個各自用 position:absolute 硬算位置的區塊，
   之前那樣做反而是誤差的來源。 */
.page-header { position: absolute; left: 26%; right: 25%; top: 13.5%; }
.nb-title { font-size: 2.763cqw; font-weight: 700; color: var(--nb-ink, #3A331F); margin: 0 0 1.316cqw; line-height: 1; } /* 21px / 10px */
.toolbar { display: flex; gap: 0.921cqw; align-items: center; flex-wrap: wrap; } /* 7px */
/* top 原本設成 nb-line-1 減一行，想讓排序下拉緊貼在第一條線正上方——但實測發現第一行工具列
   （標題＋搜尋＋新增/關聯/重新整理）用文件流排下來，實際佔用的高度比這個位置低，兩者疊在
   一起（這正是使用者截圖看到「搜尋框」跟「依字母排序」重疊的原因）。改成對齊 nb-line-1
   本身（不是往上減一行），這樣第一行工具列跟排序下拉之間有量出來的實際安全間距，entry-list
   對應往下多讓一行（見下面 .entry-list 的 top），排序下拉落在這條新讓出來的線的位置。 */
.toolbar--secondary {
  position: absolute; left: 26%; right: 25%; top: var(--nb-line-1);
  height: var(--nb-line-h); align-items: center;
}
/* 90px 換算成 cqw——mockup 原話：「四個工具列項目全部塞進頁面內容可用寬度（372px）時，
   130px 的搜尋框會讓總寬度超出、被迫換行」，90px 是量過確認單行排得下的數字，不要因為
   看起來很窄就隨手改大，改大會重新踩進同一個坑。

   搜尋框跟按鈕、下拉選單這三種原生元素（input/button/select）預設的內建高度計算方式
   彼此不一樣（不同瀏覽器/作業系統的原生控制項尺寸規則本來就不保證一致），只設一樣的
   padding 數字不保證渲染出來的實際高度相同——這是使用者實測回報「輸入框跟按鈕大小不一樣」
   「下拉選單超出印刷線」的根因。改成三者共用一個明確的 --nb-control-h 高度變數＋
   box-sizing:border-box＋flex 置中內容，不再各自依賴瀏覽器的預設計算。 */
.notebook-outer { --nb-control-h: 3.421cqw; } /* 26px，量過含 padding/border 後跟原本 30px 高的視覺份量接近 */
.search-box {
  flex: 0 1 11.842cqw; min-width: 6.579cqw; height: var(--nb-control-h); background: var(--paper, #FFFDF8);
  border: 1px solid var(--nb-line, #CFC6AC); border-radius: 0.789cqw; padding: 0 1.053cqw; font-size: 1.513cqw;
  font-family: inherit; color: var(--nb-ink-soft, #7A7256); box-sizing: border-box; overflow: hidden;
  text-overflow: ellipsis; white-space: nowrap;
} /* 90/50px */
.toolbar-btn {
  height: var(--nb-control-h); display: inline-flex; align-items: center; justify-content: center;
  border: 1px solid var(--nb-line, #CFC6AC); background: var(--paper, #FFFDF8); color: var(--nb-ink, #3A331F);
  font: inherit; font-size: 1.513cqw; font-weight: 600; padding: 0 1.316cqw; border-radius: 0.789cqw; cursor: pointer;
  white-space: nowrap; box-sizing: border-box;
} /* 11.5px / 10px / 6px */
.toolbar-btn.primary { background: var(--brass, #A8770F); border-color: var(--brass, #A8770F); color: #fff; }
.toolbar-btn.danger { background: var(--void-red, #A23B2A); border-color: var(--void-red, #A23B2A); color: #fff; }
.toolbar-btn:disabled { opacity: .6; cursor: default; }
/* appearance:none 拿掉原生下拉選單的作業系統外框（Windows 原生 <select> 常常比同高度的
   <button> 渲染得更高，這正是「排序下拉超過印刷線」那個問題的根因），改用背景圖畫一個
   自訂箭頭圖示，跟其他按鈕共用同一個 --nb-control-h，高度保證一致。 */
.sort-select {
  appearance: none;
  background-image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%237A7256' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M6 9l6 6 6-6'/></svg>");
  background-repeat: no-repeat; background-position: right 0.5cqw center; background-size: 1.2cqw;
  padding-right: 2.2cqw;
}

/* top 對齊第 3 條印刷線，不是第 1 條——mockup 的清單沒有排序下拉，工具列只有一行，
   entry-list 緊接在第 1 條線（var(--nb-line-1)）就好；這份元件多了排序下拉那一行
   （.toolbar--secondary，佔用第 1 條線的位置），所以清單要再往下多讓一行，改成從
   第 3 條線開始（第 1、2 條線分別留給排序下拉、跟它跟第一行工具列之間的安全間距），
   不然排序下拉會跟第一行工具列或第一筆清單項目疊在一起——這個間距是實測抓出來的，
   不是憑感覺，見 .toolbar--secondary 旁的說明。 */
.entry-list {
  position: absolute; left: 26%; right: 25%; top: calc(var(--nb-line-1) + var(--nb-line-h) * 2);
  display: flex; flex-direction: column;
}
/* 密碼欄位跟編輯按鈕這兩欄原本用 auto（讓瀏覽器依內容自己決定寬度）——這正是使用者回報
   「三顆 icon 大小不一樣」的真正根因：grid 的 auto 欄寬要跑兩階段計算（先抓每個子項目的
   內容尺寸決定欄寬，才能定案最終版面），但子項目的尺寸又是用 cqw（容器查詢單位）算出來的，
   cqw 本身要等容器整個排版定案才有值——這兩件事互相依賴，WebView2 在這個情境下算出來的
   結果不穩定（同一份 CSS、同一個數字，量出來的寬高卻兜不起來，用 inline style 或改寫任何
   sizing 手法都繞不開，因為問題根本不在「哪條規則生效」，而是欄寬計算的時序）。跟
   totp-code／totp-slot 那兩欄同一個坑、同一個解法：改成固定 cqw 寬度，不讓瀏覽器對這欄
   做內容依賴的兩階段計算，一次到位。 */
.entry-row {
  position: relative; height: var(--nb-line-h); display: grid;
  grid-template-columns: auto 1fr 14cqw 7.105cqw 3.947cqw 4cqw; align-items: center; gap: 1.053cqw; box-sizing: border-box; /* 106px/54px/30px/30px */
}
.entry-select { display: flex; align-items: center; }
.entry-main { min-width: 0; display: flex; align-items: baseline; gap: 0.789cqw; overflow: hidden; white-space: nowrap; } /* 6px */
/* 標題文字超出可用寬度就截斷加省略號，不要讓長標題把後面的密碼/TOTP/操作欄位擠出畫面
   或跟格線右邊界重疊——min-width:0 是讓 flex 子項目能夠真的縮小到比內容還窄的必要設定，
   沒有這行 overflow:hidden/text-overflow:ellipsis 不會生效（flex 預設 min-width:auto
   會讓子項目撐開到內容原始寬度，overflow 規則形同虛設）。 */
.entry-title {
  font-weight: 650; font-size: 1.645cqw; color: var(--nb-ink, #3A331F);
  min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
} /* 12.5px */
.entry-cat { width: 0.789cqw; height: 0.789cqw; border-radius: 50%; flex-shrink: 0; display: inline-block; } /* 6px */
.entry-cat.website { background: var(--brass, #A8770F); }
.entry-cat.file { background: var(--green, #1F5C34); }

/* 不用 display:flex——這裡是「flex 包 flex 包 flex」三層巢狀（.entry-secret > .eye-btn >
   .icon-box）疑似導致 icon 尺寸算錯的那一層，改用不建立巢狀 flex 格式化環境的排版方式
   （文字用 inline-block + vertical-align 對齊），詳細診斷過程見 .eye-btn 旁邊的說明。 */
.entry-secret { display: block; white-space: nowrap; gap: 0.658cqw; font-size: 1.447cqw; color: var(--nb-ink-soft, #7A7256); } /* 5px/11px */
.entry-secret .dots, .entry-secret .revealed, .entry-secret .eye-btn { display: inline-block; vertical-align: middle; }
.entry-secret .dots, .entry-secret .revealed { margin-right: 0.658cqw; }
.entry-secret .dots, .entry-secret .revealed { letter-spacing: 0.197cqw; cursor: pointer; } /* 1.5px */
/* 密碼欄位眼睛、TOTP 眼睛（未顯示狀態）、編輯用的筆——這三顆 icon 使用者要求要一樣大，
   共用同一組尺寸變數，不要各自訂各自的數字。

   踩過的坑：直接在 <svg> 標籤上設 width/height（不管是各自 cqw、aspect-ratio 搭配 auto，
   還是 1em），實測（getBoundingClientRect／getComputedStyle 都量過）三顆算出來的寬高
   一直兜不起來，即使透過 CDP 查過完整 CSS 比對規則，確認套用的規則本身完全一致、數值
   也一致，WebView2 算出來的「用值」還是不一致——懷疑是這個引擎版本對「沒有 width/height
   屬性、只有 viewBox 的 <svg>」在特定巢狀結構下的用值計算有 bug，沒有查到官方 issue
   佐證，純粹是實測現象，多次嘗試不同 CSS 寫法都繞不過去。

   最後改成不讓瀏覽器對 <svg> 元素本身做任何尺寸計算：外面包一層 .icon-box（一般
   <span>，不是替換元素，尺寸計算沒有 SVG 那些特例），用 1em 撐出正方形，<svg> 只用
   width:100%/height:100% 填滿這個已經量好的容器——把「量多大」這件事整個從 svg 身上
   移開，改成量一個行為完全常規的 <span>，這樣才確定三顆會拿到同一個數字。 */
.notebook-outer { --nb-icon-btn: 3.4cqw; --nb-icon-svg: 3cqw; }
.icon-box { display: inline-flex; width: 1em; height: 1em; }
.icon-box svg { display: block; width: 100%; height: 100%; }
/* flex-basis 明確寫成跟 width 一樣的值，不要讓瀏覽器從 width 自動推算 flex-basis:auto——
   量出來 .eye-btn／.entry-actions button（両者都是 flex 容器底下的 flex item）跟
   .totp-reveal-btn（父層 .totp-slot 不是 flex 容器，只是 position:relative）的 icon 尺寸
   兜不起來，兩者唯一的結構差異就是「是不是 flex item」，懷疑是 flex-basis:auto 從 width
   推算的過程跟 cqw 容器查詢單位有時序上的計算落差。直接寫死 flex-basis，不讓瀏覽器自己推。 */
.eye-btn {
  width: var(--nb-icon-btn); height: var(--nb-icon-btn); font-size: var(--nb-icon-svg);
  flex: 0 0 var(--nb-icon-btn);
  border: none; background: none; cursor: pointer; display: flex; align-items: center; justify-content: center;
  color: var(--nb-ink-soft, #7A7256);
}

.totp-code {
  font-family: ui-monospace, 'IBM Plex Mono', monospace; font-size: 1.513cqw; font-weight: 700; /* 11.5px */
  color: var(--brass-deep, #8C630C); letter-spacing: 0.066cqw; white-space: nowrap; font-variant-numeric: tabular-nums; text-align: right; /* .5px */
}
.totp-slot { position: relative; height: 100%; }
.totp-badge { position: relative; width: 3.158cqw; height: 3.158cqw; display: flex; align-items: center; justify-content: center; } /* 24px，懷錶素材本身的尺寸維持不變，不受圖示統一尺寸規則影響 */
.totp-reveal-btn {
  position: relative; width: var(--nb-icon-btn); height: var(--nb-icon-btn); font-size: var(--nb-icon-svg);
  border: none; background: none; cursor: pointer; padding: 0; display: flex; align-items: center; justify-content: center;
  color: var(--nb-ink-soft, #7A7256);
}
.totp-badge img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: contain; }
/* 缺口（transparent）順時針從 12 點鐘長出來，剩下的實色是 --brass，剩不到 5 秒時整個
   實色部分改成警示色（跟 App.vue totpRingStyle() 的既有邏輯一致的判斷門檻，顏色沿用
   專案既有的 --color-danger 語意，這裡沒有現成的 CSS 變數可以直接抓，用同一組紅色數值）。 */
.totp-pie {
  position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%); width: 1.645cqw; height: 1.645cqw; /* 12.5px */
  border-radius: 50%; background: conic-gradient(transparent calc(var(--totp-ratio, 0) * 360deg), var(--brass, #A8770F) 0);
}
.totp-pie.is-warning { background: conic-gradient(transparent calc(var(--totp-ratio, 0) * 360deg), #C0392B 0); }

/* 同樣拿掉這層 flex——只有一顆按鈕，不需要 flex 排多個子項目，維持跟 .entry-secret
   同樣的修法（避免形成三層巢狀 flex），button 預設就是 inline-block，不需要額外排版。 */
/* 光拿掉 .entry-actions 這層 flex 還不夠——button 本身還是 flex，量出來還是壞的，這裡連
   button 也一起改成非 flex（text-align 置中＋line-height 撐高度），只留 .icon-box 這一層
   flex，徹底避開巢狀 flex。 */
.entry-actions { display: block; }
.entry-actions button {
  width: var(--nb-icon-btn); height: var(--nb-icon-btn); font-size: var(--nb-icon-svg);
  line-height: var(--nb-icon-btn); text-align: center;
  border: none; background: none; cursor: pointer; color: var(--nb-ink-soft, #7A7256);
  display: inline-block; padding: 0; border-radius: 0.658cqw;
}
.entry-actions .icon-box { vertical-align: middle; }

.cat-tabs { position: absolute; left: 80%; top: 26%; display: flex; flex-direction: column; gap: 1.184cqw; } /* 9px */
/* 按下要立刻有回饋（apple-design「on pointer-down, not on release」），不是只有點擊後
   切換分類這個結果——:active 縮小＋快速回彈，跟現有 is-active 的放大狀態疊在一起也要正確：
   :active 規則寫在 is-active 後面，靠 CSS 來源順序讓按壓當下優先蓋過「目前選中」的放大值，
   放開後才恢復成 is-active 的 1.07 或 is-inactive 的 1。 */
.cat-tab {
  position: relative; width: 4.474cqw; cursor: pointer; border: none; background: none; padding: 0; font: inherit;
  transform-origin: 0% 50%; transition: transform 180ms cubic-bezier(0.23,1,0.32,1), filter 200ms cubic-bezier(0.23,1,0.32,1);
} /* 34px */
.cat-tab-shape { position: relative; width: 4.474cqw; height: 15.658cqw; overflow: hidden; filter: drop-shadow(0 2px 3px rgba(0,0,0,.2)); } /* 34px/119px */
.cat-tab-shape img { position: absolute; width: 29.553cqw; height: 29.553cqw; left: -14.421cqw; top: -6.961cqw; } /* 224.6/-109.6/-52.9px */
.cat-tab .tab-label { position: absolute; left: 0.658cqw; right: 0.658cqw; top: 50%; transform: translate(-0.526cqw,-50%); text-align: center; font-size: 1.579cqw; font-weight: 700; color: #fff; writing-mode: vertical-rl; letter-spacing: 0.066cqw; white-space: nowrap; } /* 5px/-4px/12px/.5px */
.cat-tab.is-inactive { filter: saturate(.55) brightness(1.05); opacity: .88; transform: scale(1); }
.cat-tab.is-active { filter: saturate(1.15); transform: scale(1.07); }
.cat-tab:active { transform: scale(0.92); transition-duration: 80ms; }

/* 跟 .entry-row 用同一個 height:var(--nb-line-h) + align-items:center，讓文字垂直置中在
   第一行格線的高度範圍內，不是用 padding 撐出隨意的位置——原本的 padding:3.158cqw 0
   沒有對齊任何一條線，只是視覺上抓個大概。 */
.empty-state-block--notebook {
  position: absolute; left: 26%; right: 25%; top: calc(var(--nb-line-1) + var(--nb-line-h) * 2);
  height: var(--nb-line-h); display: flex; align-items: center; justify-content: center;
  text-align: center; color: var(--nb-ink-soft, #7A7256); font-size: 1.513cqw;
}
.empty-state-block--notebook .empty-state-block__text { margin: 0; }

/* 分頁器——照抄 14-notebook-in-shell.html 原本的定位（top:86%），只有超過一頁才顯示，
   不佔用畫面空間，也不需要另外去精算跟印刷線的對齊（這是控制項，不是清單內容本身）。 */
.pager { position: absolute; left: 26%; right: 25%; top: 86%; display: flex; align-items: center; justify-content: center; gap: 1.842cqw; font-size: 1.579cqw; color: var(--nb-ink-soft, #7A7256); } /* 14px/12px */
.pager button { border: none; background: none; font: inherit; color: var(--brass-deep, #8C630C); font-weight: 600; cursor: pointer; padding: 0.263cqw 0.526cqw; } /* 2px/4px */
.pager button:hover:not(:disabled) { text-decoration: underline; }
.pager button:disabled { color: var(--nb-ink-soft, #7A7256); opacity: .45; cursor: default; text-decoration: none; }
.pager-num { color: var(--nb-ink-soft, #7A7256); }
</style>
