# `13-sidebar-ticket-shell.html` 介面元素與設計風格

這份文件記錄 `13-sidebar-ticket-shell.html`（側欄殼子＋票根清單＋信封加密流程＋獨立解密流程）
目前實際呈現出來的視覺語言與元件清單——是「這個殼子現在長什麼樣子」的設計語言快照，跟另外
兩份文件的分工不同：《[GUI造型探索_定案文件.md](GUI造型探索_定案文件.md)》記「為什麼決定
這樣做」，《[GUI造型探索_技術規格.md](GUI造型探索_技術規格.md)》記「怎麼刻出來的、踩過
哪些坑」；這份只做一件事——把目前檔案裡實際在用的色票、字體、間距、元件外觀整理成一份可以
快速查閱的清單，供之後移植進 `App.vue` 或延伸其他分頁時對照，不重複另外兩份文件已經寫過的
決策理由與實作細節。

## 色彩 Token

定義在 `:root`，深色模式透過 `html[data-theme="dark"]` 覆蓋（測試用機制，切 `data-theme`，
不跟系統 `prefers-color-scheme` 走）。

| Token | 淺色 | 深色 | 用途 |
|---|---|---|---|
| `--vault-steel` | `#F4F3F0` | `#1E1D1A` | 殼子整體背景（標題列、側欄、主內容區底色） |
| `--vault-steel-dim` | `#ECEAE5` | `#26241F` | 略深一階的中性底色（collapse 按鈕 hover、已選清單外框底） |
| `--vault-line` | `#E0DDD5` | `#38352D` | 一般分隔線（標題列下緣、側欄右緣） |
| `--vault-line-strong` | `#CFCAC0` | `#4A4638` | 較強的邊框（按鈕外框、卡片 hover 邊框） |
| `--ink` | `#22221E` | `#EDEAE0` | 主要文字色 |
| `--ink-soft` | `#63604F` | `#B7B09B` | 次要文字（中繼資料、側欄未選中項目） |
| `--ink-faint` | `#8B8776` | `#847D68` | 最淡文字（提示文字、撕線顏色、debug 標註） |
| `--brass` | `#A8770F` | 不變 | 主強調色（等同 `App.vue` 既有 `--color-accent`，選中狀態、主按鈕、信封蠟封呼應色） |
| `--brass-deep` | `#8C630C` | 不變 | 強調色的加深版（郵戳鎖定文字、連結文字） |
| `--brass-tint` | `#FBF1DF` | `#3A2F17` | 強調色的淺底（側欄選中項目背景、批次加密列底色） |
| `--brass-line` | `#EAD5A6` | `#5C4A22` | 強調色系邊框（批次加密列邊框） |
| `--void-red` | `#A23B2A` | 不變 | 警示色（撕開中的撕線、找不到指標檔文字、重置按鈕） |
| `--paper` | `#FFFDF8` | `#242119` | 卡片/表面底色（票根卡片、按鈕、Sheet、輸入框——所有「浮在殼子背景上的一層」都用這個，不是寫死 `#fff`） |
| `--line` | `#E7DCC0` | `#3A3327` | 卡片內部分隔線（票根卡片邊框、Sheet 操作列分隔線） |

深色模式額外設定 `color-scheme:dark`（淺色對應 `color-scheme:light`），讓瀏覽器原生控制項
（`<input type="checkbox">` 等）跟著換成對應的深/淺色外觀，不然打勾方塊這類原生元件只認
`prefers-color-scheme`、不會跟著這裡自訂的 `data-theme` 機制換色。核取方塊另外設
`accent-color:var(--brass)`，勾選色跟整體強調色一致。

### 一次性/局部色（未收進 token，直接寫死在對應元素上）

| 顏色 | 用途 |
|---|---|
| `#FF5F57` / `#FEBC2E` / `#28C840` | 紅黃綠三色 macOS 風格視窗控制鈕（`.traffic-light--close/minimize/maximize`），這輪固定 macOS 造型，Windows 風格切換是待辦（見〈技術規格〉§2.1.1 附近的 TODO 註解） |
| `#1F5C34` | 成功狀態（驗證成功打勾圖示 `.check-mark`、存檔完成文字） |
| `#DCC289` | 輸入框聚焦強調色，跟信封本體邊緣線同一色值，刻意跟瀏覽器預設藍色系聚焦色脫鉤 |
| `#fffaf0` | 信封上檔名標籤 `.mail-filename` 底色（暖白，跟信封牛皮紙材質呼應，不用 `--paper`） |
| `#6b5527` / `#8a6a1f` | 檔名標籤文字色／加密時間文字色（暖棕色系，同上理由） |

### 票根圖示識別色（依檔案類型，目前寫死在 mockup markup 裡，未來要接真實類型判斷邏輯）

| 顏色 | 檔案類型範例 |
|---|---|
| `#C1502F` | PDF（合約書） |
| `var(--brass)` | 資料夾 |
| `var(--brass)` on `var(--brass-tint)` 底 | 批次加密列 |
| `#4F7A52`（這輪從 `#7B4FE0` 紫色調整過來） | 壓縮檔（zip）——原本的飽和紫色跟整體暖色調（棕/金/紅）格格不入，改成同樣偏暗的墨綠，跟既有色票更協調 |
| `#2B6CB0` | 憑證檔（pfx） |
| `var(--ink-faint)` | 找不到指標檔的項目（用最淡的中性色，呼應「這筆已經有問題」的降階觀感） |

## 字體

| Token | 字體堆疊 | 用途 |
|---|---|---|
| `--font-body` | `'IBM Plex Sans','Noto Sans TC',-apple-system,sans-serif` | 介面主要文字 |
| `--font-stamp` | `'IBM Plex Mono','JetBrains Mono',ui-monospace,monospace` | 等寬數字/戳記風格文字（頁首統計數字 `.page-stats`、恢復金鑰字串） |

字級沒有抽成一組系統化的 scale token，目前是依元件各自寫死（例如頁面大標題 22px、票根檔名
14px、中繼資料 11.5px、動作按鈕文字 12px、郵戳文字 11px）——這是這輪 mockup 尚未做的
系統化，移植進 `App.vue` 時建議比照 `App.vue` 既有的文字階層（primary/secondary/tertiary/
muted 四級）重新對應，不要逐一複製这裡的裸數字。

## 圓角與間距基準

| Token | 值 | 用途 |
|---|---|---|
| `--radius` | 8px | （目前定義了但實際較少直接使用，卡片圓角多半各自寫 9-10px） |
| `--radius-sm` | 5px | 按鈕、側欄項目、collapse 按鈕的圓角 |

實際圓角散落在：票根卡片 10px、Sheet 卡片 9px、票根圖示/checkbox 圓形（50%）、輸入框 6px、
郵戳徽章圖示無圓角（跟隨素材本身）。跟字級一樣，這輪沒有做成嚴謹的圓角 scale，屬於之後系統化
時要收斂的部分。

陰影只用在需要「浮起來」的層次：票根圖示 `box-shadow:0 1px 2px rgba(34,34,30,0.08)`、
Sheet 卡片 `box-shadow:0 4px 10px rgba(34,34,30,.16)`——越浮在越上層的元件陰影越重，跟
`interface-design` skill〈Subtle Layering〉的既有原則一致。

## 版面結構

```
.shell（height:100vh，flex-column）
├─ .title-bar（38px，橫跨全寬：紅綠燈 + 品牌名 + 拖曳區 + 深色模式測試按鈕）
└─ .body（flex:1，flex-row）
   ├─ .sidebar（展開 200px／收合 60px，可收合側欄導覽）
   └─ .main（flex:1，可捲動主內容區，padding 28px 40px 48px）
      ├─ .page-head（頁面圖示 + 標題 + 右側統計文字）
      ├─ .toolbar-row（分頁籤 .tabs + 間隔 + 一排 .btn 操作按鈕）
      └─ .list（清單本體，由多個 .ticket-wrap 組成）
```

側欄收合時只留 60px 寬、只顯示圖示，文字改成 hover 才浮出的 tooltip（`::after` 偽元素，讀
`data-label` 屬性）；收合按鈕本身用一個會依收合狀態翻轉 180 度的箭頭 SVG，靠旋轉角度表達
「這個動作可逆」，不用另外文字說明。

## 元件清單

### 按鈕系統

- `.btn`：預設樣式（`var(--paper)` 底、`var(--vault-line-strong)` 邊框、`var(--ink)` 文字），
  `:active` 時 `scale(0.97)` 給即時按壓回饋。
- `.btn--primary`：實心 `var(--brass)` 底、白字，用在整個流程裡份量最重的單一動作（加密新
  檔案、下一步、確認加密、選擇檔案空狀態下的按鈕）。
- `.btn--reset`：外框變 `var(--void-red)`，只用在「重置清單」這顆測試用按鈕，不是正式功能。
- `.actions button`（票根列的動作按鈕，解密／Passkey／恢復金鑰／全部解鎖）：獨立一套跟
  `.btn` 外觀相同但不共用 class 的樣式，統一白底外框、不特別標示主要按鈕（理由見〈定案文件〉
  §3.4——每列按鈕數量不固定，金色實心只套一顆會讓顏色在畫面上亂跳）。

### 側欄導覽（`.sidebar` / `.nav-item`）

選中項目：`color:var(--brass)` + `background:var(--brass-tint)` + 字重加粗。圖示全部是
`stroke="currentColor"` 的線條 SVG（`stroke-width` 約 1.7-1.8），跟 `App.vue` 既有分頁圖示
共用同一套視覺語言（除了資料夾防護頁的盾牌圖示是這輪新畫的，其餘都直接複製 `App.vue` 既有
path）。

### 票根清單列（`.ticket-wrap` → `.ticket-stage` → `.ticket`）

三層圖層結構，各自職責：
- `.ticket-wrap`：這一列在清單裡占多少垂直空間（含收合動畫的高度起點）。
- `.ticket-stage`：整列要不要往旁邊飛走的位移/淡出。
- `.ticket`：實際看得到的白底卡片本體，內部由左而右：
  - `.ticket__seal`（撕線 + 圓形圖示，貫穿卡片全高，是唯一的撕開觸發熱區）
  - `.info`（檔名 + 中繼資料，`·` 分隔）
  - `.postmark-slot`（固定 90px 寬的郵戳徽章欄位，可以是空的）
  - `.actions`（固定 180px 寬、靠右對齊的動作按鈕欄位）

圖示是 32px 圓形、`1.6px solid currentColor` 邊線、`var(--paper)` 底（深色模式會跟著换
成深色底），邊線顏色 = icon 顏色本身，維持「貼紙/蠟封」的觀感而不是印刷色塊。

### 郵戳徽章（`.postmark`）

56px 大圖示 + 文字並排，兩種狀態：`.postmark.lock`（巢狀鎖定 ×N，文字用 `--brass-deep`）、
`.postmark.warn`（找不到指標檔，文字用 `--void-red`）。素材沿用 `Postmark_Nested_Lock.svg`／
`Postmark_Warning.svg`。

### 信封組件（加密流程 / 解密流程共用）

`.envelope-outer` → `.mailaway-rig` → `.envelope-canvas` → `.flap-group`（封口，可
`rotateX` 開合）+ `.wax-drip-back` / `.wax-seal`（蠟封正反面）。420px 正方形畫布，`open`／
`closed` 兩態靠 class 切換驅動整組 transform；`.dropzone p` 提示文字、`.mail-filename`／
`.mail-postmark`（檔名標籤 + 郵戳 + 加密時間）疊在信封本體上，依開合狀態淡入淡出。落下+回彈
開場動畫 `mail-drop-bounce`（820ms）、寄出飛走動畫（`.mailaway-rig.is-flying`）都是共用
`.mailaway-rig` 這一層驅動。

### Sheet 抽拉卡片（共用元件，加密流程步驟一/二、解密流程、恢復金鑰揭露都在用）

`position:absolute` 釘在信封本體上固定座標（`top:245px`），不參與 `.envelope-outer` 版面
排版。三種狀態 class：`.sheet--hidden`（不可見，被信封擋住）／`.sheet--reveal`（往下滑出，
完全清出信封輪廓）／`.sheet--settle`（收回、疊上信封範圍，最終定位）。另有 `.sheet--retreat`
（退場專用，有轉場動畫）、`.sheet--morph-start`／`.sheet--fade-out`／`.sheet--fade-in`（同一
張卡片內容切換用的縮放交叉淡化，非抽出/收回）。曲線統一用 `cubic-bezier(0.32,0.72,0,1)`
（iOS 風格抽屜曲線）。

### 表單元素

輸入框（`.step2-form input`／`.decrypt-sheet input`）：`var(--paper)` 底、`var(--line)`
邊框、6px 圓角，聚焦色 `#DCC289`（跟信封本體邊緣線同色，`box-shadow` 光暈搭配同色系半透明）。
核取方塊用瀏覽器原生 `<input type="checkbox">`，靠 `accent-color` + `color-scheme` 兩個
CSS 屬性讓外觀跟著深/淺色模式走（見上方色彩 token 一節）。

### 狀態指示

- `.spinner`：26px 圓形 `border-top-color` 轉動的載入動畫，800ms 線性無限迴圈，用在 Passkey
  自動驗證中。
- `.check-mark`：30px 圓形實心 `#1F5C34` 打勾徽章，驗證成功／存檔完成的統一視覺。

## 深色模式切換機制

標題列右側 `#themeToggle`（☀/☾ 圓形小按鈕），明確標註「測試用」——切換 `html` 的
`data-theme` 屬性，不是正式功能的一部分，是這一系列 mockup（`14-notebook-in-shell.html`
起）共用的既有驗證機制。信封素材本身刻意不做深色版（維持原色），密碼庫的分類標籤/懷錶素材
同理；其餘所有介面色彩（背景、文字、邊框、按鈕、圖示底色）都要能響應 `data-theme`——這輪
（見〈定案文件〉§6.3、§7.5 已知限制與待辦）修正了三個先前遺漏的地方：`.btn` 背景寫死
`#fff`、`.actions button` 背景寫死 `#fff`、`.ticket__icon` 背景寫死 `#fff`，這三處原本
深色模式下都會呈現「淺色文字疊在死白底色上」的低對比度問題，現已改用 `var(--paper)`。

## 維護提醒

這份文件是**快照**，不是自動同步的產物——`13-sidebar-ticket-shell.html` 之後如果再調整
色票/元件外觀，要記得回來更新對應段落，不要讓這份文件跟實際檔案內容漂移。決策層面的「為什麼
改」記在〈定案文件〉，實作技巧記在〈技術規格〉，這份只更新「現在長怎樣」。
