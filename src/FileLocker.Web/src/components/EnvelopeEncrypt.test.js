import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount } from '@vue/test-utils'
import EnvelopeEncrypt from './EnvelopeEncrypt.vue'
import envelopeBodyTwoDarkUrl from '../assets/Envelope_Body_Two_Dark.svg'
import envelopeBodyEmptyDarkUrl from '../assets/Envelope_Body_Empty_Dark.svg'
import envelopeFlapDarkUrl from '../assets/Envelope_Flap_Dark.svg'
import envelopeBodyEmptyUrl from '../assets/Envelope_Body_Empty.svg'
import envelopeFlapUrl from '../assets/Envelope_Flap.svg'

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
      enableStandaloneMode: false,
      standaloneDestinationDir: null,
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

  describe('單檔案分散式加密勾選項（規劃文件 §3、實作計畫片 5）', () => {
    async function gotoStep2(wrapper) {
      await vi.advanceTimersByTimeAsync(2000)
      await wrapper.vm.$nextTick()
      await wrapper.find('[data-action="next"]').trigger('click')
      await vi.advanceTimersByTimeAsync(500)
      await wrapper.vm.$nextTick()
    }

    it('勾選主選項會 emit request-toggle-standalone-mode，且不直接改本地勾選狀態（要等 App.vue 確認風險提示）', async () => {
      const wrapper = mountEnvelope({ paths: ['C:\\a.txt'] })
      await gotoStep2(wrapper)

      const checkbox = wrapper.find('[data-field="enableStandaloneMode"]')
      await checkbox.setValue(true)

      expect(wrapper.emitted('request-toggle-standalone-mode')).toEqual([[true]])
      // 沒有一次性風險提示的確認/取消結果之前，checkbox 的原生勾選狀態要被復原成未勾選——
      // 不然使用者取消風險提示時，畫面會卡在「看起來已勾選、邏輯其實沒勾」的不一致狀態。
      expect(checkbox.element.checked).toBe(false)
    })

    it('沒勾主選項時，「存放到其他地方」子選項不顯示', async () => {
      const wrapper = mountEnvelope({ paths: ['C:\\a.txt'], enableStandaloneMode: false })
      await gotoStep2(wrapper)

      expect(wrapper.find('[data-field="standaloneOtherLocation"]').exists()).toBe(false)
    })

    it('勾了主選項後，子選項才會顯示', async () => {
      const wrapper = mountEnvelope({ paths: ['C:\\a.txt'], enableStandaloneMode: true })
      await gotoStep2(wrapper)

      expect(wrapper.find('[data-field="standaloneOtherLocation"]').exists()).toBe(true)
    })

    it('勾選子選項（目前還沒選定目的地）會 emit pick-standalone-destination 觸發選資料夾，不會直接改本地狀態', async () => {
      const wrapper = mountEnvelope({ paths: ['C:\\a.txt'], enableStandaloneMode: true, standaloneDestinationDir: null })
      await gotoStep2(wrapper)

      await wrapper.find('[data-field="standaloneOtherLocation"]').setValue(true)

      expect(wrapper.emitted('pick-standalone-destination')).toHaveLength(1)
    })

    it('已經選定 destinationDir 時，子選項顯示為勾選並顯示選定的路徑', async () => {
      const wrapper = mountEnvelope({
        paths: ['C:\\a.txt'], enableStandaloneMode: true, standaloneDestinationDir: 'D:\\我的資料夾',
      })
      await gotoStep2(wrapper)

      expect(wrapper.find('[data-field="standaloneOtherLocation"]').element.checked).toBe(true)
      expect(wrapper.text()).toContain('D:\\我的資料夾')
    })

    it('取消勾選子選項（已經選定過目的地）會 emit update:standaloneDestinationDir 帶 null，清掉選定路徑', async () => {
      const wrapper = mountEnvelope({
        paths: ['C:\\a.txt'], enableStandaloneMode: true, standaloneDestinationDir: 'D:\\我的資料夾',
      })
      await gotoStep2(wrapper)

      await wrapper.find('[data-field="standaloneOtherLocation"]').setValue(false)

      expect(wrapper.emitted('update:standaloneDestinationDir')).toEqual([[null]])
    })
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

  it('確認密碼欄位離開焦點時，兩次密碼不一致要顯示提示文字', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.txt'], password: 'hunter2', passwordConfirm: 'hunter3' })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-action="next"]').trigger('click')
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()

    expect(wrapper.find('[data-hint="password-mismatch"]').exists()).toBe(false)

    await wrapper.find('[data-field="passwordConfirm"]').trigger('blur')
    expect(wrapper.find('[data-hint="password-mismatch"]').exists()).toBe(true)
    expect(wrapper.find('[data-hint="password-mismatch"]').text()).toBe('encrypt.passwordMismatch')
  })

  it('確認密碼欄位是空的時，離開焦點也不該顯示不一致提示（使用者還沒開始打，不是打錯）', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.txt'], password: 'hunter2', passwordConfirm: '' })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-action="next"]').trigger('click')
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()

    await wrapper.find('[data-field="passwordConfirm"]').trigger('blur')
    expect(wrapper.find('[data-hint="password-mismatch"]').exists()).toBe(false)
  })

  it('顯示提示後，改到兩次密碼一致，提示要立刻消失，不用再次離開焦點', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.txt'], password: 'hunter2', passwordConfirm: 'hunter3' })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-action="next"]').trigger('click')
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-field="passwordConfirm"]').trigger('blur')
    expect(wrapper.find('[data-hint="password-mismatch"]').exists()).toBe(true)

    await wrapper.setProps({ passwordConfirm: 'hunter2' })
    expect(wrapper.find('[data-hint="password-mismatch"]').exists()).toBe(false)
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

  describe('深色模式下換用深色版信封素材（§8.5 待辦）', () => {
    // 深色模式是 App.vue 的 settingsTheme 應用程式設定（見 App.vue isDarkTheme computed），
    // 不是作業系統的 prefers-color-scheme，所以這裡直接用 prop 驅動，不需要／不應該
    // mock window.matchMedia——真的用 matchMedia 偵測的話，使用者在設定頁切換深色模式
    // 不會反映在信封上，只有真的改了作業系統主題才會變，這不是這個功能要的行為。

    it('isDarkTheme=false（預設）時，本體與封口用原本的淺色素材', async () => {
      const wrapper = mountEnvelope()
      await vi.advanceTimersByTimeAsync(2000)
      await wrapper.vm.$nextTick()

      expect(wrapper.find('.envelope-canvas__body').attributes('src')).toBe(envelopeBodyEmptyUrl)
      expect(wrapper.find('.flap-group__flap').attributes('src')).toBe(envelopeFlapUrl)
    })

    it('isDarkTheme=true 時，本體與封口改用 _Dark 版素材', async () => {
      const wrapper = mountEnvelope({ isDarkTheme: true })
      await vi.advanceTimersByTimeAsync(2000)
      await wrapper.vm.$nextTick()

      expect(wrapper.find('.envelope-canvas__body').attributes('src')).toBe(envelopeBodyEmptyDarkUrl)
      expect(wrapper.find('.flap-group__flap').attributes('src')).toBe(envelopeFlapDarkUrl)
    })

    it('深色模式下懸停多檔預覽，一樣切成對應張數的深色版本（不是退回淺色）', async () => {
      const wrapper = mountEnvelope({ isDarkTheme: true })
      await vi.advanceTimersByTimeAsync(2000)
      await wrapper.vm.$nextTick()

      const canvas = wrapper.find('.envelope-canvas')
      await canvas.trigger('dragenter', { dataTransfer: { items: [{ kind: 'file' }, { kind: 'file' }] } })

      expect(wrapper.find('.envelope-canvas__body').attributes('src')).toBe(envelopeBodyTwoDarkUrl)
    })
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

  // 走到「確認/取消」畫面前的共用步驟：進場→選檔→切到密碼頁→送出→processing→confirming。
  // 抽出來給下面幾個測試共用，因為現在確認 sheet 的出現是跟著這整條真實過場鏈觸發的
  // （不像舊版直接用 phase prop 算 class），沒辦法用 mountEnvelope({ phase: 'confirming' })
  // 這種直接掛載進某個階段的捷徑跳過中間過程。
  async function advanceToConfirmingSheetVisible(wrapper) {
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

    // 回饋：確認/取消畫面原本沒有 sheet、也沒有抽出動畫——現在蓋章收尾播完停留一下
    // （SETTLE_HOLD_MS）才會冒出確認 sheet，不是立刻出現。
    expect(wrapper.find('.sheet--confirm').classes()).toContain('sheet--hidden')
    await vi.advanceTimersByTimeAsync(500) // SETTLE_HOLD_MS
    await wrapper.vm.$nextTick()
  }

  it('phase 從 processing 變成 confirming：密碼頁先播兩段式抽出/收回，信封闔上顯示檔名/郵戳後，停留一下確認 sheet 才冒出來', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'form', pendingSummary: 'a.pdf' })
    await advanceToConfirmingSheetVisible(wrapper)

    expect(wrapper.find('.sheet--confirm').classes()).not.toContain('sheet--hidden')
    expect(wrapper.find('.confirm-summary').text()).toBe('a.pdf')
  })

  it('confirming 時顯示確認/取消按鈕，committing 時兩顆都要 disabled', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'form', pendingSummary: 'a.pdf' })
    await advanceToConfirmingSheetVisible(wrapper)
    expect(wrapper.find('.sheet--confirm').classes()).not.toContain('sheet--hidden')

    await wrapper.setProps({ phase: 'committing' })
    expect(wrapper.find('[data-action="confirm"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-action="cancel"]').attributes('disabled')).toBeDefined()
  })

  it('按確認、commit 成功進入 flying：確認 sheet 播兩段式抽出/收回退場，不會留在畫面上不管信封自己飛走', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'form', pendingSummary: 'a.pdf' })
    await advanceToConfirmingSheetVisible(wrapper)

    await wrapper.setProps({ phase: 'committing' })
    await wrapper.setProps({ phase: 'flying' })
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--confirm').classes()).toContain('sheet--reveal')

    await vi.advanceTimersByTimeAsync(280 + 280)
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--confirm').classes()).toContain('sheet--hidden')
  })

  it('按確認、commit 成功進入 flying：信封本體要等確認 sheet 完全收回才套用 is-flying，不能兩個動畫同時播', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'form', pendingSummary: 'a.pdf' })
    await advanceToConfirmingSheetVisible(wrapper)

    await wrapper.setProps({ phase: 'committing' })
    await wrapper.setProps({ phase: 'flying' })
    await wrapper.vm.$nextTick()
    // 確認 sheet 才剛開始播退場（reveal 階段），信封本體不該提前套用 is-flying。
    expect(wrapper.find('.mailaway-rig').classes()).not.toContain('is-flying')

    await vi.advanceTimersByTimeAsync(280 + 280) // 確認 sheet 兩段式退場播完
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.mailaway-rig').classes()).toContain('is-flying')
  })

  it('勾了恢復金鑰：confirming 時確認 sheet 先不出現，要等 recovery-key-modal-open 變 false（使用者關掉彈窗）才播抽出動畫', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'form', pendingSummary: 'a.pdf', recoveryKeyModalOpen: true })
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    await wrapper.find('[data-action="next"]').trigger('click')
    await vi.advanceTimersByTimeAsync(500)
    await wrapper.vm.$nextTick()

    await wrapper.setProps({ phase: 'processing' })
    await wrapper.setProps({ phase: 'confirming' })
    await vi.advanceTimersByTimeAsync(280 + 280 + 500) // 密碼頁退場 + 闔上信封蓋章收尾
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.envelope-outer').classes()).toContain('show-mail-info')

    // 恢復金鑰彈窗還開著：就算已經停留超過 SETTLE_HOLD_MS，確認 sheet 也不該冒出來。
    await vi.advanceTimersByTimeAsync(2000)
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--confirm').classes()).toContain('sheet--hidden')

    // 使用者關掉彈窗（App.vue 把 recoveryKeyDisplay 清空，這個 prop 跟著變 false）
    await wrapper.setProps({ recoveryKeyModalOpen: false })
    await vi.advanceTimersByTimeAsync(500) // SETTLE_HOLD_MS
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--confirm').classes()).not.toContain('sheet--hidden')
  })

  it('phase 是 flying 時，只有 translate 這個 transitionend 播完才 emit fly-away-complete（rotate/opacity 各自的 transitionend 較早播完，不能提早觸發）', async () => {
    const wrapper = mountEnvelope({ phase: 'flying' })
    const rig = wrapper.find('.mailaway-rig')

    await rig.trigger('transitionend', { propertyName: 'rotate' })
    expect(wrapper.emitted('fly-away-complete')).toBeUndefined()
    await rig.trigger('transitionend', { propertyName: 'opacity' })
    expect(wrapper.emitted('fly-away-complete')).toBeUndefined()

    await rig.trigger('transitionend', { propertyName: 'translate' })
    expect(wrapper.emitted('fly-away-complete')).toHaveLength(1)
  })

  it('從 confirming 取消（phase 回到 form）：確認 sheet 先播兩段式抽出/收回退場，信封才重新打開、回到密碼頁（不是選檔頁，密碼欄位要保留）', async () => {
    const wrapper = mountEnvelope({ paths: ['C:\\a.pdf'], phase: 'form', pendingSummary: 'a.pdf' })
    await advanceToConfirmingSheetVisible(wrapper)
    expect(wrapper.find('.sheet--confirm').classes()).not.toContain('sheet--hidden')

    await wrapper.setProps({ phase: 'form' })
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.sheet--confirm').classes()).toContain('sheet--reveal') // 回饋：按下取消要跑回去信封後面的動畫，不是瞬間消失

    await vi.advanceTimersByTimeAsync(280 + 280) // 確認 sheet 抽出/收回退場播完
    await wrapper.vm.$nextTick()
    await vi.advanceTimersByTimeAsync(420) // FLAP_MS，信封重新打開
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
