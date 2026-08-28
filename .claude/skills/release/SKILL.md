---
name: release
description: 依照這個專案實際的流程準備一次新版本發布——建置、測試、更新雙語 Release Notes、commit、打 tag，並提示需要在這個 repo 之外手動完成的安裝程式打包步驟。
---

# Release

這個 skill 是照 FileLocker 這個 repo 實際的發布慣例寫的，不是通用範本：

- Release Notes 是**單一檔案內雙語**（`docs/releases/vX.Y.Z.md`，先 `## 繁體中文` 後 `## English`），不是分成 `README.md`／`README.zh-CN.md` 兩個檔案——比照 [`docs/releases/v1.1.0.md`](../../../docs/releases/v1.1.0.md) 的段落結構（亮點 + 已知限制，各自中英文對應）。
- 這個 repo 確實有在打 git tag（`v1.0.0`、`v1.1.0`），commit 訊息慣例是 `feat:`／`fix:`／`docs:`／`refactor:`／`style:` 開頭，但不是嚴格的 Conventional Commits 格式（後面接的是完整中文句子說明「為什麼」，不是簡短英文摘要）。
- **正式安裝程式的打包不在這個 repo 裡完成，但可以用 `mswi-cli` 自動化**——技術規格文件第 19 節說明是對接另一個獨立專案 [mac-style-windows-installer](https://github.com/lx-kvn/mac-style-windows-installer)，這個 skill 負責 repo 內的部分（程式碼、測試、文件、tag）。打包本身現在透過該專案的 `mswi-cli pack --config installer/filelocker_installer.json` 完成，設定檔已經檢查進這個 repo（見步驟 8），不用再進另一個專案手動跑 GUI。上傳 GitHub Release 附件仍然是手動步驟。
- `gh` CLI 已安裝並登入（`lx-kvn` 帳號，裝在 `C:\Program Files\GitHub CLI\gh.exe`，PATH 找不到就用完整路徑），可以用 `gh release create` 自動建立 GitHub Release、上傳附件。**但發布前一定要先把要執行的指令列給使用者確認過，取得明確同意才能真的執行**——這是使用者明確要求的規則，不是預設的謹慎行事而已，不能省略這個確認步驟。執行前也要先用 `gh release list --repo lx-kvn/FileLocker` 確認這個版本還沒被使用者手動在網頁上發布過，避免重複。

## 步驟

1. **確認工作目錄乾淨**：`git status --short`，如果有非預期的未追蹤/未提交檔案，先跟使用者確認要不要一併處理，不要悄悄略過。
2. **跑完整測試套件**：`dotnet test`。任何一個測試沒過就停下來，不要continue——回報給使用者，不要自己決定要不要跳過。
3. **Release 組態建置**：`dotnet build src/FileLocker.App/FileLocker.App.csproj -c Release`，確認 0 錯誤 0 警告；如果 `src/FileLocker.ShellExtension/*.cpp` 有變更過，提醒使用者（或直接執行，如果 VS Dev Shell 可用）用 CLAUDE.md 裡的 `cl` 指令重新編譯 x64 Shell Extension DLL——C# 建置不會自動重編 C++ 部分。
4. **決定版本號**：讀 `git tag` 列出目前最新的版本，跟使用者確認這次是 patch／minor／major，不要自己猜。
5. **產生 Release Notes 草稿**：`git log <上一個 tag>..HEAD --oneline` 整理這段期間的變更，依照 [`docs/releases/v1.1.0.md`](../../../docs/releases/v1.1.0.md) 的雙語段落結構寫成新的 `docs/releases/vX.Y.Z.md`（亮點／已知限制，中文在前、英文在後，兩邊內容要對等——這個對話稽核過，這兩個檔案目前逐條對應，維持這個標準）。同時檢查 `README.md` 需不需要跟著更新（新功能通常要）。
6. **Commit**：訊息比照這個 repo 現有風格（`feat:`／`docs:` 等前綴 + 完整中文說明），文件變更可以跟程式碼變更分開兩個 commit（比照這個對話稍早的做法）。
7. **打 tag**：跟使用者確認要不要打這個 tag、要不要 push（`git tag vX.Y.Z` 本身跟 push 都是視覺化「發布」的動作，兩個都要先問，不要自動打／自動推）。
8. **打包安裝程式**：這一步不用跟使用者確認，執行 release skill 時直接自動打包——這是使用者明確要求的規則（只有建立 GitHub Release、push、打 tag 這三件事才需要先問）。**開始之前先 Read `d:\Github\mac-style-windows-installer_專案\mac-style-windows-installer\CLI_USAGE.md`**——mswi-cli 用法隨時可能改版，使用者會持續把最新用法寫回這份文件，不要憑記憶或這份 skill 裡舊的範例假設。需要更完整脈絡（例如安裝路徑、跟 GUI 的對照）再讀同目錄下的 `使用說明書.md`。

   `dotnet publish src/FileLocker.App/FileLocker.App.csproj -c Release -p:Version=X.Y.Z` 先確保 `publish/`（含 `cli/` 子資料夾）是最新的，再跑（以下是目前已知的範例，flag 名稱／JSON 欄位如果跟剛讀到的 `CLI_USAGE.md` 對不上，一律以文件為準）：

   **`-p:Version=X.Y.Z` 這個參數不能漏**——沒帶的話 `FileLocker.exe`／`FileLocker.Cli.exe`／`FileLocker.UpdateRelauncher.exe` 的檔案版本號（檔案總管右鍵「內容」→「詳細資料」看得到那個欄位）會全部停在 .NET SDK 預設的 `1.0.0.0`，跟這次實際發布的版本號對不上。`FileLocker.App.csproj` 裡 `BuildCli`／`BuildUpdateRelauncher` 這兩個 Target 各自另開一個 `dotnet build` 呼叫（Exec 直接開新的命令列行程），不會自動繼承外層 `dotnet publish` 帶的版本號，已經在這兩個 Target 裡加上 `-p:Version=$(Version)` 顯式往下傳——只要這裡最外層的 `dotnet publish` 有帶版本號，$(Version) 就會正確傳到底。
   ```
   mswi-cli pack --config installer/filelocker_installer.json --version X.Y.Z --exe-name FileLocker_vX.Y.Z_setup
   ```
   `--version`／`--exe-name` 覆蓋 JSON 裡的預設值，確保檔名跟版本號對得上這次發布，JSON 本身不用每次改。
   `installer/filelocker_installer.json` 裡的 `app_dir`／`png_icon`／`ico_icon` 目前是這台機器上的絕對路徑（`mswi-cli` 會把相對路徑誤判成相對於它自己的安裝目錄，不是相對於執行指令當下的工作目錄，只能用絕對路徑繞過），**在不同機器上執行前要先確認這幾個路徑仍然正確**。
   `mswi-cli` 現在裝在使用者可寫的路徑（`%LOCALAPPDATA%\Programs\mac-style-windows-installer`，不再是 `C:\Program Files`），不需要系統管理員權限就能跑。`mswi-cli` 找不到就用完整路徑 `%LOCALAPPDATA%\Programs\mac-style-windows-installer\mswi-cli.exe`（PATH 沒吃到新安裝時的備援）。這段狀況以 `CLI_USAGE.md` 當下內容為準，工具安裝位置以後還可能再變。
   編譯完成後，輸出在 `mswi-cli` 自己的 `dist\` 資料夾（例如 `%LOCALAPPDATA%\Programs\mac-style-windows-installer\dist\FileLocker_vX.Y.Z_setup.exe`），複製一份到 `d:\Github\FileLocker_專案\vX.Y.Z\`（比照既有 v1.0.0／v1.1.0／v1.1.1／v1.1.2 的擺放慣例）。

   **另外還有兩個 CLI 獨立發布產物（CLI_setup／CLI_zip），跟 GUI 安裝檔同一次發布一起打包，不要漏掉**（見 `CONTEXT.md`「CLI 獨立發布產物」詞條——v2.1.0 這輪第一次手動打包驗證過沒有意外的坑，從這輪起收進自動化流程）：
   - `FileLocker_CLI_vX.Y.Z_setup.exe`：`mswi-cli pack --config installer/filelocker_cli_installer.json --version X.Y.Z --exe-name FileLocker_CLI_vX.Y.Z_setup`（跟 GUI 那份一樣，`--version`／`--exe-name` 覆蓋 JSON 預設值，`installer/filelocker_cli_installer.json` 裡的版本號／`exe_name` 欄位維持舊值不用改，理由跟 `filelocker_installer.json` 一致）。
   - `FileLocker_CLI_vX.Y.Z_portable.zip`：把 `src/FileLocker.App/bin/Release/net10.0-windows10.0.19041.0/cli-standalone/` 資料夾內容原封不動打包（**不是 `publish/cli/`，也不是 `bin/.../cli/`**——那兩份從這輪起被 `TrimCliForGuiInstaller` 瘦身過，缺少共用套件、要靠旁邊的 GUI 資料夾補齊，獨立打包會裝出一個找不到 DLL、跑不起來的 CLI；`cli-standalone/` 才是完整自足的那份，見 `FileLocker.App.csproj` 裡 `CopyCliForRelease` 開頭的說明——這份資料夾刻意留在 `bin/Release/` 底下、不會被發布進 `publish/`），額外加一份中英雙語 `README.md`（說明手動加入 PATH 的步驟，內容可比照上一輪的版本微調版本號），不需要另開 build 流程。`Compress-Archive -Path '<暫存資料夾>/*' -DestinationPath 'FileLocker_CLI_vX.Y.Z_portable.zip' -Force`。
   - 兩者都複製到同一個 `d:\Github\FileLocker_專案\vX.Y.Z\` 資料夾，跟 GUI 安裝檔放在一起。
9. **建立 GitHub Release**：先 `gh release list --repo lx-kvn/FileLocker` 確認這個版本還沒發布過。沒發布過的話，把要跑的指令（大致是 `gh release create vX.Y.Z <GUI安裝檔路徑> --title "FileLocker vX.Y.Z" --notes-file docs/releases/vX.Y.Z.md`，再用 `gh release upload vX.Y.Z <CLI_setup路徑> <CLI_zip路徑> --repo lx-kvn/FileLocker` 補上另外兩個 CLI 產物，三個檔案一起列出來給使用者確認也可以，不一定要分兩次指令）列出來給使用者看過、明確同意後才執行——不能自己直接發布。使用者也可能自己先在網頁上手動建立了，跑之前的確認步驟就會發現，發現的話就不用再跑 `gh release create`，直接跟使用者核對內容（Release Notes、附件檔名/大小，這次應該有三個檔案）對不對即可。

   **真實抓到的 bug（v2.1.0 這輪）**：GitHub Release 頁面／API 回傳的 assets 順序是照檔名字母排序，不是照上傳順序——`FileLocker_CLI_v2.1.0_setup.exe` 的 `C` 字母順序排在 `FileLocker_v2.1.0_setup.exe` 的 `v` 之前，所以先建 Release 只丟 GUI 安裝檔、之後才 `gh release upload` 補 CLI 產物，並不能保證 GUI 安裝檔在 assets 清單裡排第一個。應用程式自動更新（`FetchLatestGitHubReleaseAsync`）原本「掃 assets 抓第一個 .exe」的邏輯因此選錯，實際下載並靜默安裝了 CLI 版安裝檔，回報成功但 GUI 本體完全沒被更新——已改成 `SelfUpdateAssetSelector`（`src/FileLocker.Core/UpdateCheck/SelfUpdateAssetSelector.cs`）明確排除檔名含 `_CLI_` 的資產，不依賴上傳順序或 GitHub 的排序方式。這裡純粹記錄一下：**上傳順序本來就救不了這個問題，不用想著靠調整 `gh release create`／`gh release upload` 的呼叫順序防禦，真正的防線在應用程式端的篩選邏輯。**

## 不做的事

- 不自動打 tag、不自動 push——一律先問。
- 不用 `gh release create`／`gh release upload` 未經確認就直接發布——一律先列出指令給使用者看過同意。
- 不把 Release Notes 拆成分開的中英文檔案——這個 repo 的慣例是單一檔案。

需要確認的只有這三件事：打 tag、push、建立 GitHub Release（含上傳附件）。打包安裝程式（步驟 8，含 GUI 與兩個 CLI 產物）不用問，直接執行。
