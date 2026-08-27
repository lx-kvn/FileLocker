# CLAUDE.md
完整技術規格在 `docs/specs/FileLocker_技術規格文件.md`


## 語言
用使用者寫訊息的語言回覆。這個專案的主要開發者用中文——除非對方特別要求，否則一律用中文回覆。所有使用者看得到的文字都要同時準備中文與英文兩個版本。

## 專案是什麼
FileLocker：Windows 檔案/資料夾加密工具。使用者在檔案總管選取檔案或資料夾，右鍵加密，內容移到集中管理區（Vault），原位置留一個 `.locked` 指標檔；雙擊指標檔或在 App 裡操作可以解回原狀。支援密碼、Passkey（Windows Hello）、恢復金鑰三種獨立的解鎖方式。

另設有不使用加密功能，僅透過 Windows 存取權限限制資料夾的 `資料夾保護功能` ，詳情請見技術規格文件。

GitHub: `https://github.com/lx-kvn/FileLocker`（公開的Repo）

## 技術棧
- **後端**：C#/.NET 10，獨立的 Class Library（`FileLocker.Core`）+ WPF 宿主（`FileLocker.App`）
- **前端**：Vue 3（Composition API）+ Vite，透過 WebView2 呈現，跟後端用 `postMessage`／`postMessageWithAdditionalObjects` 溝通
- **Shell Extension**：C++ COM `IContextMenu`，獨立元件，只負責右鍵選單跟把選取路徑轉交給主程式
- **加密演算法**：Argon2id 金鑰衍生 + AES-256-GCM

## 專案結構
```
FileLocker/
├── FileLocker.slnx
├── src/
│   ├── FileLocker.Core/                     # 核心邏輯（加解密、Vault、Metadata、安全機制）
│   ├── FileLocker.App/                      # WPF 宿主（視窗、WebView2、單一執行個體、拖放、系統匣）
│   ├── FileLocker.Cli/                      # CLI（隨安裝程式一起發布，加入系統 PATH，見規格文件第 19 節）
│   ├── FileLocker.ShellExtension/           # C++ COM Shell Extension（右鍵選單）
│   ├── FileLocker.UpdateRelauncher/         # 軟體更新下載完成、主程式關閉後負責重啟的小工具
│   ├── FileLocker.PluginContracts/          # 可選配部件共用的介面契約（例如 IPasswordLockerPlugin）
│   └── FileLocker.Web/                      # Vue 3 + Vite 前端
│       └── src/
│           ├── App.vue                      # 主要畫面邏輯所在，較獨立的視覺已陸續拆到 components/
│           ├── components/                  # 拆出的元件（信封加密/解密、側欄、票根列、金庫轉輪…）
│           ├── composables/                 # 共用邏輯（IPC、側欄收合狀態、動畫產生器）
│           ├── locales/                     # zh-TW.json、en.json
│           └── assets/                      # 使用者自製的 SVG 圖示
├── tests/
│   ├── FileLocker.Core.Tests/
│   ├── FileLocker.App.Tests/
│   └── FileLocker.Cli.Tests/
├── docs/
│   ├── adr/                                 # 架構決策紀錄（ADR），例如密碼庫獨立化、原生訊息橋接的決策
│   ├── specs/                               # 專案級技術規格文件（FileLocker_技術規格文件.md）
│   │   └── features/                        # 各功能各自的規劃訪談紀錄（密碼庫／資料夾防護等）
│   └── releases/                            # 各版本 release notes
├── design-exploration/                      # GUI 造型探索的 mockup 與定案文件
├── assets/                                  # 美術素材原始檔（Affinity Designer .af + 匯出的 .svg／.png／.ico），
│                                             # 使用者用設計工具產出/修改的地方；前端實際會用到的那個子集另外
│                                             # 手動複製一份進 src/FileLocker.Web/src/assets/（Vite 建置只認得
│                                             # 那邊，不會去讀根目錄這份）——新增/修改素材時兩邊都要動，只改
│                                             # 一邊的症狀是「明明檔案已經換了，畫面還是舊的」。這裡的檔案數量
│                                             # 通常比前端那份多，因為包含已經沒在用的舊版素材（例如被 revert
│                                             # 掉的方向）跟 .af 原始檔本身，不代表前端沒複製到是漏做。
└── installer/                               # 安裝程式打包設定
```

密碼庫（Password Locker）可選配部件的原始碼已經遷出獨立成 [PasswordVault](https://github.com/lx-kvn/PasswordVault) repo（見
`docs/specs/features/PasswordVault_獨立化_規劃.md`、ADR-0003），這個 repo 只透過既有的「下載外掛部件」機制取得
PasswordVault repo 编譯產出的 `PasswordVault.Core.dll`，不再包含它的原始碼——`FileLocker.PluginContracts` 是
FileLocker 這邊仍然維護的介面契約，兩邊都要靠它溝通。

## 建置與測試指令
```bash
# 後端測試
dotnet test

# 前端開發伺服器（App.xaml.cs 的 Debug 建置會連到 http://localhost:5173）
cd src/FileLocker.Web
npm run dev

# 跑整個 App（另開一個終端機，跟 npm run dev 同時跑）
dotnet run --project src/FileLocker.App

# Shell Extension 編譯（VS Developer Command Prompt）
cl /LD /EHsc /utf-8 dllmain.cpp /Fe:FileLockerShellExtension.dll /link /DEF:FileLockerShellExtension.def
```

## 程式碼慣例
- **註解使用用繁體中文，說明「為什麼」不是「做了什麼」**——尤其是不直覺的決定（例如「不使用 X，因為 Y」），保持這種寫法。
- **每個修正/決策留下理由**，不要只留下程式碼本身——之後回頭看才知道當初為什麼這樣做，避免被後面的人誤改回錯的版本。
- C# 端：`private static` 輔助方法搭配 XML doc 註解；例外處理優先接基底類別（例如 `CryptographicException` 而不是特定子類別），避免未來 .NET 版本更新後子類別改變導致漏接。
- Vue 端：`<script setup>` Composition API，所有文字一律走 `t('key')` 翻譯函式，不寫死中文或英文字串；新增文字要同時補 `zh-TW.json` 和 `en.json` 兩份。
- 兩邊都要維持前後端分離——UI 邏輯留在 Vue，商業邏輯/加密/檔案系統操作留在 C#。

## 開發流程：新功能／改架構先寫測試
依規劃寫測試 → 測試通過規劃的行為定義 → 才動手寫實作，不要先寫實作、事後補測試。

測試的作用是把規劃時的定義「固定住」。如果實作過程中為了讓測試通過而發現規劃本身有問題，回頭改規劃／測試，不要悄悄放寬測試遷就當下的實作——否則代碼會跟原始設計定義自己漂移掉，之後也看不出來當初規劃到底是什麼。

## 開發時注意：新增或修改功能時，`GUI` 與 `CLI` 必須同步更新
工具同時提供 `GUI` 與 `CLI` 介面，功能異動時兩邊需同步覆蓋：

1. 確認 `GUI` 與 `CLI` 是否都需要對應調整；只實作一方時，需在回報中說明原因（例如該功能本質上只適用其中一種介面）
2. 兩邊對同一概念、參數、選項使用相同名詞，避免各自採用不同用語

## 開發重點：對未來程式發展保留擴充空間
修改或新增功能前，先評估此次變更是否會限制未來擴充性，優先選擇保留冗餘空間的設計。

> **反面案例**：單檔案分散式加密的 `.flocked` 檔頭 Header 長度被寫死為固定長度，導致未來要新增欄位時會與舊版本檔案不相容，或需額外寫版本判斷邏輯。

設計原則：
1. 涉及檔案格式、資料結構、API 介面等會被序列化或跨版本使用的設計，優先採用可擴充結構（長度可變欄位、版本號欄位、保留位元/位元組）
2. 動手前先自問：「如果未來要在這裡加東西，現在的設計會不會擋路？」
3. 若某個決策明顯犧牲未來擴充性以換取當下方便，需明確告知使用者這個取捨

## 撰寫技術規格文件之規範
- 語言使用繁體中文，已知限制與待辦事項分為兩個項目，放在文件的最末尾。
- 詳盡說明使用到的規格，以及為何，若是有反直覺的決議請註記。
- 語氣使用專業、非日常口語化用法之「紀錄式」敘述法。
- 絕對不使用「刻意不...」此類語法，因為 「刻意」 這個詞在我看來是貶義的詞，改採用「不使用 X ，因為 Y 」此類較為單純敘述的語法。
- 如果待辦清單內的事項完成了則改列至「已完成之待辦」項目，而不是將待辦清單內的文字劃線表示已解決。

## UI 變更注意事項
標題列、彈窗、對話框的排版每次改動後：(1) 檢查新增的控制項有沒有意外繼承到全域規則（例如 `width: 100%`）、(2) 互動控制項（下拉選單、按鈕）不能放進視窗拖曳區內、(3) 確認在支援的最窄視窗寬度下文字不會被截斷、(4) 截圖或描述排版結果後再回報完成，不要沒看過畫面就宣告做完。

## 建置與驗證
Commit 前一律先跑過完整測試套件（`dotnet test`，目前三個測試專案共約 349 個測試都要過；這個數字會隨開發持續增加，抓「跑完顯示失敗:0」為準，不要死記這個數字）。部署到 `Program Files` 需要提權的 shell——如果拿不到提權權限，就先建置到本機暫存資料夾，把確切的手動提權指令交給使用者執行，不要悄悄跳過驗證步驟。

**不要自己判斷「差不多了」就直接下 `git commit`。** 當你認為工作已經到一個可以或應該 commit 的段落時，先跟使用者說一聲（例如「這輪改動看起來完整了，要 commit 嗎？」），等對方明確要求（像是直接說「commit」）才執行。使用者曾經明確要求要保留這個確認步驟。

**`git push` 是獨立於 `git commit` 的另一個確認步驟，commit 的同意不等於 push 的同意。** 每一次要 push（不管是第一次還是後續），都要另外明確問過、等使用者同意才執行，不要因為前一次 push 被同意過就假設這次也自動有同意。這條規則不限這個 repo，任何工作目錄（包含這台機器上其他的 git repo，例如 PasswordVault）都適用。

## 已知的坑
單一執行個體的 Mutex 處理路徑是右鍵/上鎖這類進入點過去真的出過當機事故的地方——新增任何啟動路徑時，要處理「Mutex 已經被別的執行個體持有」的情況，並呼叫 `SetForegroundWindow`（或等效的前景焦點搶奪機制）把既有視窗搶到最前面，而不是直接結束或讓例外把行程弄崩潰。

**密碼庫部件的原始碼已經遷出到 [PasswordVault](https://github.com/lx-kvn/PasswordVault) repo，這個 repo 裡不再有它的原始碼可以改。** 要動密碼庫的邏輯，去那個 repo 改、build 出 `PasswordVault.Core.dll`，本機開發測試時手動複製到 `src/FileLocker.App/bin/<組態>/<TFM>/plugins/PasswordLocker/`——`FileLocker.App` 對它沒有編譯期參考（這是刻意的架構決定，密碼庫是可選配部件），所以 `dotnet build` 不會自動更新那個資料夾裡的副本，App 載入的永遠是那份手動放進去的舊 DLL。忘記複製的症狀是：新加的 IPC 訊息在舊部件的 switch 撞到 `_ => null`，前端 `requestMessage()` 等不到回應，畫面完全沒反應、DevTools 也沒有任何錯誤（這個坑實際發生過，CSV 匯出功能耗掉很久才定位到；`HandlePasswordLockerModuleRequestAsync` 已經會在部件回傳 null 時送出明確的錯誤訊息，但根本原因還是要靠記得複製 DLL）。正式使用者不受影響——`PasswordLockerModuleInstaller` 會自動從 PasswordVault repo 的 GitHub Release 下載對應版本。刻意不加自動複製的 MSBuild 步驟，因為那會讓「部件未安裝」這個狀態沒辦法用刪除資料夾的方式測試。

**只複製 `PasswordVault.Core.dll` 本體是不夠的**——它依賴 `Konscious.Security.Cryptography.Argon2`／`Konscious.Security.Cryptography.Blake2` 這兩個 NuGet 套件做密碼雜湊，但它自己是 Class Library 專案，`dotnet build` 不會把套件依賴的 dll 複製到它自己的 `bin/` 資料夾（.NET SDK 只有「可執行專案」的輸出才會有完整攤平的依賴集合，Library 專案的 `bin/` 只有自己的 dll）。要拿這兩個檔案，去 `PasswordVault.App`（或 `PasswordVault.Cli`）自己的 `bin/` 資料夾複製，不是 `PasswordVault.Core` 自己的。同一批還要複製 `PasswordVault.NativeHost.exe`（連同它自己的 `.dll`／`.deps.json`／`.runtimeconfig.json`／`extension-id.txt`）取代舊的 `FileLocker.PasswordLockerNativeHost.*`。

**前端 `requestMessage()` 的每一種回應類型，都必須在 `App.vue` 的 `messageHandlers` 裡有一個對應項目呼叫 `resolvePending()`。** 漏掉的話那個 Promise 永遠不會被解開，一樣是「按了完全沒反應、沒有任何錯誤訊息」。新增 IPC 往返時，後端送回應、前端註冊處理常式這兩件事要一起做完。