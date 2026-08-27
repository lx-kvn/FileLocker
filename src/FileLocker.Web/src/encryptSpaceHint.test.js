import { describe, it, expect } from 'vitest'
import { formatBytes, encryptSpaceHintFor } from './encryptSpaceHint.js'

describe('formatBytes', () => {
  it('依大小自動選單位，不會出現「0.0009 GB」這種讀不出來的數字', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(2048)).toBe('2.0 KB')
    expect(formatBytes(5 * 1024 * 1024)).toBe('5.0 MB')
    expect(formatBytes(3.5 * 1024 * 1024 * 1024)).toBe('3.5 GB')
  })

  it('剛好在單位邊界時進位到下一個單位', () => {
    expect(formatBytes(1024)).toBe('1.0 KB')
    expect(formatBytes(1024 * 1024)).toBe('1.0 MB')
  })

  it('0 不顯示小數', () => {
    expect(formatBytes(0)).toBe('0 B')
  })
})

describe('encryptSpaceHintFor', () => {
  const Gb = 1024 * 1024 * 1024
  const Mb = 1024 * 1024

  it('空間不足時一律提醒，不管量多小——這是使用者真的會踩到的情況', () => {
    const hint = encryptSpaceHintFor({ totalRequiredBytes: 50 * Mb, sufficient: false })

    expect(hint).not.toBeNull()
    expect(hint.level).toBe('warning')
    expect(hint.amount).toBe('50.0 MB')
  })

  it('空間夠、但量大到值得先知道時，給一則資訊性提示', () => {
    const hint = encryptSpaceHintFor({ totalRequiredBytes: 4 * Gb, sufficient: true })

    expect(hint).not.toBeNull()
    expect(hint.level).toBe('info')
    expect(hint.amount).toBe('4.0 GB')
  })

  it('空間夠而且量不大時完全不提醒——每次加密都跳一行沒人要看的數字只是雜訊', () => {
    expect(encryptSpaceHintFor({ totalRequiredBytes: 20 * Mb, sufficient: true })).toBeNull()
  })

  it('還沒選任何項目（估算為 0）時不提醒', () => {
    expect(encryptSpaceHintFor({ totalRequiredBytes: 0, sufficient: true })).toBeNull()
  })

  it('還沒拿到估算結果時不提醒，不會閃一下錯的數字', () => {
    expect(encryptSpaceHintFor(null)).toBeNull()
    expect(encryptSpaceHintFor(undefined)).toBeNull()
  })
})
