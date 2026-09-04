---
name: run-test-vm
description: 操作本機的驗證用虛擬機——Windows 11 25H2（繁體中文）與 Windows 10 1809（Enterprise LTSC 2019）。還原快照、開機、把安裝檔送進去、在裡面安裝並實際操作、截圖、取回結果。用於本機開發環境驗證不了的項目：正式安裝程式裝完後的檔案總管雙擊 `.flocked`／`.lockfolder`、右鍵選單、瀏覽器擴充功能連線、乾淨環境下的第一次啟動行為。使用者要求「開虛擬機」「在虛擬機上測」「用正式安裝檔測一次」或輸入 /run-test-vm 時觸發。
---

# run-test-vm — 本機驗證環境

`dotnet test` 與 `/run` 這兩條路徑驗證的都是「開發機上、從 `bin/` 直接跑」的行為。
這兩台虛擬機補的是它們涵蓋不到的部分：**正式安裝程式真的裝進乾淨系統之後**的樣子
——副檔名關聯、右鍵選單、登錄檔寫入、系統匣、開機自動啟動、擴充功能連線，以及
「這台機器上從來沒裝過 FileLocker」這個開發機永遠回不去的起始狀態。

## 這套東西的原始出處

虛擬機的驅動模組是 `tools/vms.py`，**實體放在 mac-style-windows-installer 那個 repo**：

```
D:\Github\mac-style-windows-installer_專案\mac-style-windows-installer\tools\vms.py
```

不複製一份到 FileLocker repo，因為裡面記的是「這台實體機器上有哪些虛擬機、快照叫什麼名字、
密碼放在哪個環境變數」——那是機器層級的事實，不是專案層級的事實。每個 repo 各留一份的話，
之後新增快照或換路徑會出現多份互相矛盾的版本。代價是那個 repo 被搬走或改掉 API 時，這個
skill 會跟著失效；發生時去該 repo 的 `.claude/skills/run-test-vm/` 對照現況即可。

更詳細的手寫 vmrun 指令、九項實測出來的陷阱、兩台機器各自的環境事實，記在那邊的
`REFERENCE.md`，平常不用讀。

## 機器清單

| 代號 | 版本 | 對 FileLocker 的意義 |
|---|---|---|
| `win11` | Win11 25H2 · 26200.8037 · **zh-TW** · 已裝 WebView2 | **主力**。中文介面、有 WebView2、有網路 |
| `win1809` | Win10 Ent LTSC 2019 · 17763.316 · en-US · 無 WebView2、無網路 | 英文介面驗證；跑 GUI 前要先想清楚 WebView2 怎麼進去 |

`win1809` **不連網路**（為了讓組建號停在 17763.316），所以安裝程式那套「缺 WebView2 就
上網抓」的流程在那台上一定失敗——要在那台跑 GUI，得自己把 WebView2 Runtime 的離線安裝檔
一起送進去。純 CLI 驗證不受影響。這點沒有在 FileLocker 的情境下實測過，先當成待確認。

### 起始情境（`profile`）

一張快照代表一種起始情境，**快照與登入帳號是成對的**——配錯時 vmrun 回報的是認證失敗，
不會告訴你情境選錯了。

| 代號 | 機器 | 帳號 | 給什麼 |
|---|---|---|---|
| `default` | 兩台 | `Tester` | 單一 C 槽，管理員帳號 |
| `two_disks` | `win11` | `Tester` | 多一顆 E:（10 GB），可用來測 Vault 放在別顆磁碟 |
| `standard_user` | `win11` | `User` | **真正的標準使用者**（不在 Administrators 群組） |
| `standard_user_two_disks` | `win11` | `User` | 前兩者兼具 |

`standard_user` 對這個專案特別有價值：資料夾防護整套機制的前提就是「不需要提權」，
那個宣稱只有在真的沒有管理員身分的帳號上才驗證得到。

密碼由環境變數提供，不寫進任何檔案。

## 用法

```python
import sys; sys.path.insert(0, r"D:\Github\mac-style-windows-installer_專案\mac-style-windows-installer")
from tools import vms

vm = vms.connect("win11")                    # 密碼自環境變數讀，不落地
with vms.preserved_tab(vm.machine.vmx):      # 用完把 VMware 分頁補回去
    vms.fresh_boot(vm)                       # 還原 → 開機 → 等到真的可用
    vm.copy_in(local_installer, r"C:\Windows\Temp\FileLocker_Setup.exe")
    vms.write_guest_script(local_ps1, script_text)   # 寫成客體讀得懂的編碼
    vm.copy_in(local_ps1, r"C:\Windows\Temp\job.ps1")
    vm.run_program(POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass",
                   "-File", r"C:\Windows\Temp\job.ps1")
    vm.copy_out(r"C:\Windows\Temp\out.txt", back)
    vm.capture_screen(shot)
    vm.stop()
```

`run_program(..., interactive=True)` 讓程式跑在使用者看得到的桌面上；`check=False` 讓客體的
非零結束碼不算錯誤（驗證「本來就該失敗」的情況時用）。

## 先佔住再動手（多個 session 同時在跑）

這批虛擬機不只 FileLocker 在用——mac-style-windows-installer 那個 repo 走的是同一批
機器，而使用者有時會同時開著兩個 agent session。`revertToSnapshot` 是破壞性的：
另一邊裝到一半的安裝程式、正在等的畫面，會在毫無徵兆的情況下被還原掉，事後從症狀
也看不出成因（看起來只像「剛才那步沒生效」）。

`connect()` 預設就會取得占用租約，不需要另外做什麼，但**要先讓自己有名字**：

```bash
$env:VM_LOCK_OWNER = "filelocker-ca"      # 用你的 session 代號
```

沒設會直接失敗並告訴你要設什麼——不接受匿名持有，否則誰都能續租別人的租約。

```python
vm = vms.connect("win11", purpose="驗證雙擊 .flocked 解密")   # 佔住，並說明為什麼
...
vms.release("win11")                                          # 用完交回去
```

機器被別人佔著時 `connect()` 會拋 `VmBusy`，訊息裡有持有者、用途、租約到幾點——
**直接把那句話轉述給使用者，不要自己去搶**。要不要搶是使用者的決定。

租約預設 30 分鐘，同一個持有者重複 `connect()` 是續租。時間到自動失效，所以卡住
不會變成永久鎖死。租約檔在 `%LocalAppData%\vm-locks\<機器代號>.lock`，是人看得懂的
JSON，使用者想強制放掉直接刪檔即可。

協調機制本身（`tools/vm_lock.py`）跟 `vms.py` 放在一起，同樣在 mswi 那個 repo。

## 對 FileLocker 特別要留意的四件事

### 一、要驗互動行為的話，不能用「掛 ISO 冷開機」那條快路

模組支援把檔案做成 ISO 掛給客體（傳 GB 級檔案快非常多），但代價是冷開機之後客體停在鎖定
畫面、**沒有互動工作階段**，`interactive=True` 會被拒絕。

而 FileLocker 目前欠的驗證項目**全部都是互動的**——檔案總管裡雙擊 `.flocked`、雙擊
`.lockfolder`、右鍵選單、系統匣選單彈出位置。這些一定要走「一般 `fresh_boot`（不掛 ISO）
＋ `copy_in`」這條，然後用 `interactive=True` 執行。

FileLocker 的安裝檔目前約 82 MB，走 VMware Tools 通道（實測約 1.8 MB/s）大概 45 秒，
可以接受，不需要為了它去做 ISO。

### 二、雙擊這種操作沒辦法用 vmrun 直接下指令

vmrun 只能「執行某個程式」，沒有「在檔案總管裡雙擊某個圖示」這種動作。要驗證雙擊流程有兩種走法：

- **驗到八成**：用 `interactive=True` 直接執行那個檔案本身（`Start-Process` 那個 `.flocked`），
  這會走跟雙擊同一條副檔名關聯路徑，可以驗證「關聯有沒有註冊對、有沒有把 App 叫起來、
  App 有沒有正確認出這個檔案」
- **驗到十成**：`gui=True` 開有畫面的模式，讓使用者自己伸手點

預設用第一種，除非使用者主動說想自己看。

### 三、驗證「誰連進來」這類機制時，權限要跟真實情況一致

密碼庫的瀏覽器整合有一層保護是「宿主程式反查連進來的是哪一支程式，路徑對不上就切斷」。
用預設的背景路徑（`interactive` 不傳）啟動轉接程式時，它會帶著**已提升的權限**跑，而宿主
程式是桌面上的一般權限——一般權限的行程讀不到高權限行程的模組路徑，那個反查直接被系統
拒絕，於是宿主判定「對不上」把線切掉。

**症狀跟真正的「路徑對不上」完全一樣**（轉接程式兩種情況都只會回 `Pipe is broken`），
2026-09-04 這輪實測踩到，一度把測試環境造成的假象當成產品的缺陷。要驗這類東西，probe 一律
走 `interactive=True`（那條路徑拿到的是桌面使用者的一般權杖，跟 Chrome 啟動轉接程式時一致）。

同一類的判斷也適用其他「反查對方身分」的機制：**先問自己「真實情況下這支程式是用什麼身分
跑的」，再挑對應的執行方式**，不要因為背景路徑比較好用就一律用它。

### 四、結果要靠檔案帶回來

vmrun 不轉達客體的輸出，也不轉達客體的結束碼——只知道成敗。要判斷原因，就讓客體把結果寫進
檔案再 `copy_out()` 回來。客體端一律用 `powershell.exe`，不要用 `cmd.exe`。

顯示模式預設無畫面，不必每次問。截圖兩種模式都能用，所以「想留下畫面證據」不構成用有畫面
模式的理由。

## 適合丟上來跑的項目

技術規格文件第 24.1 節「進行中」那幾條，除了雲端同步跨裝置（需要兩台真的不同裝置）之外，
其他都落在這套環境涵蓋得到的範圍：

- 單檔案分散式加密：雙擊 `.flocked` 解密、`.flocked` 副檔名圖示有沒有正確顯示
- 雙擊已上鎖資料夾（`.lockfolder`）直接解鎖
- 右鍵選單（Shell Extension）在乾淨系統上有沒有正確註冊、選單項目長什麼樣
- 背景模式主視窗／系統匣選單彈出位置——這條在開發機上排查兩輪沒抓到根因，換一台乾淨的
  機器看看是不是開發機自己的狀態造成的，是還沒試過的方向
- 瀏覽器擴充功能連線（`win11` 有網路，可以裝 Chrome 側載擴充功能，驗證「Pipe is broken」
  那條修好了沒）
- 資料夾防護在**真正的標準使用者**帳號下的行為（`profile="standard_user"`）

## 已知限制

- 兩台系統皆未啟用，畫面上有浮水印、個人化設定被鎖。不影響功能驗證，但截圖給使用者看時
  會出現那行浮水印。
- `win1809` 無網路、無 WebView2，GUI 相關驗證在那台上的可行性尚未確認。
- 上面這些「適合丟上來跑的項目」目前都還沒有寫成腳本，每次要現寫。

## 待辦事項

- 把常跑的驗證情境寫成可重複執行的腳本（至少「裝正式安裝檔 → 造一個 `.flocked` → 執行它 →
  截圖」這條），不要每次重寫。
