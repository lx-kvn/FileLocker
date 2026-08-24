import { describe, it, expect, afterEach } from 'vitest'
import { useSidebar } from './useSidebar.js'

function setViewportWidth(width) {
  Object.defineProperty(window, 'innerWidth', { writable: true, configurable: true, value: width })
}

function fireResize() {
  window.dispatchEvent(new Event('resize'))
}

describe('useSidebar', () => {
  afterEach(() => {
    // 每個測試都可能改過 window.innerWidth／掛過 resize listener，還原成 jsdom 預設寬度，
    // 避免影響下一個測試的初始收合判斷。
    setViewportWidth(1024)
  })

  it('預設是展開狀態', () => {
    const { collapsed } = useSidebar()
    expect(collapsed.value).toBe(false)
  })

  it('視窗寬度小於 768px 時，初始化就是收合狀態', () => {
    setViewportWidth(500)
    const { collapsed } = useSidebar()
    expect(collapsed.value).toBe(true)
  })

  it('視窗從寬變窄跨過 768px 斷點時，自動收合', () => {
    setViewportWidth(1024)
    const { collapsed } = useSidebar()
    expect(collapsed.value).toBe(false)
    setViewportWidth(600)
    fireResize()
    expect(collapsed.value).toBe(true)
  })

  it('視窗從窄變寬跨過 768px 斷點時，自動展開', () => {
    setViewportWidth(600)
    const { collapsed } = useSidebar()
    expect(collapsed.value).toBe(true)
    setViewportWidth(1024)
    fireResize()
    expect(collapsed.value).toBe(false)
  })

  it('手動 toggle() 過後，斷點自動收合/展開不再覆蓋使用者的選擇', () => {
    setViewportWidth(1024)
    const { collapsed, toggle } = useSidebar()
    toggle() // 使用者手動收合
    expect(collapsed.value).toBe(true)
    setViewportWidth(500)
    fireResize() // 斷點本來也會判定收合，這裡驗證的是「不再由斷點接管」這件事本身
    expect(collapsed.value).toBe(true)
    setViewportWidth(1024)
    fireResize()
    expect(collapsed.value).toBe(true) // 使用者手動收合過，寬度變回來也不會被斷點自動展開
  })

  it('toggle() 會切換收合狀態，呼叫兩次要回到原狀', () => {
    const { collapsed, toggle } = useSidebar()
    toggle()
    expect(collapsed.value).toBe(true)
    toggle()
    expect(collapsed.value).toBe(false)
  })

  it('每次呼叫 useSidebar() 都是獨立狀態，不共用同一個 ref', () => {
    const a = useSidebar()
    const b = useSidebar()
    a.toggle()
    expect(a.collapsed.value).toBe(true)
    expect(b.collapsed.value).toBe(false)
  })
})
