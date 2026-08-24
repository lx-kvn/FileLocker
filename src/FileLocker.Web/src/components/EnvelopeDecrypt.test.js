import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import EnvelopeDecrypt from './EnvelopeDecrypt.vue'

const t = (key, params) => (params ? `${key}:${JSON.stringify(params)}` : key)

function mountEnvelope(props = {}) {
  return mount(EnvelopeDecrypt, {
    props: {
      t,
      originalName: '合約掃描件.pdf',
      createdAtUtc: '2026-08-01T12:00:00Z',
      passkeyEnabled: false,
      recoveryKeyEnabled: false,
      verifyState: { status: 'idle' },
      commitState: { status: 'idle' },
      ...props,
    },
  })
}

const DROP_MS = 820
const SETTLE_HOLD_MS = 500
const SHEET_PHASE_MS = 280
const MORPH_OUT_MS = 200
const MORPH_IN_MS = 260
const VERIFIED_TO_DESTINATION_MS = SETTLE_HOLD_MS + MORPH_OUT_MS + MORPH_IN_MS

async function advancePastEntrance(wrapper) {
  await vi.advanceTimersByTimeAsync(DROP_MS + 20 + SETTLE_HOLD_MS + 10)
  await wrapper.vm.$nextTick()
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('EnvelopeDecrypt', () => {
  it('掛載時信封是闔著的，開始播落下動畫', () => {
    const wrapper = mountEnvelope()
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-closed')
    expect(wrapper.find('.mailaway-rig').classes()).toContain('is-dropping')
  })

  it('落下回彈播完後停在闔著（不像加密流程會自動打開封口）', async () => {
    const wrapper = mountEnvelope()
    await advancePastEntrance(wrapper)
    expect(wrapper.find('.mailaway-rig').classes()).not.toContain('is-dropping')
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-closed')
    expect(wrapper.find('.envelope-outer').classes()).not.toContain('is-open')
  })

  it('檔名／時間戳記從一開始就顯示，不用等任何動畫', () => {
    const wrapper = mountEnvelope()
    expect(wrapper.find('.mail-filename').text()).toBe('合約掃描件.pdf')
  })

  it('落地＋停等後，驗證 sheet 才進場', async () => {
    const wrapper = mountEnvelope()
    const sheet = wrapper.find('.decrypt-sheet')
    expect(sheet.classes()).toContain('sheet--hidden')

    await advancePastEntrance(wrapper)
    expect(sheet.classes()).not.toContain('sheet--hidden')
  })

  it('沒有開 Passkey 時不會自動送出 verify-passkey', async () => {
    const wrapper = mountEnvelope({ passkeyEnabled: false })
    await advancePastEntrance(wrapper)
    expect(wrapper.emitted('verify-passkey')).toBeUndefined()
  })

  it('有開 Passkey 時，sheet 一出現就自動觸發 verify-passkey，並顯示驗證中', async () => {
    const wrapper = mountEnvelope({ passkeyEnabled: true })
    await advancePastEntrance(wrapper)
    expect(wrapper.emitted('verify-passkey')).toHaveLength(1)
    expect(wrapper.find('.spinner').exists()).toBe(true)
  })

  it('Passkey 驗證失敗只顯示提示文字，不會自動重試', async () => {
    const wrapper = mountEnvelope({ passkeyEnabled: true })
    await advancePastEntrance(wrapper)

    await wrapper.setProps({ verifyState: { status: 'failed', message: 'Passkey 驗證未完成' } })
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.spinner').exists()).toBe(false)
    expect(wrapper.find('.decrypt-sheet__hint').text()).toBe('Passkey 驗證未完成')
    expect(wrapper.emitted('verify-passkey')).toHaveLength(1) // 沒有自動重試
  })

  it('輸入密碼送出會 emit submit-password 並立刻顯示驗證中', async () => {
    const wrapper = mountEnvelope()
    await advancePastEntrance(wrapper)

    await wrapper.find('input[type="password"]').setValue('my-password')
    await wrapper.find('.decrypt-sheet__submit').trigger('click')

    expect(wrapper.emitted('submit-password')).toEqual([['my-password']])
    expect(wrapper.find('.spinner').exists()).toBe(true)
  })

  it('點「使用恢復金鑰」在同一張卡片內部翻頁，不是抽出新卡片', async () => {
    const wrapper = mountEnvelope({ recoveryKeyEnabled: true })
    await advancePastEntrance(wrapper)

    expect(wrapper.find('.decrypt-sheet').classes()).not.toContain('decrypt-sheet--page2')
    const altButtons = wrapper.findAll('.decrypt-sheet__alt-btn')
    await altButtons[altButtons.length - 1].trigger('click')
    expect(wrapper.find('.decrypt-sheet').classes()).toContain('decrypt-sheet--page2')

    await wrapper.find('.decrypt-sheet__link').trigger('click')
    expect(wrapper.find('.decrypt-sheet').classes()).not.toContain('decrypt-sheet--page2')
  })

  it('驗證成功：打勾停留後 sheet 收回、信封打開、抽出選存檔位置 sheet', async () => {
    const wrapper = mountEnvelope()
    await advancePastEntrance(wrapper)

    await wrapper.find('input[type="password"]').setValue('pw')
    await wrapper.find('.decrypt-sheet__submit').trigger('click')
    await wrapper.setProps({ verifyState: { status: 'success' } })
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.check-mark').exists()).toBe(true)
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-closed')

    await vi.advanceTimersByTimeAsync(SETTLE_HOLD_MS + SHEET_PHASE_MS * 2 + 100) // 停等 + sheet 兩段式收回
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.envelope-outer').classes()).toContain('is-open')
    const destinationSheet = wrapper.find('.destination-sheet')
    expect(destinationSheet.classes()).not.toContain('sheet--hidden')
  })

  it('選存檔位置會 emit pick-destination', async () => {
    const wrapper = mountEnvelope({ verifyState: { status: 'success' } })
    await advancePastEntrance(wrapper)
    await vi.advanceTimersByTimeAsync(SETTLE_HOLD_MS + SHEET_PHASE_MS * 2 + 30)
    await wrapper.vm.$nextTick()

    await wrapper.find('.destination-sheet__body .button--primary').trigger('click')
    expect(wrapper.emitted('pick-destination')).toHaveLength(1)
  })

  it('存檔位置階段按取消會 emit cancel', async () => {
    const wrapper = mountEnvelope({ verifyState: { status: 'success' } })
    await advancePastEntrance(wrapper)
    await vi.advanceTimersByTimeAsync(SETTLE_HOLD_MS + SHEET_PHASE_MS * 2 + 30)
    await wrapper.vm.$nextTick()

    await wrapper.find('.destination-sheet__body .decrypt-sheet__link').trigger('click')
    expect(wrapper.emitted('cancel')).toHaveLength(1)
  })

  it('commit 成功後顯示「已還原到指定位置」，停留一段時間才 emit done', async () => {
    const wrapper = mountEnvelope({ verifyState: { status: 'success' } })
    await advancePastEntrance(wrapper)
    await vi.advanceTimersByTimeAsync(SETTLE_HOLD_MS + SHEET_PHASE_MS * 2 + 30)
    await wrapper.vm.$nextTick()

    await wrapper.setProps({ commitState: { status: 'success', restoredPath: 'C:\\restored.pdf' } })
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.destination-sheet__success').exists()).toBe(true)
    expect(wrapper.emitted('done')).toBeUndefined()

    await vi.advanceTimersByTimeAsync(SETTLE_HOLD_MS + 500)
    expect(wrapper.emitted('done')).toHaveLength(1)
  })
})
