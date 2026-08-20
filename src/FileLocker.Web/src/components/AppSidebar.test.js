import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import AppSidebar from './AppSidebar.vue'

const t = (key) => key

function mountSidebar(props = {}) {
  return mount(AppSidebar, {
    props: { active: 'encrypt', collapsed: false, t, ...props },
  })
}

describe('AppSidebar', () => {
  it('展開狀態下顯示 4 個 nav 項目的文字 label', () => {
    const wrapper = mountSidebar()
    const labels = wrapper.findAll('.app-sidebar__nav-item .label').map((el) => el.text())
    expect(labels).toEqual([
      'tab.encrypt',
      'tab.folderGuard',
      'tab.passwordLocker',
      'tab.settings',
    ])
  })

  it('active prop 對應的項目要有 is-active class，其他沒有', () => {
    const wrapper = mountSidebar({ active: 'passwordLocker' })
    const items = wrapper.findAll('.app-sidebar__nav-item')
    const activeStates = items.map((el) => el.classes('is-active'))
    expect(activeStates).toEqual([false, false, true, false])
  })

  it('點 nav 項目會 emit navigate，帶對應的 key', async () => {
    const wrapper = mountSidebar()
    await wrapper.findAll('.app-sidebar__nav-item')[1].trigger('click')
    expect(wrapper.emitted('navigate')).toEqual([['folderGuard']])
  })

  it('collapsed 為 true 時外層元素要有 is-collapsed class', () => {
    const wrapper = mountSidebar({ collapsed: true })
    expect(wrapper.find('.app-sidebar').classes()).toContain('is-collapsed')
  })

  it('點收合按鈕會 emit toggle-collapse，不是自己切換狀態（狀態由呼叫端的 useSidebar 決定）', async () => {
    const wrapper = mountSidebar()
    await wrapper.find('.app-sidebar__collapse-btn').trigger('click')
    expect(wrapper.emitted('toggle-collapse')).toHaveLength(1)
  })

  it('有一個獨立的滑動色塊元素，不是把背景色直接畫在按鈕本身上（這樣切換時才能做位移動畫）', () => {
    const wrapper = mountSidebar()
    expect(wrapper.find('.app-sidebar__nav-highlight').exists()).toBe(true)
  })

  it('每個 nav 項目都帶 data-label（收合時 CSS ::after tooltip 靠這個屬性顯示文字）', () => {
    const wrapper = mountSidebar()
    const first = wrapper.findAll('.app-sidebar__nav-item')[0]
    expect(first.attributes('data-label')).toBe('tab.encrypt')
  })
})
