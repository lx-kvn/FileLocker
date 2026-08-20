import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import TicketRow from './TicketRow.vue'

const t = (key, params) => (params ? `${key}:${JSON.stringify(params)}` : key)

function baseItem(overrides = {}) {
  return {
    uuid: 'abc-1',
    originalName: '專案合約書.pdf',
    type: 'File',
    originalSizeBytes: 2516582,
    hint: '合約相關',
    createdAtUtc: '2026-08-10T14:22:00Z',
    markerFound: true,
    hasNestedLocks: false,
    nestedLockCount: 0,
    passkeyEnabled: false,
    recoveryKeyEnabled: false,
    ...overrides,
  }
}

function mountRow(item, extraProps = {}) {
  return mount(TicketRow, {
    props: { item, t, decrypting: false, ...extraProps },
  })
}

describe('TicketRow', () => {
  it('顯示檔名，一般情況下沒有 warning、沒有 nested-lock 徽章', () => {
    const wrapper = mountRow(baseItem())
    expect(wrapper.find('.info .name').text()).toBe('專案合約書.pdf')
    expect(wrapper.find('.status-warning').exists()).toBe(false)
    expect(wrapper.find('.badge--nested-lock').exists()).toBe(false)
  })

  it('markerFound 為 false 時顯示 postmark 警告', () => {
    const wrapper = mountRow(baseItem({ markerFound: false }))
    expect(wrapper.find('.status-warning').exists()).toBe(true)
  })

  it('hasNestedLocks 時顯示巢狀鎖定徽章跟數量', () => {
    const wrapper = mountRow(baseItem({ hasNestedLocks: true, nestedLockCount: 2 }))
    const badge = wrapper.find('.badge--nested-lock')
    expect(badge.exists()).toBe(true)
    expect(badge.text()).toContain('2')
  })

  it('永遠顯示基本的解密按鈕', () => {
    const wrapper = mountRow(baseItem())
    expect(wrapper.find('[data-action="decrypt"]').exists()).toBe(true)
  })

  it('passkeyEnabled 才顯示 Passkey 解鎖按鈕', () => {
    expect(mountRow(baseItem({ passkeyEnabled: false })).find('[data-action="passkey"]').exists()).toBe(false)
    expect(mountRow(baseItem({ passkeyEnabled: true })).find('[data-action="passkey"]').exists()).toBe(true)
  })

  it('recoveryKeyEnabled 才顯示恢復金鑰解鎖按鈕', () => {
    expect(mountRow(baseItem({ recoveryKeyEnabled: false })).find('[data-action="recovery-key"]').exists()).toBe(false)
    expect(mountRow(baseItem({ recoveryKeyEnabled: true })).find('[data-action="recovery-key"]').exists()).toBe(true)
  })

  it('點解密按鈕會 emit decrypt 事件並帶上這一列的 item', async () => {
    const item = baseItem()
    const wrapper = mountRow(item)
    await wrapper.find('[data-action="decrypt"]').trigger('click')
    expect(wrapper.emitted('decrypt')).toEqual([[item]])
  })

  it('點籤頭（撕線熱區）：立刻進入 is-peeking（即時觸覺回饋），並 emit decrypt（跟點解密按鈕走同一條密碼驗證路徑）', async () => {
    const item = baseItem()
    const wrapper = mountRow(item)
    await wrapper.find('.ticket__seal').trigger('click')

    expect(wrapper.find('.ticket').classes()).toContain('is-peeking')
    expect(wrapper.emitted('decrypt')).toEqual([[item]])
  })

  it('點籤頭後 decrypting 變 true（驗證通過、解密進行中）：維持撕開狀態，不受逾時影響', async () => {
    vi.useFakeTimers()
    const item = baseItem()
    const wrapper = mountRow(item)
    await wrapper.find('.ticket__seal').trigger('click')
    await wrapper.setProps({ decrypting: true })

    await vi.advanceTimersByTimeAsync(3000) // 遠超過撕一小角的逾時保底時間
    expect(wrapper.find('.ticket').classes()).toContain('is-peeking')
    vi.useRealTimers()
  })

  it('點籤頭後使用者取消密碼驗證（decrypting 沒有變 true）：逾時後自動退回，不會卡在撕開狀態', async () => {
    vi.useFakeTimers()
    const item = baseItem()
    const wrapper = mountRow(item)
    await wrapper.find('.ticket__seal').trigger('click')

    await vi.advanceTimersByTimeAsync(1600)
    expect(wrapper.find('.ticket').classes()).not.toContain('is-peeking')
    vi.useRealTimers()
  })

  it('decrypting 從 true 變回 false（驗證失敗）：立刻還原撕開狀態，不用等逾時', async () => {
    const item = baseItem()
    const wrapper = mountRow(item, { decrypting: true })
    expect(wrapper.find('.ticket').classes()).toContain('is-peeking') // 掛載當下 decrypting 就是 true，一開始就該是撕開狀態

    await wrapper.setProps({ decrypting: false })
    expect(wrapper.find('.ticket').classes()).not.toContain('is-peeking')
  })

  it('點 Passkey 按鈕會 emit decrypt-via-passkey', async () => {
    const item = baseItem({ passkeyEnabled: true })
    const wrapper = mountRow(item)
    await wrapper.find('[data-action="passkey"]').trigger('click')
    expect(wrapper.emitted('decrypt-via-passkey')).toEqual([[item]])
  })

  it('點恢復金鑰按鈕會 emit decrypt-via-recovery-key', async () => {
    const item = baseItem({ recoveryKeyEnabled: true })
    const wrapper = mountRow(item)
    await wrapper.find('[data-action="recovery-key"]').trigger('click')
    expect(wrapper.emitted('decrypt-via-recovery-key')).toEqual([[item]])
  })

  it('永遠顯示刪除按鈕，點下去會 emit delete 事件並帶上這一列的 item（純重新蒙皮不能把舊功能弄丟）', async () => {
    const item = baseItem()
    const wrapper = mountRow(item)
    expect(wrapper.find('[data-action="delete"]').exists()).toBe(true)
    await wrapper.find('[data-action="delete"]').trigger('click')
    expect(wrapper.emitted('delete')).toEqual([[item]])
  })

  it('decrypting 為 true 時解密相關按鈕要 disabled（避免重複觸發），但刪除鈕維持可點——照舊行為，原本表格版的刪除鈕本來就不受解密中狀態影響', () => {
    const wrapper = mountRow(baseItem({ passkeyEnabled: true, recoveryKeyEnabled: true }), { decrypting: true })
    expect(wrapper.find('[data-action="decrypt"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-action="passkey"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-action="recovery-key"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-action="delete"]').attributes('disabled')).toBeUndefined()
  })

  it('資料夾類型的圖示用資料夾視覺，檔案類型依副檔名（間接驗證有接上 fileTypeVisuals）', () => {
    const folderWrapper = mountRow(baseItem({ type: 'Folder', originalName: '家庭照片備份' }))
    const fileWrapper = mountRow(baseItem({ type: 'File', originalName: '密碼管理備份.zip' }))
    const folderColor = folderWrapper.find('.ticket__icon').attributes('style')
    const zipColor = fileWrapper.find('.ticket__icon').attributes('style')
    expect(folderColor).not.toBe(zipColor)
  })
})
