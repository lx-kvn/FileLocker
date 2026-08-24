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
    props: { item, t, decrypting: false, tearing: false, ...extraProps },
  })
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

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

  it('Vault 模式（一般加密）找不到指標檔時，不顯示「前往檔案原始位置」按鈕——內容仍在 Vault，清單頁本來就能直接解密', () => {
    const wrapper = mountRow(baseItem({ markerFound: false, storageMode: 'Vault' }))
    expect(wrapper.find('.status-warning__goto-link').exists()).toBe(false)
  })

  it('Standalone 模式找不到 .flocked 時，顯示「前往檔案原始位置」按鈕，點擊會 emit go-to-original-location', async () => {
    const item = baseItem({ markerFound: false, storageMode: 'Standalone', originalPath: 'D:\\某資料夾\\專案合約書.flocked' })
    const wrapper = mountRow(item)

    const link = wrapper.find('.status-warning__goto-link')
    expect(link.exists()).toBe(true)

    await link.trigger('click')
    expect(wrapper.emitted('go-to-original-location')).toEqual([[item]])
  })

  it('Standalone 模式但檔案還在（markerFound 為 true）時，不顯示這顆按鈕', () => {
    const wrapper = mountRow(baseItem({ markerFound: true, storageMode: 'Standalone' }))
    expect(wrapper.find('.status-warning__goto-link').exists()).toBe(false)
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

    expect(wrapper.find('.ticket-wrap').classes()).toContain('is-peeking')
    expect(wrapper.emitted('decrypt')).toEqual([[item]])
  })

  it('點籤頭後 decrypting 變 true（驗證通過、解密進行中）：維持撕開狀態，不受逾時影響', async () => {
    const item = baseItem()
    const wrapper = mountRow(item)
    await wrapper.find('.ticket__seal').trigger('click')
    await wrapper.setProps({ decrypting: true })

    await vi.advanceTimersByTimeAsync(3000) // 遠超過撕一小角的逾時保底時間
    expect(wrapper.find('.ticket-wrap').classes()).toContain('is-peeking')
  })

  it('點籤頭後使用者取消密碼驗證（decrypting 沒有變 true）：逾時後自動退回，不會卡在撕開狀態', async () => {
    const item = baseItem()
    const wrapper = mountRow(item)
    await wrapper.find('.ticket__seal').trigger('click')

    await vi.advanceTimersByTimeAsync(1600)
    expect(wrapper.find('.ticket-wrap').classes()).not.toContain('is-peeking')
  })

  it('decrypting 從 true 變回 false（驗證失敗）：立刻還原撕開狀態，不用等逾時', async () => {
    const item = baseItem()
    const wrapper = mountRow(item, { decrypting: true })
    expect(wrapper.find('.ticket-wrap').classes()).toContain('is-peeking') // 掛載當下 decrypting 就是 true，一開始就該是撕開狀態

    await wrapper.setProps({ decrypting: false })
    expect(wrapper.find('.ticket-wrap').classes()).not.toContain('is-peeking')
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

  // ---- 撕開＋停頓＋飛走完整序列（回饋：先前這裡用單一元素小幅抖動取代 mockup 的兩半
  // clone 機制，支點、撕開的點、停頓、時長全部對不上，這輪照 mockup 的 playSequence 重做，
  // 見 TicketRow.vue 檔案開頭的完整時序說明）。----

  it('tearing prop 變 true：兩輪 rAF 後進入 is-tearing + is-open，撕開的半邊套用完整幅度的 transform', async () => {
    const wrapper = mountRow(baseItem(), { decrypting: true })
    await wrapper.setProps({ tearing: true })

    // 兩輪 requestAnimationFrame 才會真的切到 is-open——vitest 的 fake timer 預設也會接管
    // rAF，用 advanceTimersByTimeAsync 就能把兩輪都推進去。
    await vi.advanceTimersByTimeAsync(50)

    const wrap = wrapper.find('.ticket-wrap')
    expect(wrap.classes()).toContain('is-tearing')
    expect(wrap.classes()).toContain('is-open')
    expect(wrap.classes()).not.toContain('is-peeking') // 真正撕開了，不再是「撕一小角」的狀態
  })

  it('is-open 之後停頓 TEAR_HOLD_MS（550ms）才進入 is-leaving，不是立刻飛走', async () => {
    const wrapper = mountRow(baseItem(), { decrypting: true })
    await wrapper.setProps({ tearing: true })
    await vi.advanceTimersByTimeAsync(50) // 進入 is-open

    expect(wrapper.find('.ticket-wrap').classes()).not.toContain('is-leaving')

    await vi.advanceTimersByTimeAsync(500) // 還沒到 550ms
    expect(wrapper.find('.ticket-wrap').classes()).not.toContain('is-leaving')

    await vi.advanceTimersByTimeAsync(60) // 超過 550ms
    expect(wrapper.find('.ticket-wrap').classes()).toContain('is-leaving')
  })

  it('is-leaving 之後，.ticket-stage 的 transform transitionend 播完才 emit torn-away（opacity 那個 transitionend 先到也不算數）', async () => {
    const wrapper = mountRow(baseItem(), { decrypting: true })
    await wrapper.setProps({ tearing: true })
    await vi.advanceTimersByTimeAsync(50 + 600) // 進場 + 停頓，進入 is-leaving

    const stage = wrapper.find('.ticket-stage')
    await stage.trigger('transitionend', { propertyName: 'opacity' })
    expect(wrapper.emitted('torn-away')).toBeUndefined()

    await stage.trigger('transitionend', { propertyName: 'transform' })
    expect(wrapper.emitted('torn-away')).toEqual([[wrapper.props('item')]])
  })

  it('快速連續重新掛載/卸載不會讓舊的 rAF/setTimeout 在新一輪動畫套用過期的狀態', async () => {
    const wrapper = mountRow(baseItem(), { decrypting: true })
    await wrapper.setProps({ tearing: true })
    wrapper.unmount() // 模擬撕開動畫播到一半整個元件就被移除（例如清單被清空）

    const advance = () => vi.advanceTimersByTimeAsync(3000)
    await expect(advance()).resolves.not.toThrow()
  })
})
