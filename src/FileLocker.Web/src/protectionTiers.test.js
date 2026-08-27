import { describe, it, expect } from 'vitest'
import { ACTION_TIERS, gatesFor } from './protectionTiers.js'

describe('ACTION_TIERS', () => {
  it('永久刪除加密項目是最高等級——它是整個工具裡唯一按下去內容就真的救不回來的動作', () => {
    expect(ACTION_TIERS.deleteRecord).toBe('T3')
  })

  it('清除使用紀錄是 T2——刪掉的只是本機操作日誌，加密內容一個都沒少', () => {
    expect(ACTION_TIERS.clearHistory).toBe('T2')
  })

  it('搬移集中管理區是 T2——影響範圍大但可逆', () => {
    expect(ACTION_TIERS.moveVault).toBe('T2')
  })
})

describe('gatesFor', () => {
  // 改版前三個動作三種規則，而且保護最重的是後果最輕的那個（清除紀錄沒設定過驗證就整個功能
  // 鎖死，永久刪除卻只要密碼）。改版後收斂成一句話：已設定過關鍵操作驗證時 T2 以上一律要驗，
  // 沒設定過就退回確認彈窗，但 T3 一定要密碼。

  describe('已設定過關鍵操作驗證', () => {
    it('永久刪除要密碼、要 Windows Hello、還要最終確認', () => {
      expect(gatesFor('deleteRecord', true)).toEqual({
        needsPassword: true,
        needsCriticalAction: true,
        needsFinalConfirm: true,
      })
    })

    it('清除使用紀錄要 Windows Hello 跟最終確認，但不用密碼', () => {
      expect(gatesFor('clearHistory', true)).toEqual({
        needsPassword: false,
        needsCriticalAction: true,
        needsFinalConfirm: true,
      })
    })

    it('搬移集中管理區要 Windows Hello', () => {
      expect(gatesFor('moveVault', true).needsCriticalAction).toBe(true)
    })
  })

  describe('沒設定過關鍵操作驗證', () => {
    it('永久刪除仍然要密碼跟最終確認——不可逆的動作不會因為沒設定驗證就變成一路放行', () => {
      expect(gatesFor('deleteRecord', false)).toEqual({
        needsPassword: true,
        needsCriticalAction: false,
        needsFinalConfirm: true,
      })
    })

    it('清除使用紀錄退回一般確認彈窗，不再整個功能鎖死', () => {
      expect(gatesFor('clearHistory', false)).toEqual({
        needsPassword: false,
        needsCriticalAction: false,
        needsFinalConfirm: true,
      })
    })

    it('搬移集中管理區直接放行，維持原本行為', () => {
      expect(gatesFor('moveVault', false).needsCriticalAction).toBe(false)
    })
  })

  it('不認得的動作一律當成最高等級處理，不是一路放行', () => {
    // 之後有人新增動作卻忘記加進對照表時，寧可多問幾道也不要靜悄悄地放行。
    expect(gatesFor('somethingNew', false)).toEqual({
      needsPassword: true,
      needsCriticalAction: false,
      needsFinalConfirm: true,
    })
    expect(gatesFor('somethingNew', true).needsCriticalAction).toBe(true)
  })
})
