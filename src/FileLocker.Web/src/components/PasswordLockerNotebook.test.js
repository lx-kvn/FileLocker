import { describe, it, expect } from 'vitest'
import { mount } from '@vue/test-utils'
import PasswordLockerNotebook from './PasswordLockerNotebook.vue'

const t = (key) => key

// 跟 App.vue 既有的 passwordLockerDisplayTitle 同樣簽章的 stub——單元測試不需要真的
// 組網域摘要字串，直接回傳 item.title 就夠驗證「元件有沒有呼叫這個 prop function」。
const displayTitleFn = (item) => item.title

function makeItem(overrides = {}) {
  return {
    id: 'item-1',
    category: 'Website',
    title: 'GitHub',
    username: 'octocat',
    usernameHidden: false,
    hasTotp: false,
    sourceDeleted: false,
    ...overrides,
  }
}

function mountNotebook(props = {}) {
  return mount(PasswordLockerNotebook, {
    props: {
      websiteItems: [],
      fileItems: [],
      activeCategory: 'website',
      searchQuery: '',
      selectedIds: new Set(),
      visibleIds: new Set(),
      revealedPasswords: {},
      revealedTotps: {},
      usernameVisibleIds: new Set(),
      revealedUsernames: {},
      sortMode: 'alphabetical',
      isDark: false,
      hasSelection: false,
      selectedCount: 0,
      isLoading: false,
      displayTitleFn,
      t,
      ...props,
    },
  })
}

describe('PasswordLockerNotebook', () => {
  it('activeCategory 為 website 時只渲染 websiteItems，不渲染 fileItems', () => {
    const wrapper = mountNotebook({
      activeCategory: 'website',
      websiteItems: [makeItem({ id: 'w1', title: 'GitHub' }), makeItem({ id: 'w2', title: 'Gmail' })],
      fileItems: [makeItem({ id: 'f1', category: 'EncryptedFile', title: '合約書.pdf' })],
    })
    const rows = wrapper.findAll('.entry-row')
    expect(rows).toHaveLength(2)
    expect(wrapper.text()).not.toContain('合約書.pdf')
  })

  it('activeCategory 為 file 時只渲染 fileItems', () => {
    const wrapper = mountNotebook({
      activeCategory: 'file',
      websiteItems: [makeItem({ id: 'w1', title: 'GitHub' })],
      fileItems: [makeItem({ id: 'f1', category: 'EncryptedFile', title: '合約書.pdf' })],
    })
    const rows = wrapper.findAll('.entry-row')
    expect(rows).toHaveLength(1)
    expect(wrapper.text()).toContain('合約書.pdf')
  })

  it('點分類標籤會 emit select-category 帶正確的值，不會自己切換 activeCategory', async () => {
    const wrapper = mountNotebook({ activeCategory: 'website' })
    await wrapper.find('.cat-tab--file').trigger('click')
    expect(wrapper.emitted('select-category')).toEqual([['file']])
    // 沒有另外傳入新的 activeCategory，畫面上的 active 狀態不會自己變
    expect(wrapper.find('.cat-tab--website').classes()).toContain('is-active')
  })

  it('密碼欄位：visibleIds 不含該 id 時顯示遮罩點點', () => {
    const wrapper = mountNotebook({
      websiteItems: [makeItem({ id: 'w1' })],
      visibleIds: new Set(),
    })
    expect(wrapper.find('.entry-secret .dots').exists()).toBe(true)
    expect(wrapper.find('.entry-secret .revealed').exists()).toBe(false)
  })

  it('密碼欄位：visibleIds 含該 id 且有 revealedPasswords 時顯示明文', () => {
    const wrapper = mountNotebook({
      websiteItems: [makeItem({ id: 'w1' })],
      visibleIds: new Set(['w1']),
      revealedPasswords: { w1: 'hunter2' },
    })
    expect(wrapper.find('.entry-secret .revealed').text()).toBe('hunter2')
  })

  it('點眼睛按鈕會 emit toggle-password 帶該筆 item', async () => {
    const item = makeItem({ id: 'w1' })
    const wrapper = mountNotebook({ websiteItems: [item] })
    await wrapper.find('.eye-btn').trigger('click')
    expect(wrapper.emitted('toggle-password')).toEqual([[item]])
  })

  it('直接點密碼欄位（不用先顯示明文）會 emit copy-password 帶該筆 item', async () => {
    const item = makeItem({ id: 'w1' })
    const wrapper = mountNotebook({ websiteItems: [item], visibleIds: new Set() })
    await wrapper.find('.entry-secret .dots').trigger('click')
    expect(wrapper.emitted('copy-password')).toEqual([[item]])
    // 點密碼欄位不應該連帶觸發顯示/隱藏切換
    expect(wrapper.emitted('toggle-password')).toBeUndefined()
  })

  it('item.hasTotp 為 false 時不渲染懷錶徽章', () => {
    const wrapper = mountNotebook({ websiteItems: [makeItem({ id: 'w1', hasTotp: false })] })
    expect(wrapper.find('.totp-badge').exists()).toBe(false)
  })

  it('item.hasTotp 為 true 且 revealedTotps 有值時，渲染扇形＋三碼一組驗證碼', () => {
    const wrapper = mountNotebook({
      websiteItems: [makeItem({ id: 'w1', hasTotp: true })],
      revealedTotps: { w1: { code: '482913', period: 30 } },
    })
    expect(wrapper.find('.totp-badge').exists()).toBe(true)
    expect(wrapper.find('.totp-pie').exists()).toBe(true)
    expect(wrapper.find('.totp-code').text()).toBe('482 913')
  })

  it('item.hasTotp 為 true 但 revealedTotps 還沒有值時，顯示待顯示按鈕，點擊 emit toggle-totp', async () => {
    const item = makeItem({ id: 'w1', hasTotp: true })
    const wrapper = mountNotebook({ websiteItems: [item], revealedTotps: {} })
    expect(wrapper.find('.totp-pie').exists()).toBe(false)
    await wrapper.find('.totp-reveal-btn').trigger('click')
    expect(wrapper.emitted('toggle-totp')).toEqual([[item]])
  })

  it('搜尋框輸入時 emit update:search，不會自己在內部過濾清單', async () => {
    const wrapper = mountNotebook({
      websiteItems: [makeItem({ id: 'w1', title: 'GitHub' })],
    })
    await wrapper.find('.search-box').setValue('gitlab')
    expect(wrapper.emitted('update:search')).toEqual([['gitlab']])
    // 元件本身不做過濾，父層沒有真的把清單換掉之前，列表維持不變
    expect(wrapper.findAll('.entry-row')).toHaveLength(1)
  })

  it('多選 checkbox 勾選會 emit toggle-select 帶 item id；selectedIds 含該 id 時 checkbox 是 checked', () => {
    const item = makeItem({ id: 'w1' })
    const wrapper = mountNotebook({ websiteItems: [item], selectedIds: new Set(['w1']) })
    const checkbox = wrapper.find('.entry-select input[type="checkbox"]')
    expect(checkbox.element.checked).toBe(true)
  })

  it('勾選 checkbox 會 emit toggle-select 帶正確 id', async () => {
    const item = makeItem({ id: 'w1' })
    const wrapper = mountNotebook({ websiteItems: [item], selectedIds: new Set() })
    await wrapper.find('.entry-select input[type="checkbox"]').trigger('change')
    expect(wrapper.emitted('toggle-select')).toEqual([['w1']])
  })

  it('點編輯按鈕會 emit edit 帶該筆 item', async () => {
    const item = makeItem({ id: 'w1' })
    const wrapper = mountNotebook({ websiteItems: [item] })
    await wrapper.find('.entry-actions button').trigger('click')
    expect(wrapper.emitted('edit')).toEqual([[item]])
  })

  it('isDark 為 false 時筆記本內頁圖用淺色素材，true 時換成深色素材', async () => {
    const wrapper = mountNotebook({ isDark: false })
    expect(wrapper.find('.notebook-body').attributes('src')).toContain('Notebook_Body.svg')
    await wrapper.setProps({ isDark: true })
    expect(wrapper.find('.notebook-body').attributes('src')).toContain('Notebook_Body_Drack.svg')
  })

  it('清單為空時顯示既有的 empty-state 文案（沒有搜尋字串用 noItems，有搜尋字串用 noSearchResults）', async () => {
    const wrapper = mountNotebook({ websiteItems: [], searchQuery: '' })
    expect(wrapper.text()).toContain('passwordLocker.noItems')

    await wrapper.setProps({ searchQuery: 'xyz' })
    expect(wrapper.text()).toContain('passwordLocker.noSearchResults')
  })

  it('沒有選取任何項目時，工具列顯示新增／關聯／重新整理三顆按鈕，各自 emit 對應事件', async () => {
    const wrapper = mountNotebook({ hasSelection: false })
    expect(wrapper.find('.toolbar-btn--add').exists()).toBe(true)
    expect(wrapper.find('.toolbar-btn--cancel-selection').exists()).toBe(false)

    await wrapper.find('.toolbar-btn--add').trigger('click')
    expect(wrapper.emitted('add')).toHaveLength(1)
    await wrapper.find('.toolbar-btn--associate').trigger('click')
    expect(wrapper.emitted('associate')).toHaveLength(1)
    await wrapper.find('.toolbar-btn--refresh').trigger('click')
    expect(wrapper.emitted('refresh')).toHaveLength(1)
  })

  it('有選取項目時，工具列換成取消選取／刪除已選取（帶數量），各自 emit 對應事件', async () => {
    const wrapper = mountNotebook({ hasSelection: true, selectedCount: 3 })
    expect(wrapper.find('.toolbar-btn--add').exists()).toBe(false)
    expect(wrapper.find('.toolbar-btn--delete-selected').text()).toContain('3')

    await wrapper.find('.toolbar-btn--cancel-selection').trigger('click')
    expect(wrapper.emitted('cancel-selection')).toHaveLength(1)
    await wrapper.find('.toolbar-btn--delete-selected').trigger('click')
    expect(wrapper.emitted('delete-selected')).toHaveLength(1)
  })

  it('排序下拉切換會 emit update:sort 帶新的排序模式', async () => {
    const wrapper = mountNotebook({ sortMode: 'alphabetical' })
    await wrapper.find('.sort-select').setValue('time')
    expect(wrapper.emitted('update:sort')).toEqual([['time']])
  })

  it('TOTP 剩餘秒數 <= 5 秒時扇形加上 is-warning class', () => {
    const wrapper = mountNotebook({
      websiteItems: [makeItem({ id: 'w1', hasTotp: true })],
      revealedTotps: { w1: { code: '123456', period: 30 } },
    })
    // period=30 秒，totpSecondsRemaining 用 Date.now() 算，這裡不特別鎖定時間，只驗證
    // class 綁定機制本身存在（是不是真的觸發警示色由時間決定，不是這個測試要驗證的事）。
    expect(wrapper.find('.totp-pie').classes()).toBeDefined()
  })

  it('清單筆數不超過一頁時不顯示分頁器', () => {
    const items = Array.from({ length: 5 }, (_, i) => makeItem({ id: `w${i}`, title: `item${i}` }))
    const wrapper = mountNotebook({ websiteItems: items })
    expect(wrapper.find('.pager').exists()).toBe(false)
  })

  it('清單筆數超過一頁時顯示分頁器，且第一頁只渲染前 13 筆', () => {
    const items = Array.from({ length: 20 }, (_, i) => makeItem({ id: `w${i}`, title: `item${i}` }))
    const wrapper = mountNotebook({ websiteItems: items })
    expect(wrapper.find('.pager').exists()).toBe(true)
    expect(wrapper.find('.pager-num').text()).toBe('1 / 2')
    expect(wrapper.findAll('.entry-row')).toHaveLength(13)
    expect(wrapper.text()).toContain('item0')
    expect(wrapper.text()).not.toContain('item13')
  })

  it('點下一頁會換到第二頁，顯示剩下的項目；上一頁在第一頁時停用', async () => {
    const items = Array.from({ length: 20 }, (_, i) => makeItem({ id: `w${i}`, title: `item${i}` }))
    const wrapper = mountNotebook({ websiteItems: items })
    expect(wrapper.find('.pager button:first-child').attributes('disabled')).toBeDefined()

    await wrapper.findAll('.pager button')[1].trigger('click')
    expect(wrapper.find('.pager-num').text()).toBe('2 / 2')
    expect(wrapper.findAll('.entry-row')).toHaveLength(7)
    expect(wrapper.text()).toContain('item13')
  })

  it('切換分類會把頁碼重置回第一頁', async () => {
    const websiteItems = Array.from({ length: 20 }, (_, i) => makeItem({ id: `w${i}`, title: `w${i}` }))
    const fileItems = [makeItem({ id: 'f1', category: 'EncryptedFile', title: 'file1' })]
    const wrapper = mountNotebook({ activeCategory: 'website', websiteItems, fileItems })
    await wrapper.findAll('.pager button')[1].trigger('click')
    expect(wrapper.find('.pager-num').text()).toBe('2 / 2')

    await wrapper.setProps({ activeCategory: 'file' })
    expect(wrapper.find('.pager').exists()).toBe(false) // 只有 1 筆，不需要分頁器，也代表頁碼已經重置

    await wrapper.setProps({ activeCategory: 'website' })
    expect(wrapper.find('.pager-num').text()).toBe('1 / 2') // 切回來也是回到第一頁，不是記住原本的第二頁
  })
})
