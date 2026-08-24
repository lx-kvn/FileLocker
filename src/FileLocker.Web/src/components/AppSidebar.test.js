import { describe, it, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import AppSidebar from './AppSidebar.vue'

const t = (key) => key

function mountSidebar(props = {}) {
  return mount(AppSidebar, {
    props: { active: 'encrypt', collapsed: false, t, ...props },
    // teleport 真的搬到 document.body 的話，wrapper.find() 找不到（那是元件自己 subtree
    // 以外的地方）——測試只關心「有沒有正確顯示/隱藏、內容對不對」這種邏輯，不是真的驗證
    // DOM 有沒有被搬家，所以 stub 掉讓內容原地渲染，方便用 wrapper.find() 查。
    global: { stubs: { teleport: true } },
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

  it('每個 nav 項目都帶 aria-label，收合時文字被隱藏但螢幕閱讀器仍讀得到項目名稱', () => {
    const wrapper = mountSidebar()
    const first = wrapper.findAll('.app-sidebar__nav-item')[0]
    expect(first.attributes('aria-label')).toBe('tab.encrypt')
  })

  it('收合狀態下 mouseenter 會顯示對應項目的提示框，mouseleave 會隱藏', async () => {
    const wrapper = mountSidebar({ collapsed: true })
    const second = wrapper.findAll('.app-sidebar__nav-item')[1]
    await second.trigger('mouseenter')
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.app-sidebar__tooltip').exists()).toBe(true)
    expect(wrapper.find('.app-sidebar__tooltip').text()).toBe('tab.folderGuard')

    await second.trigger('mouseleave')
    expect(wrapper.find('.app-sidebar__tooltip').exists()).toBe(false)
  })

  it('展開狀態下 mouseenter 不顯示提示框（文字本來就看得到，不用重複提示）', async () => {
    const wrapper = mountSidebar({ collapsed: false })
    const first = wrapper.findAll('.app-sidebar__nav-item')[0]
    await first.trigger('mouseenter')
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.app-sidebar__tooltip').exists()).toBe(false)
  })
})
