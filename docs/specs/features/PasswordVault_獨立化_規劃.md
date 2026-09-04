# PasswordVault：密碼庫獨立化規劃

**狀態：規劃階段第二輪已完成（架構骨幹＋主要實作細節皆已定案）**——這份文件記錄的是決策與理由，不是實作步驟；動工前如果規劃有調整，先改這份文件，不要邊做邊讓實作偷偷偏離這裡的定義。

---

## 目錄

- [1. 背景](#1-背景)
- [2. 命名與改名範圍](#2-命名與改名範圍)
- [3. 執行形態與元件關係](#3-執行形態與元件關係)
- [4. 資料模型：類別從固定 enum 改成兩個獨立欄位](#4-資料模型類別從固定-enum-改成兩個獨立欄位)
- [5. 自訂類別](#5-自訂類別)
- [6. 「已加密檔案」憑證在獨立版的顯示規則](#6-已加密檔案憑證在獨立版的顯示規則)
- [7. 資料遷移](#7-資料遷移)
- [8. 瀏覽器擴充功能／Native Messaging Host 共存](#8-瀏覽器擴充功能native-messaging-host-共存)
  - [8.1 實測發現的缺口：兩邊各自的 NativeHost.exe 副本與註冊表互相打架（2026-08-26）](#81-實測發現的缺口兩邊各自的-nativehostexe-副本與註冊表互相打架2026-08-26)
- [9. 發布方式](#9-發布方式)
- [10. Repo 遷移的實際步驟](#10-repo-遷移的實際步驟)
- [11. 安裝程式打包](#11-安裝程式打包)
- [12. CLI 工具](#12-cli-工具)
- [13. 版本號策略](#13-版本號策略)
- [14. 單一執行個體 Mutex／系統匣圖示](#14-單一執行個體-mutex系統匣圖示)
- [15. 設定不共享](#15-設定不共享)
- [16. 瀏覽器擴充功能品牌文字](#16-瀏覽器擴充功能品牌文字)
- [17. 測試補齊與 FileLocker 消費端切換（2026-08-26 grilling 定案並實作完成）](#17-測試補齊與-filelocker-消費端切換2026-08-26-grilling-定案並實作完成)
  - [測試覆蓋補齊](#測試覆蓋補齊)
  - [FileLocker 本體切換消費來源](#filelocker-本體切換消費來源)
  - [資產命名規則（PasswordVault 版）](#資產命名規則passwordvault-版)
- [待辦事項](#待辦事項)
- [已完成之待辦](#已完成之待辦)
- [18. 已知會延後或不做的事](#18-已知會延後或不做的事)

---

## 1. 背景

`已完成/密碼庫_功能規劃.md` 第 12 節原本把「獨立單機介面版本」列為明確擱置的構想。可選配部件架構（含第二階段自動安裝）落地之後，重新排入規劃，並在這輪 grilling 中決定：不是單純幫既有的 `FileLocker.PasswordLocker` 部件多做一份 UI 外殼，而是把整個密碼庫功能獨立成一個品牌、一個產品——**PasswordVault**，原始碼遷出到獨立 repo（見 [ADR-0003](../../adr/0003-passwordvault-separate-repo.md)）。

FileLocker 本體跟 PasswordVault 的關係，从「FileLocker 的一個可選配部件」重新定位成「FileLocker 是 PasswordVault 的其中一個消費端」——FileLocker.App 要用密碼庫功能，一樣是下載編譯好的核心元件（見第 3 節）動態載入。

**這輪規劃階段判斷錯誤，實作前更正**：原本認為「這件事本來就是既有架構，不需要改動 FileLocker.App 這一側的下載/載入機制」——實際檢視 `PasswordLockerModuleInstaller.cs` 才發現不成立：它目前寫死向 FileLocker 自己（`lx-kvn/FileLocker`）的 GitHub Release 尋找 `FileLocker.PasswordLocker.dll`，PasswordVault 遷出獨立 repo 之後，编譯產出已經改放在 `lx-kvn/PasswordVault` 的 Release、檔名也變成 `PasswordVault.Core.dll`——這兩邊完全對不上，FileLocker.App 這一側**確實需要改**（下載來源 repo、資產命名比對、部件檔名判斷三處都要跟著換），見第 10 節新增的實作規劃。

## 2. 命名與改名範圍

品牌名確定為 **PasswordVault**，套用範圍是整個專案徹底改名，不只是新獨立程式的對外顯示名稱：

| 現有名稱 | 新名稱 |
| --- | --- |
| `FileLocker.PasswordLocker`（C# 專案） | `PasswordVault.Core` |
| `plugins/PasswordLocker/`（FileLocker.App 外掛資料夾） | `plugins/PasswordVault/` |
| `src/FileLocker.Extension/`（瀏覽器擴充功能） | 隨 repo 遷移一併改名／改品牌文字 |
| （新）獨立桌面程式 | `PasswordVault.exe` |

FileLocker 本體 UI 上「密碼庫」分頁的中文名稱維持不變——這是使用者已經熟悉的既有語彙（見 `../../../CONTEXT.md` 的「密碼庫（Password Locker）」定義），改名範圍是專案／程式碼／安裝路徑層級，不是要求使用者跟著改口。

## 3. 執行形態與元件關係

`PasswordVault.exe` 是一個全新的 WPF 宿主專案（不是 `FileLocker.App` 加命令列參數切換模式），有自己的視窗、單一執行個體 Mutex、系統匣圖示，架構上跟 `FileLocker.App` 完全平行、互不影響——理由跟既有的可選配部件決策一致：`FileLocker.App` 拿掉密碼庫功能要能繼續正常運作，兩個消費端各自的生命週期不應該互相牽制。

前端不整支複製，改採**實體分割**：把現有 `App.vue` 裡密碼庫分頁相關的 template/script 拆成獨立 Vue 元件，`PasswordVault.exe`（新的 Vite 專案）與 `FileLocker.App`（現有 `FileLocker.Web`）兩邊都 import 同一份元件——避免以後修一個 bug 要改兩份程式碼、兩邊行為逐漸漂移的既有風險（跟這個專案「不使用複製貼上式的雙份維護」的一貫原則一致）。

**實際拆分方式（這輪定案，見 [ADR-0004](../../adr/0004-shared-password-locker-ui-npm-package.md)）**：

- **跨 repo 共用機制**：把整個密碼庫分頁封裝成一個整體元件（`<PasswordLockerPage>`，內部細節怎麼再拆是套件自己的事），發布成公開 npm 套件 `@lx-kvn/password-locker-ui`——兩個 repo 是完全獨立的 git 歷史、沒有共同的 monorepo workspace，公開 npm registry 免費、不需要自架任何發布基礎設施，版本號天然解決「兩邊發布節奏不同步」的問題。
- **套件原始碼位置**：放在 PasswordVault repo 的 `packages/password-locker-ui/`——跟 ADR-0003「PasswordVault repo 是密碼庫功能唯一真相來源」的定位一致，不另開第三個 repo。
- **PasswordVault repo 內部引用**：`src/PasswordVault.Web/`（新 Vite 專案，結構比照 `FileLocker.Web`）用 **npm workspaces** 連結本地版本的 `packages/password-locker-ui/`，開發時不需要先 publish 才能看到最新改動。
- **FileLocker.Web 引用**：透過 npm 安裝已發布的版本，**金定精確版本號**（不用 `^` caret 範圍）——同一個開發者維護兩邊，不需要 semver 自動升級的便利性，換取「FileLocker 不會因為 PasswordVault 那邊發了新版套件就意外拿到還沒測試過的行為」的穩定性，升級版本要手動改 `package.json` 裡的版本號、重新 `npm install`。
- **樣式**：套件自己帶一份預設 CSS 變數（`var(--color-accent, #A37E2C)` 這種帶 fallback 值的寫法），允許外層覆蓋——`FileLocker.Web` 繼續用它現有的 `.theme-vault` 等機制從外層覆蓋，行為跟現在完全一致；`PasswordVault.Web`（全新專案，沒有 FileLocker 那套現成的主題 CSS）不提供這些變數也能看到合理的預設樣式。
- **翻譯字串**：密碼庫相關的 157 個翻譯鍵值（`FileLocker.Web` 的 `locales/*.json` 裡 `passwordLocker.` 前綴那批）整批搬進套件內部，套件自己帶完整的 zh-TW／en 翻譯表，只接受 `lang` prop 決定顯示哪個語言，不需要外層把 157 個字串逐一透過 props 傳進去。
- **IPC 層**：套件不假設宿主一定是 WebView2，改成接受外層注入的 `sendMessage`／`requestMessage` 函式（props）——兩邊宿主現在都是 WebView2，實際上都是包一層 `window.chrome.webview.postMessage` 當作注入值，但套件本身的介面不寫死這個假設，保留以後宿主環境改變的彈性。

元件的消費關係，兩個消費端不對稱：

- **`PasswordVault.exe` 內建 `PasswordVault.Core`**，編譯期就參考，安裝好即可用，不需要另外跑一次「下載外掛部件」的流程——畢竟這支程式存在的唯一目的就是密碼庫功能，沒有「不想要這個功能」的情境。
- **`FileLocker.App` 維持現有「執行期偵測外部 dll、動態載入」的可選配部件機制不變**，只是使用者下載到的 dll 換成從 PasswordVault repo 編譯出來的 `PasswordVault.Core`。`FileLocker.App` 這一側完全不需要改動架構。

## 4. 資料模型：類別從固定 enum 改成兩個獨立欄位

現有 `PasswordCredentialEntry.Category` 是 `CredentialCategory` enum（`Website` / `EncryptedFile`）。這輪規劃新增「自訂類別」需求（見第 5 節），順勢發現「網站」其實不該是特殊值，真正跟其他類別行為不同的只有「已加密檔案」——因此拆成兩個獨立欄位：

- **`CategoryLabel`（自由文字）**：使用者新增憑證時直接打字輸入，相同名稱自動歸為同一類，不另外做一套「建立/管理類別」的介面。「網站」不再是寫死的特殊值，只是系統預設帶入的其中一個標籤字串，使用者可以改成任何名稱（例如「銀行」「軟體授權金鑰」）。
- **`IsEncryptedFile`（布林值）**：決定要不要走 Vault 連結那套特殊行為（見第 6 節）。

`CategoryLabel` 不等於 `"已加密檔案"` 就自動視為一般憑證，一律支援關聯網域、瀏覽器自動填入、TOTP——不因為標籤名稱不同而有功能差異，這點跟「網站」目前的完整能力對齊，避免使用者建立自訂類別後功能反而縮水。

既有資料要做一次遷移：`Category == Website` → `CategoryLabel = "網站"`、`IsEncryptedFile = false`；`Category == EncryptedFile` → `CategoryLabel = "已加密檔案"`、`IsEncryptedFile = true`。

## 5. 自訂類別

範圍確認為這輪一起做，FileLocker 整合版與 PasswordVault 獨立版都要有（不是獨立版限定的差異化功能），理由是分開實作徒增之後兩邊行為不一致的風險，且反正已經決定要把密碼庫分頁拆成共用元件（見第 3 節），類別開放本來就該在共用元件層級一次做好。

建立方式選擇「新增時直接打字輸入名稱，相同名稱自動歸為同一類」，不做獨立的「類別管理」畫面——類別本身沒有自己的 ID、圖示、可以重新命名/刪除的獨立生命週期，純粹是憑證上的一個字串欄位，靠字串比對分組。取捨是拼字錯誤會產生看似獨立、實際上是重複的類別（例如「銀行」跟「銀行 」），第一版接受這個限制，不做額外的模糊比對或自動合併。

## 6. 「已加密檔案」憑證在獨立版的顯示規則

`IsEncryptedFile = true` 的憑證，在 PasswordVault 獨立版預設隱藏——這類憑證的存在前提是「有一個真正被加密的檔案」，獨立版沒有加密功能，顯示這個分類形同暗示使用者這裡也能管理加密檔案，會造成誤解。

只有偵測到同一台電腦上也裝了 FileLocker 主體，才顯示這個分類。偵測方式是查 `FileLocker.App` 的安裝路徑／登錄檔項目（不是比對執行檔是否存在於固定相對路徑——兩個產品現在是各自獨立安裝，路徑不再保證相鄰）：查登錄檔 `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FileLocker`（`mac-style-windows-installer` 安裝程式寫入的解除安裝項目，見該專案 `installer_core.py`；FileLocker 目前部署在 `Program Files`，走的是需要系統管理員權限的安裝路徑，對應 `no_admin_install=false`，所以是 `HKEY_LOCAL_MACHINE` 不是 `HKEY_CURRENT_USER`），這個鍵底下的 `InstallLocation` 值就是安裝路徑，鍵存在即代表已安裝，不需要再進一步驗證路徑內容。

## 7. 資料遷移

真正的加密資料（`credentials.json`）現在存在 `%AppData%\FileLocker\PasswordLocker\`，跟安裝用的 `plugins/PasswordLocker/` dll 資料夾是不同路徑（前者是使用者資料，後者是程式檔案），改名後兩者都需要處理，但風險等級不同：dll 資料夾找不到只會被判定成「部件未安裝」，使用者資料路徑找不到則是使用者會誤以為密碼資料遺失，是更高風險的一項。

新版程式（不論是 `FileLocker.App` 载入改名後的 `PasswordVault.Core`，或是全新的 `PasswordVault.exe`）啟動時自動偵測：新路徑（例如 `%AppData%\PasswordVault\`）沒有資料、舊路徑（`%AppData%\FileLocker\PasswordLocker\`）有資料的話，自動把舊檔案搬過去。搬移的詳細行為（搬移後要不要刪除舊檔案、搬移失敗要怎麼提示使用者、兩邊都有資料時如何處理）留到動工前另外定案，這輪只確認「要自動遷移，不要求使用者手動處理」這個方向。

## 8. 瀏覽器擴充功能／Native Messaging Host 共存

瀏覽器擴充功能的自動填入功能，這輪確認獨立版也要完整支援——`PasswordVault.exe` 自己也會跑一份 Native Messaging Host（Named Pipe Server），複製 `PasswordLockerNativePipeServer` 現有的邏輯，不是把獨立版定位成「閹割版、不支援瀏覽器整合」的工具。

使用者同時安裝 `FileLocker.App` 與 `PasswordVault.exe` 時，兩邊會搶同一條 Named Pipe。共存規則沿用這個專案既有的單一執行個體 Mutex 處理哲學（見 `CLAUDE.md`「已知的坑」）：**互相偵測、先來後到，不強制接手**——兩邊啟動時都先檢查 Pipe 有沒有被佔用，沒被佔用才自己起 Server；已經有一邊在跑的話，晚啟動的那一邊就不再重複起自己的 Server，但其他功能（視窗、密碼庫本身的操作）不受影響。即使 `FileLocker.App` 後啟動、發現 Pipe 已經被 `PasswordVault.exe` 佔用，也不會搶過來——理由是強制接手需要設計一套跨行程協商訊息，確保接手當下沒有正在處理中的請求被中斷，複雜度與這個功能的實際需求不成比例，跟 ADR-0001 拒絕「擁有權轉移」方案是同一種「不為了邊緣情境的體驗細節換取不成比例架構成本」的判斷。

### 8.1 實測發現的缺口：兩邊各自的 NativeHost.exe 副本與註冊表互相打架（2026-08-26）

`PasswordVault.exe` 這一側的 Native Messaging Host 註冊邏輯這輪規劃時說要做（見上方段落），但實際遷移時被排除在 `git filter-repo` 範圍外、延後處理（見 `src/PasswordVault.Extension/README.md` 現況說明），也就是說**目前只有 `FileLocker.App` 真的會呼叫 `PasswordLockerNativeHostRegistrar.EnsureRegistered`**，`PasswordVault.exe` 完全沒有對應邏輯。

單純之後幫 `PasswordVault.exe` 補一份自己的註冊呼叫，並不能真正解決共存問題——會出現兩套「搶先」機制互相打架：

- **註冊表**（`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.filelocker.passwordlocker`）：兩邊都在啟動時自我修復、覆寫成自己認得的路徑，所以是**最後啟動的一方贏**。
- **Named Pipe**：先搶到的一方持有連線，是**最先啟動的一方贏**。

這兩個「贏家」判斷邏輯不一致，可能出現「Chrome 被註冊表指向 A 的轉接程式副本，但 Pipe 被 B 持有」的組合——A 的轉接程式連上 Pipe 時，B 的 `VerifyClientIsExpectedHost` 拿它自己認得的路徑（A 的轉接程式跟 B 認得的路徑不同）去比對，比對失敗，連線被判定不合法而中斷（「Pipe is broken」）。這正是使用者實測時回報的現象。

**修正方向（已定案，尚未實作）**：不要讓兩邊各自帶一份自己的轉接程式副本，改成兩邊共用同一個實體檔案、同一個註冊值——概念上跟第 7 節「密碼庫資料改指向共用路徑」是同一招：

1. 找一個兩邊都能穩定讀寫、不受各自安裝路徑影響的共用位置放這支轉接程式（例如 `%LocalAppData%\PasswordVault\NativeHost\`，不是任何一邊各自的安裝資料夾）。
2. 誰先啟動，就負責把轉接程式複製到這個共用位置（如果還沒有的話）。
3. **兩邊的 Pipe 伺服器（`PasswordLockerNativePipeServer`／`PasswordVaultNativePipeServer`）的 `expectedClientExePath` 都改成認這同一個共用路徑**，不再各自認自己安裝資料夾裡的那份——這樣不管誰贏得 Pipe、誰贏得註冊表，雙方講的都是同一個地址，不會再對不上。
4. 註冊表寫入的內容，兩邊寫入的值最終會收斂成同一個（都指向這個共用路徑），不會再因為「誰後寫」而流失一致性。

需要改動的地方：`FileLocker.App`（`PasswordLockerNativeHostRegistrar`、`PasswordLockerNativePipeServer` 建構時傳入的 `expectedClientExePath`）、`PasswordVault.App`（新增自己的註冊邏輯、`PasswordVaultNativePipeServer` 建構時傳入的 `expectedClientExePath` 也要跟著改）。

**實作完成（2026-08-26，同一天稍晚）**：共用位置固定為 `%LocalAppData%\PasswordVault\NativeHost\`。

- `FileLocker.App`：`PasswordLockerNativeHostRegistrar` 新增 `SharedExePath`（固定回傳共用路徑字串，不檢查檔案是否存在）與 `EnsureSharedExeCopied`（誰先啟動就把 `plugins/PasswordLocker/` 底下所有 `PasswordVault.NativeHost.*` 檔案複製到共用位置，共用位置已經有檔案就什麼都不做，不比對版本新舊）；manifest 的 `path` 欄位改寫共用路徑。`App.xaml.cs` 建構 `PasswordLockerNativePipeServer` 時的 `expectedClientExePath` 也改傳 `SharedExePath`。
- `PasswordVault.App`：新增 `PasswordVaultNativeHostSync`（同一套邏輯的獨立實作，因為兩個 repo 沒有共用程式碼），`App.xaml.cs` 啟動時呼叫 `EnsureCopiedFrom(AppContext.BaseDirectory)` 後，`PasswordVaultNativePipeServer` 的 `expectedClientExePath` 改傳 `SharedExePath`。**這裡刻意沒有一併補上 `PasswordVault.exe` 自己的登錄檔／manifest 寫入邏輯**——那是待辦事項另一個獨立缺口（見下方待辦事項一節），目前只有 `FileLocker.App` 會寫登錄檔，這次只需要確保它寫入的 `path` 指向共用位置即可收斂一致，不需要 `PasswordVault.exe` 也重複寫一次登錄檔。
- 實測：先靜默啟動 `FileLocker.exe --startup`，確認共用資料夾被建立、四個檔案（`.exe`/`.dll`/`.deps.json`/`.runtimeconfig.json`）都複製過去、manifest 的 `path` 欄位正確指向共用路徑；關掉後再啟動 `PasswordVault.exe --startup`，確認共用資料夾已存在時不會重複複製、程式正常啟動不崩潰。`dotnet test` 兩邊都全數通過（FileLocker 349 個、PasswordVault 159 個）。
- **尚未驗證**：兩邊 App 同時開著、透過真實 Chrome 擴充功能實際觸發一次自動填入，確認不再出現「Pipe is broken」——這一步需要瀏覽器環境與已載入的擴充功能，留給使用者實機操作確認。

## 9. 發布方式

`PasswordVault.exe` 的安裝程式發布在獨立的 GitHub repo／Release，不掛在 FileLocker 現有的 repo 底下。原始碼本身也遷過去（見 [ADR-0003](../../adr/0003-passwordvault-separate-repo.md)），這份 repo 是新 repo 唯一真相來源，`FileLocker.PasswordLocker`／`FileLocker.Extension` 遷移後從這個 FileLocker repo 移除。

## 10. Repo 遷移的實際步驟

用 `git filter-repo`（非 Git 內建工具，需要另外安裝）從現有 FileLocker repo 切出 `src/FileLocker.PasswordLocker/`、`src/FileLocker.Extension/`、`tests/FileLocker.PasswordLocker.Tests/` 這幾個目錄的完整 commit 歷史，保留到新 repo，不是複製檔案內容重新開一筆 commit——理由是保住「這一行程式碼當初為什麼這樣寫」的可追溯性（`git blame` 查得到過去的決策脈絡），跟 CLAUDE.md「每個修正/決策留下理由」的既有慣例一致，只是這次是保留在 commit 歷史裡而不是程式碼註解裡。

動工前要先確認這幾個目錄跟 repo 其他部分（例如 `FileLocker.Core`、`FileLocker.PluginContracts`）有沒有交叉依賴——`git filter-repo` 只挑選指定路徑，任何被排除路徑的參照在切出來的新 repo 裡都會編譯失敗，需要先盤點清楚。

## 11. 安裝程式打包

PasswordVault 走 **no_admin_install** 模式（`mac-style-windows-installer` 既有支援的安裝模式），裝到 `%LOCALAPPDATA%\Programs\PasswordVault\`，不需要系統管理員權限、不彈 UAC——跟 FileLocker 現有的「裝到 `Program Files`、需要提權」不同，因為 PasswordVault 定位是輕量、獨立的密碼管理工具，沒有檔案關聯、沒有 Shell Extension 這類真的需要系統層級寫入的元件，沒有理由要求提權。

這個決定連帶影響第 6 節「偵測 FileLocker 主體是否安裝」的登錄檔查詢——那一題查的是 **FileLocker 本體**（走提權安裝、寫在 `HKEY_LOCAL_MACHINE`），不是 PasswordVault 自己；PasswordVault 自己的解除安裝登錄項目會依 `no_admin_install` 規則寫在 `HKEY_CURRENT_USER` 底下（見 `install_scope.py`），兩者是獨立的兩件事，不要混淆。

## 12. CLI 工具

PasswordVault 內建 CLI（隨 `PasswordVault.exe` 一起發布、一起編號，見第 13 節），提供自動化腳本查詢憑證的入口，用法比照 FileLocker 現有 `FileLocker.Cli` 的定位（安裝時加入 PATH）。

**驗證方式限定只能互動式輸入主密碼**：CLI 指令執行當下用命令列互動提示（隱藏輸入）要求輸入主密碼，不提供具名參數或環境變數傳遞密碼的方式——具名參數會被寫進 shell 歷史記錄，環境變數則是同一台機器上其他行程都讀得到，兩者都會讓「不經過驗證就能查到密碼」的攻擊面實質存在。這代表 CLI 沒辦法支援完全無人值守的自動化腳本（例如排程工作半夜自動跑），每次查詢都需要有人在旁邊互動輸入一次主密碼——這是刻意的取捨，安全性優先於自動化的完整程度，取捨方向與第 6 節「已加密檔案憑證」`IsEncryptedFile` 決策時同樣的原則一致：不因為便利性犧牲既有的驗證強度承諾。

## 13. 版本號策略

`PasswordVault.Core` 與 `PasswordVault.exe`（含內建 CLI）同一個 repo、同一個版號——因為 `PasswordVault.exe` 編譯期直接內建 `PasswordVault.Core`（見第 3 節），兩者本來就是同一次建置的產物，沒有獨立編號的必要，版號從 `0.1.0` 起算（沿用現有 `FileLocker.PasswordLocker` 的既有慣例）。

`FileLocker.App` 下載使用的部件版本，維持「版號各自獨立」——這是既有慣例（見 `已完成/密碼庫_功能規劃.md`），FileLocker 本體發新版不代表部件也要跟著出新版，PasswordVault 獨立化後這個既有規則不變。

## 14. 單一執行個體 Mutex／系統匣圖示

`PasswordVault.exe` 這一側完全比照 `FileLocker.App` 現有模式（`TrayIconManager.cs`／`WindowActivation.cs`）——專屬於 PasswordVault 的固定 Mutex 名稱、系統匣選單項目與行為（顯示主視窗／退出）直接複製既有實作邏輯，只換掉品牌相關的素材（圖示、選單文字）。單一執行個體處理仍然要遵守 `CLAUDE.md`「已知的坑」的既有原則：偵測到 Mutex 已被持有時呼叫 `SetForegroundWindow`（或等效前景焦點搶奪機制）把既有視窗拉到最前面，不能直接結束或讓例外把行程弄崩潰。

## 15. 設定不共享

`FileLocker.App` 與 `PasswordVault.exe` 的語言／主題設定各自獨立一份設定檔（`%AppData%\PasswordVault\settings.json` 與 FileLocker 現有的 `AppSettings`），不互通——跟第 7 節「資料遷移」同樣的理由：兩者現在是完全獨立的產品，共享設定檔需要額外設計跨程序讀寫的並發/鎖定機制，且版本升級進度不一致時舊版讀到新版寫入的欄位要怎麼處理也是一個問題，用一份共用設定檔換來的便利性不成比例。使用者在兩邊各自設一次語言/主題，改一邊不影響另一邊。

## 16. 瀏覽器擴充功能品牌文字

擴充功能 popup／content-script 目前顯示的「FileLocker 密碼庫」等字樣，遷移後直接改稱「PasswordVault」，不保留 FileLocker 名稱過渡——跟第 2 節命名策略一致（品牌層級徹底改名，不做雙名並存），逐字文案（各處確切字串）留待實作時對照既有的 `zh-TW`／`en` locale 檔案逐一替換，這份文件不重複列出每一個字串。

## 17. 測試補齊與 FileLocker 消費端切換（2026-08-26 grilling 定案並實作完成）

### 測試覆蓋補齊

`PasswordVault` repo 目前只有 `tests/PasswordVault.Core.Tests`，`PasswordVault.App`／`PasswordVault.Cli` 都還沒有對應的測試專案。**「比照 FileLocker.App.Tests／FileLocker.Cli.Tests 的既有測試範疇」這個前提本身要更正**——回頭檢視發現：

- `FileLocker.App.Tests` 不是「App 層測試慣例」的代表，是 2026-08-09 一次資安稽核（Pipe Server 原本無條件信任連線端）之後，把稽核發現的攻擊流程固定成回歸測試的產物，範圍很窄，沒有更廣泛的「App 層該測什麼」先例可以照抄。
- `FileLocker.Cli.Tests` 測的是被抽出來的 4 個獨立邏輯類別（`CliArgumentParser`／`CliExitCode`／`CliLocalization`／`CliShellCompletion`），`PasswordVault.Cli` 目前整個是一支 163 行的 top-level statements 檔案，沒有拆出對應的可測類別。

定案的實際範圍：
- **`PasswordVault.App.Tests`**：查過 `PasswordVaultNativePipeServer.cs`，那次資安稽核的修復邏輯（`VerifyClientIsExpectedHost`）本身已經跟著遷移過來了，只是對應的回歸測試沒有跟著搬——把 `PasswordLockerNativePipeServerTests.cs` 整份邏輯移植成 `PasswordVaultNativePipeServerTests.cs`（改類別名稱、管道名稱前綴），不主動找其他新的測試範圍。
- **`PasswordVault.Cli.Tests`**：先從 `Program.cs` 抽出兩塊可測邏輯，再對抽出來的部分寫測試（不直接測 top-level statements 本身）：`ReadPasswordMasked` 這個 local function（已經處理「輸入被重新導向時退回 `ReadLine`」的邊界情況，拉成 `private static` 方法即可）、以及 `ListCommandAsync` 裡「一筆憑證怎麼格式化成輸出文字」的部分（拆成「輸入憑證物件、回傳字串」的純函式）。互動流程本身、實際呼叫 `PasswordVault.Core` 驗證主密碼的部分不動。

兩個測試專案都遵照 CLAUDE.md「先寫測試」的開發流程逐一補齊，不是一次全補；這裡是回頭補測試（產品邏輯已存在、已定案），不是新功能開發，測試針對既有行為寫，不是先猜測試再讓實作遷就。

**實作完成**：`PasswordVault.App.Tests`（8 個測試，移植自 `PasswordLockerNativePipeServerTests.cs`）、`PasswordVault.Cli.Tests`（5 個測試，針對新抽出的 `CliHelpers.ReadPasswordMasked`／`CliHelpers.FormatCredentialLines`）皆已完成，`dotnet test PasswordVault.slnx` 全數通過（3 個測試專案共 159 個）。`ReadPasswordMasked` 額外把 `Console.IsInputRedirected`／`Console.In` 改成可選的注入參數——實作時發現 `Console.SetIn` 並不會讓 `Console.IsInputRedirected` 跟著變（後者查的是行程實際的標準輸入控制代碼），測試沒辦法只靠換讀取來源就切到重新導向分支，只能把判斷結果一起傳進來，這是規劃階段沒有預見、動手才發現的細節。

### FileLocker 本體切換消費來源

見本節開頭的更正說明——`PasswordLockerModuleInstaller.cs`／`PasswordLockerPluginLoader.cs`／`App.xaml.cs` 這幾處目前寫死指向 FileLocker 自己的 GitHub Release 與 `FileLocker.PasswordLocker.*` 系列檔名，需要改成指向 `lx-kvn/PasswordVault` 的 Release 與 `PasswordVault.Core.dll`／`PasswordVault.NativeHost.exe`。原本列的三個待定細節，這輪 grilling 已全數定案：

1. **`plugins/PasswordLocker/` 資料夾名稱維持不變，不改名**——載入邏輯（`PasswordLockerPluginLoader`）只是去固定資料夾找固定檔名的 dll，資料夾名稱本身使用者完全看不到（不是 UI 顯示的東西，純粹磁碟路徑）。改名的話，舊使用者資料夾裡還放著舊版 dll，程式改成去找新資料夾會誤判成「沒裝」，需要另外寫一次性遷移/偵測邏輯；不改名的話，既有的自動下載/更新流程（偵測資料夾有沒有 dll、沒有就自動抓）完全不用改，舊使用者下次自動更新時新 zip 內容自然蓋掉舊 dll。純美觀上的資料夾命名不一致，不值得為它多背一段遷移相容邏輯——跟 ADR-0001「不為了邊緣情境的體驗細節換取不成比例架構成本」同一種取捨。只換裡面找的檔名常數，從 `FileLocker.PasswordLocker.dll` 換成 `PasswordVault.Core.dll`。
2. **資產命名比對邏輯**（`PasswordLockerAssetSelector`）——已定案，見下方「資產命名規則」小節。
3. **Named Pipe 名稱／NativeHost 路徑**——查證後發現比預期單純：`PasswordLockerNativePipeServer.PipeName` 與 `PasswordVaultNativePipeServer.PipeName` 目前**字面上已經是同一個字串**（`"FileLocker-PasswordLocker-Pipe"`），第 8 節「兩邊搶同一條 Pipe」的共存設計早已成立，不需要為了這次切換再改；且 Pipe Server 本身是寫在 `FileLocker.App` 專案自己的程式碼裡（不是部件 dll 的一部分），換掉載入的 dll完全不影響它。唯一要改的是 `App.xaml.cs` 裡驗證連線端身分寫死比對的 NativeHost exe 路徑，從 `plugins/PasswordLocker/FileLocker.PasswordLockerNativeHost.exe` 換成 `plugins/PasswordLocker/PasswordVault.NativeHost.exe`（資料夾名稱不變，見第 1 點）。

**額外發現且一併定案的遺留項目**：`src/FileLocker.Extension/`（舊版瀏覽器擴充功能原始碼）目前 FileLocker repo 跟 PasswordVault repo 兩邊都有，第 9 節寫的「遷移後從這個 FileLocker repo 移除」這一步沒有真的做——這次一併定案：從 FileLocker repo 刪除，`PasswordVault` repo 的 `PasswordVault.Extension` 是唯一真相來源，FileLocker.App 的瀏覽器整合完全依賴部件 zip 裡帶的 `PasswordVault.NativeHost.exe`，不需要 FileLocker repo 自己再維護一份擴充功能原始碼。

**實作完成（2026-08-26）**：`PasswordLockerModuleInstaller`（改查 `lx-kvn/PasswordVault` Release）、`PasswordLockerAssetSelector`（改認新命名規則，測試先改紅燈再改實作）、`PasswordLockerPluginLoader`（改找 `PasswordVault.Core.dll`）、`PasswordLockerNativeHostRegistrar`／`App.xaml.cs`（改找 `PasswordVault.NativeHost.exe`）皆已完成，`src/FileLocker.PasswordLocker/`／`src/FileLocker.PasswordLockerNativeHost/`／`src/FileLocker.Extension/`／`tests/FileLocker.PasswordLocker.Tests/` 這幾個重複的舊原始碼一併從 FileLocker repo 刪除（連同 `FileLocker.slnx`／`FileLocker.App.Tests.csproj` 的對應參照），`dotnet test FileLocker.slnx` 全數通過（3 個測試專案共 349 個）。**尚未完成的部分**：PasswordVault 那邊的發布流程還沒能真正產出符合「資產命名規則」的 zip（含 `PasswordVault.Core.dll` 及其相依檔案、`PasswordVault.NativeHost.exe`），所以「FileLocker.App 實際從 Release 自動下載、切換部件生效」這條路徑目前只驗證到程式碼層級，還沒有機會人工實測。

**實測發現且修正（2026-08-26，同一天稍晚）**：使用者實際同時開著 `FileLocker.App` 跟 `PasswordVault.exe` 測試時發現，兩邊的密碼庫資料完全不一致（FileLocker 那邊 22 筆真實資料，PasswordVault 那邊只有 2 筆——回頭查是這輪稍早測試時塞進去的假資料）。根因：這輪切換消費來源只處理了「去哪個 repo 找部件、部件叫什麼名字」，沒有處理「密碼庫**資料**實際存在哪個資料夾」——`FileLocker.App` 把 `PasswordLockerPluginContext` 的 `dataDirectory` 指向自己既有的 `%LocalAppData%\FileLocker\PasswordLocker\`，`PasswordVault.exe` 指向自己的 `%LocalAppData%\PasswordVault\PasswordLocker\`，兩邊各自初始化同一份共用程式庫、但各自指向不同資料夾，變成兩份互相獨立、不同步的密碼庫，不是真正共用同一份——這違背了第 3 節「兩個消費端共用同一份密碼庫」的原始設計意圖（雖然規劃文件裡沒有把這句話講得很白，但第 7 節「資料遷移」的既有描述已經暗示了這個最終狀態）。

修正：`FileLocker.App` 改成指向跟 `PasswordVault.exe` 相同的共用路徑（`%LocalAppData%\PasswordVault\PasswordLocker\`），啟動時比照 `PasswordVault.Core.LegacyDataMigration` 同一套邏輯（複製不刪舊檔、新舊路徑都有資料時新路徑優先安靜略過）自動搬移舊資料——但這段邏輯不能直接呼叫 `LegacyDataMigration`（`FileLocker.App` 編譯期不依賴部件本體，是刻意的架構決定），改成在 `App.xaml.cs` 內部獨立複製一份等價的小邏輯（`MigratePasswordLockerDataIfNeeded`，不到 20 行）。實測搬移正確（22／23 筆真實資料從 FileLocker 舊路徑搬到共用新路徑），`dotnet test` 349 個測試全過。

### 資產命名規則（PasswordVault 版）

沿用既有 `PasswordLockerAssetSelector` 的設計精神（版本相容區間，理由見該檔案的 XML doc 註解），只換品牌前綴。原本 `PasswordLocker_vX.Y.Z_x.y.z-x.y.z.zip` 這種三組版本號緊貼在一起的寫法容易眼花撩亂（哪個是自己版本、哪個是相容區間上下限不容易一眼看出），改良為插入固定字詞當視覺分隔：

```
PasswordVault_v{PasswordVault.Core 自己的版本}_for-FileLocker-{相容最小版本}-to-{相容最大版本}.zip
```

例如 `PasswordVault_v0.1.0_for-FileLocker-1.3.0-to-2.0.0.zip`：一眼就看得出是「`0.1.0` 版的 PasswordVault，給 FileLocker 用，相容 1.3.0 到 2.0.0」，不需要先知道這個命名慣例才看得懂。解析端（`PasswordLockerAssetSelector` 的後繼者）只是多認 `for-FileLocker-`／`-to-` 這兩個固定字串，正則表達式複雜度不變。

相容區間**由 `PasswordVault` repo 每次更新 `vendor/FileLocker.PluginContracts.dll` 時手動決定並填入**（見該 repo `vendor/README.md`「已知的坑」——只有 FileLocker 那份介面契約變動時才需要重新 vendor），不自動推算：開發者需要對照 FileLocker 那邊介面契約異動的 commit／版本，判斷這次要標記的相容範圍上下限。這一步刻意維持人工判斷、不寫進自動化流程——跟 CLI_setup／CLI_zip 那輪「新流程先手動、驗證過沒有意外的坑再收進自動化」同樣的考量。

## 待辦事項

- **`PasswordVault.exe` 自己的 Native Messaging Host 註冊機制尚未實作**：目前只有 `PasswordVaultNativePipeServer`（Named Pipe Server 本體）遷移過來，對應的「寫入 `com.filelocker.passwordlocker.json` manifest、登記 `HKCU\Software\Google\Chrome\NativeMessagingHosts\...`」這一段（比照 `FileLocker.App` 那邊的 `PasswordLockerNativeHostRegistrar`）還沒有 `PasswordVault` 自己的版本——見 `PasswordVault.Extension/README.md` 已經記錄的同一個缺口。
  - **實際觀察到的症狀**（2026-08-26）：目前登記在 Chrome 底下的 manifest 路徑，是先前某次跑 `FileLocker.App` Debug 建置時寫下的舊路徑，跟使用者實際在跑的 `PasswordVault.exe`（或 Program Files 安裝版 `FileLocker.exe`，該台機器上這個安裝版根本沒裝密碼庫部件）路徑都對不上。兩邊 Pipe Server 各自的 `VerifyClientIsExpectedHost` 安全檢查會因為路徑不符直接切斷連線，瀏覽器擴充功能收到「Pipe is broken」；且因為兩邊搶同一條 Named Pipe（見第 8 節），這次連線被哪一邊接走帶有隨機性，導致「時好時壞」。
  - **How to apply**：補這塊時要一併考慮「兩邊都能各自正確註冊、但只有一份 manifest 位置」的情境——目前設計是後啟動的一方發現 Pipe 已被佔用就不再起自己的 Server（第 8 節），但**登錄機碼／manifest 路徑該由誰寫、寫誰的 exe 路徑**這件事目前完全沒定案，需要在動工前先想清楚，不然會重演這次「manifest 指向的路徑，兩邊實際在跑的程式都對不上」的狀況。

## 已完成之待辦

- 資料遷移失敗的處理細節：已定案並實作——複製不刪舊檔、新舊路徑都有資料時新路徑優先安靜略過，見 `PasswordVault` repo 的 `LegacyDataMigration`。
- `mac-style-windows-installer` 設定檔的實際欄位：已完成，見 `PasswordVault` repo 的 `installer/passwordvault_installer.json`，`no_admin_install` 模式、雙語 EULA、實測打包成功。
- CLI 指令集的實際語法：已定案並實作——`PasswordVault.Cli` 提供 `--list`／`--get` 兩個指令，只支援互動式輸入主密碼。
- 前端拆分的實際方式：已定案，見第 3 節與 ADR-0004。
- **`packages/password-locker-ui` 套件骨架與 `App.vue` 實際拆分**：已完成——密碼庫主畫面已從 `FileLocker.Web` 的 `App.vue` 搬進共用套件 `PasswordLockerPage.vue`，`PasswordVault.Web` 專案骨架已建立，`PasswordVault.exe` 已接上 WebView2 顯示真實密碼庫畫面。

## 18. 已知會延後或不做的事

- 沒有討論要不要幫 PasswordVault 做手機/其他平台版本——這輪範圍限定在 Windows 桌面。
- 「類別」目前接受純字串比對可能因為拼字誤差產生重複類別的限制，不做模糊比對或自動合併建議。
- CLI 不支援無人值守的自動化查詢（例如排程工作半夜自動跑）——見第 12 節，這是刻意的安全性取捨，不是遺漏。
