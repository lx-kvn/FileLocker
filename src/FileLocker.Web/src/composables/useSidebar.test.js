import { describe, it, expect } from 'vitest'
import { useSidebar } from './useSidebar.js'

describe('useSidebar', () => {
  it('預設是展開狀態', () => {
    const { collapsed } = useSidebar()
    expect(collapsed.value).toBe(false)
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
