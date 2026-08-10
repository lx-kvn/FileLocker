# 密碼庫前端跨 Repo 共用：發布成公開 npm 套件

**狀態**：accepted（規劃階段，尚未動工實作）

## 背景

ADR-0003 決定把密碼庫功能獨立成 PasswordVault，原始碼遷到獨立 GitHub repo。`PasswordVault_獨立化_規劃.md` 第 3 節同時定案「前端不整支複製，改採實體分割」——`FileLocker.App`（消費 PasswordVault.Core 編譯好的部件）跟全新的 `PasswordVault.exe`（編譯期直接內建 PasswordVault.Core）兩邊都需要顯示密碼庫畫面，要共用同一份 Vue 元件，不要各自維護一份逐漸漂移的複本。

但當時只確認方向，沒有定案機制，因為兩個 repo 是完全獨立的 git 歷史、獨立發布節奏，沒有共同的 monorepo workspace 可以用——這份 ADR 補上這個機制決定。

## 考慮過的方案

1. **發布成公開 npm 套件**（採用）：把整個密碼庫分頁封裝成一個 `<PasswordLockerPage>` 元件，發布到 npm 公開 registry（`@lx-kvn/password-locker-ui`），兩邊 repo 都當成一般的 npm 相依套件安裝。
2. **Git submodule**：兩邊 repo 都把共用元件的原始碼當 submodule 引入，不需要 npm 發布流程。但 submodule 的操作體驗（commit/push 順序、clone 時容易忘記 `--recurse-submodules`）對單人維護的專案規模來說不夠直觀，且解決不了「兩個獨立 Vite 建置」各自都要正確解析到 submodule 內容的問題。
3. **直接複製一份原始碼，之後手動同步**：零基礎設施成本，但正是 `PasswordVault_獨立化_規劃.md` 第 3 節一開始就想避免的「改一個 bug 要改兩份、行為逐漸漂移」問題，只是把問題往後延，不是解決。

## 決策理由

選擇方案 1。npm 公開套件發布對單人維護的專案規模來說基礎設施成本很低——不需要自架 registry、不需要 CI 發布管線，一個免費的 npm 帳號、`npm publish` 一行指令就能動。版本號（semver）天然解決「兩邊發布節奏不同步」的問題：`FileLocker.Web` 金定安裝的精確版本號，不用 `^` caret 自動升級，PasswordVault 那邊發新版套件不會讓 FileLocker 意外拿到還沒測試過的行為，升級與否是刻意的手動決定。

套件原始碼放在 PasswordVault repo 的 `packages/password-locker-ui/`，不另開第三個 repo——跟 ADR-0003「PasswordVault repo 是密碼庫功能唯一真相來源」的定位一致。PasswordVault repo 內部用 npm workspaces 讓 `src/PasswordVault.Web/` 直接連結本地版本的套件，開發時不需要先 publish 才能看到最新改動；`FileLocker.Web`（外部 repo）則透過正常的 npm 安裝流程取得已發布版本。

套件對外的介面刻意做了三個「不假設宿主環境」的設計：

- **樣式**：套件自帶預設 CSS 變數（`var(--color-accent, #A37E2C)` 這種帶 fallback 值的寫法），外層可以覆蓋——`FileLocker.Web` 現有的 `.theme-vault` 等主題機制不用改，`PasswordVault.Web`（全新專案沒有這套主題 CSS）也能看到合理預設值。
- **翻譯**：套件自帶完整的 zh-TW／en 翻譯表（密碼庫相關的 157 個翻譯鍵值整批搬進套件內部），只接受 `lang` prop，外層不需要透過 props 逐一傳 157 個字串進去。
- **IPC**：套件接受外層注入的 `sendMessage`／`requestMessage` 函式，不直接內嵌 `window.chrome.webview.postMessage`——兩邊宿主現在都是 WebView2，實際上注入值都是包一層同樣的 API，但套件介面本身不寫死這個假設，保留以後宿主環境改變的彈性。

### 依賴注入介面的完整清單（第二輪定案）

套件骨架建起來、對 `App.vue` 的實際依賴做完整盤點後，發現外層依賴不只 `lang`／`sendMessage`／`requestMessage` 三個，逐一定案如下：

| 依賴 | 決定 | 理由 |
| --- | --- | --- |
| `showToast`／`askConfirm`／`translateError` | 整套當 props（函式）注入 | host 本來就有一套現成的視覺實作，注入確保兩邊視覺一致；`PasswordVault.Web` 需要自己實作一份對應 UI 才能傳進去，這是刻意換來一致性的代價。 |
| `copyToClipboardWithAutoClear` | 套件內建一份，不注入 | 純函式，只用 `navigator.clipboard`／`setTimeout`，不吃任何 host 狀態，複製一份沒有「兩邊行為漂移」的風險。 |
| `vaultItems`／`refreshList` | 選填 props，不提供就隱藏「已加密檔案」類別的關聯功能 | 跟 `PasswordVault_獨立化_規劃.md` 第 6 節既有定案一致（獨立版預設隱藏這個類別）；`PasswordVault.Web` 不需要假造一份空陣列。 |
| 停用 Passkey／停用恢復金鑰的密碼提示彈窗 | 套件自己一份獨立簡化版，不再共用 host 的 `passwordPromptContext` 彈窗 | 這兩個流程本質上是密碼庫自己的事，只是歷史上因為同一個檔案裡方便共用；抽出獨立套件後沒有理由再依賴 host 的共用彈窗系統（那個彈窗還服務雙擊 `.locked` 檔案、資料夾防護解鎖等密碼庫以外的情境）。 |
| 訊息路由（25 個密碼庫訊息類型的回應） | 套件內部自己管一份 `pendingResolvers`，host 只需要把收到的每則訊息轉發給套件一個統一入口方法 | 原本 host 端 25 條 `messageHandlers` 全是機械化的 `resolvePending(同名, data)`，套件抽出去後沒理由讓 host 繼續知道密碼庫有哪些訊息類型的細節。 |
| Esc 鍵關彈窗、TOTP 計時器清理 | 搬進元件自己的 `onMounted`／`onUnmounted`／`@keydown` | 純粹是元件生命週期該管的事，不需要 host 介入。 |
| 加密成功後「順便存進密碼庫」的鉤子 | `defineExpose` 暴露方法，host 用 template ref 呼叫 | 這是 host（加密頁）主動呼叫密碼庫元件內部邏輯（含它自己的 confirm／驗證彈窗）的情境，`defineExpose` + template ref 是 Vue 3 標準做法。 |
| 密碼庫分頁從沒被打開過，也要能跳出上面那個鉤子的警報視窗 | 兩套並存：主畫面維持現有的 `v-if`/`Transition` 分頁切換動畫；另外在 DOM 裡藏一份專門接這個鉤子的密碼庫實例 | 保留現有分頁切換動畫不受影響，用一個額外的隱藏實例處理「使用者還沒點過密碼庫分頁」的情境，取捨是同時存在兩個元件實例、各自狀態不同步——但因為隱藏那份只服務這一個鉤子，不會有實際的狀態衝突。 |
| 深色／淺色主題（Passkey／恢復金鑰圖示要換黑白版本） | 新增 `theme` prop（`'light'`／`'dark'`），跟 `lang` 同樣由 host 傳入 | 套件目前只有 `lang`，沒有主題資訊；跟 `lang` 同一個設計模式，一致性高。 |

套件對外的完整 props 介面（這輪定案後）：`lang`、`theme`、`sendMessage`、`requestMessage`、`showToast`、`askConfirm`、`translateError`、`vaultItems`（選填）、`refreshList`（選填），另外透過 `defineExpose` 暴露至少一個方法給 host 主動呼叫（例如 `offerSaveEncryptedFiles(password, items)`）。

## 代價與風險

- **多一個需要獨立維護的套件**：改共用元件的 bug 或加功能，要先在 `packages/password-locker-ui/` 改，`npm publish` 新版本，`FileLocker.Web` 才需要另外去 bump 版本號、`npm install`——比起「複製貼上」多了一道發布步驟，但這是刻意換來的：避免兩邊各自修各自的、行為逐漸不一致。
- **精確版本號金定的取捨**：`FileLocker.Web` 不會自動拿到套件的新版本（即使是 bugfix），需要手動記得升級——這是刻意選擇「穩定優先於便利」，跟這輪 CLI（第 12 節）拒絕自動化查詢是同一種安全/穩定優先於便利的判斷邏輯。
- **npm 公開套件名稱一旦發布就很難改**：`@lx-kvn/password-locker-ui` 這個名稱定案後，之後想換名字要處理舊版本廢棄、兩邊 repo 同步改相依名稱，不是無痛的事。

## 已知限制

- 這份 ADR 只記錄「用什麼機制共用」跟「套件對外介面的設計原則」，套件內部實際的元件拆分顆粒度（`<PasswordLockerPage>` 內部要不要再拆表單／清單／TOTP 圓環等子元件）、`packages/password-locker-ui/` 的建置工具鏈設定（Vite library mode／Rollup 設定等）留到實作前另外規劃。
- npm 帳號的 scope（`@lx-kvn`）、發布權限、是否啟用 2FA 等帳號管理細節，這份 ADR 不涵蓋。
