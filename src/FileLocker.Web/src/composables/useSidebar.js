import { ref } from 'vue'

// 側欄展開/收合狀態。這輪（Phase 1：側欄殼子＋票根清單）刻意不把這個狀態存到後端設定裡——
// 存起來需要新增一個 IPC 訊息類型，而這個階段的移植範圍明確排除新增後端溝通（見
// design-exploration/gui-styles-v2 的移植計畫）。收合狀態目前只存在記憶體裡，重開 App 會
// 回到預設展開狀態，之後如果要記住使用者偏好，再回頭把這裡接到 settings IPC。

// 視窗寬度小於這個值時自動收合，沿用常見手機/平板響應式斷點，跟其他前端框架的慣例對齊，
// 不是這個專案另外量出來的特殊數字。
const COLLAPSE_BREAKPOINT_PX = 768

export function useSidebar() {
  const collapsed = ref(window.innerWidth < COLLAPSE_BREAKPOINT_PX)

  // 使用者只要手動觸發過一次 toggle()，就代表這次使用期間他自己決定了收合/展開狀態，
  // 之後不管視窗怎麼跨過斷點都不再由斷點自動接管——避免使用者手動展開後，恰好在斷點
  // 附近拖曳視窗邊緣就被自動收合蓋掉選擇，體感上會很煩躁。這個旗標只存在記憶體裡，
  // 跟 collapsed 本身一樣不跨重啟持久化。
  let userOverridden = false

  function toggle() {
    userOverridden = true
    collapsed.value = !collapsed.value
  }

  function handleResize() {
    if (userOverridden) return
    collapsed.value = window.innerWidth < COLLAPSE_BREAKPOINT_PX
  }

  window.addEventListener('resize', handleResize)

  return { collapsed, toggle }
}
