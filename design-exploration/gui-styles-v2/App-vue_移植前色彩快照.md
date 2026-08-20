# App.vue 移植前（Phase 1 側欄殼子＋票根清單之前）的色彩方案快照

這份文件只做一件事：把 `src/FileLocker.Web/src/App.vue` 在套用 `13-sidebar-ticket-shell.html`
定案配色之前，`:root` 跟 `.app--dark` 裡實際在用的中性色 token 記下來，留一份歷史對照——之後
如果發現新配色在某個既有畫面（加密精靈、設定頁…）視覺上有退步，可以回頭比對原本的值，不用
去 git log 裡翻。

跟《[design.md](design.md)》的差異：那份記的是 mockup（`13-sidebar-ticket-shell.html`）自己的
色票，這份記的是移植之前 `App.vue` 原本的色票——兩者是「新／舊」的對照關係，不是同一份文件的
不同版本。

## 中性色 token（這輪要被取代的部分）

| Token | 淺色（舊） | 深色（舊） | 用途 |
|---|---|---|---|
| `--color-bg` | `#EDEEF2` | `#1C1D21` | 殼子整體背景 |
| `--color-surface` | `#FFFFFF` | `#232428` | 卡片/表面底色 |
| `--color-border` | `#E1E4EA` | `#34363C` | 一般分隔線 |
| `--color-border-strong` | `#C9CDD6` | `#454850` | 較強邊框 |
| `--color-text` | `#1B1E24` | `#ECEDEF` | 主要文字色 |
| `--color-text-secondary` | `#454A54` | `#B0B4BC` | 次要文字 |
| `--color-text-tertiary` | `#6B707A` | `#82868F` | 最淡文字 |

舊配色偏「冷灰藍」（`--color-bg` 帶一點藍灰調），跟 `13-sidebar-ticket-shell.html` 的
`--vault-steel`／`--paper` 那種「暖米白／牛皮紙」調性不是同一個色相方向——這是使用者實際跑
起來的 App 視窗看起來跟 mockup「不像同一份設計」的主因，不是強調色（`--color-accent`）的
問題（強調色數值本來就跟 mockup 的 `--brass` 完全相同，見〈GUI造型探索_技術規格〉§2.14）。

## 不受影響、沒有被取代的部分

- `--color-accent*`、`--color-success*`、`--color-danger*`：跟 mockup 的 `--brass`／
  （尚未有對應語意色）數值已經一致或夠接近，這輪沒有動。
- `--tint-decrypt*`／`--tint-list*`／`--tint-guard*`／`--tint-vault*`／`--tint-settings*`：
  各分頁的主題強調色，跟中性色是分開的兩組 token，這輪也沒有動。
- `--radius-sm/md/lg`、`--shadow-*`、`--ease-out`、`--duration-*`：維持原樣。

## 後續

新配色（把上面這組中性色換成 mockup 的 `--vault-steel`／`--vault-steel-dim`／`--vault-line`／
`--vault-line-strong`／`--ink`／`--ink-soft`／`--ink-faint`／`--paper`／`--line` 對應值）套用
狀態記在〈GUI造型探索_技術規格〉§2.14 的更新裡，不重複寫在這份快照文件裡。
