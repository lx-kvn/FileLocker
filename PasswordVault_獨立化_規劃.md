# PasswordVault：密碼庫獨立化規劃

**狀態：規劃階段第二輪已完成（架構骨幹＋主要實作細節皆已定案）**——這份文件記錄的是決策與理由，不是實作步驟；動工前如果規劃有調整，先改這份文件，不要邊做邊讓實作偷偷偏離這裡的定義。

## 1. 背景

`FileLocker_密碼庫_功能規劃.md` 第 12 節原本把「獨立單機介面版本」列為明確擱置的構想。可選配部件架構（含第二階段自動安裝）落地之後，重新排入規劃，並在這輪 grilling 中決定：不是單純幫既有的 `FileLocker.PasswordLocker` 部件多做一份 UI 外殼，而是把整個密碼庫功能獨立成一個品牌、一個產品——**PasswordVault**，原始碼遷出到獨立 repo（見 [ADR-0003](docs/adr/0003-passwordvault-separate-repo.md)）。

FileLocker 本體跟 PasswordVault 的關係，从「FileLocker 的一個可選配部件」重新定位成「FileLocker 是 PasswordVault 的其中一個消費端」——FileLocker.App 要用密碼庫功能，一樣是下載 PasswordVault 的核心元件（見第 3 節），這件事本來就已經是既有架構，這輪不需要改動 FileLocker.App 這一側的下載/載入機制。

## 2. 命名與改名範圍

品牌名確定為 **PasswordVault**，套用範圍是整個專案徹底改名，不只是新獨立程式的對外顯示名稱：

| 現有名稱 | 新名稱 |
| --- | --- |
| `FileLocker.PasswordLocker`（C# 專案） | `PasswordVault.Core` |
| `plugins/PasswordLocker/`（FileLocker.App 外掛資料夾） | `plugins/PasswordVault/` |
| `src/FileLocker.Extension/`（瀏覽器擴充功能） | 隨 repo 遷移一併改名／改品牌文字 |
| （新）獨立桌面程式 | `PasswordVault.exe` |

FileLocker 本體 UI 上「密碼庫」分頁的中文名稱維持不變——這是使用者已經熟悉的既有語彙（見 `CONTEXT.md` 的「密碼庫（Password Locker）」定義），改名範圍是專案／程式碼／安裝路徑層級，不是要求使用者跟著改口。

## 3. 執行形態與元件關係

`PasswordVault.exe` 是一個全新的 WPF 宿主專案（不是 `FileLocker.App` 加命令列參數切換模式），有自己的視窗、單一執行個體 Mutex、系統匣圖示，架構上跟 `FileLocker.App` 完全平行、互不影響——理由跟既有的可選配部件決策一致：`FileLocker.App` 拿掉密碼庫功能要能繼續正常運作，兩個消費端各自的生命週期不應該互相牽制。

前端不整支複製，改採**實體分割**：把現有 `App.vue` 裡密碼庫分頁相關的 template/script 拆成獨立 Vue 元件，`PasswordVault.exe`（新的 Vite 專案）與 `FileLocker.App`（現有 `FileLocker.Web`）兩邊都 import 同一份元件——避免以後修一個 bug 要改兩份程式碼、兩邊行為逐漸漂移的既有風險（跟這個專案「不使用複製貼上式的雙份維護」的一貫原則一致）。這輪只確認方向，實際拆分方式（獨立套件、monorepo workspace、或其他形式）留待遷移動工前另外規劃。

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

## 9. 發布方式

`PasswordVault.exe` 的安裝程式發布在獨立的 GitHub repo／Release，不掛在 FileLocker 現有的 repo 底下。原始碼本身也遷過去（見 [ADR-0003](docs/adr/0003-passwordvault-separate-repo.md)），這份 repo 是新 repo 唯一真相來源，`FileLocker.PasswordLocker`／`FileLocker.Extension` 遷移後從這個 FileLocker repo 移除。

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

`FileLocker.App` 下載使用的部件版本，維持「版號各自獨立」——這是既有慣例（見 `FileLocker_密碼庫_功能規劃.md`），FileLocker 本體發新版不代表部件也要跟著出新版，PasswordVault 獨立化後這個既有規則不變。

## 14. 單一執行個體 Mutex／系統匣圖示

`PasswordVault.exe` 這一側完全比照 `FileLocker.App` 現有模式（`TrayIconManager.cs`／`WindowActivation.cs`）——專屬於 PasswordVault 的固定 Mutex 名稱、系統匣選單項目與行為（顯示主視窗／退出）直接複製既有實作邏輯，只換掉品牌相關的素材（圖示、選單文字）。單一執行個體處理仍然要遵守 `CLAUDE.md`「已知的坑」的既有原則：偵測到 Mutex 已被持有時呼叫 `SetForegroundWindow`（或等效前景焦點搶奪機制）把既有視窗拉到最前面，不能直接結束或讓例外把行程弄崩潰。

## 15. 設定不共享

`FileLocker.App` 與 `PasswordVault.exe` 的語言／主題設定各自獨立一份設定檔（`%AppData%\PasswordVault\settings.json` 與 FileLocker 現有的 `AppSettings`），不互通——跟第 7 節「資料遷移」同樣的理由：兩者現在是完全獨立的產品，共享設定檔需要額外設計跨程序讀寫的並發/鎖定機制，且版本升級進度不一致時舊版讀到新版寫入的欄位要怎麼處理也是一個問題，用一份共用設定檔換來的便利性不成比例。使用者在兩邊各自設一次語言/主題，改一邊不影響另一邊。

## 16. 瀏覽器擴充功能品牌文字

擴充功能 popup／content-script 目前顯示的「FileLocker 密碼庫」等字樣，遷移後直接改稱「PasswordVault」，不保留 FileLocker 名稱過渡——跟第 2 節命名策略一致（品牌層級徹底改名，不做雙名並存），逐字文案（各處確切字串）留待實作時對照既有的 `zh-TW`／`en` locale 檔案逐一替換，這份文件不重複列出每一個字串。

## 17. 尚待規劃的細節（下一輪）

- **資料遷移失敗的處理細節**：第 7 節「自動搬移舊路徑資料」的具體行為（搬移後是否刪除舊檔案、搬移失敗時怎麼提示使用者、新舊路徑都有資料時如何處理）留到動工前另外定案。
- **`mac-style-windows-installer` 設定檔的實際欄位**：PasswordVault 的 `installer.json`（EULA、圖示、`no_admin_install` 旗標等）要對照該專案當下的 `CLI_USAGE.md` 準備，這份文件不重複列出工具鏈的用法細節。
- **CLI 指令集的實際語法**：這輪只定案驗證方式（互動式主密碼），指令名稱、子命令、輸出格式（純文字／JSON）留到實作前設計。

## 18. 已知會延後或不做的事

- 沒有討論要不要幫 PasswordVault 做手機/其他平台版本——這輪範圍限定在 Windows 桌面。
- 「類別」目前接受純字串比對可能因為拼字誤差產生重複類別的限制，不做模糊比對或自動合併建議。
- CLI 不支援無人值守的自動化查詢（例如排程工作半夜自動跑）——見第 12 節，這是刻意的安全性取捨，不是遺漏。
