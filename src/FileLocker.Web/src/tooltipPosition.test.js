import { describe, it, expect } from 'vitest'
import { computeTooltipPosition, computeStackedTooltipPosition } from './tooltipPosition.js'

const viewportWidth = 1200
const viewportHeight = 800

describe('computeTooltipPosition', () => {
  it('正常情況下，放在錨點右側、垂直置中', () => {
    const pos = computeTooltipPosition({
      anchorRect: { top: 100, left: 20, right: 60, bottom: 140, height: 40 },
      tooltipSize: { width: 80, height: 24 },
      viewportWidth,
      viewportHeight,
    })
    expect(pos.left).toBe(60 + 10) // anchor.right + gap
    expect(pos.top).toBe(100 + 20 - 12) // 垂直中心對齊，減去 tooltip 高度一半
  })

  it('右側放不下時（例如視窗被拉得極窄），改放到錨點左側', () => {
    const pos = computeTooltipPosition({
      anchorRect: { top: 100, left: 200, right: 240, bottom: 140, height: 40 },
      tooltipSize: { width: 80, height: 24 },
      viewportWidth: 260, // 右側只剩 20px 空間，放不下寬 80 的提示框；左側有 200px 空間放得下
      viewportHeight,
    })
    expect(pos.left).toBe(200 - 10 - 80) // anchor.left - gap - tooltipWidth
  })

  it('提示框貼著視窗頂部的項目，不會被往上推出可視範圍外', () => {
    const pos = computeTooltipPosition({
      anchorRect: { top: 2, left: 20, right: 60, bottom: 20, height: 18 },
      tooltipSize: { width: 80, height: 24 },
      viewportWidth,
      viewportHeight,
    })
    expect(pos.top).toBeGreaterThanOrEqual(8) // margin
  })

  it('提示框貼著視窗底部的項目，不會被往下推出可視範圍外', () => {
    const pos = computeTooltipPosition({
      anchorRect: { top: 790, left: 20, right: 60, bottom: 810, height: 20 },
      tooltipSize: { width: 80, height: 24 },
      viewportWidth,
      viewportHeight,
    })
    expect(pos.top + 24).toBeLessThanOrEqual(viewportHeight - 8)
  })

  it('視窗窄到左右兩側都放不下時，至少不會讓提示框超出左邊界', () => {
    const pos = computeTooltipPosition({
      anchorRect: { top: 100, left: 5, right: 45, bottom: 140, height: 40 },
      tooltipSize: { width: 300, height: 24 },
      viewportWidth: 200,
      viewportHeight,
    })
    expect(pos.left).toBeGreaterThanOrEqual(8)
  })
})

// ---- 疊在錨點上方／下方的提示框（資訊圖示用）----
//
// 跟側欄那種「放在錨點側邊」的提示框是不同的擺法，所以是另一個函式而不是加參數：側邊那個
// 的取捨是左右擇一、垂直置中，這個是上下擇一、水平置中，共用一個函式只會變成一堆互斥的
// if。兩者共同的部分只有「不能超出視窗邊界」這個夾限。

describe('computeStackedTooltipPosition', () => {
  const viewport = { viewportWidth: 1000, viewportHeight: 800 }

  it('預設放在錨點正上方、水平置中', () => {
    const pos = computeStackedTooltipPosition({
      anchorRect: { left: 500, right: 520, top: 400, bottom: 420, width: 20, height: 20 },
      tooltipSize: { width: 200, height: 60 },
      ...viewport,
    })

    expect(pos.left).toBe(510 - 100) // 錨點中心 510，提示框寬 200
    expect(pos.top).toBe(400 - 8 - 60) // 錨點上緣往上留 gap，再放提示框高度
  })

  it('上方空間不夠時改放到錨點下方', () => {
    const pos = computeStackedTooltipPosition({
      anchorRect: { left: 500, right: 520, top: 30, bottom: 50, width: 20, height: 20 },
      tooltipSize: { width: 200, height: 60 },
      ...viewport,
    })

    expect(pos.top).toBe(50 + 8) // 錨點下緣加 gap
  })

  it('提示框比視窗還高時貼齊視窗上緣，至少讓開頭讀得到', () => {
    // 夾限的兩端會互相牴觸：貼齊下緣算出來是負值，這時候要以上緣為準，
    // 而不是讓提示框整塊飄到視窗外面。
    const pos = computeStackedTooltipPosition({
      anchorRect: { left: 500, right: 520, top: 10, bottom: 30, width: 20, height: 20 },
      tooltipSize: { width: 200, height: 900 },
      ...viewport,
    })

    expect(pos.top).toBe(8)
  })

  it('上方放不下、翻到下方後仍超出下緣時，往上夾住讓整塊留在視窗內', () => {
    const pos = computeStackedTooltipPosition({
      anchorRect: { left: 500, right: 520, top: 30, bottom: 50, width: 20, height: 20 },
      tooltipSize: { width: 200, height: 760 },
      ...viewport,
    })

    expect(pos.top).toBe(800 - 760 - 8)
  })

  it('靠近視窗左緣時往右夾住，不會超出去', () => {
    const pos = computeStackedTooltipPosition({
      anchorRect: { left: 5, right: 25, top: 400, bottom: 420, width: 20, height: 20 },
      tooltipSize: { width: 200, height: 60 },
      ...viewport,
    })

    expect(pos.left).toBe(8)
  })

  it('靠近視窗右緣時往左夾住，不會超出去', () => {
    const pos = computeStackedTooltipPosition({
      anchorRect: { left: 975, right: 995, top: 400, bottom: 420, width: 20, height: 20 },
      tooltipSize: { width: 200, height: 60 },
      ...viewport,
    })

    expect(pos.left).toBe(1000 - 200 - 8)
  })
})
