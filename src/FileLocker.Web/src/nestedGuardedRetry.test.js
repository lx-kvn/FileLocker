import { describe, it, expect } from 'vitest'
import {
  parseNestedGuardedPaths,
  formatNestedGuardedNames,
  shouldOfferNestedGuardedRetry,
  NESTED_GUARDED_ERROR_CODE,
} from './nestedGuardedRetry.js'

describe('parseNestedGuardedPaths', () => {
  it('用 | 拆開後端塞在 errorDetail 裡的多個資料夾路徑', () => {
    expect(parseNestedGuardedPaths('C:\\a\\機密|C:\\a\\帳務')).toEqual([
      'C:\\a\\機密',
      'C:\\a\\帳務',
    ])
  })

  it('只有一個路徑時回傳單元素陣列', () => {
    expect(parseNestedGuardedPaths('C:\\a\\機密')).toEqual(['C:\\a\\機密'])
  })

  it('空字串、null、undefined 一律回傳空陣列，呼叫端不用各自防一次', () => {
    expect(parseNestedGuardedPaths('')).toEqual([])
    expect(parseNestedGuardedPaths(null)).toEqual([])
    expect(parseNestedGuardedPaths(undefined)).toEqual([])
  })

  it('濾掉分隔符造成的空片段（例如結尾多一個 |）', () => {
    expect(parseNestedGuardedPaths('C:\\a\\機密||C:\\a\\帳務|')).toEqual([
      'C:\\a\\機密',
      'C:\\a\\帳務',
    ])
  })
})

describe('formatNestedGuardedNames', () => {
  it('只取每個路徑最後一層的名稱，用頓號相連', () => {
    expect(formatNestedGuardedNames(['C:\\a\\機密', 'D:\\b\\帳務'])).toBe('機密、帳務')
  })

  it('正斜線路徑也要能取到最後一層', () => {
    expect(formatNestedGuardedNames(['C:/a/機密'])).toBe('機密')
  })

  it('路徑結尾多一個分隔符時，取到的仍是資料夾名稱而不是空字串', () => {
    expect(formatNestedGuardedNames(['C:\\a\\機密\\'])).toBe('機密')
  })

  it('空陣列回傳空字串', () => {
    expect(formatNestedGuardedNames([])).toBe('')
  })
})

describe('shouldOfferNestedGuardedRetry', () => {
  it('錯誤碼相符且只選了一個項目時，才提供解鎖並重試的引導', () => {
    expect(shouldOfferNestedGuardedRetry(NESTED_GUARDED_ERROR_CODE, 1)).toBe(true)
  })

  it('沒有選取項目時（理論上不會發生）仍視為單一項目情境，不因此漏掉引導', () => {
    expect(shouldOfferNestedGuardedRetry(NESTED_GUARDED_ERROR_CODE, 0)).toBe(true)
  })

  it('批次多筆時不提供引導——多筆的重試協調複雜度不成比例，照一般錯誤訊息處理', () => {
    expect(shouldOfferNestedGuardedRetry(NESTED_GUARDED_ERROR_CODE, 2)).toBe(false)
  })

  it('其他錯誤碼一律不觸發', () => {
    expect(shouldOfferNestedGuardedRetry('MARKER_ALREADY_EXISTS', 1)).toBe(false)
    expect(shouldOfferNestedGuardedRetry(undefined, 1)).toBe(false)
  })
})
