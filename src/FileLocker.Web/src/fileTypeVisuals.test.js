import { describe, it, expect } from 'vitest'
import { fileTypeVisual } from './fileTypeVisuals.js'

describe('fileTypeVisual', () => {
  it('資料夾一律走資料夾圖示，不看名稱', () => {
    expect(fileTypeVisual({ type: 'Folder', originalName: '家庭照片備份' })).toEqual({
      icon: 'folder',
      color: 'var(--color-accent)',
    })
  })

  it('已知副檔名對到對應的圖示與顏色', () => {
    expect(fileTypeVisual({ type: 'File', originalName: '專案合約書.pdf' })).toEqual({
      icon: 'document',
      color: '#C1502F',
    })
    expect(fileTypeVisual({ type: 'File', originalName: '密碼管理備份.zip' })).toEqual({
      icon: 'archive',
      color: '#4F7A52',
    })
    expect(fileTypeVisual({ type: 'File', originalName: '遠端桌面憑證.pfx' })).toEqual({
      icon: 'certificate',
      color: '#2B6CB0',
    })
  })

  it('音檔跟文字檔各自用獨立的圖示與顏色，不是共用泛用文件圖示', () => {
    expect(fileTypeVisual({ type: 'File', originalName: '會議錄音.mp3' })).toEqual({
      icon: 'audio',
      color: '#1E8A8A',
    })
    expect(fileTypeVisual({ type: 'File', originalName: '筆記.txt' })).toEqual({
      icon: 'text',
      color: '#B8752E',
    })
    // 音檔跟文字檔彼此顏色也要不同，不能兩個新類型互相撞色。
    expect(fileTypeVisual({ type: 'File', originalName: 'a.mp3' }).color)
      .not.toBe(fileTypeVisual({ type: 'File', originalName: 'a.txt' }).color)
  })

  it('執行檔（exe/dll/msi）共用文件圖示，但顏色跟其他 document 類型不同', () => {
    expect(fileTypeVisual({ type: 'File', originalName: '安裝程式.exe' })).toEqual({
      icon: 'document',
      color: '#6B5B95',
    })
    expect(fileTypeVisual({ type: 'File', originalName: '相依套件.dll' })).toEqual({
      icon: 'document',
      color: '#6B5B95',
    })
    expect(fileTypeVisual({ type: 'File', originalName: '安裝包.msi' })).toEqual({
      icon: 'document',
      color: '#6B5B95',
    })
    // 跟既有的 doc/docx（#2B5FA6）、pdf（#C1502F）不能撞色，否則使用者分不出「這是執行檔」。
    expect(fileTypeVisual({ type: 'File', originalName: 'a.exe' }).color)
      .not.toBe(fileTypeVisual({ type: 'File', originalName: 'a.docx' }).color)
  })

  it('常見影片副檔名共用圖片圖示，但顏色跟一般圖片不同', () => {
    for (const ext of ['mp4', 'mov', 'avi', 'mkv', 'wmv']) {
      expect(fileTypeVisual({ type: 'File', originalName: `影片.${ext}` })).toEqual({
        icon: 'image',
        color: '#B6396B',
      })
    }
    // 跟既有的 jpg/png（#8A5CB0）不能撞色，否則清單裡分不出照片跟影片。
    expect(fileTypeVisual({ type: 'File', originalName: 'a.mp4' }).color)
      .not.toBe(fileTypeVisual({ type: 'File', originalName: 'a.png' }).color)
  })

  it('副檔名比對不分大小寫', () => {
    expect(fileTypeVisual({ type: 'File', originalName: 'REPORT.PDF' })).toEqual({
      icon: 'document',
      color: '#C1502F',
    })
  })

  it('查不到對照的副檔名退回泛用檔案圖示，不是警告色', () => {
    expect(fileTypeVisual({ type: 'File', originalName: '未知格式.xyz' })).toEqual({
      icon: 'file',
      color: 'var(--color-text-tertiary)',
    })
  })

  it('沒有副檔名（檔名裡沒有點，或點在最前面/最後面）也退回泛用檔案圖示', () => {
    expect(fileTypeVisual({ type: 'File', originalName: '沒有副檔名的檔案' })).toEqual({
      icon: 'file',
      color: 'var(--color-text-tertiary)',
    })
    expect(fileTypeVisual({ type: 'File', originalName: '.gitignore' })).toEqual({
      icon: 'file',
      color: 'var(--color-text-tertiary)',
    })
    expect(fileTypeVisual({ type: 'File', originalName: '結尾是點.' })).toEqual({
      icon: 'file',
      color: 'var(--color-text-tertiary)',
    })
  })
})
