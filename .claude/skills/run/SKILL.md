---
name: run
description: 啟動 FileLocker.App（WPF + WebView2）並用背景截圖驗證畫面實際長什麼樣子，不用手動開視窗、不會搶走使用者的前景焦點。用在「這個 UI 改動有沒有生效」「畫面看起來對不對」這類需要眼見為憑的情境。
---

# 啟動並截圖 FileLocker

這個 skill 解決的問題：`dotnet test` 只驗證 `FileLocker.Core` 的邏輯層，不驗證 UI 有沒有正常
渲染。CLAUDE.md 的「UI 變更注意事項」要求截圖或描述排版結果再回報完成——這個 skill 提供一套
可重複執行的啟動＋截圖流程，取代「手動開視窗、手動截圖」。

**這個 skill 只能證明「有沒有崩潰、內容有沒有出現」，不能取代人眼判斷排版好不好看、間距對不對這種
細節主觀判斷**——複雜的排版變更（標題列、彈窗、對話框）截完圖之後還是要仔細看，必要時請使用者也看一眼。

## 兩種截圖方式，看情境選

### A. 只改了前端（Vue/CSS），還沒動 C#：直接截 Vite dev server

不需要重新編譯/啟動整個 WPF App，最快驗證前端改動：

```bash
cd src/FileLocker.Web
npm run dev -- --port 5173 &   # 背景執行；如果已經在跑就跳過這步
```

```bash
npx playwright screenshot --viewport-size=1100,750 "http://localhost:5173/" /path/to/out.png
```

第一次在這台機器上跑需要先下載瀏覽器執行檔（一次性，之後都是本機快取）：
```bash
npx playwright install chromium
```

用 Read 工具打開存出來的 PNG 直接看。

### B. 改了 C#（或想看完整原生視窗，含標題列/系統匣互動）：截真正的 WPF 視窗

1. **檢查有沒有已經在跑的實體**（單一執行個體 Mutex，見 CLAUDE.md「已知的坑」）：
   ```powershell
   Get-Process FileLocker -ErrorAction SilentlyContinue
   ```
   - 有的話：如果要驗證新編譯的程式碼，**先關掉它**再重新建置/啟動（下面步驟 2）——不然
     `dotnet build` 會被鎖住的 DLL 擋下來（CLAUDE.md 記錄過這個坑，症狀是 `FileLocker.App.Tests`
     建置失敗、`MSB3027`/`MSB3021`）。如果只是想看「現在畫面長怎樣」不需要重新編譯，直接跳到
     步驟 3 截圖，不用重啟。

2. **啟動**（Debug 建置會連到 `http://localhost:5173`，前端 dev server 要先跑，見上面方式 A）：
   ```bash
   cd src/FileLocker.Web && npm run dev -- --port 5173 &
   dotnet run --project src/FileLocker.App &
   ```
   App 啟動當下**會自動把自己的視窗搶到前景**（`WindowActivation.ForceToForeground`，CLAUDE.md
   要求的既有行為，不是這個 skill 的問題）——只有第一次啟動這一下會打斷使用者，啟動完成後的
   截圖動作本身不會再搶焦點。

3. **背景截圖**（不搶前景、不打斷使用者）：
   ```powershell
   pwsh .claude/skills/run/screenshot-window.ps1 -ProcessName FileLocker -OutputPath C:\path\to\out.png
   ```
   用 Read 工具打開看。這支腳本用 `PrintWindow` API，前提是視窗真的開著（不是縮到系統匣）。

4. **需要切分頁／觸發某個互動再截圖**：目前沒有腳本化的 UI 操作手段（沒有裝
   Playwright-for-Electron 那類工具去操作原生 WPF 控制項）——這種情境退回請使用者手動操作到
   那個畫面，或者改用方式 A 對著 Vite dev server 走（前端邏輯如果不真的需要 WebView2 環境，
   在純瀏覽器裡也能操作到同樣的畫面）。

## 收尾

測試完不需要特地關掉背景行程（`npm run dev`／`dotnet run` 的行程留著方便下次繼續測，不重複啟動
省時間）；如果要清乾淨：
```powershell
Get-Process FileLocker -ErrorAction SilentlyContinue | Stop-Process -Force
```
```bash
pkill -f "vite" 2>/dev/null   # 停掉 npm run dev 的 Vite dev server
```
