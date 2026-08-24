import { describe, it, expect } from 'vitest'
import { computeTooltipPosition } from './tooltipPosition.js'

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
