import { ref } from 'vue'

// 側欄展開/收合狀態。這輪（Phase 1：側欄殼子＋票根清單）刻意不把這個狀態存到後端設定裡——
// 存起來需要新增一個 IPC 訊息類型，而這個階段的移植範圍明確排除新增後端溝通（見
// design-exploration/gui-styles-v2 的移植計畫）。收合狀態目前只存在記憶體裡，重開 App 會
// 回到預設展開狀態，之後如果要記住使用者偏好，再回頭把這裡接到 settings IPC。
export function useSidebar() {
  const collapsed = ref(false)

  function toggle() {
    collapsed.value = !collapsed.value
  }

  return { collapsed, toggle }
}
