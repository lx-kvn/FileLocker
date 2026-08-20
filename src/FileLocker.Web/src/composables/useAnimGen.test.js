import { describe, it, expect } from 'vitest'
import { bumpGen, isCurrentGen } from './useAnimGen.js'

describe('useAnimGen', () => {
  it('第一次 bumpGen 回傳 1，是目前最新的一輪', () => {
    const key = {}
    const gen = bumpGen(key)
    expect(gen).toBe(1)
    expect(isCurrentGen(key, gen)).toBe(true)
  })

  it('再次 bumpGen 會讓舊的世代編號變成不是最新的', () => {
    const key = {}
    const oldGen = bumpGen(key)
    const newGen = bumpGen(key)

    expect(newGen).toBe(oldGen + 1)
    expect(isCurrentGen(key, oldGen)).toBe(false)
    expect(isCurrentGen(key, newGen)).toBe(true)
  })

  it('沒被 bumpGen 過的 key，任何世代編號都不是「目前最新」', () => {
    const key = {}
    expect(isCurrentGen(key, 1)).toBe(false)
  })

  it('不同的 key 各自獨立計數，互不影響', () => {
    const keyA = {}
    const keyB = {}
    bumpGen(keyA)
    bumpGen(keyA)
    const genB = bumpGen(keyB)

    expect(isCurrentGen(keyB, genB)).toBe(true)
    expect(isCurrentGen(keyA, genB)).toBe(false) // 不同 key，同樣數字也不算數
  })

  it('模擬「快速連續開啟/取消」：舊動畫的 callback 檢查世代編號後應該直接放棄，不套用任何狀態', async () => {
    const envelope = {}
    const applied = []

    function playOpenAnimation() {
      const gen = bumpGen(envelope)
      // 模擬 setTimeout 排定的後續步驟
      return () => {
        if (!isCurrentGen(envelope, gen)) return
        applied.push('opened')
      }
    }

    const firstStepTwo = playOpenAnimation() // 第一輪：使用者點了開啟
    playOpenAnimation() // 使用者在第一輪動畫播完前又點了一次（例如取消後重開）
    firstStepTwo() // 第一輪排定的 callback 這時候才執行

    expect(applied).toEqual([]) // 第一輪已經是舊世代，不該套用任何狀態
  })
})
