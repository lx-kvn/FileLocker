<script setup>
import { ref, watch, computed, nextTick, onMounted, onUnmounted } from 'vue'
import { marked } from 'marked'
import DOMPurify from 'dompurify'
import { PasswordLockerPage } from '@lx-kvn/password-locker-ui'
import '@lx-kvn/password-locker-ui/style.css'
import '@fontsource/ibm-plex-sans/400.css'
import '@fontsource/ibm-plex-sans/500.css'
import '@fontsource/ibm-plex-sans/600.css'
import '@fontsource/ibm-plex-mono/400.css'
import '@fontsource/ibm-plex-mono/500.css'
import zhTW from './locales/zh-TW.json'
import en from './locales/en.json'
import lockedWaxSealUrl from './assets/Locked_Wax_Seal.svg'
import passkeyBlackUrl from './assets/Passkey_Black.svg'
import passkeyWhiteUrl from './assets/Passkey_White.svg'
import recoveryKeyBlackUrl from './assets/Recovery_Key_Black.svg'
import recoveryKeyWhiteUrl from './assets/Recovery_Key_White.svg'
import lightModeBlackUrl from './assets/Light_Mode_Black.svg'
import lightModeWhiteUrl from './assets/Light_Mode_White.svg'
import darkModeBlackUrl from './assets/Dark_Mode_Black.svg'
import darkModeWhiteUrl from './assets/Dark_Mode_White.svg'
import VaultWheelIcon from './components/VaultWheelIcon.vue'
import VaultAddFolderOverlay from './components/VaultAddFolderOverlay.vue'
import AppSidebar from './components/AppSidebar.vue'
import TicketRow from './components/TicketRow.vue'
import EnvelopeEncrypt from './components/EnvelopeEncrypt.vue'
import EnvelopeDecrypt from './components/EnvelopeDecrypt.vue'
import { sendMessage, requestMessage, resolvePending, rejectAllPending } from './composables/useIpc.js'
import { useSidebar } from './composables/useSidebar.js'
import {
  parseNestedGuardedPaths,
  formatNestedGuardedNames,
  shouldOfferNestedGuardedRetry,
} from './nestedGuardedRetry.js'
import {
  groupVaultItems,
  batchPreviewText as batchPreviewTextPure,
  nestedLockPreviewText as nestedLockPreviewTextPure
} from './vaultListProjections.js'

// ---- 多語言：目前支援繁體中文／英文，語言包放在 locales/ 底下的 JSON 檔。
// t() 找不到對應的語言檔或找不到 key 時，會退回繁體中文，再找不到就直接顯示 key 本身
// （方便開發時發現漏翻的字串）。{name} 這種花括號佔位符用來塞動態內容。
const locales = { 'zh-TW': zhTW, en }
const currentLocale = ref('zh-TW')

function t(key, params) {
  let text = locales[currentLocale.value]?.[key] ?? locales['zh-TW'][key] ?? key
  if (params) {
    for (const [paramKey, value] of Object.entries(params)) {
      text = text.replaceAll(`{${paramKey}}`, value)
    }
  }
  return text
}

// 後端（C#）失敗結果目前有兩種：新的走 errorCode／errorDetail（例如密碼錯誤、找不到紀錄這些
// 常見情境，能完整翻譯），舊的／少數還沒涵蓋到的邊界情況只有固定繁體中文的 errorMessage
// （例如搬移 Vault、存恢復金鑰檔案失敗這些）。這個函式統一處理：有 errorCode 且查得到翻譯就用
// 翻譯後的文字，查不到（或根本沒有 errorCode）就退回原本的繁體中文 errorMessage，不會讓使用者
// 看到「錯誤代碼」這種內部識別字串。
function translateError(errorCode, errorDetail, fallbackMessage) {
  if (!errorCode) {
    return fallbackMessage
  }
  let detail = errorDetail
  if (errorCode === 'LOCKED_OUT' && errorDetail) {
    detail = formatRemainingTime(parseInt(errorDetail, 10))
  }
  const key = `error.${errorCode}`
  const translated = t(key, { detail })
  return translated !== key ? translated : fallbackMessage
}

// 鎖定剩餘時間的格式，跟 LockService.FormatRemaining 的邏輯對應，但這裡依目前語言決定用詞
// （後端只給原始秒數，格式化交給前端才能配合語言顯示）。
function formatRemainingTime(seconds) {
  if (currentLocale.value === 'en') {
    return seconds >= 60 ? `${Math.ceil(seconds / 60)} minute(s)` : `${seconds} second(s)`
  }
  return seconds >= 60 ? `${Math.ceil(seconds / 60)} 分鐘` : `${seconds} 秒`
}

// ---- 自訂通知（取代原生 alert()）：原生對話框在桌面應用程式裡會顯示「localhost:5173 說」
// 這種瀏覽器痕跡，看起來完全不像原生軟體。改用畫面右下角的通知卡片，跟其他 UI 一致。
const toasts = ref([])
function showToast(message, kind = 'error') {
  const id = `${Date.now()}-${Math.random()}`
  toasts.value.push({ id, message, kind })
  setTimeout(() => {
    toasts.value = toasts.value.filter((toast) => toast.id !== id)
  }, 6000)
}
function dismissToast(id) {
  toasts.value = toasts.value.filter((toast) => toast.id !== id)
}

// 沒有這兩個監聽器的話，@click 綁定的 async 函式裡任何一個未預期的 JS 例外（或沒接
// .catch 的 rejected Promise）就是純粹靜默失敗——沒有任何畫面反應，連 console 都不一定
// 會顯眼地印出來，使用者只會覺得「按了完全沒用」，跟後端例外沒接住是同一類問題（見
// rejectAllPending 的說明），這裡是前端這一側對稱的最後一道防線。
window.addEventListener('error', (event) => {
  showToast(`發生未預期的錯誤：${event.error?.message || event.message}`)
})
window.addEventListener('unhandledrejection', (event) => {
  showToast(`發生未預期的錯誤：${event.reason?.message || event.reason}`)
})

// ---- 自訂確認對話框（取代原生 confirm()）：同樣的理由，換成跟其他彈窗一致的樣式。
// askConfirm 回傳一個 Promise，呼叫端用 await 取得使用者按了「確定」還是「取消」。
// 只適合真正的二選一、而且「取消」單純代表不做任何事的情境。永久刪除用在密碼驗證
// 通過之後（見 verifyPasswordForDeleteResult）：密碼驗證負責證明「這個人真的知道密碼」，
// 這裡負責「使用者真的要做這個不可逆的動作」的最後一道確認，兩件事分開確認。
const confirmDialogState = ref(null) // { message, confirmLabel, cancelLabel, variant, confirmIconUrl, resolve }
function askConfirm(message, options = {}) {
  return new Promise((resolve) => {
    confirmDialogState.value = {
      message,
      confirmLabel: options.confirmLabel || t('confirmDialog.defaultConfirm'),
      cancelLabel: options.cancelLabel || t('passwordPrompt.cancel'),
      variant: options.variant || 'default',
      // 目前只有「刪除所有使用紀錄」這個確認鍵同時是「觸發 Windows Hello 驗證」的按鈕，
      // 才需要在確定鍵前面帶一個 icon 表明按下去會做什麼；其餘呼叫端不帶這個選項就跟以前一樣。
      confirmIconUrl: options.confirmIconUrl || null,
      resolve
    }
  })
}
function resolveConfirmDialog(result) {
  confirmDialogState.value?.resolve(result)
  confirmDialogState.value = null
}

const activeTab = ref('list')
// 信封加密流程（Phase 2b）：不是獨立分頁，是疊在目前畫面上的懸浮層（背景模糊），
// 對應定案文件的信封比喻——「加密」這個動作本身是從清單頁彈出來的一個短暫任務，
// 不該把使用者整個導去另一個頁面，關掉信封退回的地方永遠是原本在看的清單。
const showEncryptOverlay = ref(false)
const activeListSubTab = ref('files') // 'files' | 'history'

// 分頁主題色：套在 .app 最外層（不是只套在分頁內容），因為彈窗（新增密碼、驗證、確認
// 對話框…）在模板裡是跟 .page-wrapper 平行的獨立節點，不是分頁內容的子節點，只套在分頁
// 內容上覆蓋不到彈窗按鈕。套在最外層順便讓分頁列的當前分頁指示器也跟著換色，呼應「現在在
// 哪一頁」而不是額外的不一致——加密／資料夾防護跟全域預設同一款金色，不需要對應的 class。
//
// themeTab（不是直接吃 activeTab）：顏色切換要跟 pageWidthTab 同一個時機點——tab-page
// 過渡是 mode="out-in"，舊內容淡出、新內容淡入中間有一段交疊的空窗期。如果直接綁
// activeTab，點分頁的當下顏色就立刻整個換掉，舊內容明明還在淡出、卻已經套用新分頁的顏色，
// 使用者會看到「還沒退場完的舊畫面用著新顏色」這種對不上的瞬間。延到 @before-enter（新
// 內容要進場的前一刻，也就是舊內容已經完全淡出隱形的時間點）才切換，顏色變化就會剛好對齊
// 舊畫面完全消失的瞬間。
// 「加密」（原本的清單頁，現在跟加密流程合併，見側欄殼子移植）改用全域預設金色，不需要
// 覆蓋規則；原本 list 專屬的藍色改分給「資料夾防護」（回饋：金銅黃色是加密頁的主題色，
// 藍色挪去資料夾防護頁）——class 名稱維持 theme-list 不改，只是 key 從 'list' 換成
// 'folderGuard'，避免連帶要改 CSS 自訂屬性命名跟 .theme-list 選擇器本身。
const THEME_CLASS_BY_TAB = { decrypt: 'theme-decrypt', folderGuard: 'theme-list', passwordLocker: 'theme-vault', settings: 'theme-settings' }
const themeTab = ref(activeTab.value)
// 信封疊層開著時不跟著底下分頁的主題色（例如清單頁的藍色）走——疊層本身是獨立的任務層，
// 視覺上應該維持加密／資料夾防護共用的預設金色，不是「因為底下剛好是清單頁所以變藍」這種
// 使用者感知不到因果關係的巧合。
const activeThemeClass = computed(() => showEncryptOverlay.value ? '' : (THEME_CLASS_BY_TAB[themeTab.value] || ''))

// .page 的寬度（page--wide）要延到 tab-page 過渡完全透明的瞬間才切換，
// 不能直接跟 activeTab 綁在一起——否則點分頁的當下寬度就先跳掉，
// 舊內容還沒開始淡出就已經被塞進新寬度的容器。
const pageWidthTab = ref(activeTab.value)

// ---- 側欄導覽（design-exploration/gui-styles-v2 §3.3 定案版本，取代原本頂部水平分頁列的
// 滑動指示條寫法——原本量測按鈕位置/寬度來做滑動底線的那組邏輯，換成側欄之後不再需要，
// 整批一起移除，見這輪移植計畫「Phase 1：側欄殼子＋票根清單」）。
// 側欄把 encrypt／decrypt／list 三個分頁合併成一個「加密」導覽項目：list（票根清單）是
// 這個項目的預設落地頁，encrypt／decrypt 精靈仍是原本內容、只是從清單頁的工具列按鈕進入，
// 不是各自獨立的頂層分頁。
// 側欄「檔案加密」這個項目涵蓋已加密清單跟加密 overlay 兩種畫面狀態。原本還有一個 decrypt
// 鍵，但那個畫面在解密改成信封流程之後就不存在了（沒有任何地方會把 activeTab 設成 'decrypt'），
// 留著只會讓人以為還有一個到不了的分頁。
const SIDEBAR_KEY_BY_TAB = { encrypt: 'encrypt', list: 'encrypt', folderGuard: 'folderGuard', passwordLocker: 'passwordLocker', settings: 'settings' }
const sidebarActiveKey = computed(() => SIDEBAR_KEY_BY_TAB[activeTab.value] || 'encrypt')
const { collapsed: sidebarCollapsed, toggle: toggleSidebar } = useSidebar()

function onSidebarNavigate(key) {
  if (key === 'encrypt') {
    activeTab.value = 'list'
  } else {
    activeTab.value = key
  }
}

// Esc 關閉目前開啟的彈窗——照優先權由上而下檢查哪個彈窗開著就關掉哪個，正常情況下同時間
// 只會有一個開著。恢復金鑰顯示彈窗刻意不放進來：那個彈窗本來就設計成要強制使用者先複製、
// 存檔，或確認已經抄下來才能關閉，Esc 不該是繞過這個安全機制的後門。
function handleGlobalKeydown(event) {
  if (event.key !== 'Escape') {
    return
  }
  if (folderGuardOverlayVisible.value) {
    // 金庫層 z-index 蓋過其他所有彈窗，一定排最優先——跟點外面立即取消是同一套邏輯
    // （見 onFolderGuardAddOverlayCancel），不再播剩餘的關門動畫。
    onFolderGuardAddOverlayCancel()
  } else if (showEncryptOverlay.value && encryptPhase.value === 'form') {
    // 只有還在表單階段（選檔案／填密碼）才能用 Esc 快速關掉——pending 已經送出、正在等
    // 使用者確認/取消，或已經在 committing／flying，都是「阻斷式任務」不該被 Esc 意外中斷，
    // 跟點外面關閉是同一套判斷（見 .encrypt-overlay 的 @click.self）。
    closeEncryptOverlayAndResetForm()
  } else if (showDecryptOverlay.value) {
    // 獨立解密流程比照 mockup 最終定案：不管進行到哪個階段，Esc／點外面一律直接整個關閉
    // （見 closeDecryptOverlay 的說明——這裡沒有加密流程那種「pending 已送出就不能中途
    // 關閉」的限制，因為 verify 階段本身不寫入任何檔案，隨時取消都是安全的）。
    closeDecryptOverlay()
  } else if (confirmDialogState.value) {
    resolveConfirmDialog(false)
  } else if (passwordPromptContext.value) {
    cancelPasswordPrompt()
  } else if (recoveryKeyPromptItem.value) {
    cancelRecoveryKeyPrompt()
  } else if (isHelpOpen.value) {
    isHelpOpen.value = false
  }
  // 密碼庫（PasswordLockerPage）自己的彈窗有自己的 Esc 處理邏輯，見套件內部實作，
  // 這裡不再需要幫它處理。
}

onMounted(() => {
  window.addEventListener('keydown', handleGlobalKeydown)

  // 回饋（使用者實測抓到）：已加密清單開機時是空的，要手動按「重新整理」才會跳出來——
  // activeTab 預設值本身就是 'list'，App 一開機就已經停在這個值上，下面 watch(activeTab)
  // 永遠等不到「值改變」這個觸發時機。跟前面 isRunningInWebView2 區塊裡
  // refreshPasswordLockerModuleStatus() 是同一種坑，但這裡不能放在那個區塊裡一起呼叫——
  // refreshList() 用到的 listLoadStartedAt 是這個檔案後段才宣告的 let 變數，那個區塊是
  // <script setup> 最上層、跟著整份腳本由上到下執行的一般敘述（不是等掛載後才跑的回呼），
  // 執行到那裡時 listLoadStartedAt 還沒宣告，會直接丟「Cannot access before initialization」
  // 例外。onMounted 的回呼本來就是等整份 setup() 腳本（含所有 let/const 宣告）都跑完、
  // 元件真的掛載之後才觸發，沒有這個時序問題，才是安全的呼叫時機。
  if (isRunningInWebView2) {
    refreshList()
  }
})

onUnmounted(() => {
  window.removeEventListener('keydown', handleGlobalKeydown)
})

// ---- 自訂標題列：視窗是不是最大化狀態（由 C# 那邊在視窗狀態改變時通知）----
const isWindowMaximized = ref(false)

function minimizeWindow() {
  sendMessage('windowMinimize')
}

function toggleMaximizeWindow() {
  sendMessage('windowMaximizeToggle')
}

function closeWindow() {
  sendMessage('windowClose')
}

// ---- 設定頁籤 ----
const settingsVaultPath = ref('')
const settingsLanguage = ref('zh-TW')
const settingsTheme = ref('light')
const settingsMinimizeToTrayEnabled = ref(true)
const settingsLaunchAtStartupEnabled = ref(true)
// 標題列視窗控制鈕造型：macos（現行預設）／windows-native／windows-styled 三選一，
// 見 title-bar 模板跟下面對應的 CSS（.traffic-light／.win-btn／.win-btn-styled）。
const settingsWindowControlStyle = ref('macos')
// 「關鍵操作」的 Windows Hello 驗證是否已經設定過——目前唯一用途是「清除所有使用紀錄」，
// 見 requestClearHistory。
const settingsCriticalActionConfigured = ref(false)

// 主題按鈕的圖示要跟著目前的主題換黑白版本——淺色背景配黑色線條、深色背景配白色線條，
// 不是照哪顆按鈕決定，是照「畫面現在是亮色還是深色」決定，兩顆按鈕的圖示會一起切換。
const lightModeIconUrl = computed(() => settingsTheme.value === 'dark' ? lightModeWhiteUrl : lightModeBlackUrl)
const darkModeIconUrl = computed(() => settingsTheme.value === 'dark' ? darkModeWhiteUrl : darkModeBlackUrl)
const passkeyIconUrl = computed(() => settingsTheme.value === 'dark' ? passkeyWhiteUrl : passkeyBlackUrl)
const recoveryKeyIconUrl = computed(() => settingsTheme.value === 'dark' ? recoveryKeyWhiteUrl : recoveryKeyBlackUrl)
// EnvelopeEncrypt 信封素材的深色版本判斷（§8.5 待辦）：跟上面幾個圖示同一套慣例，深色模式
// 是這裡的 settingsTheme 設定值決定，不是作業系統的 prefers-color-scheme，所以只能由
// App.vue 算好、往下傳 prop，不能讓子元件自己用 CSS media query 或 matchMedia 偵測。
const isDarkTheme = computed(() => settingsTheme.value === 'dark')
const settingsSaveMessage = ref('')
const isChangingVaultPath = ref(false)

// ---- 資料夾防護頁籤：純 ACL 存取限制，不加密，跟「加密」是完全獨立的保護機制，見
// FileLocker_資料夾防護_功能規劃.md。整個功能共用一組密碼＋選配 Passkey（不像加密每項目
//各自一組），密碼必填、Passkey 選配，密碼永遠是保底解鎖手段。 ----
const folderGuardConfigured = ref(false)
const folderGuardPasskeyEnabled = ref(false)
const folderGuardDoubleClickUnlockEnabled = ref(false)
const isTogglingFolderGuardDoubleClickUnlock = ref(false)
const folderGuardAutoRelockEnabled = ref(false)
const folderGuardAutoRelockMinutes = ref(15)
const isTogglingFolderGuardAutoRelock = ref(false)
const folderGuardItems = ref([])
const isLoadingFolderGuard = ref(false)
const folderGuardSetupPassword = ref('')

// ---- 金庫門動畫：見《資料夾防護_金庫門_定案文件.md》。轉輪圖示元件實例用路徑當 key 存放，
// 這樣才能在解鎖/上鎖/批次解鎖成功時，指名呼叫對應那一列的 spin() 播放完整旋轉動畫。
// resolveFolderGuardPick 是「新增資料夾」開門儀式專用的輕量 resolver（不套用既有的
// requestMessage/pendingResolvers 機制——那個以 response-type 字串當 key，同一時間只支援
// 一個在途請求，會跟其他呼叫 pickFolder 的地方衝突，見定案文件〈新增資料夾的開門儀式〉）。
const folderGuardWheelRefs = {}
const folderGuardOverlayVisible = ref(false)
const folderGuardOverlayRef = ref(null)
let resolveFolderGuardPick = null
let folderGuardJustAddedPath = null

function setFolderGuardWheelRef(path, el) {
  if (el) {
    folderGuardWheelRefs[path] = el
  } else {
    delete folderGuardWheelRefs[path]
  }
}
const folderGuardSetupPasswordConfirm = ref('')
// 右鍵「上鎖」在整個功能還沒設定過密碼時，會先開主視窗導引完成首次設定（見 App.xaml.cs
// HandleFolderGuardLockLaunch），這裡暫存那批路徑，設定完成後自動接著上鎖，不用使用者
// 再手動選一次資料夾。
const folderGuardPendingLockPaths = ref([])
// 加密流程撞到巢狀防護中的資料夾（見 LockService.EncryptPendingAsync 的
// FolderGuardContainsNestedGuarded 錯誤碼）、使用者要解鎖並重試時，暫存那批要解鎖的子資料夾
// 路徑，交給密碼輸入彈窗用。重試本身不需要暫存加密參數——重試直接再呼叫一次
// submitEncryptPending()，它讀的就是 encryptPaths／encryptPassword 這些表單狀態本身，而信封
// 流程失敗時會退回 form 階段、不清空這些欄位（見 cancelEncryptPending 的說明），所以重試當下
// 表單內容跟第一次送出時完全相同。

// ---- 加密頁籤：分兩步驟，第一步只選檔案/資料夾，第二步才是密碼跟進階選項——
// 兩者視覺權重差很多（一個是必經流程，一個是偶爾用得到的進階功能），分開後主線操作
// 不會被一長串表單稀釋掉。 ----
const encryptStep = ref(1) // 1 | 2
// 「下一步」「上一步」要往相反方向滑動（從哪裡來就從哪裡回去），這個狀態決定
// <Transition> 套哪一組方向性的 enter/leave class，見下面 encryptStep 那段模板。
const encryptStepDirection = ref('forward') // 'forward' | 'backward'
const encryptPaths = ref([])
const isDraggingFile = ref(false) // 拖著檔案進入視窗範圍時為 true，見 MainWindow.xaml.cs 的拖放事件說明
const encryptPassword = ref('')
const encryptPasswordConfirm = ref('')
// 密碼跟確認密碼共用同一個顯示/隱藏狀態——兩個欄位本來就是要互相核對，同時顯示比較好核對，
// 沒必要分開切換。
const showEncryptPassword = ref(false)
// 密碼／確認密碼共用同一個顯示狀態，理由同上（見 showEncryptPassword 註解）。
const showFolderGuardSetupPassword = ref(false)
const showPasswordPromptValue = ref(false)
const hint = ref('')
const enablePasskey = ref(false)
const enableRecoveryKey = ref(false)
// 對應「單檔案分散式加密」功能規劃 §3：storageMode 這個內部詞彙不直接暴露成 ref 名稱，
// 用使用者實際勾選的意圖命名（是否啟用分散式模式／存放到其他地方的資料夾），送 IPC
// 時才轉換成 StorageMode.Standalone／Vault（見 submitEncryptPending）。
const enableStandaloneMode = ref(false)
const standaloneDestinationDir = ref(null)

// 對應功能規劃 §10「沒有集中備份」的一次性風險提示：勾選當下先跳確認對話框，使用者確認
// 才真的啟用，取消就維持不勾——每次勾選都跳（不是整個 App 生命週期只跳一次），因為這個
// 風險每次使用都真實存在，跟「永久刪除確認」「關閉金鑰機制確認」這幾個既有的 askConfirm
// 用途同一個道理，不需要額外的「已經看過」持久化狀態。取消勾選不用確認，直接生效。
async function onRequestToggleStandaloneMode(checked) {
  if (!checked) {
    enableStandaloneMode.value = false
    standaloneDestinationDir.value = null
    return
  }

  const confirmed = await askConfirm(t('encrypt.standaloneModeRiskWarning'), { variant: 'danger' })
  if (confirmed) {
    enableStandaloneMode.value = true
  }
}
const recoveryKeyDisplay = ref('') // 非空字串時顯示恢復金鑰彈窗
const recoveryKeySaveState = ref('') // '' | 'saved' | 'acknowledged'

// ---- 信封加密流程：全站唯一的加密路徑。
//
// 曾經有第二套「一次到位」的舊流程（前端送 encrypt、配一條依檔案大小估算時間的假進度條），
// 在信封流程導入時保留下來當作範圍縮小的過渡，只剩巢狀資料夾防護重試還在用它。實際追查後
// 發現那條重試唯一的觸發點掛在舊流程自己的結果處理常式上，而舊流程的入口函式已經沒有任何
// 呼叫端，整條變成沒有入口的封閉迴圈——使用者撞到「內含防護中的資料夾」時只會看到一個錯誤
// 訊息，拿不到解鎖重試的引導。舊流程已整套移除，重試改接到這裡（見
// handleNestedGuardedEncrypt），加密只剩這一條路，進度也一律是後端回報的真實百分比。
const encryptPhase = ref('form') // 'form' | 'processing' | 'confirming' | 'committing' | 'flying'
const encryptRealProgressPercent = ref(0)
// 後端正在等 Windows Hello 驗證——這段期間真實進度本來就會停在原地，用這個旗標把進度文字
// 換成「等待驗證」，讓使用者知道畫面不動是在等人，不是當掉了。
const encryptWaitingPasskey = ref(false)
const encryptPendingItems = ref([]) // 這一輪 pending 完成的逐項結果 { path, uuid, success, errorMessage, note, recoveryKey }
let encryptCommitsExpected = 0
let encryptCommitsDone = 0

const encryptPendingSummary = computed(() => {
  const successItems = encryptPendingItems.value
    .filter((item) => item.success)
    .map((item) => ({ originalName: item.path.split(/[\\/]/).pop() }))
  if (successItems.length === 0) return ''
  return batchPreviewTextPure(successItems, t)
})

async function submitEncryptPending() {
  if (!encryptPassword.value || encryptPassword.value !== encryptPasswordConfirm.value) {
    showToast(t('encrypt.passwordMismatch'))
    return
  }

  const nestedLockCount = await requestNestedLockCount(encryptPaths.value)
  if (nestedLockCount > 0) {
    showToast(t('alert.nestedLockNotice', { count: nestedLockCount }), 'info')
  }

  encryptPhase.value = 'processing'
  encryptRealProgressPercent.value = 0
  encryptWaitingPasskey.value = false
  encryptPendingItems.value = []

  const isBatch = encryptPaths.value.length > 1
  sendMessage('encryptPending', {
    paths: encryptPaths.value,
    password: encryptPassword.value,
    hint: hint.value,
    enablePasskey: isBatch ? false : enablePasskey.value,
    enableRecoveryKey: isBatch ? false : enableRecoveryKey.value,
    // 對應「單檔案分散式加密」功能規劃 §3：布林值直接送，不送 StorageMode 這個內部列舉的
    // 字串名稱——C# 端只需要一個是非題（要不要用分散式模式），不需要知道這個列舉本身的存在，
    // 少一層字串對應要維護。destinationDir 是 null 就是原地取代，跟 GUI 上不勾「存放到其他
    // 地方」同一個意思，C# 端直接原樣轉成 LockService 的 destinationDir 參數。
    standaloneMode: enableStandaloneMode.value,
    destinationDir: standaloneDestinationDir.value
  })
}

function confirmEncryptPending() {
  const successItems = encryptPendingItems.value.filter((item) => item.success)
  if (successItems.length === 0) {
    // 全部失敗，沒有東西可以 commit，直接退回表單讓使用者看錯誤訊息重新來過。
    encryptPhase.value = 'form'
    return
  }
  encryptPhase.value = 'committing'
  encryptCommitsExpected = successItems.length
  encryptCommitsDone = 0
  for (const item of successItems) {
    sendMessage('commitEncrypt', { uuid: item.uuid })
  }
}

function cancelEncryptPending() {
  if (encryptPhase.value === 'confirming' || encryptPhase.value === 'committing') {
    for (const item of encryptPendingItems.value.filter((i) => i.success)) {
      sendMessage('rollbackPendingEncrypt', { uuid: item.uuid })
    }
  }
  // 定案文件 §1.8：取消後密碼欄位／勾選狀態要保留，不清空——這裡刻意不動 encryptPassword
  // 等欄位，讓使用者可以直接改個地方再送一次，不用整組重打。
  encryptPhase.value = 'form'
  encryptPendingItems.value = []
}

// 寄出飛走動畫播完才呼叫（見 EnvelopeEncrypt.vue 的 fly-away-complete emit）——對應定案文件
// §1.8「動畫播完之後自動切換到已加密清單分頁」。
function onEncryptFlyAwayComplete() {
  encryptPhase.value = 'form'
  encryptPaths.value = []
  const passwordUsed = encryptPassword.value
  const successItems = encryptPendingItems.value.filter((i) => i.success).map((i) => ({ uuid: i.uuid, path: i.path }))
  encryptPassword.value = ''
  encryptPasswordConfirm.value = ''
  hint.value = ''
  encryptPendingItems.value = []
  showEncryptOverlay.value = false
  activeTab.value = 'list'
  // 回饋（使用者實測抓到）：原本 refreshList() 跟存密碼庫的詢問是平行觸發，如果密碼庫部件
  // 已安裝且設定過密碼，會跳出「要不要存密碼」確認彈窗——那個彈窗背景會整個模糊暗化，
  // 剛好蓋住清單這時候正在播的新項目滑入動畫，使用者實際上完全看不到。改成先等這個詢問
  // 整個處理完（不管有沒有真的跳出彈窗、使用者按確認還是取消）才呼叫 refreshList()，滑入
  // 動畫才會等到背景真的乾淨了才播放。沒裝密碼庫部件／沒設定過密碼的情境，
  // offerSaveEncryptedFiles 內部很快就會提早 return（只有一兩個本機 IPC 來回，不是網路
  // 請求），幾乎不會有感覺得到的延遲，等於維持原本「立刻重新整理」的體感。
  //
  // 合併回 main 時的落差（merge fd67091）：main 的 abc323f 把密碼庫換成
  // @lx-kvn/password-locker-ui 共用元件後，這個「加密完成問要不要存密碼庫」的邏輯已經搬進
  // 元件內部（PasswordLockerPage.vue 的 offerSaveEncryptedFilesToLocker，透過 defineExpose
  // 的 offerSaveEncryptedFiles 對外呼叫），不再是 App.vue 自己的 maybeOfferSaveEncryptedFilesToLocker
  // 函式——那個函式已經被刪掉了，這裡原本還在呼叫它，合併後會直接 ReferenceError 崩潰
  // （使用者實測抓到）。改成跟舊版信封流程的 encryptBatchDone 一樣，透過
  // hiddenPasswordLockerRef（永遠掛載但隱藏的那份實例，理由見它掛載處的註解）呼叫。
  // Promise.resolve(...) 包一層是因為 ?. 在 ref 還沒掛載完成（理論上不會發生，但保守起見）
  // 時會回傳 undefined 而不是 Promise，直接對 undefined 呼叫 .finally 會拋錯。
  Promise.resolve(hiddenPasswordLockerRef.value?.offerSaveEncryptedFiles(passwordUsed, successItems)).finally(() => {
    // 使用者原本就在清單頁的話（最常見的情境：從清單頁點「加密」），上面 activeTab 賦值
    // 不會觸發 watch(activeTab)（值沒變），清單就永遠不會自動重新整理——這裡直接補呼叫一次，
    // 不能只依賴那個 watcher。
    refreshList()
  })
}
// 加密前掃描選取項目裡有沒有巢狀 .locked 檔案——純資訊性用途，數量只拿來顯示一個不擋
// 流程的提示（見 submitEncryptPending），不影響加密行為本身。
function requestNestedLockCount(paths) {
  return requestMessage('checkNestedLocks', 'nestedLockCheckResult', { paths })
}

// ---- 獨立解密流程（信封＋Sheet，定案文件 §1.11）----
// 選定的 .locked 檔案的唯讀 metadata（inspectLockedFile 查回來的），也是信封上顯示的內容。
const decryptItemInfo = ref(null) // { uuid, originalName, hint, passkeyEnabled, recoveryKeyEnabled, createdAtUtc }
const showDecryptOverlay = ref(false)
const decryptVerifyState = ref({ status: 'idle' })
const decryptCommitState = ref({ status: 'idle' })

// ---- 已加密檔案子頁籤 ----
const vaultItems = ref([])
const isLoadingList = ref(false)
// 使用者自己在這個視窗做的加密/解密/刪除，事後 VaultChangeWatcher 一定會偵測到對應的
// .meta.json 變化並推播 vaultChanged——這其實是自己操作的回音，不是真的「有別的地方
// 動了 Vault」，不該再跳一次「有更新」提示。收到 vaultChanged 時如果離最近一次本機異動
// 不到這個時間窗，就當作回音略過。2 秒是抓 VaultChangeWatcher 750ms 的全域通知 debounce
// 加上 IPC 往返的寬鬆估計。
const LOCAL_MUTATION_ECHO_WINDOW_MS = 2000
let lastLocalVaultMutationAt = 0
function markLocalVaultMutation() {
  lastLocalVaultMutationAt = Date.now()
}
const decryptingUuids = ref(new Set())
// 驗證成功、TicketRow.vue 播完撕開＋停頓＋飛走序列並 emit torn-away 之前，這個項目暫時
// 待在這個集合裡——見下面 removeVaultItem／handleTicketTornAway。
const tearingUuids = ref(new Set())
const expandedGroups = ref(new Set())
// 回饋（使用者實測抓到）：批次群組展開清單第一筆撕開時，撐開動畫會往上溢出到手風琴容器
// 邊界外，被 .ticket-group__items 的 overflow:hidden（手風琴收合動畫需要）直接切掉——
// 中間那幾筆撕開時溢出的範圍還在容器內，只有貼著容器邊緣（第一筆/最後一筆）才會被切到，
// 所以先前只測中間那筆沒抓到這個情況。解法：手風琴展開動畫播完（跟 CSS 的 280ms 對齊）
// 才放開裁切，展開動畫進行中／收合狀態下仍然維持裁切（收合需要裁切才看不到內容，這個
// 不能放，只有「已經完全展開、不會再有高度變化」這個狀態才安全放開）。
const settledGroups = ref(new Set())
const groupExpandSettleTimers = {} // batchId -> timeoutId，避免快速連續切換時前一個計時器還在跑
const GROUP_EXPAND_TRANSITION_MS = 280 // 要跟 .ticket-group__items-wrapper 的 transition duration 對齊
const decryptingBatchIds = ref(new Set())

// ---- 使用紀錄子頁籤 ----
const historyItems = ref([])
const isLoadingHistory = ref(false)


// 恢復金鑰解鎖：暫存正在處理哪一筆，等使用者輸入恢復金鑰。
const recoveryKeyPromptItem = ref(null)
const recoveryKeyPromptDestination = ref(null)
const recoveryKeyPromptMarkerPath = ref(null)
const recoveryKeyInputValue = ref('')
const recoveryKeyInputRef = ref(null)

// 密碼庫分頁元件（@lx-kvn/password-locker-ui）的兩個實例——可見分頁那份跟永遠掛載的隱藏
// 那份分開存放，見模板裡 <PasswordLockerPage> 的說明。
const passwordLockerPageRef = ref(null)
const hiddenPasswordLockerRef = ref(null)

watch(recoveryKeyPromptItem, (item) => {
  if (item) {
    nextTick(() => recoveryKeyInputRef.value?.focus())
  }
})

// 使用說明彈窗：內容比其他彈窗長很多，需要能捲動，用獨立的 modal--help 樣式處理。
const isHelpOpen = ref(false)

const isCheckingUpdate = ref(false)
const isInstallingUpdate = ref(false)
const updateCheckResult = ref(null) // null | { currentVersion, latestVersion, updateAvailable, releaseNotes, hasDownloadUrl }

// 更新彈窗獨立於通用的 askConfirm/confirmDialogState 之外——release notes 需要可捲動的
// Markdown 渲染框框，通用彈窗是給其他 9 處短句訊息用的，不能為了這一個情境改動它的樣式。
// 彈窗本身就是唯一入口（裡面的「更新」按鈕直接動手做事），不用像 askConfirm 那樣包一層
// Promise 把結果丟回呼叫端決定下一步，單純開/關布林狀態就夠。
const isUpdateModalOpen = ref(false)

function openUpdateDetailsModal() {
  isUpdateModalOpen.value = true
}

// 下載中不能被關掉——背景點擊/取消鍵都要被這個擋下來，避免使用者以為取消了、其實後端還在
// 背景繼續下載安裝（IPC 呼叫不會因為彈窗關掉就中止）。
function closeUpdateDetailsModal() {
  if (isInstallingUpdate.value) {
    return
  }
  isUpdateModalOpen.value = false
}

// GitHub release body 雖然是使用者自己 repo 發布的內容，但 marked 轉出來的 HTML 還是先過一次
// DOMPurify 淨化才用 v-html 注入，避免 Markdown 原文裡混雜的任何原始 HTML/script 被直接執行。
const renderedReleaseNotes = computed(() => {
  const raw = updateCheckResult.value?.releaseNotes || ''
  return DOMPurify.sanitize(marked.parse(raw))
})

// 密碼輸入彈窗：取代原本用瀏覽器原生 prompt() 明碼輸入密碼的做法——prompt() 的輸入框不會把
// 打字內容用點點遮起來，旁邊有人看、或畫面被錄影/遠端連線時會直接看到密碼，這裡改用跟
// 其他表單一致的遮罩密碼欄位。
// mode 額外多了五種資料夾防護用途：'folderGuardUnlock'（item）、'folderGuardUnlockAll'（無額外欄位）、
// 'folderGuardNestedEncrypt'（nestedPaths）、'folderGuardDisable'（無額外欄位）、
// 'folderGuardDisablePasskey'（無額外欄位）——這裡的密碼是資料夾防護的共用密碼，跟加密/解密用的
// 密碼是完全不同的命名空間，共用這個彈窗純粹是因為「輸入密碼」這個互動外觀一致，不代表憑證共用。
const passwordPromptContext = ref(null) // { mode: 'single' | 'batch' | 'delete' | 'folderGuardUnlock' | 'folderGuardUnlockAll' | 'folderGuardNestedEncrypt' | 'folderGuardDisable' | 'folderGuardDisablePasskey', item或group, destinationDir, nestedPaths }
const passwordPromptValue = ref('')
const passwordPromptInputRef = ref(null)

// 永久刪除送出密碼驗證的當下，passwordPromptContext 就已經被清空（跟其他模式共用同一段
// 收尾邏輯），等驗證結果回來時記不得是哪個項目——這裡另外記一份，驗證通過後用來組出
// 最終確認彈窗的訊息，最終確認完（不管確定或取消）就清掉。
const pendingDeleteItem = ref(null)

// 原生的 autofocus 屬性對 Vue 動態插入的元素不可靠——瀏覽器通常只在「這個元素是網頁一開始
// 載入時就存在」的情況下才會處理 autofocus，像這種用 v-if 動態生成的彈窗，瀏覽器常常不會
// 主動聚焦，使用者按下「還原到原始位置」之後鍵盤輸入不會自動跳進密碼欄位就是這個原因。
// 改成手動在彈窗真的顯示出來之後（nextTick，等 DOM 更新完成）呼叫 .focus()。
watch(passwordPromptContext, (context) => {
  if (context) {
    nextTick(() => passwordPromptInputRef.value?.focus())
  }
})

const isRunningInWebView2 = typeof window.chrome?.webview !== 'undefined'

// 清單頁四種不同的解密結果訊息（單筆／Passkey／恢復金鑰／批次逐項）都要做同一件事：
// 把成功解密的項目從 vaultItems 篩掉。集中成一個具名函式，之後改「篩掉」的邏輯
// （例如改成標記狀態、動畫淡出）只需要改一個地方，四個呼叫端都受益。
function removeVaultItem(uuid) {
  // 進入「撕開中」狀態，交給 TicketRow.vue 自己播完整的撕開→停頓→飛走序列（見那個檔案
  // 開頭的完整時序說明）。不用固定的 setTimeout 猜測動畫要多久才移除——時長是 TicketRow
  // 內部的動畫細節，這裡猜一個數字兩邊很容易對不上（之前就是這樣：這裡的計時器跟
  // TicketRow 實際播的動畫時長脫鉤，導致列被移出陣列、TransitionGroup 開始飛走的時候，
  // TicketRow 自己的撕開動畫其實還沒播完，兩段動畫互相打架）。改成事件驅動：TicketRow
  // 播完自己的序列才 emit torn-away，這裡收到才真的把項目從陣列篩掉，見 handleTicketTornAway。
  tearingUuids.value.add(uuid)
}

// TicketRow.vue 的撕開＋停頓＋飛走序列播完才會 emit 這個事件（見那個檔案的 onStageTransitionEnd）
// ——這時候這一列在畫面上已經完全飛出去、看不到了，才真的把它從 vaultItems 篩掉，交給
// App.vue 這層的 <TransitionGroup> 接手「其餘列往上補位」，不會跟 TicketRow 自己的飛走動畫
// 重疊打架。
function handleTicketTornAway(item) {
  tearingUuids.value.delete(item.uuid)
  vaultItems.value = vaultItems.value.filter((i) => i.uuid !== item.uuid)
}

// 對應架構審查（2026-07-27）：decryptResult／decryptByUuidResult／decryptByPasskeyResult／
// decryptByRecoveryKeyResult 四個 handler 形狀完全一致（成功→做點清理+toast success，
// 失敗→toast translateError），收斂成這一個共用函式，四個呼叫端只需要各自帶
// onSuccess／successMessage／failureFallback。其餘看起來類似但形狀其實不同的 handler
// （例如 verifyPasswordForDeleteResult 成功後接的是確認彈窗、handleDeleteRecordResult
// 多一個 blockedByNestedLocks 分支）刻意不套用這個函式，硬塞會讓介面被迫變複雜。
function handleOperationResult(data, { onSuccess, successMessage, failureFallback } = {}) {
  if (data.success) {
    onSuccess?.()
    if (successMessage) {
      showToast(successMessage, 'success')
    }
  } else {
    showToast(translateError(data.errorCode, data.errorDetail, failureFallback))
  }
}

// 對應架構審查（2026-07-26）：訊息分派從一長串 if-else 改成 { type: handler } 的對照表——
// 新增一種訊息類型變成「在這個物件裡加一個 key」，不是「在共用鏈裡插隊」，鏈不會再無限變長。
// 每個 handler 只做這一種訊息該做的事，彼此互不干擾，順序也不重要。
const messageHandlers = {
  // 後端跳出 Windows Hello 驗證視窗、阻塞等待使用者操作的期間，真實進度本來就會停在原地
  // （後端沒有在處理位元組，不會回報新的百分比）——這裡只負責換掉進度文字，讓使用者知道
  // 畫面不動是在等驗證，不是當掉了。
  encryptPasskeyVerifying(data) {
    encryptWaitingPasskey.value = !!data.verifying
  },

  // ---- 信封加密流程（Phase 2b）：pending/commit/rollback 三兄弟的回應處理，見 2a／2b 後端 ----

  encryptPendingBatchStarted() {
    encryptPendingItems.value = []
  },

  encryptProgress(data) {
    encryptRealProgressPercent.value = data.percent
  },

  encryptPendingItemResult(data) {
    let note = ''
    if (data.passkeyRequested && !data.passkeyEnabled) {
      note = t('note.passkeyNotEnabled')
    } else if (data.passkeyEnabled) {
      note = t('note.passkeyEnabled')
    }
    // errorCode／errorDetail 除了翻譯成訊息之外還要原封不動留一份：批次結束時要靠它們判斷
    // 這次失敗是不是「內含防護中的資料夾」，能不能提供解鎖並重試的引導（見
    // encryptPendingBatchDone）。只留翻譯後的字串就沒辦法再判斷了。
    encryptPendingItems.value.push({
      path: data.path,
      uuid: data.uuid,
      success: data.success,
      errorCode: data.errorCode,
      errorDetail: data.errorDetail,
      errorMessage: translateError(data.errorCode, data.errorDetail, data.errorMessage),
      note
    })
    if (data.recoveryKey) {
      recoveryKeyDisplay.value = data.recoveryKey
      recoveryKeySaveState.value = ''
    }
  },

  encryptPendingBatchDone() {
    encryptWaitingPasskey.value = false
    const anySuccess = encryptPendingItems.value.some((item) => item.success)
    if (anySuccess) {
      encryptPhase.value = 'confirming'
      return
    }

    const firstError = encryptPendingItems.value[0]
    encryptPhase.value = 'form'

    // 內含防護中的子資料夾：改成提供「解鎖並重試」的引導，不是丟一個使用者無從處理的錯誤
    // 訊息。在這裡（批次結束）判斷而不是在逐項結果裡判斷，是因為引導本身是非同步的彈窗，
    // 放在逐項結果會跟接著抵達的批次結束訊息交錯，變成彈窗跟錯誤 toast 同時出現。
    if (firstError && shouldOfferNestedGuardedRetry(firstError.errorCode, encryptPaths.value.length)) {
      handleNestedGuardedEncrypt(firstError.errorDetail)
      return
    }

    showToast(firstError ? firstError.errorMessage : t('alert.genericError', { message: '' }))
  },

  commitEncryptResult(data) {
    encryptCommitsDone++
    if (!data.success) {
      // marker 寫入失敗：維持 committing 狀態，讓使用者看到錯誤，可以手動取消（回滾）或
      // 之後再想辦法重試——不自動回滾，避免使用者還沒看清楚發生什麼事，資料就已經被清掉了。
      showToast(translateError(data.errorCode, data.errorDetail, data.errorMessage))
      return
    }
    markLocalVaultMutation()
    if (encryptCommitsDone >= encryptCommitsExpected) {
      encryptPhase.value = 'flying'
    }
  },

  rollbackPendingEncryptResult() {
    // App.vue 這端已經在 cancelEncryptPending() 當下就把畫面切回表單了，這裡不用再做什麼，
    // 純粹是既有慣例「每個請求都有對應回應」，回來確認後端真的清乾淨了。
  },

  decryptByUuidResult(data) {
    decryptingUuids.value.delete(data.uuid)
    handleOperationResult(data, {
      successMessage: t('decrypt.success', { path: data.restoredPath }),
      failureFallback: t('decrypt.failed', { error: data.errorMessage }),
      onSuccess: () => {
        removeVaultItem(data.uuid)
        markLocalVaultMutation()
      }
    })
  },

  decryptByPasskeyResult(data) {
    decryptingUuids.value.delete(data.uuid)
    handleOperationResult(data, {
      successMessage: t('alert.passkeyDecryptSuccess', { path: data.restoredPath }),
      failureFallback: t('alert.passkeyDecryptFailed', { error: data.errorMessage }),
      onSuccess: () => {
        removeVaultItem(data.uuid)
        markLocalVaultMutation()
      }
    })
  },

  decryptByRecoveryKeyResult(data) {
    decryptingUuids.value.delete(data.uuid)
    handleOperationResult(data, {
      successMessage: t('alert.recoveryKeyDecryptSuccess', { path: data.restoredPath }),
      failureFallback: t('alert.recoveryKeyDecryptFailed', { error: data.errorMessage }),
      onSuccess: () => {
        removeVaultItem(data.uuid)
        markLocalVaultMutation()
      }
    })
  },

  // ---- 獨立解密流程（信封＋Sheet）Verify/Commit/Cancel 五個 IPC 回應：跟 App.vue 其他用
  // requestMessage() 的既有慣例一樣，這裡只需要 resolvePending，實際的狀態更新/動畫觸發
  // 邏輯留在呼叫端（submitDecryptPassword／verifyDecryptPasskey／...）自己 await 完成後處理，
  // 不在這裡直接動 UI 狀態——避免同一份邏輯分裂成一半在 handler、一半在呼叫端。
  verifyDecryptPasswordResult(data) {
    resolvePending('verifyDecryptPasswordResult', data)
  },
  verifyDecryptPasskeyResult(data) {
    resolvePending('verifyDecryptPasskeyResult', data)
  },
  verifyDecryptRecoveryKeyResult(data) {
    resolvePending('verifyDecryptRecoveryKeyResult', data)
  },
  commitPendingDecryptResult(data) {
    resolvePending('commitPendingDecryptResult', data)
  },
  cancelPendingDecryptResult() {
    // 呼叫端目前是 fire-and-forget（見 closeDecryptOverlay），純粹是既有慣例「每個請求都有
    // 對應回應」，不需要在這裡做什麼。
  },

  decryptBatchStarted() {
    // totalCount 目前先不用另外存，逐項回報時直接從 vaultItems 篩掉即可。
  },

  decryptBatchItemResult(data) {
    if (data.success) {
      removeVaultItem(data.uuid)
      markLocalVaultMutation()
    }
  },

  decryptBatchDone(data) {
    // 找出這批是哪個 batchId（此時對應項目如果全部成功，vaultItems 裡已經不會再有它們了）。
    for (const batchId of decryptingBatchIds.value) {
      const stillHasItems = vaultItems.value.some((item) => item.batchId === batchId)
      if (!stillHasItems) {
        decryptingBatchIds.value.delete(batchId)
      }
    }
    decryptingBatchIds.value.clear()
    if (data.successCount < data.totalCount) {
      showToast(t('alert.batchUnlockPartial', { success: data.successCount, total: data.totalCount }))
    }
  },

  nestedLockCheckResult(data) {
    resolvePending('nestedLockCheckResult', data.count)
  },

  saveRecoveryKeyToFileResult(data) {
    if (data.success) {
      recoveryKeySaveState.value = 'saved'
    } else if (!data.cancelled) {
      showToast(translateError(data.errorCode, data.errorDetail, t('alert.saveFileFailed', { error: data.errorMessage })))
    }
  },

  inspectLockedFileResult(data) {
    resolvePending('inspectLockedFileResult', data)
  },

  error(data) {
    // 後端未預期的例外統一走這裡（見 MainWindow.OnWebMessageReceived 最外層 catch），不是
    // 那個訊息原本該回的 xxxResult 類型——任何一個 requestMessage() 呼叫如果剛好撞上，
    // 沒有這行會永遠卡住、畫面完全沒反應，見 rejectAllPending 的說明。
    rejectAllPending(data.message)
    encryptRealProgressPercent.value = 0
    encryptWaitingPasskey.value = false
    isLoadingList.value = false
    isLoadingHistory.value = false
    // 信封流程進行中途發生嚴重錯誤，退回表單頁讓使用者看得到 toast、可以重新來過——
    // 不會卡在「處理中」動畫或確認畫面上不知道發生什麼事。
    if (encryptPhase.value !== 'form') {
      encryptPhase.value = 'form'
    }
    showToast(t('alert.genericError', { message: data.message }))
  },

  pathPicked(data) {
    if (data.purpose === 'decryptPath') {
      handleDecryptPathPicked(data.path)
    } else if (data.purpose === 'decryptDestination') {
      commitPendingDecrypt(data.path)
    } else if (data.purpose === 'vaultFolder') {
      isChangingVaultPath.value = true
      sendMessage('changeVaultPath', { newPath: data.path })
    } else if (data.purpose === 'folderGuardLock') {
      // 選定資料夾只回報結果，實際發 lockFolders 的時機交給 pickFolderGuardFolder 自己收尾
      // （要等關門動畫播完、懸浮層消失才上鎖，見定案文件〈新增資料夾的開門儀式〉）。
      resolveFolderGuardPick?.({ path: data.path })
      resolveFolderGuardPick = null
    } else if (data.purpose === 'flockedDestination') {
      // 對應「單檔案分散式加密」功能規劃 §3：使用者選好「存放到其他地方」的目的地資料夾。
      // 取消（沒有選）不用特別處理——EnvelopeEncrypt.vue 只有在 standaloneDestinationDir
      // 還是 null 的時候才會觸發這個 pick，取消的話它本來就還是 null，checkbox 自然維持
      // 未勾選狀態，不需要額外的 pathPickCancelled 分支去復原什麼。
      standaloneDestinationDir.value = data.path
    } else {
      // 資料夾選擇（單選）走這裡，加到清單裡而不是取代整份清單。
      if (!encryptPaths.value.includes(data.path)) {
        encryptPaths.value.push(data.path)
      }
    }
  },

  pathsPicked(data) {
    // 加密頁籤的「選擇檔案」允許多選，選完的路徑合併進現有清單（去除重複）。
    for (const path of data.paths) {
      if (!encryptPaths.value.includes(path)) {
        encryptPaths.value.push(path)
      }
    }
  },

  vaultList(data) {
    applyAfterMinSkeletonDuration(listLoadStartedAt, () => {
      isLoadingList.value = false
      vaultItems.value = data.items
    })
  },

  vaultChanged() {
    // 剛剛才在這個視窗裡自己做過加密/解密/刪除，這則推播十之八九是那個操作的回音
    // （watcher 偵測到自己剛寫入/刪除的 .meta.json），略過、不要再叫使用者去刷新一個
    // 其實已經是最新狀態的畫面。
    if (Date.now() - lastLocalVaultMutationAt < LOCAL_MUTATION_ECHO_WINDOW_MS) {
      return
    }
    // 使用者不在清單頁的話什麼都不用做——之後切換分頁時，既有的 watch(activeTab)/
    // watch(activeListSubTab) 邏輯自然會呼叫 refreshList() 拿到最新資料。
    // 回饋：有新內容時直接更新清單，不要跳一顆「有新的內容」按鈕要使用者自己點——
    // 這裡不是使用者自己在編輯中途的內容（清單本身沒有「草稿」概念，重新整理不會弄丟
    // 使用者輸入），沒有理由讓使用者多按一次才看到新東西。
    if (activeTab.value === 'list' && activeListSubTab.value === 'files') {
      refreshList()
    }
  },

  historyList(data) {
    applyAfterMinSkeletonDuration(historyLoadStartedAt, () => {
      isLoadingHistory.value = false
      historyItems.value = data.items
    })
  },

  deleteRecordResult(data) {
    handleDeleteRecordResult(data)
  },

  async verifyPasswordForDeleteResult(data) {
    const item = pendingDeleteItem.value
    pendingDeleteItem.value = null

    if (!data.success) {
      showToast(translateError(data.errorCode, data.errorDetail, t('alert.deleteFailed', { error: data.errorMessage })))
      return
    }

    const confirmed = await askConfirm(t('confirm.deleteWarning', { name: item?.originalName ?? '' }), {
      confirmLabel: t('list.delete'),
      variant: 'danger'
    })
    if (!confirmed) {
      return
    }
    sendMessage('deleteRecord', { uuid: data.uuid })
  },

  pathPickCancelled(data) {
    if (data.purpose === 'folderGuardLock') {
      // 取消跟選定走同一套收場（都播完整關門動畫才讓懸浮層消失），path 給 null 讓
      // pickFolderGuardFolder 知道不用真的呼叫 lockFolders。
      resolveFolderGuardPick?.({ path: null })
      resolveFolderGuardPick = null
    }
  },

  settingsResult(data) {
    settingsVaultPath.value = data.vaultPath
    settingsLanguage.value = data.language
    currentLocale.value = data.language
    settingsTheme.value = data.theme
    settingsCriticalActionConfigured.value = data.criticalActionConfigured
    settingsMinimizeToTrayEnabled.value = data.minimizeToTrayEnabled
    settingsLaunchAtStartupEnabled.value = data.launchAtStartupEnabled
    settingsWindowControlStyle.value = data.windowControlStyle
  },

  setupCriticalActionResult(data) {
    resolvePending('setupCriticalActionResult', data)
  },

  verifyCriticalActionResult(data) {
    resolvePending('verifyCriticalActionResult', data)
  },

  clearHistoryResult(data) {
    if (data.success) {
      historyItems.value = []
      showToast(t('history.cleared'), 'success')
    } else {
      showToast(t('history.clearFailed'))
    }
  },

  disableCriticalActionResult(data) {
    resolvePending('disableCriticalActionResult', data)
  },

  changeVaultPathResult(data) {
    isChangingVaultPath.value = false
    if (data.success) {
      settingsVaultPath.value = data.newPath
      settingsSaveMessage.value = t('settings.vaultMoveSuccess')
    } else {
      showToast(translateError(data.errorCode, data.errorDetail, t('settings.vaultMoveFailed', { error: data.errorMessage })))
    }
  },

  updateSettingResult(data) {
    // 主題切換時畫面本身就會立刻變色，是使用者用眼睛就看得出來的變更，不需要額外文字提示；
    // 語言等其他設定沒有這種即時可見的回饋，維持原本提示。
    if (data.key === 'theme') return
    settingsSaveMessage.value = t('settings.saved')
    setTimeout(() => { settingsSaveMessage.value = '' }, 2000)
  },

  windowStateChanged(data) {
    isWindowMaximized.value = data.isMaximized
  },

  filesDropped(data) {
    // 拖放進來的檔案：合併進現有清單（去除重複），不是整份取代——使用者可能已經選了
    // 一些東西，拖放應該是「再加一些」，不是「重新開始」。這則訊息現在來自
    // HandleFilesDroppedFromWebView（見 handleFileDrop 函式），不是原生 WPF 拖放。
    activeTab.value = 'list'
    showEncryptOverlay.value = true
    for (const path of data.paths) {
      if (!encryptPaths.value.includes(path)) {
        encryptPaths.value.push(path)
      }
    }
  },

  initialPaths(data) {
    // action 由後端決定要切去哪個分頁：'folderGuardSetup' 是右鍵「上鎖」但整個資料夾防護功能
    // 還沒設定過共用密碼時的引導路徑（見 App.xaml.cs HandleFolderGuardLockLaunch）；'list'／
    // 'folderGuard'／'passwordLocker' 是系統匣選單的分頁捷徑（見 App.xaml.cs ShowMainWindow），
    // 純粹切分頁、不帶路徑；其餘（包含沒有 action 欄位的既有情境）維持原本行為，切到加密頁籤。
    if (data.action === 'folderGuardSetup') {
      activeTab.value = 'folderGuard'
      folderGuardPendingLockPaths.value = data.paths ? [...data.paths] : []
      return
    }
    if (data.action === 'list' || data.action === 'folderGuard' || data.action === 'passwordLocker') {
      activeTab.value = data.action
      return
    }
    // 正常啟動（雙擊圖示、系統匣「開啟主視窗」）沒有 action、也沒有 paths，落到這裡——
    // 只有真的帶著檔案路徑進來（右鍵選單「加密」、拖檔案到程式圖示）才跳信封，不能只要
    // 沒有 action 欄位就一律跳出來，那樣連正常開啟 App 都會被硬塞一個信封疊層。
    activeTab.value = 'list'
    if (data.paths && data.paths.length > 0) {
      showEncryptOverlay.value = true
      encryptPaths.value = [...data.paths]
    }
  },

  folderGuardListResult(data) {
    resolvePending('folderGuardListResult', data)
  },

  setupFolderGuardCredentialResult(data) {
    resolvePending('setupFolderGuardCredentialResult', data)
  },

  setupFolderGuardPasskeyResult(data) {
    resolvePending('setupFolderGuardPasskeyResult', data)
  },

  disableFolderGuardPasskeyResult(data) {
    resolvePending('disableFolderGuardPasskeyResult', data)
  },

  setFolderGuardDoubleClickUnlockResult(data) {
    resolvePending('setFolderGuardDoubleClickUnlockResult', data)
  },

  setFolderGuardAutoRelockResult(data) {
    resolvePending('setFolderGuardAutoRelockResult', data)
  },

  // 解鎖後閒置自動重新上鎖觸發時的背景推播（不是使用者在這個視窗主動觸發的操作，沒有對應的
  // pending request 可以 resolve，走跟 vaultChanged 一樣的「背景通知」模式）：跳 toast，
  // 使用者目前在資料夾防護分頁的話順便刷新清單，狀態才會立刻變回「已鎖定」，不用手動切分頁。
  folderGuardAutoRelocked(data) {
    showToast(t('folderGuard.autoRelockedToast', { count: data.paths.length }), 'info')
    if (activeTab.value === 'folderGuard') {
      refreshFolderGuardList()
    }
  },

  checkForUpdatesResult(data) {
    resolvePending('checkForUpdatesResult', data)
  },

  downloadAndInstallUpdateResult(data) {
    resolvePending('downloadAndInstallUpdateResult', data)
  },

  disableFolderGuardResult(data) {
    resolvePending('disableFolderGuardResult', data)
  },

  unlockFolderResult(data) {
    resolvePending('unlockFolderResult', data)
  },

  unlockAllFoldersResult(data) {
    resolvePending('unlockAllFoldersResult', data)
  },

  removeFolderGuardEntryResult(data) {
    resolvePending('removeFolderGuardEntryResult', data)
  },

  unlockFoldersForEncryptionResult(data) {
    resolvePending('unlockFoldersForEncryptionResult', data)
  },

  lockFoldersResult(data) {
    // 來自右鍵選單批次上鎖（首次設定完成後自動接續）、或分頁內「新增資料夾」按鈕——
    // 完成後一律重新整理清單，個別失敗的項目用 toast 提示，不中斷其他成功的項目。
    const failedCount = data.items.filter((item) => !item.success).length
    if (failedCount > 0) {
      showToast(t('folderGuard.lockPartialFailed', { count: failedCount }))
    }
    // 剛透過「新增資料夾」開門儀式加進來的那筆，等清單真的刷新完、Vue 也把新的一列渲染出來
    // 之後，才對那一列的轉輪播一段反向旋轉（呼應信封清單「新列進場、轉盤同時反向旋轉」的
    // 既有概念，見定案文件〈金庫門分頁的互動細節〉）。
    const addedPath = folderGuardJustAddedPath
    folderGuardJustAddedPath = null
    if (activeTab.value === 'folderGuard') {
      refreshFolderGuardList().then(() => {
        if (addedPath) {
          nextTick(() => {
            folderGuardWheelRefs[addedPath]?.spin('lock')
          })
        }
      })
    }
  },

}

if (isRunningInWebView2) {
  window.chrome.webview.addEventListener('message', (event) => {
    const data = event.data
    messageHandlers[data.type]?.(data)
    // 密碼庫（@lx-kvn/password-locker-ui）自己的 IPC 往返走套件內部的 pendingResolvers，
    // 這裡收到的訊息類型只要含有 "passwordLocker" 字樣（不分大小寫）就一併轉發給兩個元件
    // 實例（可見分頁跟隱藏那份都轉發，未掛載/沒在等這個類型回應的那份呼叫 handleMessage 是
    // 安全的 no-op）。**不能用區分大小寫的比對**：像 passwordLockerListResult／
    // passwordLockerModuleStatusResult 這兩個回應類型剛好是「passwordLocker」當整個字串的
    // 第一個字，開頭是小寫 p，區分大小寫比對 "PasswordLocker"（大寫 P）會漏掉這兩個——
    // 而這兩個又剛好是元件掛載時最先需要、資料完全載不出來的關鍵回應，這個坑真的踩過一次。
    if (typeof data.type === 'string' && data.type.toLowerCase().includes('passwordlocker')) {
      passwordLockerPageRef.value?.handleMessage(data.type, data)
      hiddenPasswordLockerRef.value?.handleMessage(data.type, data)
    }
  })

  // 監聽器掛好之後才要一次設定值（尤其是語言），不要等到使用者自己點進「設定」頁籤才套用——
  // 不然使用者明明上次選了英文，重開 App 卻會先看到繁體中文，要點進設定頁才切回來，體驗很怪。
  sendMessage('getSettings')
}

watch(activeTab, (tab) => {
  if (tab === 'list') {
    refreshList()
  } else if (tab === 'settings') {
    sendMessage('getSettings')
    // 設定頁裡的「資料夾防護密碼／Passkey」區塊需要 folderGuardConfigured 狀態——使用者可能
    // 直接切到設定頁、根本沒去過那個分頁，這個值會是預設的 false，錯誤顯示成「尚未設定」，
    // 所以這裡也要主動刷新一次。密碼庫的對應狀態改由 PasswordLockerPage 元件自己管理。
    refreshFolderGuardList()
  } else if (tab === 'folderGuard') {
    refreshFolderGuardList()
  }
})

watch(activeListSubTab, (subTab) => {
  if (subTab === 'files') {
    refreshList()
  } else {
    refreshHistory()
  }
})

// 骨架畫面最短顯示時間：資料回來得太快時（例如本機讀取幾乎瞬間完成），骨架只閃現幾毫秒
// 反而像個畫面雜訊、不是有意義的載入提示。這裡保證骨架至少完整顯示過一次呼吸閃爍週期，
// 資料本身跟 isLoadingList 一起延後套用（不能只延後 isLoadingList，vaultItems 只要一有內容，
// 真正的表格就會不管 isLoadingList 直接蓋過骨架顯示，兩個要綁在一起延後才有效）。
const MIN_SKELETON_DURATION_MS = 300
let listLoadStartedAt = 0
let historyLoadStartedAt = 0

function applyAfterMinSkeletonDuration(startedAt, applyFn) {
  const elapsed = Date.now() - startedAt
  const remaining = Math.max(0, MIN_SKELETON_DURATION_MS - elapsed)
  setTimeout(applyFn, remaining)
}

function refreshList() {
  isLoadingList.value = true
  listLoadStartedAt = Date.now()
  sendMessage('listVault')
}

// 分組/預覽文字的實際邏輯搬到 vaultListProjections.js（純函式，不碰任何 ref）——這裡留下的
// 薄包裝只負責把目前的 vaultItems／t 接進去，模板呼叫端不需要跟著改。
const groupedVaultItems = computed(() => groupVaultItems(vaultItems.value))

function batchPreviewText(items) {
  return batchPreviewTextPure(items, t)
}

function nestedLockPreviewText(item) {
  return nestedLockPreviewTextPure(item, t)
}

function toggleGroupExpanded(batchId) {
  if (groupExpandSettleTimers[batchId]) {
    clearTimeout(groupExpandSettleTimers[batchId])
    delete groupExpandSettleTimers[batchId]
  }
  if (expandedGroups.value.has(batchId)) {
    expandedGroups.value.delete(batchId)
    // 收合要立刻恢復裁切——收合過程本身就需要裁切才看得出「內容被收進去」的效果，
    // 不能等動畫播完才裁，那樣播的時候反而會看到內容跑到手風琴外面。
    settledGroups.value.delete(batchId)
  } else {
    expandedGroups.value.add(batchId)
    groupExpandSettleTimers[batchId] = window.setTimeout(() => {
      settledGroups.value.add(batchId)
      delete groupExpandSettleTimers[batchId]
    }, GROUP_EXPAND_TRANSITION_MS)
  }
}

function decryptGroupViaPassword(group) {
  passwordPromptContext.value = { mode: 'batch', group }
  passwordPromptValue.value = ''
}

function refreshHistory() {
  isLoadingHistory.value = true
  historyLoadStartedAt = Date.now()
  sendMessage('listHistory')
}

// ---- 資料夾防護（Folder Guard）----

async function refreshFolderGuardList() {
  isLoadingFolderGuard.value = true
  const data = await requestMessage('listFolderGuard', 'folderGuardListResult')
  isLoadingFolderGuard.value = false
  folderGuardConfigured.value = data.configured
  folderGuardPasskeyEnabled.value = data.passkeyEnabled
  folderGuardDoubleClickUnlockEnabled.value = data.doubleClickUnlockEnabled
  folderGuardAutoRelockEnabled.value = data.autoRelockEnabled
  folderGuardAutoRelockMinutes.value = data.autoRelockMinutes
  folderGuardItems.value = data.items
}

// 「雙擊已上鎖資料夾直接解鎖」開啟後會在資料夾旁邊多放一個 .lockfolder 標記檔（見
// FolderGuardUnlockMarkerFile），不需要身份驗證——單純是操作體驗開關，切換失敗也只顯示
// toast，不影響其他功能。
async function toggleFolderGuardDoubleClickUnlockAction(event) {
  const enabled = event.target.checked
  isTogglingFolderGuardDoubleClickUnlock.value = true
  const result = await requestMessage('setFolderGuardDoubleClickUnlock', 'setFolderGuardDoubleClickUnlockResult', { enabled })
  isTogglingFolderGuardDoubleClickUnlock.value = false
  if (result.success) {
    folderGuardDoubleClickUnlockEnabled.value = result.enabled
  } else {
    event.target.checked = folderGuardDoubleClickUnlockEnabled.value
    showToast(t('folderGuard.doubleClickUnlockToggleFailed'))
  }
}

// 「解鎖後閒置自動重新上鎖」開關跟分鐘數共用同一個後端方法（見 FolderGuardService.
// SetAutoRelockAsync），切換開關時分鐘數維持目前值一起送出；同樣不需要身份驗證，
// 失敗時把畫面狀態退回切換前，只顯示 toast。
async function toggleFolderGuardAutoRelockAction(event) {
  const enabled = event.target.checked
  isTogglingFolderGuardAutoRelock.value = true
  const result = await requestMessage('setFolderGuardAutoRelock', 'setFolderGuardAutoRelockResult', {
    enabled,
    minutes: folderGuardAutoRelockMinutes.value
  })
  isTogglingFolderGuardAutoRelock.value = false
  if (result.success) {
    folderGuardAutoRelockEnabled.value = result.enabled
    folderGuardAutoRelockMinutes.value = result.minutes
  } else {
    event.target.checked = folderGuardAutoRelockEnabled.value
    showToast(t('folderGuard.autoRelockToggleFailed'))
  }
}

async function updateFolderGuardAutoRelockMinutesAction(event) {
  const minutes = Number(event.target.value)
  if (!Number.isFinite(minutes) || minutes < 1) {
    event.target.value = folderGuardAutoRelockMinutes.value
    return
  }
  isTogglingFolderGuardAutoRelock.value = true
  const result = await requestMessage('setFolderGuardAutoRelock', 'setFolderGuardAutoRelockResult', {
    enabled: folderGuardAutoRelockEnabled.value,
    minutes
  })
  isTogglingFolderGuardAutoRelock.value = false
  if (result.success) {
    folderGuardAutoRelockMinutes.value = result.minutes
  } else {
    event.target.value = folderGuardAutoRelockMinutes.value
    showToast(t('folderGuard.autoRelockToggleFailed'))
  }
}

async function submitFolderGuardSetup() {
  if (!folderGuardSetupPassword.value) {
    showToast(t('folderGuard.passwordRequired'))
    return
  }
  if (folderGuardSetupPassword.value !== folderGuardSetupPasswordConfirm.value) {
    showToast(t('folderGuard.passwordMismatch'))
    return
  }

  await requestMessage('setupFolderGuardCredential', 'setupFolderGuardCredentialResult', {
    password: folderGuardSetupPassword.value
  })
  folderGuardSetupPassword.value = ''
  folderGuardSetupPasswordConfirm.value = ''
  folderGuardConfigured.value = true
  showToast(t('folderGuard.passwordSetupSuccess'), 'success')

  // 右鍵「上鎖」在還沒設定過密碼時，是先開這個分頁導引完成設定，設定完成後要接著把當初
  // 選取的那批資料夾真的上鎖，不用使用者自己再選一次（見 initialPaths 的 folderGuardSetup 分支）。
  if (folderGuardPendingLockPaths.value.length > 0) {
    sendMessage('lockFolders', { paths: folderGuardPendingLockPaths.value })
    folderGuardPendingLockPaths.value = []
  }

  refreshFolderGuardList()
}

async function setupFolderGuardPasskeyAction() {
  const result = await requestMessage('setupFolderGuardPasskey', 'setupFolderGuardPasskeyResult')
  if (result.success) {
    folderGuardPasskeyEnabled.value = true
    showToast(t('folderGuard.passkeySetupSuccess'), 'success')
  } else {
    showToast(t('folderGuard.passkeySetupFailed'))
  }
}

// 只停用 Passkey、保留密碼，一樣要先驗證身份——但這裡刻意保留「Passkey 驗證失敗就退回密碼」的
// fallback，跟其他四個驗證點（Passkey 已設定就只認 Passkey）不一樣：這顆按鈕本來就是 Passkey
// 硬體出問題時的逃生門，如果連這裡都不能退回密碼，使用者就真的被鎖死了。
async function disableFolderGuardPasskeyAction() {
  const confirmed = await askConfirm(t('folderGuard.passkeyDisableConfirm'), { variant: 'danger' })
  if (!confirmed) {
    return
  }
  const result = await requestMessage('disableFolderGuardPasskey', 'disableFolderGuardPasskeyResult', {})
  if (result.success) {
    folderGuardPasskeyEnabled.value = false
    showToast(t('folderGuard.passkeyDisabled'), 'success')
    return
  }
  passwordPromptContext.value = { mode: 'folderGuardDisablePasskey' }
  passwordPromptValue.value = ''
}

async function checkForUpdatesAction() {
  isCheckingUpdate.value = true
  updateCheckResult.value = null
  const result = await requestMessage('checkForUpdates', 'checkForUpdatesResult', {})
  isCheckingUpdate.value = false
  if (result.success) {
    updateCheckResult.value = result
    // 發現新版本就直接跳出彈窗，不用讓使用者自己往下滑再手動點按鈕才看得到結果。
    if (result.updateAvailable) {
      isUpdateModalOpen.value = true
    }
  } else {
    showToast(translateError(result.errorCode, result.errorDetail, t('settings.updateCheckFailed')))
  }
}

function openReleasesPageAction() {
  sendMessage('openReleasesPage', {})
}

// 彈窗裡的「更新」按鈕點下去就是唯一的確認動作，不用再跳第二次確認彈窗。
async function installUpdateAction() {
  isInstallingUpdate.value = true
  const result = await requestMessage('downloadAndInstallUpdate', 'downloadAndInstallUpdateResult', {})
  isInstallingUpdate.value = false
  if (!result.success) {
    showToast(translateError(result.errorCode, result.errorDetail, t('settings.updateDownloadFailed')))
  }
  isUpdateModalOpen.value = false
  // 成功的話後端會自己呼叫 Application.Current.Shutdown()，視窗接下來就會直接關掉，
  // 這裡關彈窗純粹是為了失敗時能回到設定頁看 toast。
}

async function disableFolderGuardAction() {
  const confirmed = await askConfirm(t('folderGuard.disableConfirm'), { variant: 'danger' })
  if (!confirmed) {
    return
  }

  // Passkey 已設定就只能用 Passkey，失敗/取消不會退回密碼輸入框——要改用密碼得先去設定頁
  // 停用 Passkey（disableFolderGuardPasskeyAction）。
  if (folderGuardPasskeyEnabled.value) {
    const result = await requestMessage('disableFolderGuard', 'disableFolderGuardResult', {})
    if (result.success) {
      folderGuardConfigured.value = false
      folderGuardPasskeyEnabled.value = false
      showToast(t('folderGuard.disabled'), 'success')
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.disableFailed')))
    }
    return
  }
  passwordPromptContext.value = { mode: 'folderGuardDisable' }
  passwordPromptValue.value = ''
}

// 「新增資料夾」的開門儀式（定案文件〈新增資料夾的開門儀式〉）：門開完才彈原生選資料夾
// 對話框，選定／取消都播完整關門動畫才讓懸浮層消失，選定的話不需要密碼、立即自動保護。
// pickFolder/pathPicked 是廣播式訊息、不是 requestMessage 那種一對一 request/response，
// 所以用 resolveFolderGuardPick 這個模組層級變數手動接起「使用者選完或取消了」這個時機。
// folderGuardAddCancelled 是點外面立即取消用的旗標——onFolderGuardAddOverlayCancel 會直接
// 把懸浮層關掉，這裡只要在每個 await 之後檢查這個旗標，發現已經被取消就直接 return，
// 不要再繼續發任何後續的 IPC 訊息（懸浮層都已經不在了，繼續做只是白工）。
let folderGuardAddCancelled = false

async function pickFolderGuardFolder() {
  folderGuardAddCancelled = false
  folderGuardOverlayVisible.value = true
  await nextTick()
  await folderGuardOverlayRef.value?.playOpen()
  if (folderGuardAddCancelled) {
    return
  }
  sendMessage('pickFolder', { purpose: 'folderGuardLock' })
  const { path } = await new Promise((resolve) => {
    resolveFolderGuardPick = resolve
  })
  if (folderGuardAddCancelled) {
    return
  }
  await folderGuardOverlayRef.value?.playClose()
  folderGuardOverlayVisible.value = false
  if (path) {
    // 新列進場動畫（轉盤反向旋轉）在 lockFoldersResult 收到成功回應、列表刷新完成後才觸發，
    // 見 messageHandlers.lockFoldersResult。
    folderGuardJustAddedPath = path
    sendMessage('lockFolders', { paths: [path] })
  }
}

// 點金庫懸浮層背景（圖示以外的地方）＝立即取消，不管目前播到開門還是關門的哪個階段，
// 直接把懸浮層關掉（連同還沒播完的轉盤/門扇動畫一起中斷），不再像之前那樣還要等一段完整
// 的關門動畫播完才收場——這是這個專案「動畫一定播完」慣例的刻意例外，使用者主動要求立刻
// 關掉時，乾脆俐落比動畫完整度更重要。
function onFolderGuardAddOverlayCancel() {
  folderGuardAddCancelled = true
  folderGuardOverlayVisible.value = false
  resolveFolderGuardPick?.({ path: null })
  resolveFolderGuardPick = null
}

function onFolderGuardWheelIconClick(item) {
  // 點轉盤：小幅轉動立刻彈回的即時觸感回饋，接著沿用既有「解鎖」按鈕的驗證邏輯，不重寫。
  folderGuardWheelRefs[item.path]?.wiggle()
  unlockFolderGuardItem(item)
}

function playFolderGuardUnlockAnimation(path) {
  return folderGuardWheelRefs[path]?.spin('unlock') ?? Promise.resolve()
}

// 「全部解鎖」批次動畫：後端只回單一聚合結果，沒有逐筆事件（見定案文件），這裡前端自己
// 記住觸發前的鎖定路徑清單，用 setTimeout 錯開 80-120ms 依序播放每列的完整旋轉。
function playFolderGuardBatchUnlockAnimation(paths) {
  const STAGGER_MS = 100
  return Promise.all(
    paths.map(
      (path, index) =>
        new Promise((resolve) => {
          window.setTimeout(() => {
            playFolderGuardUnlockAnimation(path).then(resolve)
          }, index * STAGGER_MS)
        })
    )
  )
}

async function unlockFolderGuardItem(item) {
  // Passkey 已設定就只能用 Passkey，不用先跳密碼輸入框——跟 .locked 檔案的既有互動模式一致
  // （規格文件 14.4 節）；失敗/取消不會退回密碼輸入，要改用密碼得先去設定頁停用 Passkey。
  if (folderGuardPasskeyEnabled.value) {
    const result = await requestMessage('unlockFolder', 'unlockFolderResult', {
      path: item.path, keepInListAsUnlocked: true
    })
    if (result.success) {
      showToast(t('folderGuard.unlockSuccess'), 'success')
      await playFolderGuardUnlockAnimation(item.path)
      refreshFolderGuardList()
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.unlockFailed')))
    }
    return
  }
  passwordPromptContext.value = { mode: 'folderGuardUnlock', item }
  passwordPromptValue.value = ''
}

async function confirmUnlockAllFolderGuard() {
  const confirmed = await askConfirm(t('folderGuard.unlockAllConfirm'), { variant: 'danger' })
  if (!confirmed) {
    return
  }
  const lockedPaths = folderGuardItems.value.filter((i) => i.status === 'Locked').map((i) => i.path)
  // Passkey 已設定就只能用 Passkey，失敗/取消不會退回密碼輸入框。
  if (folderGuardPasskeyEnabled.value) {
    const result = await requestMessage('unlockAllFolders', 'unlockAllFoldersResult', {})
    if (result.success) {
      showToast(t('folderGuard.unlockAllSuccess'), 'success')
      await playFolderGuardBatchUnlockAnimation(lockedPaths)
      refreshFolderGuardList()
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.unlockFailed')))
    }
    return
  }
  passwordPromptContext.value = { mode: 'folderGuardUnlockAll', lockedPaths }
  passwordPromptValue.value = ''
}

async function removeFolderGuardListEntry(item) {
  await requestMessage('removeFolderGuardEntry', 'removeFolderGuardEntryResult', { path: item.path })
  refreshFolderGuardList()
}

function openFolderGuardItemInExplorer(item) {
  sendMessage('openFolderInExplorer', { path: item.path })
}

// 使用紀錄「開啟檔案位置」：加密紀錄指向留在原位置的 .locked 指標檔所在資料夾
// （SourcePath 本身只在 Encrypted 這筆才有值），解密紀錄指向還原後檔案所在資料夾
// （RestoredPath 只在 Decrypted 這筆才有值）——兩種動作類型各自對應不同欄位，
// 見規劃這輪的決策：加密／解密兩種動作類型的紀錄列都要有這顆按鈕，其餘動作類型不涉及
// 檔案位置概念，不顯示。HandleOpenFolderInExplorer（MainWindow.xaml.cs）吃的是資料夾
// 路徑本身（`explorer.exe "path"`，不是 `/select,`），這裡取路徑的父層目錄，不是檔案本身。
function historyItemPath(entry) {
  if (entry.action === 'Encrypted') return entry.sourcePath || null
  if (entry.action === 'Decrypted') return entry.restoredPath || null
  return null
}

// 對應「單檔案分散式加密」功能規劃 §8：Standalone 項目找不到 .flocked 時，清單頁提供這顆
// 按鈕開啟原始路徑所在的資料夾，方便使用者就近尋找（例如檔案只是被移到同一層樓的另一個
// 資料夾）——路徑推導邏輯跟 openHistoryItemInExplorer 一致（取父層目錄，不是 /select,
// 選中檔案本身），item.originalPath commit 完成後一定指向 .flocked 應該落腳的位置
// （見 LockService.CommitStandaloneEncryptAsync），不是加密前的原始位置。
function openVaultItemOriginalLocationInExplorer(item) {
  const path = item.originalPath
  if (!path) return
  const lastSeparator = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'))
  const folderPath = lastSeparator > 0 ? path.slice(0, lastSeparator) : path
  sendMessage('openFolderInExplorer', { path: folderPath })
}

function openHistoryItemInExplorer(entry) {
  const path = historyItemPath(entry)
  if (!path) return
  const lastSeparator = Math.max(path.lastIndexOf('\\'), path.lastIndexOf('/'))
  const folderPath = lastSeparator > 0 ? path.slice(0, lastSeparator) : path
  sendMessage('openFolderInExplorer', { path: folderPath })
}

// 重用「新增資料夾」既有的 lockFolders IPC（見 submitFolderGuardSetup），上鎖本身不需要密碼驗證
// （規劃文件第 6 節：密碼只用來驗證解鎖身份），這裡也一樣不用先跳確認彈窗或密碼輸入。
// 轉輪播一段反方向完整旋轉，不等後端回應（後端本來就是 fire-and-forget，見定案文件〈金庫門
// 分頁的互動細節〉「再次上鎖」那一列）。
function relockFolderGuardItem(item) {
  folderGuardWheelRefs[item.path]?.spin('lock')
  sendMessage('lockFolders', { paths: [item.path] })
}

/// 對應規劃文件第 8 節：加密流程掃描到巢狀防護中的資料夾而中止，前端跳彈窗列出這些子資料夾，
/// 使用者確認後解鎖（Passkey 優先、沒設定則密碼）、成功才重新送出原本的加密請求。只在單一項目
/// 加密時提供這個引導——批次多筆的重試協調複雜度不成比例，直接照一般錯誤訊息處理即可。
///
/// 重試直接再呼叫一次 submitEncryptPending()，不另外組一份加密參數快照：信封流程失敗時會退回
/// form 階段但不清空表單欄位，所以那些欄位在重試當下跟第一次送出時完全相同，額外複製一份反而
/// 會多出一個要跟表單狀態同步的來源。這條引導過去掛在已經沒有入口的舊加密流程上（整條無法
/// 被觸發），改接到信封流程之後才真的會出現。
async function handleNestedGuardedEncrypt(errorDetail) {
  const nestedPaths = parseNestedGuardedPaths(errorDetail)
  if (nestedPaths.length === 0) {
    return
  }

  const confirmed = await askConfirm(
    t('folderGuard.nestedGuardedPrompt', { names: formatNestedGuardedNames(nestedPaths) }),
    { confirmLabel: t('folderGuard.unlock') }
  )
  if (!confirmed) {
    return
  }

  // Passkey 已設定就只能用 Passkey，失敗/取消不會退回密碼輸入框。
  if (folderGuardPasskeyEnabled.value) {
    const result = await requestMessage('unlockFoldersForEncryption', 'unlockFoldersForEncryptionResult', { paths: nestedPaths })
    if (result.success) {
      submitEncryptPending()
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.unlockFailed')))
    }
    return
  }

  passwordPromptContext.value = { mode: 'folderGuardNestedEncrypt', nestedPaths }
  passwordPromptValue.value = ''
}

// 三個步驟，缺一不可：①先問「真的要刪嗎」，確定鍵本身就是「用 Passkey 驗證身份」的觸發鍵——
// ②按下去才真的觸發 Windows Hello 挑戰簽章；③驗證通過後再問一次「真的要刪嗎」，這一步不用
// Passkey icon（身份已經驗證過了，這裡純粹是不可逆動作的最後提醒）。跟既有的刪除加密項目流程
// 一樣，身份驗證跟破壞性意圖確認分開問，不合併成一步。
async function requestClearHistory() {
  if (!settingsCriticalActionConfigured.value) {
    showToast(t('history.clearNeedsSetupFirst'))
    return
  }

  const wantsToVerify = await askConfirm(t('confirm.clearHistoryPrompt'), {
    confirmLabel: t('history.verifyWithPasskey'),
    confirmIconUrl: passkeyWhiteUrl,
    variant: 'danger'
  })
  if (!wantsToVerify) {
    return
  }

  const verifyResult = await requestMessage('verifyCriticalAction', 'verifyCriticalActionResult')
  if (!verifyResult.success) {
    showToast(t('history.clearVerificationFailed'))
    return
  }

  const finalConfirmed = await askConfirm(t('confirm.clearHistoryFinalWarning'), {
    confirmLabel: t('history.clearAll'),
    variant: 'danger'
  })
  if (!finalConfirmed) {
    return
  }

  sendMessage('clearHistory')
}

function pickFile() {
  sendMessage('pickFile', { purpose: 'encryptPath' })
}

function pickFolder() {
  sendMessage('pickFolder')
}

function removeEncryptPath(index) {
  encryptPaths.value.splice(index, 1)
}

function clearEncryptPaths() {
  encryptPaths.value = []
}

// 使用者在還沒送出 pending（encryptPhase 還是 'form'）的階段就關掉信封疊層——不管是點
// 取消、點外面、還是按 Esc，都是同一個「整個放棄這次加密」的動作。回饋：關掉之後選過的
// 檔案清單還留著，下次打開信封又看到上次的舊清單，像是沒有真的取消——這裡把選檔/密碼欄位
// 一起清乾淨，下次打開一定是全新的空白狀態。密碼欄位順便清掉也是既有慣例（見
// confirmEncryptPending 等處的既有註解：密碼是敏感資料，不管流程走到哪裡结束都不該留在
// 畫面上），不是這次才新增的規則。
function closeEncryptOverlayAndResetForm() {
  showEncryptOverlay.value = false
  encryptPaths.value = []
  encryptPassword.value = ''
  encryptPasswordConfirm.value = ''
  hint.value = ''
  enablePasskey.value = false
  enableRecoveryKey.value = false
  enableStandaloneMode.value = false
  standaloneDestinationDir.value = null
}

/// 拖放檔案：一般的 postMessage 只能傳可以轉成 JSON 的資料，瀏覽器沙盒化的 File 物件本身
/// 沒有真正的磁碟路徑可以序列化。WebView2 專門為此開了 postMessageWithAdditionalObjects
/// 這個管道，讓我們可以把 File 物件原封不動連同訊息一起送到 C# 那邊，C# 端會收到對應的
/// CoreWebView2File，讀 .Path 屬性就是真正路徑——見 MainWindow.xaml.cs 的
/// HandleFilesDroppedFromWebView 說明。
function handleFileDrop(event) {
  isDraggingFile.value = false
  const files = event.dataTransfer?.files
  if (!files || files.length === 0) {
    return
  }
  if (!window.chrome?.webview?.postMessageWithAdditionalObjects) {
    return
  }
  window.chrome.webview.postMessageWithAdditionalObjects({ type: 'filesDroppedFromWebView' }, files)
}

// 有設定過「關鍵操作驗證」才需要先過一次 Windows Hello，沒設定過就直接跳資料夾選擇器，
// 維持原本的行為。選在開啟資料夾選擇器之前擋，而不是選完資料夾之後才驗證，避免使用者
// 選好資料夾、等了一下才被擋下來的落差感。
async function pickVaultFolder() {
  if (settingsCriticalActionConfigured.value) {
    const verifyResult = await requestMessage('verifyCriticalAction', 'verifyCriticalActionResult')
    if (!verifyResult.success) {
      showToast(t('settings.vaultMoveVerificationFailed'))
      return
    }
  }
  sendMessage('pickVaultFolder')
}

function setLanguage(value) {
  settingsLanguage.value = value
  currentLocale.value = value
  sendMessage('updateSetting', { key: 'language', value })
}

function setTheme(value) {
  settingsTheme.value = value
  sendMessage('updateSetting', { key: 'theme', value })
}

// 系統匣常駐、跟隨 Windows 啟動：兩個獨立開關，各自對應後端一個即時生效的副作用（見
// MainWindow.HandleUpdateSettingRequest），跟 Theme/Language 一樣走通用的 updateSetting IPC，
// 不需要像資料夾防護那組開關另外走 request/response 校驗——這是本機 settings.json 寫入，
// 失敗機率極低，維持跟其他 App 層設定一致的簡單模式即可。
function toggleMinimizeToTrayAction(event) {
  const value = event.target.checked ? 'true' : 'false'
  settingsMinimizeToTrayEnabled.value = event.target.checked
  sendMessage('updateSetting', { key: 'minimizeToTrayEnabled', value })
}

function toggleLaunchAtStartupAction(event) {
  const value = event.target.checked ? 'true' : 'false'
  settingsLaunchAtStartupEnabled.value = event.target.checked
  sendMessage('updateSetting', { key: 'launchAtStartupEnabled', value })
}

function setWindowControlStyle(value) {
  settingsWindowControlStyle.value = value
  sendMessage('updateSetting', { key: 'windowControlStyle', value })
}

// 設定（或重新設定）「關鍵操作」用的 Windows Hello 驗證——目前用於清除所有使用紀錄前的
// 身份驗證，以及（設定過才會生效）搬移 Vault 位置前的驗證。重複呼叫會直接覆蓋舊憑證（見
// 後端 PasskeyProtector.CreateCredentialAsync 的 ReplaceExisting），前端不需要區分
// 「第一次設定」跟「重新設定」，同一個按鈕、同一個函式。
async function setupCriticalAction() {
  const result = await requestMessage('setupCriticalAction', 'setupCriticalActionResult')
  if (result.success) {
    settingsCriticalActionConfigured.value = true
    showToast(t('settings.criticalActionSetupSuccess'), 'success')
  } else {
    showToast(t('settings.criticalActionSetupFailed'))
  }
}

// 停用「關鍵操作」驗證：先要求通過一次 Windows Hello（證明還是本人在操作），驗證通過後
// 再問一次是否確定停用，通過才真的清掉設定值跟底層憑證。跟清除紀錄那套「確定鍵＝驗證
// 觸發鍵」的三步驟不同——停用本身沒有清除紀錄那樣的不可逆風險，驗證通過後單純問一次
// 「真的要停用嗎」即可。
async function disableCriticalAction() {
  const verifyResult = await requestMessage('verifyCriticalAction', 'verifyCriticalActionResult')
  if (!verifyResult.success) {
    showToast(t('settings.criticalActionDisableVerificationFailed'))
    return
  }
  const confirmed = await askConfirm(t('confirm.disableCriticalActionPrompt'), {
    confirmLabel: t('settings.criticalActionDisableButton'),
    variant: 'danger'
  })
  if (!confirmed) return

  const result = await requestMessage('disableCriticalAction', 'disableCriticalActionResult')
  if (result.success) {
    settingsCriticalActionConfigured.value = false
    showToast(t('settings.criticalActionDisabled'), 'success')
  }
}

// 獨立解密流程（信封＋Sheet，定案文件 §1.11）入口：按下「選擇要解密的檔案」直接跳原生
// 選檔視窗，不先跳信封——信封是「確認這是一個合法加密檔案」之後才要演的儀式，選檔前信封
// 無事可做。
function pickLockedFile() {
  sendMessage('pickFile', { purpose: 'decryptPath' })
}

// 選檔完成後先查一次唯讀 metadata（不需要密碼），只在確認是合法的加密檔案時才讓信封出現；
// 讀取失敗用既有 toast 錯誤機制顯示，不播信封動畫（見定案文件 §1.11「選檔完成後的合法性
// 檢查」）。
async function handleDecryptPathPicked(path) {
  const result = await requestMessage('inspectLockedFile', 'inspectLockedFileResult', { path })
  if (!result.success) {
    showToast(t('decrypt.invalidFile'))
    return
  }
  decryptItemInfo.value = {
    uuid: result.uuid,
    originalName: result.originalName,
    hint: result.hint,
    passkeyEnabled: result.passkeyEnabled,
    recoveryKeyEnabled: result.recoveryKeyEnabled,
    createdAtUtc: result.createdAtUtc
  }
  decryptVerifyState.value = { status: 'idle' }
  decryptCommitState.value = { status: 'idle' }
  showDecryptOverlay.value = true
}

// Verify 階段（密碼路徑）：只驗證密碼對不對，不還原任何檔案——EnvelopeDecrypt.vue 收到
// verifyState.status 變成 'success' 後才會自己播「打開信封→抽出選存檔位置 sheet」，
// 這裡不用管動畫時機。
async function submitDecryptPassword(password) {
  const uuid = decryptItemInfo.value?.uuid
  if (!uuid) return
  const result = await requestMessage('verifyDecryptPassword', 'verifyDecryptPasswordResult', { uuid, password })
  decryptVerifyState.value = result.success
    ? { status: 'success' }
    : { status: 'failed' }
  if (!result.success) {
    showToast(translateError(result.errorCode, result.errorDetail, t('decrypt.verifyFailed')))
  }
}

// Verify 階段（Passkey 路徑）：sheet 一出現就自動觸發（見 EnvelopeDecrypt.vue 的
// startPasskeyVerify），也可以由使用者手動點按鈕重試。失敗只在 sheet 上顯示提示文字，
// 不跳錯誤 toast——Windows Hello 取消是常見操作，不該用強制性錯誤彈窗打斷。
async function verifyDecryptPasskey() {
  const uuid = decryptItemInfo.value?.uuid
  if (!uuid) return
  const result = await requestMessage('verifyDecryptPasskey', 'verifyDecryptPasskeyResult', { uuid })
  decryptVerifyState.value = result.success
    ? { status: 'success' }
    : { status: 'failed', message: t('decrypt.passkeyVerifyIncomplete') }
}

// Verify 階段（恢復金鑰路徑）
async function submitDecryptRecoveryKey(recoveryKey) {
  const uuid = decryptItemInfo.value?.uuid
  if (!uuid) return
  const result = await requestMessage('verifyDecryptRecoveryKey', 'verifyDecryptRecoveryKeyResult', { uuid, recoveryKey })
  decryptVerifyState.value = result.success
    ? { status: 'success' }
    : { status: 'failed' }
  if (!result.success) {
    showToast(translateError(result.errorCode, result.errorDetail, t('decrypt.verifyFailed')))
  }
}

// 選存檔位置：先跳原生選資料夾視窗，選完在 pathPicked 的 'decryptDestination' 分支接著呼叫
// commitPendingDecrypt。
function pickDecryptDestination() {
  sendMessage('pickFolder', { purpose: 'decryptDestination' })
}

// Commit 階段：使用者選定存檔位置後才真正寫入檔案——到這步之前只是驗證了權限，沒有任何
// 檔案被動過（定案文件 §1.11）。
async function commitPendingDecrypt(destinationDir) {
  const uuid = decryptItemInfo.value?.uuid
  if (!uuid) return
  decryptCommitState.value = { status: 'restoring' }
  const result = await requestMessage('commitPendingDecrypt', 'commitPendingDecryptResult', { uuid, destinationDir })
  if (result.success) {
    decryptCommitState.value = { status: 'success', restoredPath: result.restoredPath }
    markLocalVaultMutation()
    showToast(t('decrypt.success', { path: result.restoredPath }), 'success')
  } else {
    decryptCommitState.value = { status: 'failed' }
    showToast(translateError(result.errorCode, result.errorDetail, t('decrypt.commitFailed')))
  }
}

// 取消（Esc／點外面／存檔位置 sheet 的取消連結）：比照 mockup 最終定案（見
// 13-sidebar-ticket-shell.html 使用者走查後的修正版），一律直接整個關閉，不做「信封開著、
// 空的」中間態。丟掉後端暫存的驗證結果（如果有的話）——fire-and-forget，不用等回應才關閉。
function closeDecryptOverlay() {
  const uuid = decryptItemInfo.value?.uuid
  if (uuid && decryptVerifyState.value.status !== 'idle') {
    sendMessage('cancelPendingDecrypt', { uuid })
  }
  showDecryptOverlay.value = false
  decryptItemInfo.value = null
  decryptVerifyState.value = { status: 'idle' }
  decryptCommitState.value = { status: 'idle' }
}

// EnvelopeDecrypt.vue 播完成功收尾（destination-sheet__success 顯示一段時間後）才 emit 這個，
// 這時才真的關閉疊層——跟 closeDecryptOverlay 共用同一個收尾，但不需要再送 cancelPendingDecrypt
// （已經 commit 過，pending 字典裡早就沒有這筆紀錄了）。
function handleDecryptDone() {
  showDecryptOverlay.value = false
  decryptItemInfo.value = null
  decryptVerifyState.value = { status: 'idle' }
  decryptCommitState.value = { status: 'idle' }
}

// 清單頁用密碼解密：一律還原到原始位置，不問要存到哪裡。
// 回饋：清單解密不再詢問要還原到哪裡，「自己選地方存」那個分支（曾經存在的
// pendingDecryptItem／pendingDecryptMode）整個拿掉了，不是隱藏起來，destinationDir 直接
// 固定傳 null。注意：獨立解密流程（信封＋Sheet，見 pickDecryptDestination／
// commitPendingDecrypt）之後重新引入了同名的 'decryptDestination' pickFolder purpose，
// 是完全不同的功能，服務的是「手上有一個不在清單裡的外部檔案」這個情境，兩者不要混淆。
function decryptFromList(item) {
  promptPasswordAndDecrypt(item, null)
}

// destinationDir 為 null 代表還原到原始位置。
function promptPasswordAndDecrypt(item, destinationDir) {
  passwordPromptContext.value = { mode: 'single', item, destinationDir }
  passwordPromptValue.value = ''
}

async function submitPasswordPrompt() {
  const ctx = passwordPromptContext.value
  const password = passwordPromptValue.value
  if (!ctx || !password) {
    return
  }
  passwordPromptContext.value = null
  showPasswordPromptValue.value = false

  if (ctx.mode === 'batch') {
    decryptingBatchIds.value.add(ctx.group.batchId)
    sendMessage('decryptBatch', {
      uuids: ctx.group.items.map((i) => i.uuid),
      password
    })
  } else if (ctx.mode === 'delete') {
    pendingDeleteItem.value = ctx.item
    sendMessage('verifyPasswordForDelete', { uuid: ctx.item.uuid, password })
  } else if (ctx.mode === 'folderGuardUnlock') {
    const result = await requestMessage('unlockFolder', 'unlockFolderResult', {
      path: ctx.item.path, password, keepInListAsUnlocked: true
    })
    if (result.success) {
      showToast(t('folderGuard.unlockSuccess'), 'success')
      await playFolderGuardUnlockAnimation(ctx.item.path)
      refreshFolderGuardList()
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.unlockFailed')))
    }
  } else if (ctx.mode === 'folderGuardUnlockAll') {
    const result = await requestMessage('unlockAllFolders', 'unlockAllFoldersResult', { password })
    if (result.success) {
      showToast(t('folderGuard.unlockAllSuccess'), 'success')
      await playFolderGuardBatchUnlockAnimation(ctx.lockedPaths ?? [])
      refreshFolderGuardList()
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.unlockFailed')))
    }
  } else if (ctx.mode === 'folderGuardNestedEncrypt') {
    const result = await requestMessage('unlockFoldersForEncryption', 'unlockFoldersForEncryptionResult', {
      paths: ctx.nestedPaths, password
    })
    if (!result.success) {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.unlockFailed')))
      return
    }
    // 解鎖成功，重新送出原本那次加密——讀的是表單狀態本身，不需要事先保存快照
    // （見 handleNestedGuardedEncrypt 的說明）。
    submitEncryptPending()
  } else if (ctx.mode === 'folderGuardDisable') {
    const result = await requestMessage('disableFolderGuard', 'disableFolderGuardResult', { password })
    if (result.success) {
      folderGuardConfigured.value = false
      folderGuardPasskeyEnabled.value = false
      showToast(t('folderGuard.disabled'), 'success')
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.disableFailed')))
    }
  } else if (ctx.mode === 'folderGuardDisablePasskey') {
    const result = await requestMessage('disableFolderGuardPasskey', 'disableFolderGuardPasskeyResult', { password })
    if (result.success) {
      folderGuardPasskeyEnabled.value = false
      showToast(t('folderGuard.passkeyDisabled'), 'success')
    } else {
      showToast(translateError(result.errorCode, result.errorDetail, t('folderGuard.passkeyDisableFailed')))
    }
  } else {
    decryptingUuids.value.add(ctx.item.uuid)
    sendMessage('decryptByUuid', { uuid: ctx.item.uuid, password, destinationDir: ctx.destinationDir })
  }
}

function cancelPasswordPrompt() {
  passwordPromptContext.value = null
  showPasswordPromptValue.value = false
}

// 清單頁用 Passkey 解密：不需要輸入密碼，直接觸發 Windows Hello 驗證，一律還原到原始位置
// （見 decryptFromList 的回饋說明）。
function decryptFromListViaPasskey(item) {
  startPasskeyDecrypt(item, null)
}

function startPasskeyDecrypt(item, destinationDir) {
  decryptingUuids.value.add(item.uuid)
  sendMessage('decryptByPasskey', { uuid: item.uuid, destinationDir })
}

// 清單頁用恢復金鑰解密：一律還原到原始位置，直接跳出輸入恢復金鑰的畫面。
function decryptFromListViaRecoveryKey(item) {
  openRecoveryKeyPrompt(item, null)
}

function openRecoveryKeyPrompt(item, destinationDir) {
  recoveryKeyPromptItem.value = item
  recoveryKeyPromptDestination.value = destinationDir
  recoveryKeyPromptMarkerPath.value = null
  recoveryKeyInputValue.value = ''
}

function submitRecoveryKeyDecrypt() {
  const item = recoveryKeyPromptItem.value
  if (!item || !recoveryKeyInputValue.value.trim()) {
    return
  }
  decryptingUuids.value.add(item.uuid)
  sendMessage('decryptByRecoveryKey', {
    uuid: item.uuid,
    recoveryKey: recoveryKeyInputValue.value.trim(),
    destinationDir: recoveryKeyPromptDestination.value,
    markerPath: recoveryKeyPromptMarkerPath.value
  })
  recoveryKeyPromptItem.value = null
  recoveryKeyPromptMarkerPath.value = null
}

function cancelRecoveryKeyPrompt() {
  recoveryKeyPromptItem.value = null
  recoveryKeyPromptMarkerPath.value = null
}

// 複製機密內容（密碼、恢復金鑰等）到剪貼簿、過一段時間自動清空——這類內容留在剪貼簿裡
// 風險不小（Windows 剪貼簿歷史紀錄會保留好幾筆之前複製過的內容，甚至可能跨裝置同步），
// 比照密碼管理工具的慣例自動清空，但只有在剪貼簿裡還是我們剛剛複製的這份內容時才清，
// 避免蓋掉使用者後來自己複製的別的東西。
async function copyToClipboardWithAutoClear(value, clearAfterMs = 45000) {
  await navigator.clipboard.writeText(value)
  setTimeout(async () => {
    try {
      const current = await navigator.clipboard.readText()
      if (current === value) {
        await navigator.clipboard.writeText('')
      }
    } catch {
      // 讀取剪貼簿失敗（例如視窗失去焦點時瀏覽器會擋）就算了，不強求。
    }
  }, clearAfterMs)
}

// 恢復金鑰顯示畫面：複製到剪貼簿。
async function copyRecoveryKey() {
  try {
    await copyToClipboardWithAutoClear(recoveryKeyDisplay.value)
    recoveryKeySaveState.value = recoveryKeySaveState.value || 'copied'
  } catch {
    showToast(t('recoveryKeyModal.copyFailed'))
  }
}

function saveRecoveryKeyToFile() {
  sendMessage('saveRecoveryKeyToFile', {
    content: t('recoveryKeyModal.fileContent', { key: recoveryKeyDisplay.value }),
    suggestedFileName: t('recoveryKeyModal.suggestedFileName')
  })
}

function acknowledgeRecoveryKey() {
  recoveryKeySaveState.value = 'acknowledged'
}

function closeRecoveryKeyDisplay() {
  recoveryKeyDisplay.value = ''
  recoveryKeySaveState.value = ''
}

// 永久刪除前要求重新輸入密碼（重用密碼輸入彈窗，見 submitPasswordPrompt 的 'delete' 分支）——
// 光按一次確認鍵沒辦法證明按下永久刪除的人真的知道密碼，這個動作又是不可逆的，門檻要跟
// 解密一樣高。
function requestDelete(item) {
  passwordPromptContext.value = { mode: 'delete', item }
  passwordPromptValue.value = ''
}

function handleDeleteRecordResult(data) {
  if (data.success) {
    vaultItems.value = vaultItems.value.filter((item) => item.uuid !== data.uuid)
    markLocalVaultMutation()
    return
  }
  if (data.blockedByNestedLocks) {
    showToast(t('alert.deleteBlockedByNested', { count: data.nestedUuids.length }))
    return
  }
  showToast(translateError(data.errorCode, null, t('alert.deleteFailed', { error: data.errorMessage })))
}

function formatSize(bytes) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function formatDate(isoString) {
  return new Date(isoString).toLocaleString(currentLocale.value === 'en' ? 'en-US' : 'zh-TW')
}

function typeLabel(type) {
  return type === 'Folder' ? t('type.folder') : t('type.file')
}

function actionLabel(action) {
  return t(`action.${action}`) !== `action.${action}` ? t(`action.${action}`) : action
}

function unlockMethodLabel(method) {
  return { password: t('unlockMethod.password'), passkey: t('unlockMethod.passkey'), recoveryKey: t('unlockMethod.recoveryKey') }[method] || t('unlockMethod.unknown')
}

function historyDetailText(entry) {
  if (entry.action === 'Encrypted') {
    const parts = []
    if (entry.sourcePath) parts.push(t('historyDetail.source', { path: entry.sourcePath }))
    parts.push(t('historyDetail.passkeyStatus', { status: entry.passkeyEnabled ? t('historyDetail.enabled') : t('historyDetail.disabled') }))
    parts.push(t('historyDetail.recoveryKeyStatus', { status: entry.recoveryKeyEnabled ? t('historyDetail.enabled') : t('historyDetail.disabled') }))
    return parts.join('｜')
  }
  if (entry.action === 'Decrypted') {
    const parts = [t('historyDetail.unlockMethod', { method: unlockMethodLabel(entry.unlockMethod) })]
    if (entry.restoredPath) parts.push(t('historyDetail.restoredTo', { path: entry.restoredPath }))
    return parts.join('｜')
  }
  return entry.detail || ''
}

// 回饋：密碼庫「新增帳密」表單開著的時候，Tab 鍵還是會切到表單背後被模糊暗化的背景元素——
// 跟 showEncryptOverlay／showDecryptOverlay 那組疊層原本就踩過、也修過的同一個問題（見
// title-bar／page-wrapper 上 :inert 那段既有註解），只是這次是密碼庫的表單彈窗，不是加密
// 疊層。這批 .modal-overlay 彈窗（確認對話框、密碼提示、恢復金鑰顯示……）跟資料夾防護的
// 新增資料夾疊層（VaultAddFolderOverlay）當初一個個加的時候都沒有補上同一道保護，是同一類
// bug 在不同地方各自漏掉，不是密碼庫這次才有的新問題。這裡統一補上：這些彈窗全部是
// .page-wrapper 的手足節點（跟 .encrypt-overlay 同一層，不是巢狀在裡面——VaultAddFolderOverlay
// 原本巢狀在資料夾防護分頁內容裡，已經搬到 .page-wrapper 外面，見它在模板裡新的掛載位置旁的
// 說明），套 inert 在 page-wrapper／title-bar 上不會連帶把彈窗本身也弄成不能互動。
//
// 合併回 main 時的落差（merge f85bab0→main）：main 那邊的 abc323f 把密碼庫整個換成
// @lx-kvn/password-locker-ui 共用元件，原本這裡列的 passwordLockerVerifyState／
// passwordLockerFormState 等六個內部狀態 ref 已經不存在（狀態封裝進共用元件內部），
// 元件本身（PasswordLockerPage.vue）目前也沒有透過 defineExpose 提供任何「內部彈窗開著嗎」
// 的查詢介面，App.vue 沒有辦法再知道密碼庫自己的彈窗有沒有開著——這裡先拿掉這六個判斷式
// 避免存取不存在的變數整個崩潰，但代表密碼庫自己的表單/驗證彈窗這輪起暫時不受這道背景
// Tab 焦點防護保護，要修的話得先在共用套件那邊加一個對外可查的「是否有彈窗開著」介面，
// 不是這次合併能單獨解決的範圍，先記錄在這裡等下一輪處理。
const isAnyBlockingModalOpen = computed(() =>
  showEncryptOverlay.value
  || showDecryptOverlay.value
  || folderGuardOverlayVisible.value
  || confirmDialogState.value != null
  || isUpdateModalOpen.value
  || isHelpOpen.value
  || recoveryKeyDisplay.value !== ''
  || passwordPromptContext.value != null
  || recoveryKeyPromptItem.value != null
)
</script>

<template>
  <div class="app" :class="[{ 'app--dark': settingsTheme === 'dark' }, activeThemeClass]">
    <!-- 永遠掛載但隱藏的密碼庫元件實例，專門用來接「加密完成後問要不要存進密碼庫」這個掛勾
         （見 encryptBatchDone 訊息處理常式）——刻意跟分頁裡可見的那份分開成兩個獨立實例，
         不是共用一份再用 v-show 切換，因為使用者觸發加密完成時不一定人在密碼庫分頁上，
         這份隱藏實例需要隨時存在、不受目前分頁影響。 -->
    <PasswordLockerPage v-show="false" ref="hiddenPasswordLockerRef"
      :lang="currentLocale" :theme="settingsTheme" :send-message="sendMessage" :request-message="requestMessage"
      :show-toast="showToast" :ask-confirm="askConfirm" :translate-error="translateError"
      :vault-items="vaultItems" :refresh-list="refreshList" />
    <!-- 自訂標題列：整條都是可拖曳區域（app-region: drag），交給作業系統的視窗管理員
         原生處理拖曳，所以能得到 Aero Snap、雙擊最大化、右鍵系統選單這些原生行為。
         三顆按鈕本身標記成 no-drag，否則點下去只會開始拖視窗、按不到按鈕。 -->
    <!-- 任何一個疊層/彈窗開著的時候，Tab 鍵切換不能切到背後被模糊暗化的元素——那些元素
         視覺上已經被 scrim 蓋住、看不清楚，鍵盤使用者卻還是能切過去操作到，是可以感知到但
         邏輯不通的狀態。用原生 inert 屬性把標題列跟主要頁面內容整個排除在 Tab 順序跟互動
         之外（inert 同時擋掉 focus 跟點擊，比只設 tabindex="-1" 更徹底——後者仍然可以被
         滑鼠點到）。isAnyBlockingModalOpen 這個判斷式定義在 script 最後面，涵蓋所有
         .modal-overlay 彈窗、加密/解密疊層，跟資料夾防護的新增資料夾疊層，完整名單見那裡。 -->
    <header class="title-bar" :inert="isAnyBlockingModalOpen">
      <!-- macOS 造型：圓點、左上角、關/縮/大順序（預設）。回饋：可點擊範圍要比看得到的
           圓點大——按鈕本身放大到 20x20（gap 收成 0，兩顆圓點中心距離維持原本 12+8=20px
           不變），圓點拆成獨立的 .traffic-light__dot（pointer-events:none，只畫色塊，
           不參與點擊判定），符號 svg 疊在圓點正上方置中。 -->
      <template v-if="settingsWindowControlStyle === 'macos'">
        <div class="traffic-lights">
          <button
            class="traffic-light traffic-light--close"
            type="button"
            :title="t('window.close')"
            :aria-label="t('window.close')"
            @click="closeWindow"
          >
            <span class="traffic-light__dot"></span>
            <svg viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M3.5 3.5l5 5M8.5 3.5l-5 5" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>
          </button>
          <button
            class="traffic-light traffic-light--minimize"
            type="button"
            :title="t('window.minimize')"
            :aria-label="t('window.minimize')"
            @click="minimizeWindow"
          >
            <span class="traffic-light__dot"></span>
            <svg viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M3 6h6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/></svg>
          </button>
          <button
            class="traffic-light traffic-light--maximize"
            type="button"
            :title="isWindowMaximized ? t('window.restore') : t('window.maximize')"
            :aria-label="isWindowMaximized ? t('window.restore') : t('window.maximize')"
            @click="toggleMaximizeWindow"
          >
            <span class="traffic-light__dot"></span>
            <svg v-if="!isWindowMaximized" viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M4 4h4v4z" fill="currentColor"/><path d="M8 8H4V4z" fill="currentColor" opacity="0"/><path d="M3.6 3.6h4.8v4.8z" fill="currentColor"/></svg>
            <svg v-else viewBox="0 0 12 12" class="traffic-light__glyph"><path d="M3.2 6.4h5.6M6.4 3.2v5.6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" opacity="0"/><path d="M3.5 5.2h3.3v3.3zM5.2 3.5h3.3v3.3z" fill="currentColor"/></svg>
          </button>
        </div>
        <span class="title-bar__title">FileLocker</span>
      </template>

      <!-- Windows 原生風：方形按鈕貼右上角、縮小/最大化/關閉順序，hover/active 整塊變色，
           貼近 Windows 11 原生行為的簡化版。 -->
      <template v-else-if="settingsWindowControlStyle === 'windows-native'">
        <span class="title-bar__title">FileLocker</span>
        <div class="win-controls">
          <button class="win-btn win-btn--minimize" type="button" :title="t('window.minimize')" :aria-label="t('window.minimize')" @click="minimizeWindow">
            <svg viewBox="0 0 10 10"><path d="M1 5h8" stroke="currentColor" stroke-width="1"/></svg>
          </button>
          <button class="win-btn win-btn--maximize" type="button" :title="isWindowMaximized ? t('window.restore') : t('window.maximize')" :aria-label="isWindowMaximized ? t('window.restore') : t('window.maximize')" @click="toggleMaximizeWindow">
            <svg v-if="!isWindowMaximized" viewBox="0 0 10 10"><rect x="1" y="1" width="8" height="8" fill="none" stroke="currentColor" stroke-width="1"/></svg>
            <svg v-else viewBox="0 0 10 10"><path d="M2.6 3.6h5.4v5.4h-5.4z" fill="none" stroke="currentColor" stroke-width="1"/><path d="M1.4 1.4h5.4v1.4M8.6 1.4v5.4" fill="none" stroke="currentColor" stroke-width="1"/></svg>
          </button>
          <button class="win-btn win-btn--close" type="button" :title="t('window.close')" :aria-label="t('window.close')" @click="closeWindow">
            <svg viewBox="0 0 10 10"><path d="M1 1l8 8M9 1l-8 8" stroke="currentColor" stroke-width="1"/></svg>
          </button>
        </div>
      </template>

      <!-- Windows 風格化版：形狀仍是方角、右上角慣例保留，但質感換成跟 macOS 燈號同一套
           「圓角小按鈕＋間距」語彙，顏色用 App 自己的強調色/危險色而不是 OS 原生紅/灰，
           平常透明看不到方塊、hover 才浮現色底。回饋：可點擊範圍要比看得到的圖示大——
           按鈕放大到 30x30，圖示維持 10x10 置中不變。 -->
      <template v-else>
        <span class="title-bar__title">FileLocker</span>
        <div class="win-controls win-controls--styled">
          <button class="win-btn-styled" type="button" :title="t('window.minimize')" :aria-label="t('window.minimize')" @click="minimizeWindow">
            <svg viewBox="0 0 10 10"><path d="M1 5h8" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>
          </button>
          <button class="win-btn-styled" type="button" :title="isWindowMaximized ? t('window.restore') : t('window.maximize')" :aria-label="isWindowMaximized ? t('window.restore') : t('window.maximize')" @click="toggleMaximizeWindow">
            <svg v-if="!isWindowMaximized" viewBox="0 0 10 10"><rect x="1.5" y="1.5" width="7" height="7" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.2"/></svg>
            <svg v-else viewBox="0 0 10 10"><path d="M3 4h4v4h-4z" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"/><path d="M2 3h4v0.01M8 3v4" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>
          </button>
          <button class="win-btn-styled win-btn-styled--close" type="button" :title="t('window.close')" :aria-label="t('window.close')" @click="closeWindow">
            <svg viewBox="0 0 10 10"><path d="M1.5 1.5l7 7M8.5 1.5l-7 7" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/></svg>
          </button>
        </div>
      </template>
    </header>

    <div class="page-wrapper" :inert="isAnyBlockingModalOpen">
      <AppSidebar
        :collapsed="sidebarCollapsed"
        :active="sidebarActiveKey"
        :t="t"
        @toggle-collapse="toggleSidebar"
        @navigate="onSidebarNavigate"
      />
      <main class="page" :class="{ 'page--wide': pageWidthTab === 'list' }">
        <Transition name="tab-page" mode="out-in" @before-enter="pageWidthTab = activeTab; themeTab = activeTab">
        <div v-if="activeTab === 'list'" key="list">
          <h1 class="page-title">
            <svg class="page-title__icon page-title__icon--list" viewBox="0 0 24 24" fill="none"><path d="M4 6h16M4 12h16M4 18h10" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>
            {{ t('list.title') }}
          </h1>

          <div class="sub-tab-bar">
            <button class="sub-tab-bar__item" :class="{ 'is-active': activeListSubTab === 'files' }" @click="activeListSubTab = 'files'">{{ t('list.subTabFiles') }}</button>
            <button class="sub-tab-bar__item" :class="{ 'is-active': activeListSubTab === 'history' }" @click="activeListSubTab = 'history'">{{ t('list.subTabHistory') }}</button>
          </div>

          <div v-if="activeListSubTab === 'files'">
            <div class="list-toolbar">
              <button class="button button--secondary refresh-button" @click="refreshList" :disabled="isLoadingList">
                {{ isLoadingList ? t('list.loading') : t('list.refresh') }}
              </button>
              <div class="list-toolbar__spacer"></div>
              <!-- 這兩顆按鈕是側欄殼子把 encrypt／decrypt／list 三個分頁合併成一個「加密」導覽項目
                   之後補上的入口——原本各自是獨立頂層分頁，現在要從清單頁的工具列進去。信封動畫
                   （這兩個按鈕點下去之後的視覺）留到下一階段，這裡先切到原本沒改過的精靈內容。 -->
              <button class="button button--secondary" type="button" @click="pickLockedFile">{{ t('list.openDecryptWizard') }}</button>
              <button class="button button--primary" type="button" @click="showEncryptOverlay = true">{{ t('list.openEncryptWizard') }}</button>
            </div>
            <div v-if="!isLoadingList && vaultItems.length === 0" class="empty-state-block">
              <svg class="empty-state-block__icon" viewBox="0 0 24 24" fill="none"><rect x="4" y="10" width="16" height="11" rx="2.5" stroke="currentColor" stroke-width="1.6"/><path d="M8 10V8a4 4 0 1 1 8 0v2" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"/></svg>
              <p class="empty-state-block__text">{{ t('list.noItems') }}</p>
            </div>

            <!-- 骨架畫面：第一次載入、還沒有任何資料時顯示，用灰色色塊模擬表格結構，資料回來
                 之前先讓畫面「看起來已經有東西」，感覺是漸漸浮現，不是空白一段時間後憑空跳出來。
                 已經有資料、只是重新整理的情況不顯示骨架——那樣每次按重新整理畫面都閃一下，
                 反而干擾，直接讓舊資料留著，等新資料回來再替換就好。 -->
            <div v-if="isLoadingList && vaultItems.length === 0" class="table-scroll">
              <table class="table table--auto">
                <thead>
                  <tr>
                    <th></th>
                    <th>{{ t('list.colName') }}</th>
                    <th>{{ t('list.colType') }}</th>
                    <th>{{ t('list.colSize') }}</th>
                    <th>{{ t('list.colHint') }}</th>
                    <th>{{ t('list.colTime') }}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="n in 8" :key="n">
                    <td><span class="skeleton-block" style="width: 20px;"></span></td>
                    <td><span class="skeleton-block" style="width: 70%;"></span></td>
                    <td><span class="skeleton-block" style="width: 50%;"></span></td>
                    <td><span class="skeleton-block" style="width: 40%;"></span></td>
                    <td><span class="skeleton-block" style="width: 30%;"></span></td>
                    <td><span class="skeleton-block" style="width: 60%;"></span></td>
                    <td><span class="skeleton-block" style="width: 80%;"></span></td>
                  </tr>
                </tbody>
              </table>
            </div>

            <!-- 票根樣式清單（design-exploration/gui-styles-v2/13-sidebar-ticket-shell.html 定案版本，
                 定案文件 §3.4）——純視覺重新蒙皮，資料欄位跟按鈕觸發的行為跟舊版表格列一模一樣
                 （原本這裡是 <table>，見這輪移植前的版本），只是換成 TicketRow.vue。撕開動畫
                 （mockup 的 tear-line／世代編號機制）留到下一階段，這裡的 TicketRow 是靜態卡片。 -->
            <!-- name="ticket-fly"：撕開驗證通過後從 vaultItems 陣列移除時，Vue 內建的 leave
                 過場機制接手飛走＋淡出，其餘列自動用 move 過場往上補位——不用手動量高度、
                 手動搬移其餘列（見 TicketRow.vue 開頭的說明跟下面 .ticket-fly-* 的 CSS）。
                 初始載入／切換分頁不會誤觸發飛走動畫，因為 TransitionGroup 預設只對「之後
                 才被加入/移除」的項目套用過場，第一次掛載時的項目不算。 -->
            <TransitionGroup v-if="vaultItems.length > 0" name="ticket-fly" tag="div" class="ticket-list">
              <template v-for="group in groupedVaultItems" :key="group.isGroup ? group.batchId : group.item.uuid">
                <!-- 獨立項目（沒有 batchId）：跟之前一樣直接顯示一張票根。 -->
                <TicketRow
                  v-if="!group.isGroup"
                  :item="group.item"
                  :t="t"
                  :decrypting="decryptingUuids.has(group.item.uuid)"
                  :tearing="tearingUuids.has(group.item.uuid)"
                  :translate-error="translateError"
                  :format-size="formatSize"
                  :format-date="formatDate"
                  :type-label="typeLabel"
                  @decrypt="decryptFromList"
                  @decrypt-via-passkey="decryptFromListViaPasskey"
                  @decrypt-via-recovery-key="decryptFromListViaRecoveryKey"
                  @delete="requestDelete"
                  @torn-away="handleTicketTornAway"
                  @go-to-original-location="openVaultItemOriginalLocationInExplorer"
                />

                <!-- 批次群組：一次選多個項目加密出來的，摺疊成一張摘要票根，展開後每個項目維持獨立操作能力。 -->
                <div v-else class="ticket-group">
                  <div class="ticket ticket--batch">
                    <button class="ticket-group__toggle" @click="toggleGroupExpanded(group.batchId)" type="button">
                      <span class="ticket-group__chevron" :class="{ 'is-expanded': expandedGroups.has(group.batchId) }">▸</span>
                      {{ batchPreviewText(group.items) }}
                    </button>
                    <button
                      class="button button--tiny"
                      @click="decryptGroupViaPassword(group)"
                      type="button"
                      :disabled="decryptingBatchIds.has(group.batchId)"
                    >
                      {{ decryptingBatchIds.has(group.batchId) ? t('list.unlockAllInProgress') : t('list.unlockAll') }}
                    </button>
                  </div>
                  <!-- 回饋（使用者實測抓到）：這裡原本用 v-if 直接切換，展開/收合完全沒有過場，
                       裡面單獨解鎖一筆時也沒有補位動畫（外層 <TransitionGroup name="ticket-fly">
                       只包住最外層清單，管不到這裡巢狀的 v-for）。改成一律掛載、用 CSS class
                       控制展開高度（grid-template-rows 0fr/1fr 技巧，不用 JS 量測高度），裡面
                       也換成 <TransitionGroup> 沿用同一組 ticket-fly 動畫語彙，解鎖/刪除單一
                       項目時其餘項目一樣會有彈性補位效果。 -->
                  <div
                    class="ticket-group__items-wrapper"
                    :class="{ 'is-expanded': expandedGroups.has(group.batchId), 'is-settled': settledGroups.has(group.batchId) }"
                  >
                    <TransitionGroup name="ticket-fly" tag="div" class="ticket-group__items">
                      <TicketRow
                        v-for="item in group.items"
                        :key="item.uuid"
                        :item="item"
                        :t="t"
                        :decrypting="decryptingUuids.has(item.uuid)"
                        :tearing="tearingUuids.has(item.uuid)"
                        :translate-error="translateError"
                        :format-size="formatSize"
                        :format-date="formatDate"
                        :type-label="typeLabel"
                        @decrypt="decryptFromList"
                        @decrypt-via-passkey="decryptFromListViaPasskey"
                        @decrypt-via-recovery-key="decryptFromListViaRecoveryKey"
                        @delete="requestDelete"
                        @torn-away="handleTicketTornAway"
                        @go-to-original-location="openVaultItemOriginalLocationInExplorer"
                      />
                    </TransitionGroup>
                  </div>
                </div>
              </template>
            </TransitionGroup>
          </div>

          <div v-else>
            <div class="history-toolbar">
              <button class="button button--secondary refresh-button" @click="refreshHistory" :disabled="isLoadingHistory">
                {{ isLoadingHistory ? t('list.loading') : t('list.refresh') }}
              </button>
              <button class="button button--danger refresh-button" @click="requestClearHistory" type="button" :disabled="historyItems.length === 0">
                {{ t('history.clearAll') }}
              </button>
            </div>
              <div v-if="!isLoadingHistory && historyItems.length === 0" class="empty-state-block">
            <svg class="empty-state-block__icon" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="8.5" stroke="currentColor" stroke-width="1.6"/><path d="M12 7.5V12l3 2" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            <p class="empty-state-block__text">{{ t('list.noHistory') }}</p>
          </div>

            <div v-if="isLoadingHistory && historyItems.length === 0" class="table-scroll">
              <table class="table">
                <colgroup>
                  <col style="width: 20%;" />
                  <col style="width: 9%;" />
                  <col style="width: 18%;" />
                  <col style="width: 34%;" />
                  <col style="width: 19%;" />
                </colgroup>
                <thead>
                  <tr>
                    <th>{{ t('list.colName') }}</th>
                    <th>{{ t('list.historyColAction') }}</th>
                    <th>{{ t('list.historyColTime') }}</th>
                    <th>{{ t('list.historyColDetail') }}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="n in 8" :key="n">
                    <td><span class="skeleton-block" style="width: 65%;"></span></td>
                    <td><span class="skeleton-block" style="width: 45%;"></span></td>
                    <td><span class="skeleton-block" style="width: 55%;"></span></td>
                    <td><span class="skeleton-block" style="width: 85%;"></span></td>
                    <td><span class="skeleton-block" style="width: 60%;"></span></td>
                  </tr>
                </tbody>
              </table>
            </div>

            <div v-if="historyItems.length > 0" class="table-scroll">
              <table class="table">
                <colgroup>
                  <col style="width: 20%;" />
                  <col style="width: 9%;" />
                  <col style="width: 18%;" />
                  <col style="width: 34%;" />
                  <col style="width: 19%;" />
                </colgroup>
                <thead>
                  <tr>
                    <th>{{ t('list.colName') }}</th>
                    <th>{{ t('list.historyColAction') }}</th>
                    <th>{{ t('list.historyColTime') }}</th>
                    <th>{{ t('list.historyColDetail') }}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(entry, index) in historyItems" :key="index">
                    <td class="table__wrap-cell" :title="entry.originalName">{{ entry.originalName }}</td>
                    <td>{{ actionLabel(entry.action) }}</td>
                    <td class="table__wrap-cell" :title="formatDate(entry.timestampUtc)">{{ formatDate(entry.timestampUtc) }}</td>
                    <td class="table__detail-cell table__wrap-cell" :title="historyDetailText(entry)">{{ historyDetailText(entry) }}</td>
                    <td>
                      <button
                        v-if="historyItemPath(entry)"
                        class="button button--tiny"
                        type="button"
                        @click="openHistoryItemInExplorer(entry)"
                      >
                        {{ t('list.historyOpenLocation') }}
                      </button>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </div>
        </div>

        <div v-else-if="activeTab === 'folderGuard'" key="folderGuard">
          <h1 class="page-title">
            <!-- 圖示跟側欄「資料夾防護」nav 項目改用同一份盾牌 path（design-exploration/gui-styles-v2
                 定案版本新畫的圖示，取代原本的資料夾圖示），跟 AppSidebar.vue 保持一致，不是側欄
                 換了圖示、頁面內容自己卻還留著舊圖示。 -->
            <svg class="page-title__icon page-title__icon--guard" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round"><path d="M12 3 4 6v6c0 5 3.5 7.7 8 9 4.5-1.3 8-4 8-9V6l-8-3Z"/></svg>
            {{ t('tab.folderGuard') }}
          </h1>
          <p class="hint-text">
            {{ t('folderGuard.pageDescriptionPrefix') }}
            <span class="text-warning-soft">{{ t('folderGuard.pageDescriptionWarning') }}</span>
            {{ t('folderGuard.pageDescriptionSuffix') }}
            <button class="link-button" @click="activeTab = 'list'; showEncryptOverlay = true" type="button">{{ t('tab.encrypt') }}</button>
          </p>

          <!-- 首次設定：整個功能還沒設定過共用密碼前，只能先設定密碼，不能上鎖任何資料夾
               （規劃文件第 3、6 節）。 -->
          <section v-if="!folderGuardConfigured" class="settings-section">
            <h3 class="settings-section__title">{{ t('folderGuard.setupTitle') }}</h3>
            <p class="hint-text">{{ t('folderGuard.setupDescription') }}</p>
            <div class="field">
              <label class="field__label">{{ t('folderGuard.passwordLabel') }}</label>
              <div class="password-field">
                <input v-model="folderGuardSetupPassword" :type="showFolderGuardSetupPassword ? 'text' : 'password'" class="text-input" />
                <button
                  type="button"
                  class="password-field__toggle"
                  :aria-label="t(showFolderGuardSetupPassword ? 'common.hidePassword' : 'common.showPassword')"
                  @click="showFolderGuardSetupPassword = !showFolderGuardSetupPassword"
                >
                  <svg v-if="showFolderGuardSetupPassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
                  <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
                </button>
              </div>
            </div>
            <div class="field">
              <label class="field__label">{{ t('folderGuard.passwordConfirmLabel') }}</label>
              <div class="password-field">
                <input v-model="folderGuardSetupPasswordConfirm" :type="showFolderGuardSetupPassword ? 'text' : 'password'" class="text-input" @keyup.enter="submitFolderGuardSetup" />
                <button
                  type="button"
                  class="password-field__toggle"
                  :aria-label="t(showFolderGuardSetupPassword ? 'common.hidePassword' : 'common.showPassword')"
                  @click="showFolderGuardSetupPassword = !showFolderGuardSetupPassword"
                >
                  <svg v-if="showFolderGuardSetupPassword" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
                  <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
                </button>
              </div>
            </div>
            <button class="button button--primary" @click="submitFolderGuardSetup" type="button">{{ t('folderGuard.setupSubmit') }}</button>
          </section>

          <template v-else>
            <div class="button-row">
              <button class="button button--primary" @click="pickFolderGuardFolder" type="button">{{ t('folderGuard.addFolder') }}</button>
              <button class="button button--secondary" @click="refreshFolderGuardList" :disabled="isLoadingFolderGuard" type="button">
                {{ isLoadingFolderGuard ? t('list.loading') : t('list.refresh') }}
              </button>
              <button
                v-if="folderGuardItems.some((item) => item.status === 'Locked')"
                class="button button--danger"
                style="margin-left: auto;"
                @click="confirmUnlockAllFolderGuard"
                type="button"
              >
                {{ t('folderGuard.unlockAllButton') }}
              </button>
            </div>

            <div v-if="!isLoadingFolderGuard && folderGuardItems.length === 0" class="empty-state-block">
              <svg class="empty-state-block__icon" viewBox="0 0 24 24" fill="none"><path d="M3.5 7.5a2 2 0 0 1 2-2h4l1.8 2h7.2a2 2 0 0 1 2 2v8.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2v-10.5Z" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/></svg>
              <p class="empty-state-block__text">{{ t('folderGuard.noItems') }}</p>
            </div>

            <div v-if="folderGuardItems.length > 0" class="table-scroll">
              <table class="table table--folder-guard">
                <colgroup>
                  <!-- 圖示欄寬度：表格 table-layout:fixed，欄跟欄的分界是靠這裡宣告的寬度決定，
                       不是靠 cell padding——原本 52px 只夠塞下 44px 圖示加一點點邊距，加大跟右邊
                       路徑文字的間距要放大這個數字，不能只加 td 的 padding-right（那樣只是在
                       固定寬度的框框裡面推，框框本身不會變寬，右邊欄的起始位置完全不會動）。 -->
                  <col style="width: 80px;" />
                  <col style="width: 37%;" />
                  <col style="width: 15%;" />
                  <col style="width: 45%;" />
                </colgroup>
                <thead>
                  <tr>
                    <th></th>
                    <th>{{ t('folderGuard.colPath') }}</th>
                    <th>{{ t('folderGuard.colStatus') }}</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="item in folderGuardItems" :key="item.path">
                    <td>
                      <VaultWheelIcon
                        :ref="(el) => setFolderGuardWheelRef(item.path, el)"
                        :locked="item.status === 'Locked'"
                        :size="44"
                        :class="{ 'vault-wheel-icon--clickable': item.status === 'Locked' }"
                        :aria-label="item.status === 'Locked' ? t('folderGuard.unlock') : undefined"
                        :role="item.status === 'Locked' ? 'button' : undefined"
                        :tabindex="item.status === 'Locked' ? 0 : undefined"
                        @click="item.status === 'Locked' && onFolderGuardWheelIconClick(item)"
                        @keydown.enter="item.status === 'Locked' && onFolderGuardWheelIconClick(item)"
                        @keydown.space.prevent="item.status === 'Locked' && onFolderGuardWheelIconClick(item)"
                      />
                    </td>
                    <td><div class="cell-name" :title="item.path">{{ item.path }}</div></td>
                    <td>{{ item.status === 'Locked' ? t('folderGuard.statusLocked') : t('folderGuard.statusUnlocked') }}</td>
                    <td>
                      <div class="table__actions">
                        <button v-if="item.status === 'Locked'" class="button button--tiny" @click="unlockFolderGuardItem(item)" type="button">
                          {{ t('folderGuard.unlock') }}
                        </button>
                        <template v-else>
                          <button class="button button--tiny" @click="openFolderGuardItemInExplorer(item)" type="button">
                            {{ t('folderGuard.openFolder') }}
                          </button>
                          <button class="button button--tiny" @click="relockFolderGuardItem(item)" type="button">
                            {{ t('folderGuard.relock') }}
                          </button>
                          <button class="button button--tiny" @click="removeFolderGuardListEntry(item)" type="button">
                            {{ t('folderGuard.removeFromList') }}
                          </button>
                        </template>
                      </div>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </template>
        </div>

        <PasswordLockerPage v-else-if="activeTab === 'passwordLocker'" key="passwordLocker" ref="passwordLockerPageRef"
          :lang="currentLocale" :theme="settingsTheme" :send-message="sendMessage" :request-message="requestMessage"
          :show-toast="showToast" :ask-confirm="askConfirm" :translate-error="translateError"
          :vault-items="vaultItems" :refresh-list="refreshList" />

        <div v-else-if="activeTab === 'settings'" key="settings" class="settings-tab">
          <h1 class="page-title">
            <svg class="page-title__icon page-title__icon--settings" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="12" r="3" stroke="currentColor" stroke-width="1.7"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg>
            {{ t('settings.title') }}
          </h1>

          <div class="settings-group">
          <h2 class="settings-group__header">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round"><line x1="4" y1="6" x2="20" y2="6"/><circle cx="9" cy="6" r="2" fill="var(--color-bg)"/><line x1="4" y1="12" x2="20" y2="12"/><circle cx="15" cy="12" r="2" fill="var(--color-bg)"/><line x1="4" y1="18" x2="20" y2="18"/><circle cx="7" cy="18" r="2" fill="var(--color-bg)"/></svg>
            {{ t('settings.groupGeneralTitle') }}
          </h2>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.vaultLocationTitle') }}</h3>
            <p class="vault-path-display" :title="settingsVaultPath">{{ settingsVaultPath }}</p>
            <button class="button button--secondary" @click="pickVaultFolder" type="button" :disabled="isChangingVaultPath">
              {{ isChangingVaultPath ? t('settings.vaultMoving') : t('settings.vaultMove') }}
            </button>
            <p class="hint-text">{{ t('settings.vaultMoveHint') }}</p>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.languageTitle') }}</h3>
            <select class="select-input" :value="settingsLanguage" @change="setLanguage($event.target.value)">
              <option value="zh-TW">繁體中文</option>
              <option value="en">English</option>
            </select>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.themeTitle') }}</h3>
            <div class="button-row">
              <button class="button button--secondary" @click="setTheme('light')" type="button" :disabled="settingsTheme === 'light'">
                <img :src="lightModeIconUrl" alt="" class="button__icon" />
                {{ t('settings.themeLight') }}
              </button>
              <button class="button button--secondary" @click="setTheme('dark')" type="button" :disabled="settingsTheme === 'dark'">
                <img :src="darkModeIconUrl" alt="" class="button__icon" />
                {{ t('settings.themeDark') }}
              </button>
            </div>
            <p class="hint-text">{{ t('settings.themeHint') }}</p>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.minimizeToTrayTitle') }}</h3>
            <div class="field">
              <label class="checkbox-field">
                <input
                  type="checkbox"
                  :checked="settingsMinimizeToTrayEnabled"
                  @change="toggleMinimizeToTrayAction"
                />
                <span>{{ t('settings.minimizeToTrayLabel') }}</span>
                <span class="info-tooltip" tabindex="0">
                  <span class="info-tooltip__icon">i</span>
                  <span class="info-tooltip__bubble info-tooltip__bubble--wide">
                    <p class="info-tooltip__intro">{{ t('settings.minimizeToTrayDetailIntro') }}</p>
                    <ul class="info-tooltip__list">
                      <li>{{ t('settings.minimizeToTrayDetailPoint1') }}</li>
                      <li>{{ t('settings.minimizeToTrayDetailPoint2') }}</li>
                    </ul>
                  </span>
                </span>
              </label>
            </div>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.launchAtStartupTitle') }}</h3>
            <div class="field">
              <label class="checkbox-field">
                <input
                  type="checkbox"
                  :checked="settingsLaunchAtStartupEnabled"
                  @change="toggleLaunchAtStartupAction"
                />
                <span>{{ t('settings.launchAtStartupLabel') }}</span>
                <span class="info-tooltip" tabindex="0">
                  <span class="info-tooltip__icon">i</span>
                  <span class="info-tooltip__bubble info-tooltip__bubble--wide">
                    <p class="info-tooltip__intro">{{ t('settings.launchAtStartupDetailIntro') }}</p>
                    <ul class="info-tooltip__list">
                      <li>{{ t('settings.launchAtStartupDetailPoint1') }}</li>
                    </ul>
                  </span>
                </span>
              </label>
            </div>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.windowControlStyleTitle') }}</h3>
            <div class="button-row">
              <button class="button button--secondary" @click="setWindowControlStyle('macos')" type="button" :disabled="settingsWindowControlStyle === 'macos'">
                {{ t('settings.windowControlStyleMacos') }}
              </button>
              <button class="button button--secondary" @click="setWindowControlStyle('windows-native')" type="button" :disabled="settingsWindowControlStyle === 'windows-native'">
                {{ t('settings.windowControlStyleWindowsNative') }}
              </button>
              <button class="button button--secondary" @click="setWindowControlStyle('windows-styled')" type="button" :disabled="settingsWindowControlStyle === 'windows-styled'">
                {{ t('settings.windowControlStyleWindowsStyled') }}
              </button>
            </div>
            <p class="hint-text">{{ t('settings.windowControlStyleHint') }}</p>
          </section>
          </div>

          <div class="settings-group">
          <h2 class="settings-group__header">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round"><path d="M12 3 4 6v6c0 5 3.5 7.7 8 9 4.5-1.3 8-4 8-9V6l-8-3Z"/></svg>
            {{ t('settings.groupSecurityTitle') }}
          </h2>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.criticalActionTitle') }}</h3>
            <p class="hint-text">{{ t('settings.criticalActionDescription') }}</p>
            <p class="status-message" :class="settingsCriticalActionConfigured ? 'status-message--success' : ''">
              {{ settingsCriticalActionConfigured ? t('settings.criticalActionConfigured') : t('settings.criticalActionNotConfigured') }}
            </p>
            <div class="button-row">
              <button class="button button--secondary" @click="setupCriticalAction" type="button">
                <img :src="passkeyIconUrl" alt="" class="button__icon" />
                {{ settingsCriticalActionConfigured ? t('settings.criticalActionResetupButton') : t('settings.criticalActionSetupButton') }}
              </button>
              <button v-if="settingsCriticalActionConfigured" class="button button--danger" @click="disableCriticalAction" type="button">
                {{ t('settings.criticalActionDisableButton') }}
              </button>
            </div>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('folderGuard.credentialTitle') }}</h3>
            <template v-if="folderGuardConfigured">
              <div class="button-row">
                <button class="button button--secondary" @click="setupFolderGuardPasskeyAction" type="button">
                  <img :src="passkeyIconUrl" alt="" class="button__icon" />
                  {{ folderGuardPasskeyEnabled ? t('folderGuard.passkeyResetupButton') : t('folderGuard.passkeySetupButton') }}
                </button>
                <button v-if="folderGuardPasskeyEnabled" class="button button--secondary" @click="disableFolderGuardPasskeyAction" type="button">
                  {{ t('folderGuard.passkeyDisableButton') }}
                </button>
                <button class="button button--danger" @click="disableFolderGuardAction" type="button">{{ t('folderGuard.disableButton') }}</button>
              </div>
              <p class="hint-text">{{ t('folderGuard.forgotPasswordHint') }}</p>

              <div class="field" style="margin-top: 16px;">
                <label class="checkbox-field">
                  <input
                    type="checkbox"
                    :checked="folderGuardDoubleClickUnlockEnabled"
                    :disabled="isTogglingFolderGuardDoubleClickUnlock"
                    @change="toggleFolderGuardDoubleClickUnlockAction"
                  />
                  <span>{{ t('folderGuard.doubleClickUnlockLabel') }}</span>
                  <span class="info-tooltip" tabindex="0">
                    <span class="info-tooltip__icon">i</span>
                    <span class="info-tooltip__bubble info-tooltip__bubble--wide">
                      <p class="info-tooltip__intro">{{ t('folderGuard.doubleClickUnlockDetailIntro') }}</p>
                      <ul class="info-tooltip__list">
                        <li>{{ t('folderGuard.doubleClickUnlockDetailPoint1') }}</li>
                        <li>{{ t('folderGuard.doubleClickUnlockDetailPoint2') }}</li>
                        <li>{{ t('folderGuard.doubleClickUnlockDetailPoint3') }}</li>
                      </ul>
                    </span>
                  </span>
                </label>
              </div>

              <div class="field" style="margin-top: 16px;">
                <label class="checkbox-field">
                  <input
                    type="checkbox"
                    :checked="folderGuardAutoRelockEnabled"
                    :disabled="isTogglingFolderGuardAutoRelock"
                    @change="toggleFolderGuardAutoRelockAction"
                  />
                  <span>{{ t('folderGuard.autoRelockLabel') }}</span>
                  <span class="info-tooltip" tabindex="0">
                    <span class="info-tooltip__icon">i</span>
                    <span class="info-tooltip__bubble info-tooltip__bubble--wide">
                      <p class="info-tooltip__intro">{{ t('folderGuard.autoRelockDetailIntro') }}</p>
                      <ul class="info-tooltip__list">
                        <li>{{ t('folderGuard.autoRelockDetailPoint1') }}</li>
                        <li>{{ t('folderGuard.autoRelockDetailPoint2') }}</li>
                        <li>{{ t('folderGuard.autoRelockDetailPoint3') }}</li>
                      </ul>
                    </span>
                  </span>
                </label>
                <div v-if="folderGuardAutoRelockEnabled" class="field" style="margin-top: 8px; margin-left: 28px;">
                  <label>
                    {{ t('folderGuard.autoRelockMinutesLabel') }}
                    <input
                      type="number"
                      min="1"
                      class="text-input"
                      style="width: 80px; display: inline-block; margin-left: 8px;"
                      :value="folderGuardAutoRelockMinutes"
                      :disabled="isTogglingFolderGuardAutoRelock"
                      @change="updateFolderGuardAutoRelockMinutesAction"
                    />
                  </label>
                </div>
              </div>
            </template>
            <template v-else>
              <p class="hint-text">
                {{ t('folderGuard.settingsNotConfiguredHint') }}
                <button class="link-button" @click="activeTab = 'folderGuard'" type="button">{{ t('tab.folderGuard') }}</button>
              </p>
            </template>
          </section>

          </div>

          <div class="settings-group">
          <h2 class="settings-group__header">
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><line x1="12" y1="11" x2="12" y2="16"/><circle cx="12" cy="8" r="0.6" fill="currentColor" stroke="none"/></svg>
            {{ t('settings.groupAboutTitle') }}
          </h2>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.helpTitle') }}</h3>
            <button class="button button--secondary" @click="isHelpOpen = true" type="button">{{ t('settings.helpButton') }}</button>
          </section>

          <section class="settings-section">
            <h3 class="settings-section__title">{{ t('settings.updateCheckTitle') }}</h3>
            <button class="button button--secondary" @click="checkForUpdatesAction" type="button" :disabled="isCheckingUpdate">
              {{ isCheckingUpdate ? t('settings.updateCheckChecking') : t('settings.updateCheckButton') }}
            </button>
            <p v-if="updateCheckResult && !updateCheckResult.updateAvailable" class="hint-text">
              {{ t('settings.updateCheckUpToDate', { version: updateCheckResult.currentVersion }) }}
            </p>
            <template v-else-if="updateCheckResult && updateCheckResult.updateAvailable">
              <p class="status-message status-message--success">
                {{ t('settings.updateCheckAvailable', { version: updateCheckResult.latestVersion }) }}
              </p>
              <button class="button button--primary" @click="openUpdateDetailsModal" type="button">
                {{ t('settings.updateViewDetailsButton') }}
              </button>
            </template>
          </section>
          </div>

          <p v-if="settingsSaveMessage" class="status-message status-message--success">{{ settingsSaveMessage }}</p>
        </div>
        </Transition>
      </main>
    </div>

    <!-- 這顆疊層原本巢狀寫在資料夾防護分頁內容裡面（.page-wrapper 底下），跟其他彈窗不同層——
         套 :inert="isAnyBlockingModalOpen" 在 page-wrapper 上時，如果這顆疊層還留在裡面，
         連它自己都會被一起擋掉，開著新增資料夾疊層時反而按不到裡面的輸入框。搬出來跟
         .encrypt-overlay 同一層（.app 的手足節點），套用同一招保護背景 Tab 順序，也不會誤傷
         疊層本身——VaultAddFolderOverlay 元件自己用 position:fixed，搬動掛載位置不影響
         視覺呈現位置，過場動畫用的 class（.vault-overlay-*）定義在元件自己的 scoped style
         裡，跟 <Transition> 標籤實際寫在哪裡無關，見該元件檔案內的說明。 -->
    <Transition name="vault-overlay">
      <VaultAddFolderOverlay
        v-if="folderGuardOverlayVisible"
        ref="folderGuardOverlayRef"
        @cancel="onFolderGuardAddOverlayCancel"
      />
    </Transition>

    <!-- 信封加密流程（Phase 2b）：不是分頁，是疊在目前畫面上的懸浮層（背景模糊+暗化），
         比照 apple-design「Dim to focus」——這是一個阻斷式任務（加密進行中不該讓使用者
         同時去操作底下的清單），所以用會模糊+略微暗化背景的 scrim，不是 VaultAddFolderOverlay
         那種只模糊不暗化的作法（那個情境使用者隨時可以點外面立即取消，這裡目前 pending/
         committing 期間點外面不該直接取消，見 EnvelopeEncrypt.vue 內部自己的取消按鈕）。 -->
    <Transition name="encrypt-overlay">
      <div
        v-if="showEncryptOverlay"
        class="encrypt-overlay"
        @click.self="encryptPhase === 'form' && closeEncryptOverlayAndResetForm()"
      >
        <EnvelopeEncrypt
          :t="t"
          :paths="encryptPaths"
          :password="encryptPassword"
          :password-confirm="encryptPasswordConfirm"
          :hint="hint"
          :enable-passkey="enablePasskey"
          :enable-recovery-key="enableRecoveryKey"
          :enable-standalone-mode="enableStandaloneMode"
          :standalone-destination-dir="standaloneDestinationDir"
          :disable-passkey-recovery-key="encryptPaths.length > 1"
          :passkey-icon-url="passkeyIconUrl"
          :recovery-key-icon-url="recoveryKeyIconUrl"
          :is-dark-theme="isDarkTheme"
          :phase="encryptPhase"
          :progress-percent="encryptRealProgressPercent"
          :waiting-passkey="encryptWaitingPasskey"
          :pending-summary="encryptPendingSummary"
          :recovery-key-modal-open="!!recoveryKeyDisplay"
          @pick-file="pickFile"
          @pick-folder="pickFolder"
          @remove-path="removeEncryptPath"
          @clear-paths="clearEncryptPaths"
          @drop="handleFileDrop"
          @update:password="encryptPassword = $event"
          @update:password-confirm="encryptPasswordConfirm = $event"
          @update:hint="hint = $event"
          @update:enable-passkey="enablePasskey = $event"
          @update:enable-recovery-key="enableRecoveryKey = $event"
          @request-toggle-standalone-mode="onRequestToggleStandaloneMode"
          @update:standalone-destination-dir="standaloneDestinationDir = $event"
          @pick-standalone-destination="sendMessage('pickFolder', { purpose: 'flockedDestination' })"
          @submit="submitEncryptPending"
          @confirm="confirmEncryptPending"
          @cancel="encryptPhase === 'form' ? closeEncryptOverlayAndResetForm() : cancelEncryptPending()"
          @fly-away-complete="onEncryptFlyAwayComplete"
        />
      </div>
    </Transition>

    <!-- 獨立解密流程（信封＋Sheet，定案文件 §1.11）：跟信封加密流程共用同一個 .encrypt-overlay
         模糊＋暗化 scrim 樣式（視覺上就是同一種疊層語言，不用另外畫一份）。跟加密流程的
         差異：這裡點外面／Esc 一律直接整個關閉，不分階段限制——見 closeDecryptOverlay 的
         說明，verify 階段本身不寫入任何檔案，隨時取消都是安全的，不需要加密流程那種
         「pending 已送出就不能中途關閉」的保護。v-if 讓每次開啟都是全新的元件實例，內部
         動畫/表單狀態自然重置，不用比照 mockup 手刻一套 resetDecryptState。 -->
    <Transition name="encrypt-overlay">
      <div
        v-if="showDecryptOverlay"
        class="encrypt-overlay"
        @click.self="closeDecryptOverlay"
      >
        <EnvelopeDecrypt
          v-if="decryptItemInfo"
          :t="t"
          :original-name="decryptItemInfo.originalName"
          :created-at-utc="decryptItemInfo.createdAtUtc"
          :passkey-enabled="decryptItemInfo.passkeyEnabled"
          :recovery-key-enabled="decryptItemInfo.recoveryKeyEnabled"
          :passkey-icon-url="passkeyIconUrl"
          :recovery-key-icon-url="recoveryKeyIconUrl"
          :verify-state="decryptVerifyState"
          :commit-state="decryptCommitState"
          @submit-password="submitDecryptPassword"
          @verify-passkey="verifyDecryptPasskey"
          @submit-recovery-key="submitDecryptRecoveryKey"
          @pick-destination="pickDecryptDestination"
          @cancel="closeDecryptOverlay"
          @done="handleDecryptDone"
        />
      </div>
    </Transition>

    <!-- 通知（取代原生 alert()） -->
    <div class="toast-stack">
      <TransitionGroup name="toast">
        <div v-for="toast in toasts" :key="toast.id" class="toast" :class="`toast--${toast.kind}`" @click="dismissToast(toast.id)">
          <svg v-if="toast.kind === 'success'" class="toast__icon" viewBox="0 0 20 20" fill="none"><circle cx="10" cy="10" r="8.5" stroke="currentColor" stroke-width="1.6"/><path d="M6.5 10.2l2.2 2.2 4.8-5" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/></svg>
          <svg v-else class="toast__icon" viewBox="0 0 20 20" fill="none"><circle cx="10" cy="10" r="8.5" stroke="currentColor" stroke-width="1.6"/><path d="M10 6v5" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/><circle cx="10" cy="13.8" r="1" fill="currentColor"/></svg>
          <span>{{ toast.message }}</span>
        </div>
      </TransitionGroup>
    </div>

    <!-- 確認對話框（取代原生 confirm()）：只用在真正的二選一（做／不做同一件事）。 -->
    <Transition name="modal">
      <div v-if="confirmDialogState" class="modal-overlay modal-overlay--confirm" @click.self="resolveConfirmDialog(false)">
        <div class="modal">
          <p class="modal__message">{{ confirmDialogState.message }}</p>
          <div class="modal__footer">
            <button class="button button--secondary" @click="resolveConfirmDialog(false)" type="button">{{ confirmDialogState.cancelLabel }}</button>
            <button
              class="button"
              :class="confirmDialogState.variant === 'danger' ? 'button--danger' : 'button--primary'"
              @click="resolveConfirmDialog(true)"
              type="button"
            >
              <img v-if="confirmDialogState.confirmIconUrl" :src="confirmDialogState.confirmIconUrl" alt="" class="button__icon" />
              {{ confirmDialogState.confirmLabel }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 更新確認彈窗：release notes 是 Markdown，內容長短不定，需要獨立版型（可捲動框框、
         按鈕列固定在外面），不能沿用上面的通用確認彈窗。 -->
    <Transition name="modal">
      <div v-if="isUpdateModalOpen" class="modal-overlay" @click.self="closeUpdateDetailsModal">
        <div class="modal modal--update">
          <h2 class="modal__title">{{ t('settings.updateFoundPrompt') }}</h2>
          <div class="modal--update__body" v-html="renderedReleaseNotes"></div>
          <div class="modal__footer">
            <button class="link-button" @click="openReleasesPageAction" type="button" style="margin-right: auto;">
              {{ t('settings.updateCheckOpenRelease') }}
            </button>
            <button class="button button--secondary" @click="closeUpdateDetailsModal" type="button" :disabled="isInstallingUpdate">
              {{ t('passwordPrompt.cancel') }}
            </button>
            <button
              v-if="updateCheckResult?.hasDownloadUrl"
              class="button button--primary"
              @click="installUpdateAction"
              type="button"
              :disabled="isInstallingUpdate"
            >
              {{ isInstallingUpdate ? t('settings.updateDownloading') : t('settings.updateInstallButton') }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 使用說明彈窗：內容比較長，用可以捲動的樣式處理。 -->
    <Transition name="modal">
      <div v-if="isHelpOpen" class="modal-overlay" @click.self="isHelpOpen = false">
        <div class="modal modal--help">
          <h2 class="modal__title">{{ t('help.title') }}</h2>
          <div class="modal--help__body">
            <section class="modal--help__section">
              <h3>{{ t('help.basicsTitle') }}</h3>
              <p>{{ t('help.basicsBody') }}</p>
            </section>
            <section class="modal--help__section">
              <h3>{{ t('help.howItWorksTitle') }}</h3>
              <p>{{ t('help.howItWorksBody') }}</p>
            </section>
            <section class="modal--help__section">
              <h3>{{ t('help.precautionsTitle') }}</h3>
              <p>{{ t('help.precautionsBody') }}</p>
            </section>
            <section class="modal--help__section">
              <h3>{{ t('help.criticalActionTitle') }}</h3>
              <p>{{ t('help.criticalActionBody') }}</p>
            </section>
            <section class="modal--help__section">
              <h3>{{ t('help.folderGuardTitle') }}</h3>
              <p>{{ t('help.folderGuardBody') }}</p>
            </section>
            <section class="modal--help__section">
              <h3>{{ t('help.passwordLockerTitle') }}</h3>
              <p>{{ t('help.passwordLockerBody') }}</p>
            </section>
          </div>
          <div class="modal__footer modal__footer--center">
            <button class="button button--primary" @click="isHelpOpen = false" type="button">{{ t('recoveryKeyModal.close') }}</button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 恢復金鑰顯示彈窗：加密成功且開啟了恢復金鑰時跳出，強制使用者做選擇才能關閉。
         這是整個 App 裡刻意做出視覺差異的一個畫面——風險最高、最需要使用者專注的一刻，
         用類似「封印/證書」的處理讓它明顯跟其他畫面不一樣。 -->
    <Transition name="modal">
      <div v-if="recoveryKeyDisplay" class="modal-overlay">
        <div class="modal modal--signature">
          <img :src="lockedWaxSealUrl" alt="" class="modal--signature__seal" />
          <h2 class="modal__title">{{ t('recoveryKeyModal.title') }}</h2>
          <p class="modal--signature__warning">{{ t('recoveryKeyModal.warning') }}</p>
          <div class="recovery-key-display" tabindex="0">{{ recoveryKeyDisplay }}</div>
          <div class="modal__actions modal__actions--wrap">
            <button class="button button--secondary" @click="copyRecoveryKey" type="button">{{ t('recoveryKeyModal.copy') }}</button>
            <button class="button button--secondary" @click="saveRecoveryKeyToFile" type="button">{{ t('recoveryKeyModal.saveToFile') }}</button>
            <button class="button button--secondary" @click="acknowledgeRecoveryKey" type="button">{{ t('recoveryKeyModal.acknowledge') }}</button>
          </div>
          <p v-if="recoveryKeySaveState === 'saved'" class="status-message status-message--success">{{ t('recoveryKeyModal.savedNotice') }}</p>
          <p v-if="recoveryKeySaveState === 'copied'" class="status-message status-message--success">{{ t('recoveryKeyModal.copiedNotice') }}</p>
          <div class="modal__footer modal__footer--center">
            <button class="button button--primary" @click="closeRecoveryKeyDisplay" type="button" :disabled="!recoveryKeySaveState">
              {{ recoveryKeySaveState ? t('recoveryKeyModal.close') : t('recoveryKeyModal.closeDisabled') }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 密碼輸入彈窗：取代原本明碼顯示的 prompt()，用遮罩密碼欄位。 -->
    <Transition name="modal">
      <div v-if="passwordPromptContext" class="modal-overlay" @click.self="cancelPasswordPrompt">
        <div class="modal">
          <h2 class="modal__title">{{ passwordPromptContext.mode === 'delete' ? t('list.delete') : t('passwordPrompt.title') }}</h2>
          <p v-if="passwordPromptContext.mode === 'delete'" class="modal__subtitle">{{ t('confirm.deletePasswordPrompt', { name: passwordPromptContext.item.originalName }) }}</p>
          <p v-else-if="passwordPromptContext.mode === 'single'" class="modal__subtitle">{{ t('passwordPrompt.unlockSingle', { name: passwordPromptContext.item.originalName }) }}</p>
          <p v-else-if="passwordPromptContext.mode === 'batch'" class="modal__subtitle">{{ t('passwordPrompt.unlockBatch', { count: passwordPromptContext.group.items.length, preview: batchPreviewText(passwordPromptContext.group.items) }) }}</p>
          <!-- 這三種是資料夾防護用途：密碼欄位輸入的是資料夾防護的共用密碼，不是加密/解密密碼。 -->
          <p v-else-if="passwordPromptContext.mode === 'folderGuardUnlock'" class="modal__subtitle">{{ t('folderGuard.unlockPasswordPrompt', { path: passwordPromptContext.item.path }) }}</p>
          <p v-else-if="passwordPromptContext.mode === 'folderGuardUnlockAll'" class="modal__subtitle">{{ t('folderGuard.unlockAllPasswordPrompt') }}</p>
          <p v-else-if="passwordPromptContext.mode === 'folderGuardNestedEncrypt'" class="modal__subtitle">{{ t('folderGuard.nestedGuardedPasswordPrompt') }}</p>
          <p v-else-if="passwordPromptContext.mode === 'folderGuardDisable'" class="modal__subtitle">{{ t('folderGuard.disablePasswordPrompt') }}</p>
          <p v-else-if="passwordPromptContext.mode === 'folderGuardDisablePasskey'" class="modal__subtitle">{{ t('folderGuard.disablePasskeyPasswordPrompt') }}</p>
          <!-- 這兩種是密碼庫用途：密碼欄位輸入的是密碼庫的密碼，跟加密/資料夾防護密碼是完全不同的命名空間。 -->
          <div class="password-field">
            <input
              ref="passwordPromptInputRef"
              v-model="passwordPromptValue"
              :type="showPasswordPromptValue ? 'text' : 'password'"
              class="text-input"
              @keyup.enter="submitPasswordPrompt"
            />
            <button
              type="button"
              class="password-field__toggle"
              :aria-label="t(showPasswordPromptValue ? 'common.hidePassword' : 'common.showPassword')"
              @click="showPasswordPromptValue = !showPasswordPromptValue"
            >
              <svg v-if="showPasswordPromptValue" viewBox="0 0 24 24" fill="none"><path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/><circle cx="12" cy="12" r="2.75" stroke="currentColor" stroke-width="1.6"/></svg>
              <svg v-else viewBox="0 0 24 24" fill="none"><path d="M3 3l18 18M9.9 5.1A10.7 10.7 0 0 1 12 5.5c6 0 9.5 6.5 9.5 6.5a17.1 17.1 0 0 1-3.15 4.05M6.5 6.9C4.1 8.6 2.5 12 2.5 12s3.5 6.5 9.5 6.5c1.1 0 2.1-.2 3-.55M14.1 14.1a2.75 2.75 0 0 1-3.9-3.9" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>
            </button>
          </div>
          <div class="modal__footer">
            <button class="button button--secondary" @click="cancelPasswordPrompt" type="button">{{ t('passwordPrompt.cancel') }}</button>
            <button
              class="button"
              :class="passwordPromptContext.mode === 'delete' ? 'button--danger' : 'button--primary'"
              @click="submitPasswordPrompt"
              type="button"
              :disabled="!passwordPromptValue"
            >
              {{ passwordPromptContext.mode === 'delete' ? t('list.delete') : t('passwordPrompt.unlock') }}
            </button>
          </div>
        </div>
      </div>
    </Transition>

    <!-- 恢復金鑰輸入彈窗：清單頁按「恢復金鑰解鎖」後跳出。 -->
    <Transition name="modal">
      <div v-if="recoveryKeyPromptItem" class="modal-overlay" @click.self="cancelRecoveryKeyPrompt">
        <div class="modal">
          <h2 class="modal__title">{{ t('recoveryKeyPrompt.title') }}</h2>
          <p class="modal__subtitle">{{ t('recoveryKeyPrompt.unlock', { name: recoveryKeyPromptItem.originalName }) }}</p>
          <textarea
            ref="recoveryKeyInputRef"
            v-model="recoveryKeyInputValue"
            rows="3"
            class="text-input text-input--mono"
            :placeholder="t('recoveryKeyPrompt.placeholder')"
          ></textarea>
          <div class="modal__footer">
            <button class="button button--secondary" @click="cancelRecoveryKeyPrompt" type="button">{{ t('recoveryKeyPrompt.cancel') }}</button>
            <button class="button button--primary" @click="submitRecoveryKeyDecrypt" type="button" :disabled="!recoveryKeyInputValue.trim()">{{ t('recoveryKeyPrompt.submit') }}</button>
          </div>
        </div>
      </div>
    </Transition>

  </div>
</template>

<style>
/* 回饋：側欄切換分頁時，強調色（金→藍之類）是瞬間跳色，不是連續漸變——側欄本身的
   .app-sidebar__nav-highlight／.app-sidebar__nav-item 早就寫了 background-color／color
   的 transition，但那些其實從來沒真的生效過：CSS 自訂屬性（--color-accent 這些）預設
   不是「可動畫」的型別，瀏覽器換算引用它的屬性值時是直接重新取值、不會在新舊兩個顏色
   之間內插，所以就算宣告了 transition，畫面上看到的還是瞬間切換。用 @property 把這幾個
   會隨分頁主題色切換的自訂屬性註冊成 <color> 型別，瀏覽器才知道「這是一個顏色，換值時
   要內插」，下面既有的 transition 才會真的對顏色本身生效，不只是對色塊的位移生效。 */
@property --color-accent {
  syntax: '<color>';
  inherits: true;
  initial-value: #A8770F;
}
@property --color-accent-hover {
  syntax: '<color>';
  inherits: true;
  initial-value: #8C630C;
}
@property --color-accent-soft {
  syntax: '<color>';
  inherits: true;
  initial-value: #FBF2DE;
}
@property --color-accent-border {
  syntax: '<color>';
  inherits: true;
  initial-value: #E4C77E;
}

/* UI/UX 走查：設定頁切換亮色/深色主題時，整個畫面的底色/文字色是瞬間硬切，不是漸變——
   apple-design skill 明講「ease dark↔light theme changes」，瞬間切換的明暗跳動容易讓人
   不舒服。理由跟上面 --color-accent 系列一樣：中性色 token 本身也要註冊成 <color> 型別，
   .app 下面的 transition 才能真的對顏色內插，不是只是宣告好看。 */
@property --color-bg {
  syntax: '<color>';
  inherits: true;
  initial-value: #F4F3F0;
}
@property --color-surface {
  syntax: '<color>';
  inherits: true;
  initial-value: #FFFDF8;
}
@property --color-text {
  syntax: '<color>';
  inherits: true;
  initial-value: #22221E;
}
@property --color-border {
  syntax: '<color>';
  inherits: true;
  initial-value: #E0DDD5;
}

:root {
  /* ---- 色彩：扣著「鎖與鑰匙」這個主題發想 ----
     中性色（--color-bg／--color-surface／--color-border*／--color-text*）這輪從原本偏冷灰藍
     的配色，換成 design-exploration/gui-styles-v2/13-sidebar-ticket-shell.html 定案的暖米白／
     牛皮紙調性（對應該檔案的 --vault-steel／--paper／--vault-line*／--ink* token）——強調色
     （--color-accent）數值本來就跟該檔案的 --brass 完全相同，沒有變動。移植前的舊色票記錄在
     design-exploration/gui-styles-v2/App-vue_移植前色彩快照.md，決策細節見
     design-exploration/gui-styles-v2/GUI造型探索_技術規格.md §2.14。 */
  /* 回饋：拖動條（捲軸）沒有跟著深色模式換色——瀏覽器原生捲軸看的是 color-scheme 這個
     CSS 屬性，不是看畫面實際顏色，沒宣告的話 Chromium 一律畫淺色捲軸，跟底下已經換成
     深色的內容格格不入。這裡宣告 light，.app--dark 那邊蓋成 dark，捲軸才會跟著切換。 */
  color-scheme: light;
  /* 回饋：拖動條的底色沒有改成跟背景一樣的顏色——color-scheme 只解決「捲軸整體用淺色
     還是深色配色」，Chromium 自己畫的滑軌（track）底色是它內建的中性灰階，不是這個 App
     的 --color-bg／--color-surface，緊貼著暖米白／牛皮紙背景看起來像一條格格不入的灰條。
     WebView2 底層是 Chromium，這裡直接用 ::-webkit-scrollbar 系列選擇器蓋掉滑軌底色（設成
     transparent，讓底下容器自己的背景透出來，不管是哪個分頁/彈窗各自的背景色都自動吃到，
     不用每個捲動容器分別調一次），拉桿本身維持中性的 --color-border-strong，hover 時
     加深到 --color-text-tertiary 這個既有的中性色階，不需要另外定義新顏色。 */

  --color-bg: #F4F3F0;
  --color-surface: #FFFDF8;
  --color-border: #E0DDD5;
  --color-border-strong: #CFCAC0;
  --color-text: #22221E;
  --color-text-secondary: #63604F;
  --color-text-tertiary: #8B8776;
  --color-accent: #A8770F;
  --color-accent-hover: #8C630C;
  --color-accent-soft: #FBF2DE;
  --color-accent-border: #E4C77E;
  --color-success: #2E7D4F;
  --color-success-soft: #E7F4EC;
  --color-danger: #B14328;
  --color-danger-soft: #FBEBE6;

  /* 六個分頁各自的主題色——不只是頁首圖示的淡色圓底，整頁的按鈕／連結／checkbox／
     focus ring 都會跟著這組色（見下面 .theme-* 修飾類別，套在每個分頁最外層的容器上，
     靠 CSS 自訂屬性往下層層覆蓋掉 --color-accent 系列，不用一一去改每個元件）。
     跟上面「有意義」的語意色（success＝結果成功、danger＝結果失敗／警告）刻意分開一組
     獨立 token，避免共用同一個變數造成「這個顏色到底代表分頁主題還是操作結果」混淆——
     即使解密頁的綠色看起來很接近 --color-success，兩者仍是各自獨立的數值。
     加密／資料夾防護沿用同一款金色（本來就是這個 App 的主色），解密＝綠、已加密清單＝原本
     解密頁用過的藍、密碼庫＝橘紅，彼此好分辨又不跟語意色搶意義。 */
  --tint-decrypt: #2F7D46;
  --tint-decrypt-hover: #256339;
  --tint-decrypt-soft: #E6F3EA;
  --tint-decrypt-border: #A9D6B7;
  --tint-list: #3568B0;
  --tint-list-hover: #28517F;
  --tint-list-soft: #E4EEFB;
  --tint-list-border: #A9C8EA;
  --tint-guard: #A8770F;
  --tint-guard-hover: #8C630C;
  --tint-guard-soft: #FBF2DE;
  --tint-guard-border: #E4C77E;
  /* 密碼庫「橘紅」刻意選比 --color-danger（#B14328，偏暗紅棕、低飽和度的「磚紅」）色相再往
     橘色偏一點、飽和度更高的「亮橘」——早期版本色相太接近 danger，新增帳密這種主要按鈕跟
     警告色混在一起，容易誤判成危險操作。 */
  --tint-vault: #C9690A;
  --tint-vault-hover: #A8560A;
  --tint-vault-soft: #FBEDDA;
  --tint-vault-border: #F0C48A;
  --tint-settings: #5B6270;
  --tint-settings-hover: #454B57;
  --tint-settings-soft: #E7E9ED;
  --tint-settings-border: #C7CBD3;

  --font-ui: 'IBM Plex Sans', -apple-system, 'Segoe UI', sans-serif;
  --font-mono: 'IBM Plex Mono', 'Cascadia Code', 'Consolas', monospace;

  --radius-sm: 6px;
  --radius-md: 10px;
  --radius-lg: 16px;

  /* ---- 陰影：用來做出真正的層次深度，取代單薄的 1px 邊框 ---- */
  --shadow-xs: 0 1px 2px rgba(20, 22, 30, 0.05);
  --shadow-sm: 0 1px 3px rgba(20, 22, 30, 0.04), 0 8px 20px rgba(20, 22, 30, 0.06);
  --shadow-md: 0 4px 10px rgba(20, 22, 30, 0.06), 0 16px 32px rgba(20, 22, 30, 0.08);
  --shadow-modal: 0 24px 64px rgba(20, 22, 30, 0.28), 0 2px 8px rgba(20, 22, 30, 0.12);

  /* ---- 動效：進場用 ease-out（不用內建的弱曲線），離場更快 ---- */
  --ease-out: cubic-bezier(0.23, 1, 0.32, 1);
  --duration-fast: 150ms;
  --duration-base: 200ms;
}

/* ---- 捲軸：滑軌透明（讓底下容器自己的背景透出來，不用每個捲動容器分別配一次色），
   拉桿用中性的 --color-border-strong，兩個變數都會隨 .app--dark 一起變色，這裡不用另外
   寫一份深色版本。全域套用（不限定 .page 這個容器），任何彈窗/清單內部有自己捲動的地方
   都一併套到。 ---- */
::-webkit-scrollbar {
  width: 10px;
  height: 10px;
}

::-webkit-scrollbar-track {
  background: transparent;
}

::-webkit-scrollbar-thumb {
  background-color: var(--color-border-strong);
  border-radius: 999px;
  border: 2px solid transparent;
  background-clip: padding-box;
}

::-webkit-scrollbar-thumb:hover {
  background-color: var(--color-text-tertiary);
}

/* ---- 深色模式：色彩變數整組覆蓋，其他所有樣式規則都直接沿用同一套 var()，不用另外寫
   一份深色專用的樣式。強調色（黃銅）在深色背景上調亮一點，不然對比度不夠、看起來髒髒的。 ---- */
.app--dark {
  /* 回饋：深色模式底色偏黃偏怪——原本 bg/surface/border 這幾個中性色的 R 通道比 B 通道
     高出快 10 個色階，大面積鋪開時人眼對這種接近中性但偏一邊的色相特別敏感，讀起來就是
     「髒黃色」而不是「暖灰」。這裡把 R-B 的落差壓小、整體往中性炭灰靠，保留一點點暖度
     （不是變成死板的純灰階，跟淺色模式的暖米白調性還是有呼應），但不再讓人覺得是黃色。 */
  color-scheme: dark;
  --color-bg: #1D1C1B;
  --color-surface: #222120;
  --color-border: #363330;
  --color-border-strong: #47433D;
  --color-text: #EDEAE0;
  --color-text-secondary: #B7B09B;
  --color-text-tertiary: #847D68;
  /* --color-accent／--color-danger 原本為了在深色背景上維持文字/圖示的可讀性刻意調亮，
     但這組色同時也拿來當 .button--primary／.button--danger 的實心底色（配白色文字）——
     亮度沒收斂的話，整顆按鈕在深色背景裡看起來像在發光，不是「深色模式的強調色」該有的
     份量。調暗到跟淺色模式下同一顆按鈕差不多的視覺重量（淺色模式的金色按鈕本來就沒人覺得
     太亮），可讀性測試沿用「白字配這個底色」這組既有搭配，不是全新組合。 */
  --color-accent: #A37E2C;
  --color-accent-hover: #BA943F;
  --color-accent-soft: #3A3220;
  --color-accent-border: #6B5726;
  --color-success: #4EAE76;
  --color-success-soft: #1E3327;
  --color-danger: #A9553E;
  --color-danger-soft: #3A2620;

  --tint-decrypt: #4FAE6E;
  --tint-decrypt-hover: #66C084;
  --tint-decrypt-soft: #1E3327;
  --tint-decrypt-border: #3E6B4D;
  /* 回饋：資料夾防護頁按鈕的藍色在深色模式下太亮太刺眼——原本 #7FAEE8 飽和度/明度都偏高，
     跟深色背景對比過強；調暗、降一點飽和度，維持同一個色相不換色（不然使用者已經記得
     「資料夾防護＝藍色」這個分頁辨識，換色相會打斷這個既有的心智模型）。 */
  --tint-list: #6B93C9;
  --tint-list-hover: #85A9DA;
  --tint-list-soft: #202B3B;
  --tint-list-border: #3C5A80;
  --tint-guard: #A37E2C;
  --tint-guard-hover: #BA943F;
  --tint-guard-soft: #3A3220;
  --tint-guard-border: #6B5726;
  --tint-vault: #B37A40;
  --tint-vault-hover: #C49260;
  --tint-vault-soft: #3B2C1B;
  --tint-vault-border: #6E4E29;
  --tint-settings: #ADB2BC;
  --tint-settings-hover: #C5C9D1;
  --tint-settings-soft: #2B2D32;
  --tint-settings-border: #6B7078;
}

/* 分頁主題色：套在 .app 最外層（見 activeThemeClass），往下覆蓋掉 --color-accent 系列——
   分頁內容、以及跟分頁內容平行的彈窗（新增密碼、驗證彈窗、確認對話框…）裡所有本來就吃
   var(--color-accent*) 的元件（主要按鈕、連結、checkbox、輸入框 focus ring…）會自動跟著
   換色，不用逐一改元件本身的樣式。「加密」（含信封疊層、已加密清單）跟全域預設同一款
   金色，不需要覆蓋規則；「資料夾防護」改套 .theme-list 這組藍色（回饋：金銅黃色留給加密頁，
   原本 list 專屬的藍色挪去資料夾防護頁，class 名稱沿用歷史命名沒有改）。 */
.theme-decrypt {
  --color-accent: var(--tint-decrypt);
  --color-accent-hover: var(--tint-decrypt-hover);
  --color-accent-soft: var(--tint-decrypt-soft);
  --color-accent-border: var(--tint-decrypt-border);
}

.theme-list {
  --color-accent: var(--tint-list);
  --color-accent-hover: var(--tint-list-hover);
  --color-accent-soft: var(--tint-list-soft);
  --color-accent-border: var(--tint-list-border);
}

.theme-vault {
  --color-accent: var(--tint-vault);
  --color-accent-hover: var(--tint-vault-hover);
  --color-accent-soft: var(--tint-vault-soft);
  --color-accent-border: var(--tint-vault-border);
}

/* 回饋：側欄「設定」頁的顏色跟加密頁共用同一款金色，容易混淆——設定頁本來就有自己一套
   低飽和度灰藍色 tint-settings（原本只用在側欄設定圖示本身，見 .app-sidebar__nav-item
   對應樣式），這裡補上完整一組（含 hover/border）讓整個設定分頁也套用，不再共用加密頁
   的金色。 */
.theme-settings {
  --color-accent: var(--tint-settings);
  --color-accent-hover: var(--tint-settings-hover);
  --color-accent-soft: var(--tint-settings-soft);
  --color-accent-border: var(--tint-settings-border);
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
}

.app {
  font-family: var(--font-ui);
  color: var(--color-text);
  background: var(--color-surface);
  /* 亮色/深色主題切換時整頁明暗漸變，不是瞬間硬切——搭配上面 --color-text／--color-surface
     的 @property 註冊才會真的生效。 */
  transition: background-color 200ms ease, color 200ms ease;
  /* 改成固定滿版高度的 flex 直向排列，標題列跟頁籤列是不會縮的固定項目，
     只有底下的內容區（.page-wrapper）自己捲動——不然內容一多，整個文件（含標題列、
     三顆視窗控制按鈕）會一起被捲走，使用者往下滑就看不到、按不到那些按鈕了。 */
  height: 100vh;
  display: flex;
  flex-direction: column;
  font-size: 14px;
  line-height: 1.55;
  -webkit-font-smoothing: antialiased;
  overflow-x: hidden;
  text-align: left;
  /* 使用者回饋：雙擊畫面上任何文字（不管是顯示用的內文還是按鈕上的字）都會被選取，
     這是網頁預設行為，但在一個桌面應用程式裡看起來很突兀（原生應用程式的介面文字
     本來就選不到）。整個 .app 底下預設關掉文字選取，輸入框／文字區域／明確標記
     contenteditable 或需要複製的內容（例如 .recovery-key-display，見下方該規則的
     user-select: all）另外個別開回來，不能一刀切連恢復金鑰、密碼這種使用者需要
     複製的內容也選不到。 */
  user-select: none;
}

.app input,
.app textarea,
.app [contenteditable='true'] {
  user-select: text;
}

/* Vite 專案範本預設的 style.css 會設定 #app { text-align: center } 跟 h1 的字級/顏色，
   跟這裡的設計系統直接衝突（文字被強制置中、標題顏色被蓋掉）。正解是把那份 import 從
   main.js 移除；這幾條是防禦性覆蓋，確保就算它還在也不會影響畫面。 */
#app {
  max-width: none;
  margin: 0;
  padding: 0;
  text-align: left;
}

.app h1,
.app h2,
.app h3 {
  color: var(--color-text);
  font-size: inherit;
  line-height: inherit;
}

/* ---- 自訂標題列（macOS 風格三顆按鈕）----
   整條標題列標記成可拖曳區域，由作業系統原生處理拖曳；按鈕本身要標記成 no-drag，
   不然滑鼠按下去只會開始拖動視窗、永遠按不到按鈕。 */
.title-bar {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  height: 38px;
  flex-shrink: 0;
  padding: 0 0.85rem;
  background: var(--color-surface);
  app-region: drag;
  -webkit-app-region: drag;
  user-select: none;
  position: relative;
  z-index: 2;
}

.traffic-lights {
  display: flex;
  align-items: center;
  /* 回饋：可點擊範圍要比看得到的圓點大，視覺大小不變——按鈕本身放大成 20x20（見下面
     .traffic-light），gap 從原本的 8px 收成 0，兩顆圓點中心的距離維持原本 12+8=20px
     不變，畫面上看起來跟改之前完全一樣。 */
  gap: 0;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}

.traffic-light {
  appearance: none;
  width: 20px;
  height: 20px;
  padding: 0;
  border: none;
  background: transparent;
  border-radius: 50%;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  position: relative;
  /* 符號平常隱形，游標移到整組按鈕上才浮現——這是 macOS 的作法，
     沒有互動時三顆燈維持乾淨的純色圓點。 */
  color: rgba(0, 0, 0, 0);
  transition: color var(--duration-fast) ease;
}

/* 圓點本身拆成獨立元素（pointer-events: none，只負責畫色塊，不參與點擊判定——判定
   交給外層 20x20 的按鈕），視覺尺寸維持原本的 12x12，用 inset:4px 置中在 20x20 的
   按鈕正中間（20 - 4*2 = 12）。 */
.traffic-light__dot {
  position: absolute;
  inset: 4px;
  border-radius: 50%;
  pointer-events: none;
  transition: filter var(--duration-fast) ease;
}

.traffic-light--close .traffic-light__dot {
  background: #FF5F57;
}

.traffic-light--minimize .traffic-light__dot {
  background: #FEBC2E;
}

.traffic-light--maximize .traffic-light__dot {
  background: #28C840;
}

/* 紅綠燈三顆各自跟自己的顏色一致，不要三顆都套用全域的強調色焦點框。 */
.traffic-light--close:focus-visible {
  outline-color: #FF5F57;
}

.traffic-light--minimize:focus-visible {
  outline-color: #FEBC2E;
}

.traffic-light--maximize:focus-visible {
  outline-color: #28C840;
}

.traffic-lights:hover .traffic-light {
  color: rgba(0, 0, 0, 0.55);
}

.traffic-light:hover .traffic-light__dot {
  filter: brightness(0.92);
}

.traffic-light:active .traffic-light__dot {
  filter: brightness(0.82);
}

.traffic-light__glyph {
  position: absolute;
  width: 12px;
  height: 12px;
  display: block;
}

.title-bar__title {
  font-size: 0.8rem;
  font-weight: 500;
  color: var(--color-text-tertiary);
  letter-spacing: 0.01em;
}

/* ---- Windows 造型・原生風：方形按鈕貼右上角、縮小/最大化/關閉順序，hover/active
   整塊變色，貼近 Windows 11 原生行為的簡化版（不追求像素級一致，見 CLAUDE.md 待辦
   第 7.3 節）。 ---- */
.win-controls {
  display: flex;
  align-items: center;
  gap: 0;
  margin-left: auto;
  height: 100%;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}

.win-btn {
  appearance: none;
  width: 46px;
  height: 38px;
  border: none;
  background: transparent;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  color: var(--color-text-secondary);
  transition: background-color 120ms ease, color 120ms ease;
}

.win-btn svg {
  width: 10px;
  height: 10px;
  display: block;
}

.win-btn:hover {
  background: rgba(0, 0, 0, 0.06);
  color: var(--color-text);
}

.app--dark .win-btn:hover {
  background: rgba(255, 255, 255, 0.08);
}

.win-btn--close:hover {
  background: #C42B1C;
  color: #fff;
}

/* 回饋：按下去要有反應——hover 只有一種狀態時，按著跟沒按著看起來一樣。 */
.win-btn:active {
  background: rgba(0, 0, 0, 0.12);
}

.app--dark .win-btn:active {
  background: rgba(255, 255, 255, 0.14);
}

.win-btn--close:active {
  background: #A4241A;
}

/* ---- Windows 造型・風格化版：不用原生方形貼邊，改用跟 macOS 燈號同一套「圓角小
   按鈕＋間距」語彙，顏色換成 App 自己的強調色/危險色，不是 OS 原生的紅/灰，平常
   透明看不到方塊、hover 才浮現色底。回饋：可點擊範圍要比看得到的圖示大，視覺大小
   不變——按鈕平常透明，放大成 30x30 不會改變「看起來的大小」，使用者平常只看到
   置中的 10x10 線條圖示，只有點擊判定範圍變大。 ---- */
.win-controls--styled {
  gap: 4px;
  padding-right: 2px;
}

.win-btn-styled {
  appearance: none;
  width: 30px;
  height: 30px;
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--color-text-secondary);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background-color 120ms ease, color 120ms ease, transform 120ms ease;
}

.win-btn-styled svg {
  width: 10px;
  height: 10px;
  display: block;
}

/* 回饋：縮小/最大化鈕的 hover 顏色太淡，跟標題列底色幾乎融在一起——
   --color-accent-soft／--color-danger-soft 這兩個 token 是為了大面積色塊（例如整顆按鈕背景、
   資訊框底色）調的低對比淡色，鋪在小按鈕上、緊貼著同樣偏白的 --color-surface 標題列時對比
   不夠。改用固定透明度的強調色/危險色疊色（不是 token），不管在哪個分頁主題色底下都能維持
   一致、看得出來的對比。 */
.win-btn-styled:hover {
  background: rgba(168, 119, 15, 0.14);
  color: var(--color-accent);
}

.win-btn-styled:active {
  transform: scale(0.92);
}

.app--dark .win-btn-styled:hover {
  background: rgba(163, 126, 44, 0.22);
}

.win-btn-styled--close:hover {
  background: rgba(177, 67, 40, 0.16);
  color: var(--color-danger);
}

.app--dark .win-btn-styled--close:hover {
  background: rgba(169, 85, 62, 0.24);
  color: #D98670;
}

/* ---- 側欄殼子取代原本的頂部頁籤列（design-exploration/gui-styles-v2 §3.3 定案版本）----
   .tab-bar／.tab-bar__item／.tab-bar__indicator 這組樣式連同滑動指示條的量測邏輯一起移除
   （見 script 區塊「側欄導覽」註解），AppSidebar.vue 自己帶了 scoped 樣式，這裡不用重寫。 */

/* ---- 主要內容區：側欄固定寬度＋主內容區各自獨立捲動，不讓側欄跟著主內容一起被捲走。
     .page-wrapper 本身不捲動（只負責左右排列），捲動交給 .page 自己，這樣側欄才會維持
     固定在畫面上，不會捲出視窗外。 ---- */
.page-wrapper {
  display: flex;
  flex: 1;
  overflow: hidden;
}

.page {
  max-width: 760px;
  width: 100%;
  margin: 0 auto;
  padding: 2rem 2.5rem 3rem;
  text-align: left;
  overflow-y: auto;
  /* `overflow-y: auto` 讓瀏覽器把這個捲動容器本身也算進可鍵盤聚焦的元素，套用全域焦點框
     樣式看起來會像一個跑版的大方框，這個容器本身不是有意義的互動目標，直接關掉焦點框。 */
  outline: none;
  /* 刻意不對 max-width 做過渡——分頁切換時內容本身已經有 .tab-page 淡入淡出，寬度如果
     也跟著平滑放大/縮小，兩個動畫疊在一起會變成「內容還看得到、框卻在動」的縮放感，
     混亂。讓寬度乾脆瞬間跳過去，切換的那一刻剛好也是內容淡到看不見的時候，感覺不出來。 */
}

/* 表單類頁面（加密／解密／設定）刻意維持適中寬度——密碼欄位、勾選項這種內容，
   拉滿整個視窗寬度只會讓每一行變得又長又空洞，讀起來反而更費力，不是每個頁面都適合
   隨視窗寬度伸展。已加密清單頁的表格則相反：資料列多一點空間才讀得舒服，
   讓它隨視窗寬度伸展，最大化時能看到更多內容而不是兩側留白。 */
.page--wide {
  max-width: 1180px;
}

/* 選擇器刻意寫成 .app h1.page-title（不是單純 .page-title）：CSS 優先權要贏過上面
   .app h1,.app h2,.app h3 那條 font-size:inherit／line-height:inherit 的規則——單純
   `.page-title`（一個 class）優先權比 `.app h1`（一個 class+一個元素選擇器）低，一直被
   悄悄蓋掉，六個分頁的標題實際上從來沒有真的套用到這裡設定的字級，全部改成繼承父層算出來
   的字級（跟這裡宣告的值不一樣、只是剛好視覺上一致沒被發現）。這裡用兩個 class（`.app`
   + `.page-title`）+ 一個元素選擇器的組合明確贏過它，不是靠來源順序這種容易被之後改動
   打亂的方式取巧贏。字級維持既有畫面看到的大小（0.875rem，不是原本寫的 1.375rem——
   1.375rem 從來沒有真的生效過，改成生效反而會讓所有分頁標題一次變大，不是這次要的效果）。 */
.app h1.page-title {
  display: flex;
  align-items: center;
  gap: 0.55rem;
  font-size: 0.875rem;
  font-weight: 600;
  letter-spacing: -0.02em;
  line-height: 1.2;
  margin: 0 0 1.75rem;
  color: var(--color-text);
  opacity: 1;
  text-align: left;
}

/* 六個分頁的圖示原本全部同一種樣式（22px 單色線框），視覺上幾乎沒辦法用「掃一眼」分辨，
   要真的讀標題文字才知道在哪一頁。改成統一的「淡色圓底徽章」語彙（跟空狀態圖示是同一套
   手法），每頁只在底色的色相上做區別——形狀／尺寸／邊框樣式完全一致，不是每頁一套全新
   視覺系統，維持 Nielsen 一致性原則。 */
.page-title__icon {
  width: 20px;
  height: 20px;
  padding: 8px;
  box-sizing: content-box;
  border-radius: 999px;
  color: var(--color-accent);
  background: var(--color-accent-soft);
  flex-shrink: 0;
}

.page-title__icon--decrypt {
  color: var(--tint-decrypt);
  background: var(--tint-decrypt-soft);
}

/* 這兩個 class 名稱維持原樣（跟 CSS 自訂屬性名稱一起沿用歷史命名），但實際引用的顏色
   變數對調了——「加密」（原本的清單頁）現在改用金色，「資料夾防護」改用藍色（原本
   list 頁的顏色），對應側欄主題色分配（見 THEME_CLASS_BY_TAB 那段註解）。 */
.page-title__icon--list {
  color: var(--tint-guard);
  background: var(--tint-guard-soft);
}

.page-title__icon--guard {
  color: var(--tint-list);
  background: var(--tint-list-soft);
}

.page-title__icon--vault {
  color: var(--tint-vault);
  background: var(--tint-vault-soft);
}

.page-title__icon--settings {
  color: var(--tint-settings);
  background: var(--tint-settings-soft);
}

.step-indicator {
  margin: -1.2rem 0 1.25rem;
  font-size: 0.8rem;
  font-weight: 500;
  color: var(--color-text-tertiary);
  text-align: left;
}

/* 加密完成頁（步驟三）的標題區——置中的打勾圖示＋文字，跟前兩步的表單排版做出區別。 */
.encrypt-complete {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 1.5rem;
  text-align: center;
}

.encrypt-complete__icon {
  width: 40px;
  height: 40px;
  color: var(--color-accent);
}

.encrypt-complete__title {
  font-size: 1.1rem;
  font-weight: 600;
  color: var(--color-text);
  margin: 0;
}

/* ---- 表單欄位 ---- */
.field {
  margin-bottom: 1.375rem;
  text-align: left;
}

.field__label {
  display: block;
  font-size: 0.825rem;
  font-weight: 500;
  color: var(--color-text-secondary);
  margin-bottom: 0.4rem;
}

.text-input,
.select-input {
  width: 100%;
  font-family: inherit;
  font-size: 0.9rem;
  color: var(--color-text);
  background: var(--color-surface);
  border: 1px solid var(--color-border-strong);
  border-radius: var(--radius-sm);
  padding: 0.55rem 0.7rem;
  transition: border-color var(--duration-fast) ease, box-shadow var(--duration-fast) ease;
}

.text-input--mono {
  font-family: var(--font-mono);
  font-size: 0.85rem;
}

.text-input:focus,
.select-input:focus {
  outline: none;
  border-color: var(--color-accent);
  box-shadow: 0 0 0 3px var(--color-accent-soft);
}

/* 全域鍵盤 Tab 焦點樣式：瀏覽器預設焦點框是一圈黑色，跟畫面完全脫節。改成跟輸入框
   focus ring 同一種語言（var(--color-accent) 外框），且自動吃 activeThemeClass 分頁主題色
   （見 .theme-decrypt／.theme-list／.theme-vault 對 --color-accent 的覆蓋）,不用逐一
   針對按鈕/分頁項目另外寫規則。只吃 :focus-visible（鍵盤/程式化 focus），不吃滑鼠點擊，
   滑鼠使用者不會被這圈框打擾。 */
button:focus-visible,
a:focus-visible,
[tabindex]:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
  border-radius: var(--radius-sm);
}

.password-field {
  position: relative;
}

/* WebView2（Chromium）內建的密碼欄位「顯示密碼」眼睛圖示會跟這裡自訂的 .password-field__toggle
   疊在一起，變成同一個欄位出現兩個眼睛圖示——關掉瀏覽器原生的那顆，只留自訂的。 */
.password-field input[type="password"]::-ms-reveal,
.password-field input[type="password"]::-ms-clear {
  display: none;
}

.password-field .text-input {
  padding-right: 2.4rem;
}

.password-field__toggle {
  position: absolute;
  top: 50%;
  right: 0.5rem;
  transform: translateY(-50%);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: none;
  background: none;
  color: var(--color-text-tertiary);
  cursor: pointer;
  border-radius: var(--radius-sm);
  transition: color var(--duration-fast) ease, transform 160ms var(--ease-out);
}

.password-field__toggle:hover {
  color: var(--color-text);
}

/* 跟 .button:active 同一個回饋原則——眼睛圖示雖然不是 <button class="button">，
   一樣是可點擊元素，按下去也要有立即回饋，不能只有滑鼠移過去的 hover 變色。
   一定要疊加 translateY(-50%)，不能只寫 scale(0.9)——CSS 的 transform 是單一屬性值，
   :active 這條規則會整個覆蓋掉 .password-field__toggle 原本用來垂直置中的
   translateY(-50%)，按下去的瞬間置中效果消失，圖示會整個往下跳半個按鈕高度，
   看起來像誇張的彈跳，不是單純的縮小回饋。 */
.password-field__toggle:active {
  transform: translateY(-50%) scale(0.9);
}

/* --inline 變體的 base transform 是 none（不是 translateY(-50%)），沒有上面那個
   疊加問題，維持單純的 scale 就好。 */
.password-field__toggle--inline:active {
  transform: scale(0.9);
}

/* 密碼庫清單裡的顯示/隱藏按鈕跟輸入欄裡的眼睛圖示共用同一顆元件，但這裡不是疊在
   輸入欄右側（不需要 absolute + translateY 置中），改用一般文件流定位。 */
.password-field__toggle--inline {
  position: static;
  top: auto;
  right: auto;
  transform: none;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
}

.password-field__toggle svg {
  width: 18px;
  height: 18px;
}

textarea.text-input {
  resize: vertical;
}

.select-input {
  width: auto;
  min-width: 200px;
}

/* 分類清單標題下面那顆排序下拉：跟表單/工具列裡的下拉選單不同，這裡旁邊沒有其他
   控制項要對齊寬度，維持 200px 最小寬度只會在文字右側留一大片沒用到的空白，
   縮到跟文字本身差不多寬即可。 */
.select-input--compact {
  min-width: 0;
}

.checkbox-field {
  display: flex;
  align-items: flex-start;
  gap: 0.55rem;
  font-size: 0.875rem;
  color: var(--color-text);
  cursor: pointer;
  line-height: 1.65;
  line-break: strict;
  text-wrap: pretty;
}

.checkbox-field.is-disabled {
  color: var(--color-text-tertiary);
  cursor: not-allowed;
}

.checkbox-field input {
  margin-top: 0.2rem;
  accent-color: var(--color-accent);
}

/* 核取方塊本身是瀏覽器原生元件，實測 Chromium 對它的 outline 完全不理會作者設的
   border-radius（就算直接、無條件地設 border-radius 在 input 本身，算出來的
   computed style 還是 0px）——這是原生表單元件的限制，不是我們的 CSS 寫錯。焦點框
   要有圓角，只能畫在外面包一層的容器上，不能畫在 input 自己身上。這裡先把 input
   自己的原生方形焦點框關掉，改成分別在下面兩種情境的外層容器上畫圓角焦點框：
   ①已經用 `<label class="checkbox-field">` 包住核取方塊＋文字的地方（多數情況），
   ②密碼庫清單那種沒有任何包裝、直接放進表格儲存格的裸 `<input>`（唯一一處，另外包了
   一層 `.checkbox-ring`）。 */
input[type='checkbox']:focus-visible {
  outline: none;
}

.checkbox-field:focus-within {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
  border-radius: var(--radius-sm);
}

.checkbox-ring {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 4px;
}

.checkbox-ring:focus-within {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
  border-radius: 4px;
}

.checkbox-field__icon {
  width: 16px;
  height: 16px;
  margin-top: 0.15rem;
  flex-shrink: 0;
}

/* 資訊提示框：把原本一長串塞在勾選項後面的說明文字收起來，滑鼠移過去（或鍵盤 focus）
   才顯示，平常畫面乾淨很多。tabindex="0" 讓鍵盤使用者也能用 Tab 鍵觸發，不是只有滑鼠。 */
.info-tooltip {
  position: relative;
  display: inline-flex;
  align-items: center;
  margin-top: 0.15rem;
  outline: none;
}

.info-tooltip__icon {
  width: 15px;
  height: 15px;
  border-radius: 50%;
  background: var(--color-border-strong);
  color: var(--color-surface);
  font-size: 0.68rem;
  font-style: italic;
  font-family: Georgia, 'Times New Roman', serif;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: help;
  flex-shrink: 0;
  transition: background-color var(--duration-fast) ease;
}

.info-tooltip:hover .info-tooltip__icon,
.info-tooltip:focus-visible .info-tooltip__icon {
  background: var(--color-accent);
}

/* 「i」用斜體襯線字體是刻意的字體選擇（看起來像印刷體的資訊符號），但同一顆殼子如果放的是
   「?」，斜體襯線的問號會顯得歪斜、像排版錯誤，不是同一種符號的視覺語言——這個修飾 class
   給用「?」的場合用（例如 EnvelopeEncrypt.vue 批次加密停用 Passkey/恢復金鑰的提示），
   退回正常字體/不斜體，問號才會端正。 */
.info-tooltip__icon--plain {
  font-style: normal;
  font-family: inherit;
}

.info-tooltip__bubble {
  position: absolute;
  bottom: calc(100% + 8px);
  left: 50%;
  transform: translateX(-50%) translateY(4px);
  width: 260px;
  background: var(--color-text);
  color: var(--color-surface);
  font-size: 0.78rem;
  font-weight: 400;
  line-height: 1.6;
  padding: 0.6rem 0.75rem;
  border-radius: var(--radius-sm);
  box-shadow: var(--shadow-md);
  opacity: 0;
  pointer-events: none;
  transition: opacity var(--duration-fast) var(--ease-out), transform var(--duration-fast) var(--ease-out);
  z-index: 20;
  text-align: left;
  line-break: strict;
  text-wrap: pretty;
}

.info-tooltip:hover .info-tooltip__bubble,
.info-tooltip:focus-visible .info-tooltip__bubble {
  opacity: 1;
  transform: translateX(-50%) translateY(0);
}

/* 內容比一般單句說明長很多（多重點、分段落）的情境用——加寬 bubble、把條列項目排版成
   有間距的清單，不然一長串文字擠在窄欄位裡會變成密密麻麻的文字牆，很難閱讀。 */
.info-tooltip__bubble--wide {
  width: 320px;
}

.info-tooltip__intro {
  margin: 0 0 0.5rem;
}

.info-tooltip__list {
  margin: 0;
  padding-left: 1.1rem;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.info-tooltip__list li {
  margin: 0;
}

@media (prefers-reduced-motion: reduce) {
  .info-tooltip__bubble {
    transition: none;
  }
}

.hint-text {
  font-size: 0.8rem;
  line-height: 1.7;
  color: var(--color-text-tertiary);
  margin: 0.4rem 0 0;
  line-break: strict;
  text-wrap: pretty;
}

.hint-text--indented {
  margin-left: 1.65rem;
}

.hint-text--danger {
  color: var(--color-danger);
}

/* 資料夾防護頁的「並非加密資料夾」警語：比 danger 淡一階，用來標示「請注意」而不是
   「出錯了」——沿用既有 --color-danger 當底色，只是不用滿彩度，跟真正的錯誤訊息拉開區隔。 */
.text-warning-soft {
  color: var(--color-danger);
  opacity: 0.8;
  font-weight: 600;
}

/* ---- 按鈕：所有可點擊元素都要有按下去的回饋（Emil Kowalski 的設計原則） ---- */
.button-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
  margin-top: 0.5rem;
}

/* 密碼庫工具列：按鈕數量會隨選取狀態切換，這一列固定不換行，寬度不夠就橫向捲動，
   避免換行行為隨按鈕數量變化而改變、連帶讓下面清單的位置跟著跳動。 */
.button-row--nowrap {
  flex-wrap: nowrap;
  overflow-x: auto;
  /* `overflow-x: auto` 依 CSS 規範會連帶把 `overflow-y` 也強制變成非 visible（兩軸只要有
     一軸不是 visible，另一軸就不能維持 visible），結果把裡面按鈕 focus-visible 那圈往外
     凸出的 `outline-offset` 裁掉一截，看起來像貼著按鈕邊緣的方形線，不是預期中鬆一點的
     圓角框。用等量的 padding 撐出裁切緩衝、再用反向 margin 把外觀位置拉回原本的樣子，
     兩者互相抵銷，視覺上這一列該在哪裡還是在哪裡。 */
  padding: 4px;
  margin: -4px;
}

/* 鍵盤 Tab 導覽的焦點框：不用瀏覽器預設那種貼在元件邊緣的細黑線，統一改成跟元件本身
   保持一點距離（`outline-offset`）的描邊，顏色用 `--color-accent`——這個變數本來就會依
   目前作用中的分頁色調自動換色（見 `.theme-*` 那組規則），所以焦點框顏色天生就跟著「目前
   選擇的按鈕／文字」使用的同一套顏色走，不用每個元件各自寫一次。Chromium（WebView2 底層）
   繪製 outline 時本來就會貼合元件自己的圓角，不需要額外設定 `outline-radius`。危險動作
   （刪除／停用等）的按鈕改用 `--color-danger`，呼應這些按鈕平常顯示的紅色，不是隨便套用
   跟其他按鈕一樣的強調色。 */
a:focus-visible,
button:focus-visible,
[tabindex]:focus-visible {
  outline: 2px solid var(--color-accent);
  outline-offset: 2px;
}

.button--danger:focus-visible,
.link-button--danger:focus-visible {
  outline-color: var(--color-danger);
}

.button {
  appearance: none;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  font-family: inherit;
  font-size: 0.875rem;
  font-weight: 500;
  border-radius: var(--radius-sm);
  padding: 0.55rem 1rem;
  cursor: pointer;
  white-space: nowrap;
  transition: background-color var(--duration-fast) ease, border-color var(--duration-fast) ease,
    opacity var(--duration-fast) ease, transform 160ms var(--ease-out);
  border: 1px solid transparent;
}

.button:active:not(:disabled) {
  transform: scale(0.97);
}

.button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.button--primary {
  background: var(--color-accent);
  color: #FFFFFF;
  box-shadow: var(--shadow-xs);
}

/* 這個上邊距只有加密/解密頁籤最下面那顆獨立的送出按鈕需要（跟上面的欄位拉開距離），
   彈窗裡的按鈕不該受影響——原本寫在 .button--primary 基礎樣式裡，導致任何地方的主要按鈕
   都跟著多了這段留白，跟旁邊的次要按鈕高度對不齊，這裡收斂成只在真正需要的地方套用。 */
.page > div > .button--primary {
  margin-top: 0.25rem;
}

.button--primary:hover:not(:disabled) {
  background: var(--color-accent-hover);
}

.button--danger {
  background: var(--color-danger);
  color: #FFFFFF;
}

.button--danger:hover:not(:disabled) {
  background: #96351f;
}

.button--secondary {
  background: var(--color-surface);
  color: var(--color-text);
  border-color: var(--color-border-strong);
}

.button--secondary:hover:not(:disabled) {
  border-color: var(--color-accent);
  color: var(--color-accent);
}

.button__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.button--tiny {
  font-size: 0.76rem;
  padding: 0.28rem 0.5rem;
  background: var(--color-surface);
  color: var(--color-text-secondary);
  border-color: var(--color-border);
  white-space: nowrap;
}

.button--tiny:hover:not(:disabled) {
  border-color: var(--color-accent);
  color: var(--color-accent);
}

.link-button {
  appearance: none;
  border: none;
  background: none;
  font-family: inherit;
  font-size: 0.8rem;
  color: var(--color-text-tertiary);
  cursor: pointer;
  padding: 0.2rem 0.4rem;
  text-decoration: underline;
  text-underline-offset: 2px;
  transition: color var(--duration-fast) ease;
  /* 沒有可見的框，平常看不出差別，但要讓 focus-visible 的焦點框跟著圓角，不是直角。 */
  border-radius: var(--radius-sm);
}

.link-button:hover {
  color: var(--color-text-secondary);
}

.link-button--danger {
  color: var(--color-danger);
  opacity: 0.75;
}

.link-button--danger:hover {
  opacity: 1;
}

/* ---- 加密項目清單／結果 ---- */
.item-list {
  position: relative; /* TransitionGroup 的 leave-active 用 absolute 定位時需要一個定位錨點 */
  list-style: none;
  margin: 0.6rem 0 0;
  padding: 0;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  overflow: hidden;
}

.item-list__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.55rem 0.75rem;
  border-bottom: 1px solid var(--color-border);
  background: var(--color-surface);
}

.item-list__row:last-child {
  border-bottom: none;
}

/* 已選檔案清單的進出場：跟 .result-row 同一套風格（純 opacity + 小幅位移，ease-out），
   移除一筆時其餘列用 TransitionGroup 內建的 move 過渡滑上去補位，不是瞬間跳掉。 */
.item-list-row-enter-active,
.item-list-row-leave-active {
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

.item-list-row-move {
  transition: transform var(--duration-base) var(--ease-out);
}

.item-list-row-enter-from {
  opacity: 0;
  transform: translateY(-4px) scale(0.98);
}

.item-list-row-leave-to {
  opacity: 0;
  transform: translateX(12px);
}

.item-list-row-leave-active {
  position: absolute;
  width: 100%;
}

.item-list__path {
  font-family: var(--font-mono);
  font-size: 0.8rem;
  /* 長路徑截斷成一行、用刪節號收尾，滑鼠移上去（title 屬性）看完整內容——比整段自動換行
     更乾淨，尤其在表格列裡，換行會讓每一列的高度參差不齊，看起來很亂。 */
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  min-width: 0;
  flex: 1 1 auto;
  cursor: default;
}

.empty-state {
  color: var(--color-text-tertiary);
  font-size: 0.85rem;
  margin: 0.6rem 0 0;
}

/* 拖放區：加密頁籤還沒選任何項目時顯示，本身也是拖放檔案的目標區域，
   拖著檔案進入視窗時（isDraggingFile）邊框跟背景會亮起來給明確的視覺回饋。
   刻意做成佔滿步驟一大半版面的主視覺卡片（而不是表單裡的一個欄位）——
   「選擇檔案／選擇資料夾」也收進卡片內部，變成卡片自己的次要動作。 */
.dropzone {
  margin-top: 0.6rem;
  min-height: 280px;
  padding: 2.5rem 1.5rem;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  border: 1.5px dashed var(--color-border-strong);
  border-radius: var(--radius-md);
  text-align: center;
  transition: border-color var(--duration-fast) ease, background-color var(--duration-fast) ease;
}

.dropzone.is-dragging {
  border-color: var(--color-accent);
  background: var(--color-accent-soft);
}

.dropzone__icon {
  width: 40px;
  height: 40px;
  color: var(--color-text-tertiary);
  margin-bottom: 0.75rem;
  transition: color var(--duration-fast) ease;
}

.dropzone.is-dragging .dropzone__icon {
  color: var(--color-accent);
}

.dropzone__text {
  font-size: 0.85rem;
  color: var(--color-text-tertiary);
  margin: 0;
  line-break: strict;
  text-wrap: pretty;
}

.dropzone__actions {
  display: flex;
  gap: 0.5rem;
  margin-top: 1.25rem;
}

/* 已選取檔案的狀態：實線邊框（相對拖放框的虛線）表達「已經確定選取」，
   維持跟 .dropzone 相近的最小高度，避免空狀態／已選取狀態切換時版面高度跳動。 */
.picked-items-card {
  margin-top: 0.6rem;
  min-height: 280px;
  padding: 1.25rem;
  display: flex;
  flex-direction: column;
  border: 1.5px solid var(--color-border-strong);
  border-radius: var(--radius-md);
}

.picked-items-card__actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  margin-top: 0.75rem;
}

.picked-items-card__actions-group {
  display: flex;
  gap: 0.5rem;
}

/* 清單類頁面（已加密清單／使用紀錄）的空狀態：不是拖放目標，單純告知「目前沒有內容」，
   用置中的圖示＋文字取代原本一行孤零零的灰字。 */
.empty-state-block {
  padding: 3rem 1rem;
  text-align: center;
}

/* 空狀態圖示：原本是 36px 純灰線框圖示孤零零躺在大片留白裡，加一圈淡色圓底（沿用既有的
   --color-accent-soft，跟 focus ring 等處同一個 token，不是新增顏色）讓空狀態不再是一片死灰。
   --icon 縮小成 28px 放進圓底裡，讓整體視覺重量跟原本的 36px 差不多，不會突然變得很搶眼。 */
.empty-state-block__icon {
  width: 28px;
  height: 28px;
  padding: 14px;
  box-sizing: content-box;
  border-radius: 999px;
  background: var(--color-accent-soft);
  color: var(--color-accent);
  margin-bottom: 0.85rem;
}

/* 密碼庫部件「損毀」是唯一語意上真的算錯誤/警告的空狀態（跟「還沒裝」「清單是空的」這種
   中性狀態不同）——沿用既有的 --color-danger-soft／--color-danger，不要跟其他中性空狀態
   共用金色調，避免使用者誤以為這也只是「沒有內容」而已。 */
.empty-state-block__icon--danger {
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

.empty-state-block__text {
  font-size: 0.85rem;
  color: var(--color-text-tertiary);
  margin: 0;
}

/* 進度條：不是精確反映後端真實進度，是依項目數量/檔案大小估算出的視覺回饋（見
   estimateEncryptDurationMs 說明）。用 width 過渡而不是重新畫整條，過渡時間刻意設短
   （tick 頻率本來就高，這裡的 transition 只是讓每一格 requestAnimationFrame 之間的
   width 變化不要看起來是硬切），真正的節奏由 JS 那邊的緩動函式控制。 */
.progress-bar {
  margin-top: 0.6rem;
  height: 4px;
  border-radius: 2px;
  background: var(--color-border);
  overflow: hidden;
}

.progress-bar__fill {
  width: 100%;
  height: 100%;
  background: var(--color-accent);
  border-radius: 2px;
  transform-origin: left;
  transition: transform 80ms linear;
}

@media (prefers-reduced-motion: reduce) {
  .progress-bar__fill {
    transition: none;
  }
}

.result-list {
  margin: 1.25rem 0;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.result-row {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  font-size: 0.85rem;
  padding: 0.5rem 0.7rem;
  border-radius: var(--radius-sm);
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

.result-row-enter-from {
  opacity: 0;
  transform: translateY(-4px) scale(0.98);
}

/* 分頁切換：分頁是這個 App 裡數一數二高頻的操作，動畫份量刻意壓到最低——只用快速的
   純透明度淡入淡出，不加位移，求「不死板」而不是「有存在感」。 */
.tab-page-enter-active,
.tab-page-leave-active {
  transition: opacity 120ms ease;
}
.tab-page-enter-from,
.tab-page-leave-to {
  opacity: 0;
}

/* 加密步驟切換：偶爾、慎重的操作，用跟 .modal／.toast 同一套節奏（--duration-base + --ease-out）
   維持整體一致性。有方向性——從哪裡來就從哪裡回去：下一步往左（舊內容往左淡出、新內容從
   右邊進來，兩者都往左，像同一條輸送帶）；上一步完全相反。 */
.step-forward-enter-active,
.step-forward-leave-active,
.step-backward-enter-active,
.step-backward-leave-active {
  transition: opacity var(--duration-base) var(--ease-out), transform var(--duration-base) var(--ease-out);
}

.step-forward-leave-to {
  opacity: 0;
  transform: translateX(-16px);
}
.step-forward-enter-from {
  opacity: 0;
  transform: translateX(16px);
}

.step-backward-leave-to {
  opacity: 0;
  transform: translateX(16px);
}
.step-backward-enter-from {
  opacity: 0;
  transform: translateX(-16px);
}

.result-row--success {
  background: var(--color-success-soft);
  color: var(--color-success);
}

.result-row--error {
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

.result-row__icon {
  font-weight: 600;
}

/* ---- 解密頁籤 ---- */
.alt-methods {
  margin-top: 1.25rem;
  padding-top: 1.25rem;
  border-top: 1px solid var(--color-border);
}

.alt-methods__label {
  font-size: 0.85rem;
  color: var(--color-text-secondary);
  margin: 0 0 0.5rem;
}

.status-message {
  font-size: 0.875rem;
  margin-top: 1rem;
  padding: 0.6rem 0.8rem;
  border-radius: var(--radius-sm);
}

.status-message--success {
  background: var(--color-success-soft);
  color: var(--color-success);
}

.status-message--error {
  background: var(--color-danger-soft);
  color: var(--color-danger);
}

/* ---- 已加密清單子頁籤 ---- */
.sub-tab-bar {
  display: flex;
  gap: 0.5rem;
  margin-bottom: 1.25rem;
}

.sub-tab-bar__item {
  appearance: none;
  font-family: inherit;
  font-size: 0.82rem;
  font-weight: 500;
  /* 藥丸形按鈕文字上下留白肉眼看起來不對稱（實測上方比下方多 2px）——瀏覽器預設
     line-height 沒有讓字glyph的上下留白對稱於 padding box，不是 padding 本身不對稱，
     鎖死 line-height:1 讓文字高度貼齊 font-size，上下留白才會真的對稱。 */
  line-height: 1;
  border: 1px solid var(--color-border-strong);
  background: var(--color-surface);
  color: var(--color-text-secondary);
  border-radius: 999px;
  padding: calc(0.35rem + 2px) 0.85rem;
  cursor: pointer;
  transition: background-color var(--duration-fast) ease, border-color var(--duration-fast) ease, color var(--duration-fast) ease;
}

.sub-tab-bar__item.is-active {
  background: var(--color-accent-soft);
  border-color: var(--color-accent-border);
  color: var(--color-accent);
}

.refresh-button {
  margin-bottom: 1rem;
}

.history-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
}

/* ---- 表格：外框用陰影而不是描邊，橫向內容過長時整個表格區域自己捲動，
     不會把整個視窗撐爆（對應「使用紀錄會炸到畫面外面」這個問題）。 ---- */
/* 骨架畫面：灰色色塊模擬表格結構，資料還沒回來之前先讓畫面「看起來已經有東西」，
   微微的呼吸閃爍暗示「還在等」，比空白畫面或純文字「載入中」更平順。 */
.skeleton-block {
  display: inline-block;
  height: 0.85rem;
  border-radius: 4px;
  background: var(--color-border);
  animation: skeleton-breathe 1.4s ease-in-out infinite;
}

@keyframes skeleton-breathe {
  0%, 100% { opacity: 0.6; }
  50% { opacity: 1; }
}

@media (prefers-reduced-motion: reduce) {
  .skeleton-block {
    animation: none;
    opacity: 0.8;
  }
}

.table-scroll {
  overflow-x: auto;
  border-radius: var(--radius-md);
  /* `overflow-x: auto` 會連帶把 `overflow-y` 也強制變成非 visible（同一個 CSS 規範規則，
     跟 `.button-row--nowrap` 那個焦點框被裁掉的問題一樣），把裡面表單元素/可聚焦儲存格
     focus-visible 往外凸出的光暈/焦點框裁掉一截。用等量 padding 撐出裁切緩衝、反向 margin
     把外觀位置拉回來抵銷，視覺上這個區塊該在哪裡還是在哪裡。 */
  padding: 4px;
  margin: -4px;
}

.table {
  width: 100%;
  min-width: 560px;
  border-collapse: collapse;
  font-size: 0.85rem;
  background: var(--color-surface);
}

.table--auto td:last-child,
.table--auto td:first-child {
  width: 1%;
  white-space: nowrap;
}

.table:not(.table--auto) {
  table-layout: fixed;
}

.table th {
  text-align: left;
  font-weight: 500;
  color: var(--color-text-tertiary);
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  padding: 0.65rem 0.85rem;
  border-bottom: 1px solid var(--color-border);
}

.table td {
  padding: 0.7rem 0.85rem;
  border-bottom: 1px solid var(--color-border);
  vertical-align: top;
  white-space: nowrap;
}

/* 回饋：資料夾防護清單的轉盤圖示要跟旁邊的路徑文字上下置中——表格儲存格預設頂端對齊
   （上面 .table td 那條規則），是為了其他表格（例如使用紀錄的「詳細資訊」欄）換行的長
   文字從第一行開始對齊。資料夾防護這張表每一列都是單行內容＋一顆固定 44px 高的圖示，
   頂端對齊反而讓圖示看起來偏上、跟文字對不齊。只針對這張表（`.table--folder-guard`
   這個既有的修飾類別）整列改成置中對齊，不動全域的 `.table td`，其他表格維持原本的
   頂端對齊行為。 */
.table--folder-guard td {
  vertical-align: middle;
}

.table tbody tr:last-child td {
  border-bottom: none;
}

/* 表格列進場動畫：只有列真正被插入 DOM 的那一刻才會播放（CSS animation 的天生行為，
   Vue 靠 :key 重複使用既有節點時不會重新觸發），資料更新但列本來就存在的情況不會
   每次都跳一次動畫，避免常常重新整理的頁面看久了膩。依序的 nth-child 延遲做出
   逐一浮現的感覺，超過第 5 列之後統一延遲，不無限累加下去。
   刻意只用 opacity、不帶 translateY 位移——原本帶位移時，動畫過程中 .page-wrapper
   那層 overflow-y: auto 會短暫判斷內容高度增加，跳出捲軸、動畫結束又消失，很干擾。
   純 opacity 變化不影響版面高度計算，不會有這個副作用。 */
@keyframes table-row-in {
  from { opacity: 0; }
  to { opacity: 1; }
}

.table tbody tr {
  animation: table-row-in 280ms var(--ease-out) backwards;
}

.table tbody tr:nth-child(1) { animation-delay: 0ms; }
.table tbody tr:nth-child(2) { animation-delay: 35ms; }
.table tbody tr:nth-child(3) { animation-delay: 70ms; }
.table tbody tr:nth-child(4) { animation-delay: 105ms; }
.table tbody tr:nth-child(5) { animation-delay: 140ms; }
.table tbody tr:nth-child(n+6) { animation-delay: 175ms; }

@media (prefers-reduced-motion: reduce) {
  .table tbody tr {
    animation: none;
  }
}

.table tbody tr:hover td {
  background: var(--color-bg);
}

/* 詳細資訊這種可能很長的欄位要能換行，不能無限撐開表格寬度——這是「使用紀錄爆版」的根本原因：
   之前沒有這個規則，長路徑會強迫整個表格（進而整個視窗）變寬。 */
.table__wrap-cell {
  max-width: 320px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
}

.table__row--nested td {
  padding-left: 2rem;
  background: #FCFCFD;
}

/* 按鈕列不能直接把 display:flex 放在 <td> 上——Chromium 對 flex 化的表格儲存格處理
   跟一般儲存格不同，會導致這個儲存格沒有跟著整列一起撐滿高度，hover 變色只蓋到一半，
   這正是「hover 沒有整塊區域變色」的成因。改成 <td> 裡包一層 div 做 flex，<td> 本身維持
   預設的 table-cell 顯示方式，高度就會跟其他儲存格一致。 */
/* 原本是 flex-direction: column，每個按鈕各自一列、還因為 align-items: stretch 被撐成整欄寬度——
   存了很多筆紀錄時，每一列因此佔用很高的垂直空間，要滑很久才能看完清單。改成同一行、
   強制不換行（flex-wrap: nowrap）：欄位本身用 .table-scroll 既有的水平捲動當保底，比允許
   換行更可靠——flex-wrap: wrap 在「表格欄寬本來就是用內容自動撐出來」的情況下（例如已加密
   清單的 .table--auto），瀏覽器計算最小寬度時會把「允許換行」這件事也算進去，導致欄位被
   算成很窄、又真的換行，兩個問題互相加強，即使按鈕本身沒有很寬還是會擠成好幾列。這個規則
   是所有表格（已加密清單、資料夾防護、密碼庫）共用的，一起受益。 */
.table__actions {
  display: flex;
  flex-direction: row;
  flex-wrap: nowrap;
  align-items: center;
  justify-content: flex-end;
  gap: 0.3rem;
}

/* 永久刪除整個搬到每一列最前面獨立一欄（見 .row-delete-button），不再跟解鎖方式的
   按鈕群組共用同一欄——空間上完全分開，比同一欄裡加分隔線更明確。 */
.table__delete-cell {
  padding-left: 0.6rem !important;
  padding-right: 0.4rem !important;
  vertical-align: top;
}

/* 沿用 .link-button--danger 的既有慣例：預設就帶一點危險色（降低透明度，不刺眼），
   hover 提升到完整不透明——全程不加背景色塊，避免跟列本身 hover 時的背景色疊加打架。 */
.row-delete-button {
  appearance: none;
  display: inline-flex;
  /* 圖示對齊按鈕頂端，不是置中——按鈕本身比圖示高（28px vs 16px），置中的話圖示會
     比同一列名稱欄位的文字第一行低了快一半按鈕高度的空隙，看起來沒對齊。頂端對齊後
     圖示緊貼儲存格頂端留白又比文字稍微高了一點點，補一點點 padding-top 往下推回去，
     跟文字第一行的視覺基準對齊。 */
  align-items: flex-start;
  justify-content: center;
  width: 28px;
  height: 28px;
  padding: 2px 0 0;
  border: none;
  background: none;
  color: var(--color-danger);
  opacity: 0.75;
  cursor: pointer;
  border-radius: var(--radius-sm);
  transition: opacity var(--duration-fast) ease;
}

.row-delete-button:hover {
  opacity: 1;
}

.row-delete-button svg {
  width: 16px;
  height: 16px;
}

/* 直向堆疊時，按鈕內容統一靠左對齊，視覺上才會像一組整齊的清單而不是散落的方塊。 */
.table__actions .button {
  justify-content: flex-start;
}

/* 資料夾防護清單每列操作欄只有一顆按鈕，不是疊多顆的清單，文字該置中，
   蓋掉上面給「疊多顆按鈕」情境用的靠左對齊。 */
.table--folder-guard .table__actions .button {
  justify-content: center;
}

/* 原本這裡有一段 `padding-top: 1rem` 手動把文字往下推，是在整張表還是「圖示置中／
   文字頂端對齊＋手動補位移」這套舊邏輯時，針對舊的 32px 圖示手調出來的數字。後來圖示
   欄跟其他欄統一改成 `.table--folder-guard td { vertical-align: middle }`（見上面），
   所有欄位都用瀏覽器原生的置中對齊，不需要再手動補位移——圖示放大到 44px 之後，這段
   寫死的 1rem 反而會把文字推到比置中後的圖示更低，兩層位移疊加，回饋「貼下緣」就是
   這樣來的，已經拿掉。第一欄的文字置中（圖示是置中的圖，不是文字）維持沿用。 */
.table--folder-guard td:first-child {
  text-align: center;
}

.vault-wheel-icon--clickable {
  cursor: pointer;
}

.vault-wheel-icon--clickable:focus-visible {
  outline: 2px solid var(--color-accent, #a8770f);
  outline-offset: 2px;
  border-radius: var(--radius-sm, 6px);
}

/* 已加密清單：跟資料夾防護同樣的理由，但排除最左邊的永久刪除按鈕欄
   （那一欄本身就是按鈕，不需要跟著文字欄一起往下移）。 */
.table--auto td:not(:first-child):not(:last-child) {
  padding-top: 1rem;
}

.table__actions .link-button {
  text-align: right;
  padding-top: 0.15rem;
}

.table__detail-cell {
  color: var(--color-text-secondary);
  font-size: 0.8rem;
  max-width: 420px;
}

.cell-name {
  font-weight: 500;
  max-width: 280px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
  /* 沒有可見的框，平常看不出差別，但要讓 `.cell-clickable` 那種可以 Tab 到、可以點擊的
     欄位，focus-visible 的焦點框跟著圓角，不是直角。 */
  border-radius: var(--radius-sm);
}

/* 密碼庫清單裡「點一下就複製／顯示」的帳號欄位——蓋掉 .cell-name 的 cursor: default，
   讓使用者看得出這裡可以點，不用另外加圖示。 */
.cell-clickable {
  cursor: pointer;
}

.cell-hint {
  max-width: 160px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
}

/* 密碼庫清單：「已加密檔案」類別憑證對應的 Vault 項目消失後，標題加刪除線（規劃文件第 4 節）。 */
.text-strikethrough {
  text-decoration: line-through;
  opacity: 0.7;
}

.cell-empty {
  color: var(--color-text-tertiary);
}

/* 密碼庫新增/編輯表單的關聯網域標籤，跟 .badge 用途類似但需要一個內建的移除按鈕。 */
.tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 999px;
  background: var(--color-accent-soft);
  color: var(--color-accent);
  font-size: 0.8rem;
}

.tag__remove {
  appearance: none;
  border: none;
  background: none;
  color: inherit;
  cursor: pointer;
  font-size: 0.9rem;
  line-height: 1;
  padding: 0;
}

/* 「使用現有密碼」選擇器整列可點擊。 */
.table__row--clickable {
  cursor: pointer;
}
.table__row--clickable:hover {
  background: var(--color-accent-soft);
}

.badge {
  display: inline-block;
  font-size: 0.75rem;
  color: var(--color-accent);
  margin-top: 0.15rem;
}

.badge--nested-lock {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
}

.badge__icon {
  width: 0.85rem;
  height: 0.85rem;
  position: relative;
  top: -1px;
}

.status-warning {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  font-size: 0.78rem;
  color: var(--color-danger);
  margin-top: 0.2rem;
}

.status-warning__icon {
  width: 0.85rem;
  height: 0.85rem;
  flex-shrink: 0;
}

.group-row td {
  background: var(--color-accent-soft);
  padding: 0;
}

.group-row__inner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.7rem 0.85rem;
  flex-wrap: wrap;
}

.group-row__toggle {
  appearance: none;
  border: none;
  background: none;
  font-family: inherit;
  font-size: 0.85rem;
  font-weight: 500;
  color: var(--color-text);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  text-align: left;
}

.group-row__chevron {
  display: inline-block;
  transition: transform var(--duration-fast) var(--ease-out);
  color: var(--color-accent);
  flex-shrink: 0;
}

.group-row__chevron.is-expanded {
  transform: rotate(90deg);
}

/* ---- 票根樣式清單（design-exploration/gui-styles-v2/13-sidebar-ticket-shell.html 定案版本，
     定案文件 §3.4）：取代原本「已加密清單」分頁的 <table>，單一項目的卡片外觀在
     TicketRow.vue 裡（scoped 樣式），這裡只放清單容器跟批次群組摘要票根的樣式——
     批次群組刻意不用 TicketRow（它的資料形狀是「一組項目」不是單一 item，且展開後
     底下每個項目才各自是一張 TicketRow）。 ---- */
.list-toolbar {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  margin-bottom: 1.25rem;
  flex-wrap: wrap;
}

.list-toolbar__spacer {
  flex: 1;
}

.ticket-list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  position: relative; /* 撕開飛走的那一列脫離文件流時（.ticket-fly-leave-active）用絕對定位疊在上面，需要這個當定位基準 */
}

/* 撕開飛走＋其餘列補位（定案文件〈信封清單虛線的互動〉§4）：name="ticket-fly" 對應到上面
   <TransitionGroup> 的 name prop，Vue 會自動組出 -move / -leave-active / -leave-to 三組
   class。飛走的位移／角度數值照抄 mockup（13-sidebar-ticket-shell.html）的
   `.ticket-wrap.is-leaving .ticket-stage`；「其餘列往上補位」則是 TransitionGroup 內建的
   FLIP move 過場，不用額外寫位移邏輯。
   回饋（使用者實測抓到）：原本補位是線性 ease、所有列同一時間一起移動，撤掉一列之後底下
   整批「唰」一聲瞬間跳上去，感覺不到真的在補位。改用彈性曲線（沿用側欄高亮色塊同一組
   cubic-bezier(0.34, 1.56, 0.64, 1)，衝過頭一點再彈回卡住，不是單純減速停下）；多筆同時
   要補位時，用 nth-child 依序加一點點延遲，做出「一筆一筆跟著往上跑」的層次感，不是所有
   列同時定格式地移動。延遲量刻意壓得很小（每筆只差 35ms）——要有層次，但不能拖沓。 */
.ticket-fly-move {
  transition: transform 360ms cubic-bezier(0.34, 1.56, 0.64, 1);
}

/* 巢狀批次群組展開後裡面那份清單（.ticket-group__items）用的是同一個 TransitionGroup
   name="ticket-fly"，共用這整組規則——選擇器一併涵蓋兩種容器，不用重複寫一份。 */
.ticket-list > *:nth-child(2).ticket-fly-move,
.ticket-group__items > *:nth-child(2).ticket-fly-move { transition-delay: 35ms; }
.ticket-list > *:nth-child(3).ticket-fly-move,
.ticket-group__items > *:nth-child(3).ticket-fly-move { transition-delay: 70ms; }
.ticket-list > *:nth-child(4).ticket-fly-move,
.ticket-group__items > *:nth-child(4).ticket-fly-move { transition-delay: 105ms; }
.ticket-list > *:nth-child(5).ticket-fly-move,
.ticket-group__items > *:nth-child(5).ticket-fly-move { transition-delay: 140ms; }
.ticket-list > *:nth-child(n+6).ticket-fly-move,
.ticket-group__items > *:nth-child(n+6).ticket-fly-move { transition-delay: 175ms; }

/* 新項目滑入（定案文件〈8.1〉：從上方滑下＋淡入，時長／曲線沿用全站清單共用的
   --duration-base／--ease-out，不另外發明新數字，跟 table-row-in、已選檔案清單
   進出場是同一套語彙）——加密委交成功、新票根出現在清單最上面時觸發。 */
.ticket-fly-enter-active {
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

.ticket-fly-enter-from {
  opacity: 0;
  transform: translateY(-16px);
}

/* 回饋（使用者實測抓到的真正病根）：下面 position:absolute 這條原本寫成單一 class
   選擇器 `.ticket-fly-leave-active`（優先權 0,0,1,0），會被 TicketRow.vue scoped 樣式裡的
   `.ticket-wrap { position: relative }` 蓋掉——scoped 樣式編譯後會自動加一個屬性選擇器
   （`.ticket-wrap[data-v-xxxx]`），優先權變成 0,0,2,0，比這裡原本的寫法高，所以飛走的那一列
   其實從頭到尾都沒有真的脫離版面流，補位動畫的容身空間一直被佔著，直到動畫完全播完、
   Vue 把節點整個移除 DOM 那一刻，其餘列才會一次瞬間跳上去——跟使用者形容的「颼的一下」
   完全吻合。改成三層選擇器（.ticket-list > .ticket-wrap.ticket-fly-leave-active，
   優先權 0,0,3,0）確保一定贏過 scoped 樣式，不管兩份樣式表打包後的先後順序為何。批次群組
   展開後裡面那份巢狀清單（.ticket-group__items）用的是同一個 TransitionGroup，一併涵蓋。 */
.ticket-list > .ticket-wrap.ticket-fly-leave-active,
.ticket-group__items > .ticket-wrap.ticket-fly-leave-active {
  transition: transform 380ms var(--ease-out, ease), opacity 340ms ease;
  /* 飛走時要蓋在其他還沒補位的列上面，且脫離文件流讓其餘列可以立刻開始往上滑，
     不然它們得等這一列真的從版面消失才會移動，補位動畫看起來會頓一下。 */
  position: absolute;
  width: 100%;
}

.ticket-fly-leave-to {
  transform: translateX(90px) rotate(2.5deg);
  opacity: 0;
}

.ticket-group {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.ticket--batch {
  padding-left: 20px;
  background: var(--color-accent-soft);
  border-color: var(--color-accent-border);
  justify-content: space-between;
}

.ticket-group__toggle {
  appearance: none;
  border: none;
  background: none;
  font-family: inherit;
  font-size: 0.85rem;
  font-weight: 500;
  color: var(--color-text);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  text-align: left;
}

.ticket-group__chevron {
  display: inline-block;
  transition: transform var(--duration-fast) var(--ease-out);
  color: var(--color-accent);
  flex-shrink: 0;
}

.ticket-group__chevron.is-expanded {
  transform: rotate(90deg);
}

/* 展開/收合手風琴動畫：用 grid-template-rows 0fr → 1fr 的技巧，不用 JS 量測 scrollHeight——
   內容高度不管多高，同一條 CSS 規則都適用，換素材/加項目都不用回頭調參數。外層負責動畫，
   裡面的 .ticket-group__items 只負責 overflow:hidden 擋住還沒展開時溢出的內容。 */
.ticket-group__items-wrapper {
  display: grid;
  grid-template-rows: 0fr;
  transition: grid-template-rows 280ms var(--ease-out);
}

.ticket-group__items-wrapper.is-expanded {
  grid-template-rows: 1fr;
}

@media (prefers-reduced-motion: reduce) {
  .ticket-group__items-wrapper {
    transition: none;
  }
}

.ticket-group__items {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-left: 20px;
  overflow: hidden;
  min-height: 0;
  position: relative; /* 撕開飛走／解鎖移除時 .ticket-fly-leave-active 用絕對定位疊在上面，需要這個當定位基準，跟 .ticket-list 是同一個道理 */
}

/* 手風琴完全展開、動畫播完之後才放開裁切——見上面 GROUP_EXPAND_TRANSITION_MS 旁邊的
   說明，第一筆/最後一筆項目撕開時撐開的視覺範圍會貼近容器邊緣，這時候還留著 overflow:hidden
   會把撐開動畫切掉一角。收合狀態／展開動畫進行中都還是要維持裁切，只有這個「已經完全展開、
   之後不會再有高度變化」的狀態才安全放開。 */
.ticket-group__items-wrapper.is-settled .ticket-group__items {
  overflow: visible;
}

/* ---- 設定頁籤 ---- */
/* 卡片式分組（GUI造型探索_定案文件 §6）：11 個既有 .settings-section 攤平在同一條捲軸上，
   使用者要找特定開關會退化成「用捲輪找設定」，不是「掃一眼找到」。分成一般／安全性／關於
   三組，用卡片背景/邊框分隔——不套獨立主題（那是密碼庫筆記本 revert 過的教訓），只是把
   既有中性風格排版得更清楚。 */
.settings-group {
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: 16px;
  box-shadow: var(--shadow-sm);
  padding: 20px 24px 4px;
  margin-bottom: 1.5rem;
}

.settings-group__header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 0.85rem;
  font-weight: 700;
  letter-spacing: 0.02em;
  text-transform: uppercase;
  color: var(--color-text-tertiary);
  margin: 0 0 1rem;
}

.settings-group__header svg {
  width: 15px;
  height: 15px;
  stroke-width: 1.8;
  flex-shrink: 0;
}

/* 組內最後一個 .settings-section 已經靠 :last-of-type 拿掉底線（見下方既有規則），
   這裡再扣掉卡片底部多餘的留白，避免卡片內緣看起來比其他三邊寬。 */
.settings-group .settings-section:last-of-type {
  padding-bottom: 0;
}

.settings-section {
  margin-bottom: 1.75rem;
  padding-bottom: 1.75rem;
  border-bottom: 1px solid var(--color-border);
  text-align: left;
}

.settings-section:last-of-type {
  border-bottom: none;
}

.settings-section__title {
  font-size: 1.15rem;
  font-weight: 700;
  line-height: 1.4;
  margin: 0 0 0.65rem;
  color: var(--color-text);
}

/* 「設定」分頁專用——這個 class 在密碼庫／資料夾防護分頁的設定精靈標題、密碼庫清單的分類標題
   （網站／已加密檔案）也共用，那些地方不需要跟著調整，只限定 .settings-tab 底下這份規則。
   先前直接把字級一路加到 1.55rem／1.3rem，跟正上方 1.375rem 的 .page-title（「設定」頁面
   大標）幾乎一樣大，兩個大標疊在一起反而讓整頁看起來很「吵」，層級感沒有更清楚。改成 Apple
   排版準則的作法：層級感靠字級＋字重＋字距一起做，不是單靠拉大字級——字級只從原本 1.15rem
   微調到 1.08rem（跟下方 0.95rem 的 .settings-subsection__title 保持適度差距即可，不用拉開
   到誇張），主要靠字重（700，本來就有）、更緊的字距（大字級字距要收緊，是 Apple 字體排版的
   既有原則）、跟 .settings-subsection__title 的次要文字色（--color-text-secondary）對比
   出來的顏色深淺，三者一起做出「一眼看出這是標題」的效果。 */
.settings-tab .settings-section__title {
  font-size: 1.08rem;
  letter-spacing: -0.015em;
  line-height: 1.3;
  margin: 0 0 0.6rem;
}

.vault-path-display {
  font-family: var(--font-mono);
  font-size: 0.8rem;
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  padding: 0.6rem 0.75rem;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: default;
  margin: 0 0 0.65rem;
}

/* ---- 信封加密流程懸浮層（Phase 2b）：apple-design「Materials & depth」——modal 任務用
   模糊+暗化的 scrim 把背景往後推，不是輕量、不打斷流程的半透明疊層那種做法。進退場除了
   opacity 也一起變化 backdrop-filter 的模糊半徑（materialize，不是純粹淡入淡出），讓這個
   表面讀起來像一塊真的材質浮出來，不是一片突然出現的灰色。 ---- */
.encrypt-overlay {
  position: fixed;
  inset: 0;
  /* 刻意比 .modal-overlay（100）／.modal-overlay--confirm（200）低——加密流程進行到一半時
     還是可能跳出恢復金鑰顯示這類全域彈窗（見 recoveryKeyDisplay），那些彈窗要蓋在這層
     上面，不能反過來被這層擋住。 */
  z-index: 90;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(20, 18, 12, 0.28);
  backdrop-filter: blur(14px);
  -webkit-backdrop-filter: blur(14px);
}

.encrypt-overlay-enter-active {
  transition: opacity var(--duration-base, 200ms) var(--ease-out, ease),
    backdrop-filter var(--duration-base, 200ms) var(--ease-out, ease),
    -webkit-backdrop-filter var(--duration-base, 200ms) var(--ease-out, ease);
}

.encrypt-overlay-leave-active {
  transition: opacity var(--duration-fast, 150ms) var(--ease-out, ease),
    backdrop-filter var(--duration-fast, 150ms) var(--ease-out, ease),
    -webkit-backdrop-filter var(--duration-fast, 150ms) var(--ease-out, ease);
}

.encrypt-overlay-enter-from,
.encrypt-overlay-leave-to {
  opacity: 0;
  backdrop-filter: blur(0px);
  -webkit-backdrop-filter: blur(0px);
}

@media (prefers-reduced-transparency: reduce) {
  .encrypt-overlay {
    backdrop-filter: none;
    -webkit-backdrop-filter: none;
    background: rgba(20, 18, 12, 0.82);
  }
}

/* ---- 通知（取代原生 alert）---- */
.toast-stack {
  position: fixed;
  right: 1.5rem;
  bottom: 1.5rem;
  display: flex;
  flex-direction: column-reverse;
  gap: 0.5rem;
  z-index: 200;
  max-width: 360px;
}

.toast {
  display: flex;
  align-items: flex-start;
  gap: 0.55rem;
  font-size: 0.85rem;
  padding: 0.7rem 0.9rem;
  border-radius: var(--radius-sm);
  box-shadow: var(--shadow-md);
  cursor: pointer;
  background: var(--color-surface);
  color: var(--color-text);
  border-left: 3px solid var(--color-danger);
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

.toast__icon {
  width: 17px;
  height: 17px;
  flex-shrink: 0;
  margin-top: 0.05rem;
  color: var(--color-danger);
}

.toast--success {
  border-left-color: var(--color-success);
}

/* 成功 toast 的打勾圖示彈一下再定住——只用在「真的完成了」這種有意義的時刻（複製密碼、
   儲存成功等），呼應 apple-design skill 的「有momentum 感的互動才用 bounce，其他一律
   critically damped」；每個 toast 都是全新的 DOM 節點（v-for :key="toast.id"），animation
   會在掛載時自動觸發一次，不用額外寫 JS 觸發。 */
.toast--success .toast__icon {
  color: var(--color-success);
  animation: toast-check-pop 420ms var(--ease-out);
}

@keyframes toast-check-pop {
  0% { transform: scale(0.4); opacity: 0; }
  55% { transform: scale(1.15); opacity: 1; }
  100% { transform: scale(1); }
}

@media (prefers-reduced-motion: reduce) {
  .toast--success .toast__icon {
    animation: none;
  }
}

.toast--info {
  border-left-color: var(--color-accent);
}

.toast--info .toast__icon {
  color: var(--color-accent);
}

.toast-enter-from,
.toast-leave-to {
  opacity: 0;
  transform: translateX(16px) scale(0.97);
}

/* TransitionGroup 的 FLIP 重新定位：沒有這條規則的話，移除一則 toast 時其他還留著的
   toast 會瞬間跳到新位置，而不是平滑滑過去。 */
.toast-move {
  transition: transform var(--duration-base) var(--ease-out);
}

/* ---- 彈窗（含確認對話框） ---- */
/* 遮罩加一點模糊而不是純黑蓋住——讓後面內容若隱若現，感覺像半透明的材質層蓋上去，
   而不是畫面被切斷（apple-design skill 的 Materials & depth 準則）。
   prefers-reduced-transparency 使用者則退回原本純色蓋住的版本，不強加毛玻璃效果。 */
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(20, 22, 28, 0.5);
  backdrop-filter: blur(6px);
  -webkit-backdrop-filter: blur(6px);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 1.5rem;
  z-index: 100;
  /* 模糊材質要「materialize」進出場（apple-design〈12. Materials & depth〉），不是只有
     opacity 淡入淡出、blur 半徑瞬間跳出來——backdrop-filter 也要參與過場，才會有清晰漸變
     模糊的實體感，而不是背景色淡出、模糊卻硬切。 */
  transition: opacity var(--duration-base) var(--ease-out),
    backdrop-filter var(--duration-base) var(--ease-out),
    -webkit-backdrop-filter var(--duration-base) var(--ease-out);
}

@media (prefers-reduced-transparency: reduce) {
  .modal-overlay {
    background: rgba(20, 22, 28, 0.75);
    backdrop-filter: none;
    -webkit-backdrop-filter: none;
  }
}

/* askConfirm 可能是從另一個已經開著的彈窗裡觸發的（例如密碼庫「使用現有密碼」流程），
   跟其他 .modal-overlay 同一個 z-index 的話，疊在誰上面只看 DOM 順序，這個確認對話框
   在模板裡的位置剛好比密碼庫表單早，反而會被蓋在下面、點不到。確認對話框永遠代表
   「當下最需要使用者處理的動作」，固定給它更高的 z-index，不管它在 DOM 裡排在哪都一定
   在最上層。 */
.modal-overlay--confirm {
  z-index: 200;
}

.modal {
  font-family: var(--font-ui);
  background: var(--color-surface);
  border-radius: var(--radius-lg);
  box-shadow: var(--shadow-modal);
  padding: 1.75rem 2rem;
  max-width: 480px;
  width: 100%;
  max-height: calc(100vh - 3rem);
  overflow-y: auto;
  /* 回饋（使用者實測抓到）：解鎖/刪除確認彈窗的檔名是內插進來的使用者資料，沒限制長度——
     遇到像沒有空白/連字號可以斷行的長檔名（例如安裝程式常見的 hash 檔名）時，內容硬撐開
     彈窗寬度，冒出水平捲軸，在這種「一次性小彈窗」的情境下捲軸看起來像排版壞掉。
     overflow-wrap: break-word 是 inherited 屬性，這裡設一次整個彈窗底下的文字（標題／
     副標題／訊息）都受益，優先在正常字詞邊界斷行，真的斷不了才強制斷在字中間，不影響
     一般短檔名的正常換行行為。 */
  overflow-wrap: break-word;
  text-align: left;
  transform-origin: center;
  transition: transform var(--duration-base) var(--ease-out), opacity var(--duration-base) var(--ease-out);
}

/* 彈窗進出場：從 scale(0.95) 進場，不是從 0——真實世界不會有東西憑空從無變有；
   離場比進場快，符合「系統回應要快、使用者決策時可以慢」的原則。 */
.modal-enter-from .modal,
.modal-leave-to .modal {
  transform: scale(0.95);
  opacity: 0;
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
  /* 進出場的模糊起點/終點要明確寫出來，CSS 過場才能內插——不寫的話瀏覽器沒有「起點」
     可以動畫，backdrop-filter 只會瞬間出現/消失。 */
  backdrop-filter: blur(0px);
  -webkit-backdrop-filter: blur(0px);
}

.modal-leave-active {
  transition-duration: var(--duration-fast);
}

.modal-leave-active .modal {
  transition-duration: var(--duration-fast);
}

.modal__title {
  font-size: 1.125rem;
  font-weight: 600;
  letter-spacing: -0.015em;
  line-height: 1.3;
  margin: 0 0 0.5rem;
  color: var(--color-text);
}

.modal__subtitle {
  font-size: 0.875rem;
  color: var(--color-text-secondary);
  margin: 0 0 0.75rem;
}

.modal__message {
  font-size: 0.9rem;
  line-height: 1.75;
  white-space: pre-line;
  text-align: left;
  margin: 0;
  /* 中文斷行處理：strict 讓標點遵守禁則（句號、逗號不會被丟到行首），
     pretty 讓瀏覽器平衡整段的斷行、避免最後一行只剩一兩個字。 */
  line-break: strict;
  text-wrap: pretty;
  word-break: normal;
  overflow-wrap: break-word;
}

.modal__footer {
  margin-top: 1.25rem;
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}

/* 用 flex:1 平分寬度，保證兩顆按鈕完全等寬——min-width 只是設下限，
   文字或圖示內容一多就會把其中一顆撐開，不夠可靠。 */
.modal__footer:not(.modal__footer--stacked) .button {
  flex: 1 1 0;
  min-width: 0;
}

.modal__footer--center {
  justify-content: flex-end;
}

.modal__footer--center .button,
.modal__footer.modal__footer--center .button {
  flex: initial;
}

/* 三選一對話框的按鈕：直向堆疊、各自撐滿寬度，比照 macOS 動作表（action sheet）的慣例——
   每個選項都是清楚標示意圖的完整一列，不是擠在同一行的兩顆按鈕。 */
.modal__footer--stacked {
  flex-direction: column;
  align-items: stretch;
}

.modal__footer--stacked .button {
  justify-content: center;
}

.modal__actions {
  margin-top: 1rem;
  display: flex;
  gap: 0.5rem;
}

.modal__actions--wrap {
  flex-wrap: wrap;
}

/* ---- 恢復金鑰彈窗：整個 App 的簽名元素，刻意跟其他畫面拉開視覺差異 ---- */
.modal--signature {
  max-width: 520px;
  text-align: left;
  border: 1px solid var(--color-accent-border);
  overflow: visible;
  position: relative;
  /* 上方留出蠟封的高度，讓標題從封印下方開始，構圖才不會擠在一起。 */
  padding-top: 5rem;
}

/* 標題是這個畫面的主角之一，字級要撐得起這個時刻的份量——照 Apple 的字體排版原則，
   階層是「字級＋字重＋行高」一起決定的，不是只靠放大字級。 */
.modal--signature .modal__title {
  font-size: 1.5rem;
  letter-spacing: -0.02em;
  line-height: 1.25;
  margin-bottom: 0.75rem;
}

.modal--signature__seal {
  width: 132px;
  height: 132px;
  position: absolute;
  top: -44px;
  left: -34px;
  filter: drop-shadow(0 10px 22px rgba(20, 22, 30, 0.34));
  pointer-events: none;
}

.modal--signature__warning {
  font-size: 0.825rem;
  line-height: 1.7;
  color: var(--color-danger);
  text-align: left;
  background: var(--color-danger-soft);
  border-radius: var(--radius-sm);
  padding: 0.75rem 0.9rem;
  margin: 0 0 1rem;
  line-break: strict;
  text-wrap: pretty;
}

/* ---- 使用說明彈窗：內容比其他彈窗長很多，固定高度、內容區自己捲動。 ---- */
.modal--help {
  max-width: 560px;
  display: flex;
  flex-direction: column;
  max-height: min(600px, 80vh);
}

.modal--help__body {
  overflow-y: auto;
  margin: 0.5rem -0.5rem 0;
  padding: 0 0.5rem;
}

.modal--update {
  max-width: 560px;
  display: flex;
  flex-direction: column;
  max-height: min(600px, 80vh);
}

.modal--update__body {
  overflow-y: auto;
  margin: 0.5rem -0.5rem 1rem;
  padding: 0.75rem 0.85rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-sm);
  background: var(--color-surface);
}

/* v-html 注入的 Markdown 內容目前全站沒有對應樣式，這裡補最小必要的排版，避免標題/清單/程式碼
   區塊看起來完全沒有層次。 */
.modal--update__body :where(h1, h2, h3) {
  margin: 0.75rem 0 0.4rem;
  font-size: 1rem;
  font-weight: 600;
}
.modal--update__body :where(h1, h2, h3):first-child {
  margin-top: 0;
}
.modal--update__body p {
  margin: 0.5rem 0;
}
.modal--update__body ul,
.modal--update__body ol {
  margin: 0.5rem 0;
  padding-left: 1.25rem;
}
.modal--update__body code {
  font-family: var(--font-mono);
  font-size: 0.85em;
  background: rgba(127, 127, 127, 0.18);
  padding: 0.1em 0.3em;
  border-radius: 3px;
}
.modal--update__body a {
  color: var(--color-accent);
}

.modal--help__section {
  margin-bottom: 1.5rem;
}

.modal--help__section:last-child {
  margin-bottom: 0;
}

.modal--help__section h3 {
  /* 回饋：標題（0.95rem）跟內文（0.85rem）只差 0.1rem，層次不夠明顯——拉大到 1.3rem，
     跟內文的差距更明確，掃視整份說明時標題才容易被抓出來當導覽用。顏色從強調色改成
     var(--color-text)（回饋：改成黑色——這裡用文字主色而不是寫死 #000，深色模式下
     --color-text 是近白色，寫死黑色會在深色背景下看不見）。 */
  font-size: 1.3rem;
  font-weight: 600;
  color: var(--color-text);
  margin: 0 0 0.5rem;
}

.modal--help__section p {
  font-size: 0.85rem;
  line-height: 1.75;
  color: var(--color-text);
  margin: 0;
  white-space: pre-line;
  line-break: strict;
  text-wrap: pretty;
}

.recovery-key-display {
  font-family: var(--font-mono);
  font-size: 1.15rem;
  font-weight: 500;
  letter-spacing: 0.04em;
  color: var(--color-text);
  background: var(--color-accent-soft);
  border: 1px dashed var(--color-accent-border);
  border-radius: var(--radius-md);
  padding: 1.1rem;
  word-break: break-all;
  user-select: all;
  cursor: text;
}

.recovery-key-display:focus {
  outline: none;
  border-style: solid;
  border-color: var(--color-accent);
}

/* 尊重系統的「減少動態效果」偏好設定：保留能幫助理解的透明度變化，
   去掉位移、縮放這類會造成前庭不適的動態。 */
@media (prefers-reduced-motion: reduce) {
  .button:active:not(:disabled) {
    transform: none;
  }

  .modal-enter-from .modal,
  .modal-leave-to .modal {
    transform: none;
  }

  .toast-enter-from,
  .toast-leave-to {
    transform: none;
  }

  .result-row-enter-from {
    transform: none;
  }

  .item-list-row-enter-from,
  .item-list-row-leave-to {
    transform: none;
  }

  .item-list-row-move {
    transition: none;
  }

  .tab-page-enter-active,
  .tab-page-leave-active,
  .step-forward-enter-active,
  .step-forward-leave-active,
  .step-backward-enter-active,
  .step-backward-leave-active {
    transition: none;
  }

  .step-forward-enter-from,
  .step-forward-leave-to,
  .step-backward-enter-from,
  .step-backward-leave-to {
    transform: none;
  }

  .group-row__chevron {
    transition: none;
  }

  /* 撕開驗證通過後其餘列補位動畫（見上方 .ticket-fly-* 說明）——這個 TransitionGroup
     漏掉這層保護，跟 TicketRow.vue 內部自己的撕開飛走動畫、EnvelopeEncrypt.vue 的
     mailaway-rig 是同一種「移動很遠的距離」動態，那兩處已經補過，這裡是盤點全站動畫
     時發現的漏網之魚，一併補上。只拿掉飛走的位移/旋轉，淡出的透明度變化保留。 */
  .ticket-fly-move {
    transition: none;
  }

  .ticket-fly-enter-active {
    transition: opacity 200ms ease;
  }

  .ticket-fly-enter-from {
    transform: none;
  }

  .ticket-fly-leave-active {
    transition: opacity 200ms ease;
  }

  .ticket-fly-leave-to {
    transform: none;
  }
}
</style>