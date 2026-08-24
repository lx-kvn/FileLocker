# 密碼庫獨立化為 PasswordVault，原始碼遷出獨立 Repo

**狀態**：accepted（規劃階段，尚未動工遷移）

## 背景

密碼庫（Password Locker）原本是 FileLocker 底下一個編譯期不被 `FileLocker.App` 依賴的可選配部件（`FileLocker.PasswordLocker`），使用者透過 App 內建的下載流程另外安裝。這輪規劃要新增一個能單獨運作、不需要先裝 FileLocker 本體的獨立桌面應用程式（品牌名 PasswordVault），核心邏輯（`FileLocker.PasswordLocker` → 更名 `PasswordVault.Core`）跟這個新的獨立應用程式（`PasswordVault.exe`）都要遷出，改放進一個新的獨立 GitHub repo。

## 考慮過的方案

1. **原始碼留在 FileLocker repo，只有安裝檔發到新 repo**：不需要拆目錄，但一個 repo 同時管兩個各自獨立品牌、各自獨立版本號、各自獨立發布節奏的產品，長期會讓 commit 歷史、issue、CI 設定互相干擾，且跟「PasswordVault 是獨立產品」這個定位本身矛盾。
2. **`PasswordVault.Core`／`PasswordVault.exe` 原始碼都遷出，新 repo 是唯一真相來源**（採用）：`src/FileLocker.PasswordLocker/`、`src/FileLocker.Extension/`（瀏覽器擴充功能，因為它是密碼庫功能的一部分，不是 FileLocker 加密/資料夾防護功能）、對應的測試專案整批搬到新 repo。FileLocker repo 之後只透過既有的「下載外掛部件」機制取得編譯好的 `PasswordVault.Core` 二進位檔，不再包含它的原始碼。

## 決策理由

選擇方案 2，因為 PasswordVault 這次明確定位成獨立品牌、獨立版本號、獨立發布節奏的產品（不是 FileLocker 的附屬功能），原始碼留在 FileLocker repo 裡會讓這個定位變得名不符實——貢獻者想改 PasswordVault 的程式碼，還是得先 clone 一個叫 FileLocker 的 repo；FileLocker 這邊的 CI／測試套件也會被迫等待一個邏輯上完全獨立產品的建置結果。

FileLocker.App 消費 `PasswordVault.Core` 的方式維持既有的「執行期偵測外部 dll、動態載入」可選配部件架構（見 `docs/specs/features/密碼庫_功能規劃.md` 第 2 節）不變，只是這個 dll 的原始碼來源換成新 repo 編譯產出，FileLocker repo 這邊看到的仍然只是一份二進位相依。

## 代價與風險

- **一次性的歷史/沿革斷裂**：`src/FileLocker.PasswordLocker/`、`src/FileLocker.Extension/` 的 git 歷史如果沒有用 `git filter-repo` 之類的工具帶過去，新 repo 就是從零開始的歷史，之前的 commit 脈絡查詢會變得不方便。遷移當下需要決定要不要保留歷史。
- **既有使用者的資料遷移**：真正的加密資料存放路徑（`%AppData%\FileLocker\PasswordLocker\credentials.json`）跟改名後的路徑不同，需要新版程式在啟動時自動偵測、搬移（見 `docs/specs/features/PasswordVault_獨立化_規劃.md` 的資料遷移小節），這步驟做錯會讓既有使用者誤以為密碼資料遺失。
- **兩邊 CLAUDE.md／開發慣例需要分別維護**：新 repo 需要一份自己的 CLAUDE.md，不能只是複製貼上 FileLocker 這份（技術棧、目錄結構都不同）。

## 已知限制

- 這份 ADR 只記錄「要拆」這個決策本身跟理由，實際遷移步驟（要不要保留 git 歷史、新 repo 的目錄結構、CI 設定）留到動工前另外規劃。
