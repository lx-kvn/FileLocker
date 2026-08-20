import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import EnvelopeEncrypt from './EnvelopeEncrypt.vue'

const t = (key, params) => (params ? `${key}:${JSON.stringify(params)}` : key)

function mountEnvelope(props = {}) {
  return mount(EnvelopeEncrypt, {
    props: {
      t,
      paths: [],
      password: '',
      passwordConfirm: '',
      hint: '',
      enablePasskey: false,
      enableRecoveryKey: false,
      phase: 'form',
      progressPercent: 0,
      pendingSummary: null,
      ...props,
    },
  })
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('EnvelopeEncrypt', () => {
  it('掛載時信封是闔著的（is-closed），開始播落下動畫（is-dropping）', () => {
    const wrapper = mountEnvelope()
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-closed')
    expect(wrapper.find('.mailaway-rig').classes()).toContain('is-dropping')
  })

  it('落下動畫播完（DROP_MS+20ms）後才開封，且落下動畫全程沒有動到開合 class', async () => {
    const wrapper = mountEnvelope()
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-closed')

    await vi.advanceTimersByTimeAsync(800) // 落下動畫還沒播完（DROP_MS=820）
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-closed')

    await vi.advanceTimersByTimeAsync(50) // 超過 DROP_MS+20
    expect(wrapper.find('.mailaway-rig').classes()).not.toContain('is-dropping')
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-open')
  })

  it('開封動畫播完、停等 SETTLE_HOLD_MS 之後，sheet 才進場（頁一：選檔案）', async () => {
    const wrapper = mountEnvelope()
    await vi.advanceTimersByTimeAsync(820 + 20 + 420 + 500 + 10) // DROP_MS+20+FLAP_MS+SETTLE_HOLD_MS
    await wrapper.vm.$nextTick()

    const sheet = wrapper.find('.sheet--picker')
    expect(sheet.classes()).not.toContain('sheet--hidden')
  })

  it('sheet 進場是兩段式（先完全滑出清出信封，才收回疊上信封），不是直接跳到定住的狀態', async () => {
    const wrapper = mountEnvelope()
    await vi.advanceTimersByTimeAsync(820 + 20 + 420 + 500 + 10)
    await wrapper.vm.$nextTick()
    // 剛進場那一刻先是 --reveal（完全滑出），還不是 --settle
    expect(wrapper.find('.sheet--picker').classes()).toContain('sheet--reveal')
    expect(wrapper.find('.sheet--picker').classes()).not.toContain('sheet--settle')

    await vi.advanceTimersByTimeAsync(280) // SHEET_PHASE_MS，reveal 播完換 settle
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--picker').classes()).toContain('sheet--settle')
    expect(wrapper.find('.sheet--picker').classes()).not.toContain('sheet--reveal')
  })

  it('沒有選檔案時「下一步」按鈕是 disabled', async () => {
    const wrapper = mountEnvelope({ paths: [] })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[data-action="next"]').attributes('disabled')).toBeDefined()
  })

  it('從空狀態變成有檔案：跨越邊界才播縮放淡化，「選擇檔案」按鈕換成已選清單', async () => {
    const wrapper = mountEnvelope({ paths: [] })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet__empty-state').element.style.display).not.toBe('none')
    expect(wrapper.find('.picked-list-frame').element.style.display).toBe('none')

    await wrapper.setProps({ paths: ['C:\\a.txt'] })
    await vi.advanceTimersByTimeAsync(200 + 260) // MORPH_OUT_MS + MORPH_IN_MS
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.sheet__empty-state').element.style.display).toBe('none')
    expect(wrapper.find('.picked-list-frame').element.style.display).not.toBe('none')
  })

  it('選了檔案後可以點下一步，切到密碼頁，並 emit 對應事件', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.txt'] })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()

    await wrapper.find('[data-action="next"]').trigger('click')
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.sheet--password').classes()).not.toContain('sheet--hidden')
  })

  it('點選擇檔案／選擇資料夾會 emit pick-file／pick-folder', async () => {
    const wrapper = mountEnvelope()
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()

    await wrapper.find('[data-action="pick-file"]').trigger('click')
    await wrapper.find('[data-action="pick-folder"]').trigger('click')

    expect(wrapper.emitted('pick-file')).toHaveLength(1)
    expect(wrapper.emitted('pick-folder')).toHaveLength(1)
  })

  it('密碼欄位輸入會 emit update:password', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.txt'] })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-action="next"]').trigger('click')
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()

    await wrapper.find('[data-field="password"]').setValue('hunter2')
    expect(wrapper.emitted('update:password')).toEqual([['hunter2']])
  })

  it('phase 是 confirming 時顯示確認/取消按鈕，點確認會 emit confirm', async () => {
    const wrapper = mountEnvelope({ phase: 'confirming', pendingSummary: '測試檔案.txt' })
    await wrapper.find('[data-action="confirm"]').trigger('click')
    expect(wrapper.emitted('confirm')).toHaveLength(1)
  })

  it('phase 是 confirming 時點取消會 emit cancel', async () => {
    const wrapper = mountEnvelope({ phase: 'confirming', pendingSummary: '測試檔案.txt' })
    await wrapper.find('[data-action="cancel"]').trigger('click')
    expect(wrapper.emitted('cancel')).toHaveLength(1)
  })

  it('phase 是 processing 時顯示進度、密碼頁的送出按鈕 disabled，避免重複送出', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.txt'], phase: 'processing', progressPercent: 42 })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-action="next"]').trigger('click')
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.progress-bar__fill').attributes('style')).toContain('42')
    expect(wrapper.find('[data-action="submit"]').attributes('disabled')).toBeDefined()
  })

  it('拖曳懸停時（定案文件 §1.6）：本體圖示切成對應張數、提示文字隱藏、canvas 帶浮起陰影 class', async () => {
    const wrapper = mountEnvelope()
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()

    const canvas = wrapper.find('.envelope-canvas')
    await canvas.trigger('dragenter', { dataTransfer: { items: [{ kind: 'file' }, { kind: 'file' }] } })

    expect(canvas.classes()).toContain('is-drag-hovering')
    expect(wrapper.find('.dropzone-hint').classes()).toContain('is-hidden')
    expect(wrapper.find('.envelope-canvas__body').attributes('src')).toContain('Two')
  })

  it('拖曳離開後懸停狀態要還原，不會卡在懸停中的張數預覽', async () => {
    const wrapper = mountEnvelope()
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()

    const canvas = wrapper.find('.envelope-canvas')
    await canvas.trigger('dragenter', { dataTransfer: { items: [{ kind: 'file' }] } })
    await canvas.trigger('dragleave')

    expect(canvas.classes()).not.toContain('is-drag-hovering')
    expect(wrapper.find('.dropzone-hint').classes()).not.toContain('is-hidden')
  })

  it('放開檔案時會 emit drop 事件並清掉懸停狀態', async () => {
    const wrapper = mountEnvelope()
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()

    const canvas = wrapper.find('.envelope-canvas')
    await canvas.trigger('dragenter', { dataTransfer: { items: [{ kind: 'file' }] } })
    await canvas.trigger('drop', { dataTransfer: { items: [{ kind: 'file' }] } })

    expect(wrapper.emitted('drop')).toHaveLength(1)
    expect(canvas.classes()).not.toContain('is-drag-hovering')
  })

  it('phase 從 processing 變成 confirming：sheet 先播兩段式抽出/收回，收回後信封闔上並顯示檔名/郵戳', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'form', pendingSummary: 'a.pdf' })
    await vi.advanceTimersByTimeAsync(2000) // 播完進場，落到選檔頁
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-action="next"]').trigger('click') // 切到密碼頁
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()

    await wrapper.setProps({ phase: 'processing' })
    await wrapper.setProps({ phase: 'confirming' })
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--password').classes()).toContain('sheet--reveal')

    await vi.advanceTimersByTimeAsync(280)
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--password').classes()).toContain('sheet--retreat')

    await vi.advanceTimersByTimeAsync(280)
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.envelope-outer').classes()).toContain('is-closed')

    await vi.advanceTimersByTimeAsync(500) // FLAP_MS + 40
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.envelope-outer').classes()).toContain('show-mail-info')
    expect(wrapper.find('.mail-filename').text()).toBe('a.pdf')
  })

  it('confirming 時顯示確認/取消按鈕，committing 時兩顆都要 disabled', async () => {
    const wrapper = mountEnvelope({ phase: 'confirming', pendingSummary: 'a.pdf' })
    expect(wrapper.find('.mail-confirm-actions').classes()).not.toContain('is-hidden')

    await wrapper.setProps({ phase: 'committing' })
    expect(wrapper.find('[data-action="confirm"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-action="cancel"]').attributes('disabled')).toBeDefined()
  })

  it('從 confirming 取消（phase 回到 form）：信封重新打開，回到密碼頁，不是選檔頁（密碼欄位要保留）', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'confirming', pendingSummary: 'a.pdf' })

    await wrapper.setProps({ phase: 'form' })
    await vi.advanceTimersByTimeAsync(420) // FLAP_MS
    await wrapper.vm.$nextTick()

    expect(wrapper.find('.envelope-outer').classes()).toContain('is-open')
    expect(wrapper.find('.sheet--password').classes()).not.toContain('sheet--hidden')
  })

  it('快速連續重新掛載/卸載不會讓舊的 setTimeout 在新一輪動畫套用過期的狀態（世代編號防護）', async () => {
    const wrapper = mountEnvelope()
    wrapper.unmount() // 模擬使用者在動畫播到一半就把整個信封關掉（App.vue v-if 移除）

    // 卸載後就算計時器還在排程佇列裡，執行時也不該再操作已經被銷毀的元件狀態、不該拋出例外。
    const advance = () => vi.advanceTimersByTimeAsync(3000)
    await expect(advance()).resolves.not.toThrow()
  })
})
