using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using FileLocker.Core;
using FileLocker.Core.FolderGuard;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.Protocol;
using FileLocker.Core.Settings;
using FileLocker.Core.UpdateCheck;
using FileLocker.Core.Vault;
using FileLocker.PluginContracts;
using Microsoft.Web.WebView2.Core;

namespace FileLocker.App;

public partial class MainWindow : Window
{
    // Release 建置時 SetVirtualHostNameToFolderMapping 用的虛擬主機名稱，純粹是本機識別用，
    // 不是真的網域，不需要真的擁有或註冊這個名稱。
    private const string AppOrigin = "filelocker.local";

    // 軟體更新檢查專用，整個 App 只有這一個用途，不用另外開 DI 容器。
    private static readonly HttpClient s_updateCheckHttpClient = new();

    // 無邊框視窗的 Win32/DWM 互操作跟視窗外框相關邏輯搬到 MainWindow.Chrome.cs 了（見該檔案
    // 開頭說明）——這裡只留 WebView2 初始化／IPC 派送／Vault 協定呼叫。

    // VaultManager／HistoryLogger／LockService／AppSettingsManager／VaultIndexCache 不再各自存成
    // 欄位——除了組出 _protocolHandlers 跟訂閱 _vaultChangeWatcher.Changed，MainWindow 本身不直接
    // 呼叫任何一個，全部透過協定分派層存取（見架構審查 2026-07-26：MainWindow 不該同時身兼視窗
    // 平台細節、WebView2 初始化、跟業務邏輯呼叫這三種不相關的職責）。
    private readonly AppSettings _settings;
    private readonly VaultChangeWatcher _vaultChangeWatcher;
    private readonly VaultProtocolHandlers _protocolHandlers;
    private readonly FolderGuardService _folderGuardService;
    private readonly IPasswordLockerPlugin? _passwordLockerPlugin;
    private readonly PasswordLockerModuleStatus _passwordLockerModuleStatus;
    private readonly List<string>? _initialPaths;
    private readonly string? _initialAction;

    // WebView2 的 CoreWebView2 是非同步初始化（見建構子最後的 Loaded 事件），但建構子裡
    // 就已經訂閱了 _vaultChangeWatcher.Changed／_folderGuardService.EntriesAutoRelocked，
    // 這兩個背景事件隨時可能在 CoreWebView2 準備好之前就先觸發（尤其是視窗剛被重新建立、
    // 或加密才剛完成、Vault watcher 幾乎同時觸發的情境）——SendToFrontend 底下如果直接呼叫
    // 還是 null 的 CoreWebView2，會丟出 NullReferenceException，而且是在 Dispatcher 排入的
    // 委派裡發生、沒有任何地方接住，整個行程會直接崩潰（實際發生過：加密完成後跳出「存進
    // 密碼庫？」詢問，使用者按確定的當下 vaultChanged 事件剛好也在跑）。改成：CoreWebView2
    // 還沒準備好之前，訊息先進這個佇列，NavigationCompleted 那一刻再依序补送出去，不遺失、
    // 也不崩潰。
    private readonly List<object> _pendingFrontendMessages = new();
    private bool _frontendReady;

    /// <summary>
    /// VaultManager／LockService 現在由 App.xaml.cs 統一建立、傳進來——這樣主視窗跟密碼小視窗
    /// 用的是同一份 Vault／History 設定，不會各自重複建立、路徑卻可能不小心兜不起來。
    /// initialPaths 是從 Shell Extension 右鍵選單過來的（可能是空的、一個，或多個路徑），
    /// 等 WebView2 頁面真的載入完成才送給前端，避免前端還沒掛上訊息監聽器就漏接。
    /// folderGuardService 是平行、獨立於 Vault/加密的子系統（見規劃文件），刻意不塞進
    /// VaultProtocolHandlers——那一層現在專責 Vault/加密，資料夾防護走自己獨立的 Handle* 方法。
    /// passwordLockerPlugin 可能是 null（部件未安裝或載入失敗），由 App.xaml.cs 的
    /// PasswordLockerPluginLoader 決定，MainWindow 不自己判斷載入與否，只依 passwordLockerModuleStatus
    /// 決定怎麼回應前端（見 HandlePasswordLockerModuleRequestAsync）。
    /// </summary>
    public MainWindow(
        VaultManager vaultManager, HistoryLogger historyLogger, LockService lockService,
        AppSettingsManager settingsManager, AppSettings settings, string appDataDir,
        VaultIndexCache vaultIndexCache, VaultChangeWatcher vaultChangeWatcher,
        FolderGuardService folderGuardService, IPasswordLockerPlugin? passwordLockerPlugin,
        PasswordLockerModuleStatus passwordLockerModuleStatus,
        List<string>? initialPaths = null, string? initialAction = null)
    {
        InitializeComponent();

        _settings = settings;
        _vaultChangeWatcher = vaultChangeWatcher;
        _folderGuardService = folderGuardService;
        _passwordLockerPlugin = passwordLockerPlugin;
        _passwordLockerModuleStatus = passwordLockerModuleStatus;
        _initialPaths = initialPaths;
        _initialAction = initialAction;

        // 協定分派層（見架構審查 2026-07-26）：純 C#、不依賴 WPF/WebView2，直接組裝既有的
        // Core 依賴即可，不需要 App.xaml.cs 額外建立、也不改動它呼叫 MainWindow 建構子的簽章。
        _protocolHandlers = new VaultProtocolHandlers(
            vaultManager, lockService, vaultIndexCache, historyLogger, settingsManager, settings);

        // Watcher 偵測到 Vault 變化（背景執行緒觸發）時，推播一則通知給前端清單頁——
        // 沿用既有的「背景推送資料進已開啟視窗」模式（見 ApplyIncomingPaths）。
        // 必須用 Dispatcher 切回 UI 執行緒，SendToFrontend 底層是 WebView2 COM 物件，
        // 不能從背景執行緒直接呼叫。
        _vaultChangeWatcher.Changed += (_, _) =>
            Dispatcher.BeginInvoke(() => SendToFrontend(new { type = "vaultChanged" }));

        // 解鎖後閒置自動重新上鎖觸發時（App.xaml.cs 的 DispatcherTimer 或啟動補跑），推播一則
        // 通知給前端跳 toast、順便刷新資料夾防護清單頁狀態——同樣要切回 UI 執行緒，理由跟上面
        // _vaultChangeWatcher.Changed 一樣。
        _folderGuardService.EntriesAutoRelocked += (_, paths) =>
            Dispatcher.BeginInvoke(() => SendToFrontend(new { type = "folderGuardAutoRelocked", paths }));

        // 啟動時就先套用一次已儲存的主題背景色，不要等使用者到設定頁重新選一次才生效——
        // 不然重開 App 之後，即使上次選的是深色模式，WebView2 邊緣那圈窄邊還是會先閃一下
        // 白色，等頁面裡的 JS 執行完才切過去。
        ApplyWindowBackgroundForTheme(_settings.Theme);

        SourceInitialized += OnSourceInitialized;

        Loaded += async (_, _) =>
        {
            // 明確指定使用者資料目錄，不依賴 WebView2 預設「在執行檔旁邊建資料夾」的行為——
            // 安裝到 C:\Program Files\ 之類系統保護目錄時，一般使用者權限沒辦法在執行檔旁邊
            // 寫入，會導致 WebView2 完全開不起來（「無法讀取及寫入其資料目錄」）。改成固定
            // 指向使用者自己的 %LocalAppData%，不管安裝到哪裡、有沒有系統管理員權限都能寫入。
            var webView2UserDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileLocker", "WebView2");
            var webView2Environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webView2UserDataFolder);
            await MainWebView.EnsureCoreWebView2Async(webView2Environment);

            // WebView2 安全性硬化：
            // 1. 關掉密碼自動儲存/自動填入——不關的話，使用者在加密/解密表單輸入的密碼可能被
            //    Chromium 內建的密碼管理員另外存一份，離開我們自己的掌控範圍，也弱化了「密碼不會被
            //    存在任何地方」的安全宣稱。這個不管 Debug/Release 都要關。
            // 2. DevTools 只有 Release 建置才關掉——Debug 建置留著方便自己開發時除錯前端問題。
            MainWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            MainWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            // 桌面應用程式不應該讓使用者用 Ctrl+滾輪或觸控手勢意外縮放畫面（那是瀏覽器的行為，
            // 這裡不是瀏覽器）。畫面本身在不同 Windows 顯示器縮放比例（100%/125%/150%...）下
            // 已經會由 WebView2 自動依照系統 DPI 正確縮放，不需要額外的網頁縮放疊加上去。
            MainWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            // 開啟 app-region CSS 支援：HTML 裡標記 app-region: drag 的區域會被當成視窗標題列，
            // 拖曳、右鍵系統選單、雙擊最大化全部交給作業系統的視窗管理員原生處理。
            // 這比「用 JavaScript 追游標位置再回頭叫視窗移動」可靠得多——後者每次移動都要跨進程
            // 來回一次，延遲累積起來視窗就會抖動，而且拿不到 Aero Snap 那些原生行為。
            // 注意：這個設定必須在導覽之前設定好，下一次導覽才會生效。
            MainWebView.CoreWebView2.Settings.IsNonClientRegionSupportEnabled = true;
#if DEBUG
            MainWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
#else
            MainWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

            // 右鍵選單：只有點在可編輯欄位（密碼／恢復金鑰輸入框）上才保留瀏覽器預設的剪下/複製/貼上選單，
            // 其餘一律不顯示——原本 Chromium 內建的右鍵選單會有「上一頁」「重新整理」「檢視原始碼」
            // 這類跟一般瀏覽器一樣的雜訊項目，在一個不是瀏覽器的桌面工具上沒有意義，關掉比較乾淨。
            MainWebView.CoreWebView2.ContextMenuRequested += (_, ctxArgs) =>
            {
                if (!ctxArgs.ContextMenuTarget.IsEditable)
                {
                    ctxArgs.Handled = true;
                }
            };

            // 導覽限制：只允許導覽到我們預期的網址，其餘一律擋下——避免 Debug 模式下本機
            // localhost 埠被其他程式搶先佔用時載入到惡意頁面；Release 模式下也是防禦性寫法，
            // 就算未來哪個環節不小心觸發了非預期的導覽，也不會真的跑到別的地方去。
            MainWebView.CoreWebView2.NavigationStarting += (_, navArgs) =>
            {
#if DEBUG
                var isAllowed = navArgs.Uri.StartsWith("http://localhost:5173/", StringComparison.Ordinal);
#else
                var isAllowed = navArgs.Uri.StartsWith($"https://{AppOrigin}/", StringComparison.Ordinal);
#endif
                if (!isAllowed)
                {
                    navArgs.Cancel = true;
                }
            };

            // 擋掉 window.open()／target="_blank" 開新視窗：WebView2 預設會直接跳出一個完全不受
            // 上面 NavigationStarting 限制的獨立 Chromium 彈出視窗。這個應用不需要彈出視窗功能，
            // 全部擋掉——就算前端未來哪個相依套件出問題被注入惡意腳本，也沒辦法藉此跳出一個
            // 能導覽到任意網址的視窗。
            MainWebView.CoreWebView2.NewWindowRequested += (_, newWindowArgs) =>
            {
                newWindowArgs.Handled = true;
            };

            // 剪貼簿權限（密碼庫「複製」按鈕用的 navigator.clipboard.writeText）自動核准，
            // 不要跳出「localhost:5173 想要...」這種看起來像網頁的權限詢問視窗——這裡載入的
            // 內容全部是我們自己的前端，不是外部網站，跟瀏覽器分頁請求任意網站權限是不同情境。
            MainWebView.CoreWebView2.PermissionRequested += (_, permArgs) =>
            {
                if (permArgs.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                {
                    permArgs.State = CoreWebView2PermissionState.Allow;
                }
            };

#if DEBUG
            // Debug 建置：連到 Vite 開發伺服器，需要另外開一個終端機跑 npm run dev。
            MainWebView.CoreWebView2.Navigate("http://localhost:5173/");
#else
            // Release 建置：直接從封裝好的靜態檔案載入，不透過任何本機網路埠——
            // 這是規格文件 8.3 節記錄的硬性阻擋項目的正式修法。webapp 資料夾由
            // FileLocker.App.csproj 的 Release 建置流程自動產生（npm run build + 複製檔案）。
            // CoreWebView2HostResourceAccessKind.Deny：不允許其他來源透過網路請求存取這個
            // 虛擬主機底下的資源，我們自己只會直接導覽過去，不需要開放跨來源存取。
            var webAppFolder = Path.Combine(AppContext.BaseDirectory, "webapp");
            if (!File.Exists(Path.Combine(webAppFolder, "index.html")))
            {
                // 找不到打包好的前端靜態檔案——通常代表建置流程沒跑過 npm run build，
                // 或是輸出目錄結構跟預期不一樣。與其讓畫面就這樣一片空白讓使用者一頭霧水，
                // 直接跳出明確的錯誤訊息，方便排查。
                MessageBox.Show(
                    $"找不到前端畫面檔案（預期位置：{webAppFolder}）。\n\n" +
                    "如果是開發階段自己編譯的 Release 版本，請確認 FileLocker.App.csproj 的建置流程有" +
                    "成功執行 npm run build，並把輸出複製到這個資料夾。",
                    "FileLocker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            MainWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                AppOrigin, webAppFolder, CoreWebView2HostResourceAccessKind.Deny);
            MainWebView.CoreWebView2.Navigate($"https://{AppOrigin}/index.html");
#endif
            MainWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            MainWebView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    return;
                }

                // 補送 CoreWebView2 準備好之前排隊的訊息（見 _pendingFrontendMessages 上的
                // 說明）——要在 SendWindowStateToFrontend／initialPaths 這些「這次導覽本來就
                // 要送」的訊息之前送出去，維持事件實際發生的先後順序。
                _frontendReady = true;
                if (_pendingFrontendMessages.Count > 0)
                {
                    var pending = _pendingFrontendMessages.ToArray();
                    _pendingFrontendMessages.Clear();
                    foreach (var message in pending)
                    {
                        SendToFrontend(message);
                    }
                }

                // 頁面載入完成先同步一次目前的視窗狀態，前端的最大化按鈕才知道該顯示哪個圖示
                // （例如上次關閉時是最大化的，這次啟動就要直接顯示「還原」而不是「最大化」）。
                SendWindowStateToFrontend();

                // paths 為空、只帶 action 的情境是系統匣選單的分頁捷徑（見 App.xaml.cs 的
                // ShowMainWindow，MainWindow 還沒開過、要新建一個直接帶指定分頁）——只看
                // _initialPaths 是否非空會漏掉這個情境，導致視窗開是開了、但沒切到指定分頁，
                // 要等視窗已經開著、再點一次托盤選單（走 ApplyIncomingPaths 那條路徑）才生效。
                if (_initialPaths is { Count: > 0 } || _initialAction is not null)
                {
                    // action 讓前端知道要切去哪個分頁：無值或 "encrypt" 維持既有行為（加密分頁）；
                    // "folderGuardSetup" 是右鍵「上鎖」但整個功能還沒設定過共用密碼時的引導路徑
                    // （見 App.xaml.cs HandleLaunchArgs 對 --folder-guard-lock 旗標的處理）。
                    SendToFrontend(new { type = "initialPaths", paths = _initialPaths, action = _initialAction });
                }
            };

            StateChanged += (_, _) => SendWindowStateToFrontend();
        };
    }

    /// <summary>
    /// 對應單一執行個體機制（見 App.xaml.cs）：已經有這個視窗開著時，之後被 Mutex 擋下來、
    /// 轉送過來的加密路徑清單就送進這裡，而不是另外開一個新的 MainWindow。
    /// 順便把視窗搶回前景（可能被壓在其他視窗底下，或被縮到最小），讓使用者知道有新的東西進來了。
    /// </summary>
    public void ApplyIncomingPaths(List<string> paths, string? action = null)
    {
        // 單靠 Activate() 在「已經在系統匣裡的實體透過 Named Pipe 收到轉送參數」這條路徑上不可靠
        // （見 WindowActivation 上的說明），改用會強制拉到最上層的版本。
        WindowActivation.ForceToForeground(this);

        // paths 為空、只帶 action 的情境是系統匣選單的分頁捷徑（見 App.xaml.cs 的
        // ShowMainWindow）——只是要切分頁，沒有要帶任何路徑進去，一樣要送出訊息，
        // 不能只看 paths 是否為空。
        if (paths.Count > 0 || action is not null)
        {
            SendToFrontend(new { type = "initialPaths", paths, action });
        }
    }

    private async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

            // 密碼庫是可選配部件（見 FileLocker_密碼庫_功能規劃.md 第 2 節）：MainWindow 完全不
            // 知道部件內部有哪些 IPC 訊息，靠訊息名稱裡有沒有 "PasswordLocker" 這個子字串判斷
            // 要不要轉發，部件以後新增/修改任何內部訊息都不需要回頭改這裡。
            // getPasswordLockerModuleStatus 是唯一的例外——這個訊息問的正是「部件本身有沒有裝、
            // 狀態正不正常」，本來就得由主體自己回答，不能轉發給（可能根本不存在的）部件。
            if (type == "getPasswordLockerModuleStatus")
            {
                HandleGetPasswordLockerModuleStatusRequest();
                return;
            }
            // 這兩個訊息名稱也含 "PasswordLocker"，但要在轉發判斷之前攔截——原生存檔/開檔對話框
            // 是 WPF 平台能力，部件本身（見規劃文件第 2.1 節）刻意不依賴 WPF，不該由部件自己跳
            // 對話框。CSV 內容的加解密/解析邏輯還是在部件裡，這裡只負責「跟磁碟打交道」那一段。
            if (type == "savePasswordLockerCsvToFile")
            {
                HandleSavePasswordLockerCsvToFileRequest(root);
                return;
            }
            if (type == "pickAndImportPasswordLockerCsv")
            {
                await HandlePickAndImportPasswordLockerCsvRequestAsync();
                return;
            }
            // 部件本身的自動搜尋/下載也是主體的事——查詢／下載對象是 FileLocker 本體自己的
            // GitHub Release 資產列表（見 PasswordLockerModuleInstaller），跟部件內部邏輯無關，
            // 部件甚至可能還沒安裝、根本沒有 _passwordLockerPlugin 可以轉發。
            if (type == "checkForPasswordLockerModuleUpdate")
            {
                await HandleCheckForPasswordLockerModuleUpdateRequestAsync();
                return;
            }
            if (type == "installPasswordLockerModuleUpdate")
            {
                await HandleInstallPasswordLockerModuleUpdateRequestAsync();
                return;
            }
            if (type == "uninstallPasswordLockerModule")
            {
                HandleUninstallPasswordLockerModuleRequest();
                return;
            }
            if (type is not null && type.Contains("PasswordLocker", StringComparison.Ordinal))
            {
                await HandlePasswordLockerModuleRequestAsync(type, root);
                return;
            }

            switch (type)
            {
                case "encrypt":
                    await HandleEncryptRequestAsync(root);
                    break;

                case "encryptPending":
                    await HandleEncryptPendingRequestAsync(root);
                    break;

                case "commitEncrypt":
                    await HandleCommitEncryptRequestAsync(root);
                    break;

                case "rollbackPendingEncrypt":
                    await HandleRollbackPendingEncryptRequestAsync(root);
                    break;

                case "decrypt":
                    await HandleDecryptRequestAsync(root);
                    break;

                case "decryptByUuid":
                    await HandleDecryptByUuidRequestAsync(root);
                    break;

                case "decryptByPasskey":
                    await HandleDecryptByPasskeyRequestAsync(root);
                    break;

                case "decryptByRecoveryKey":
                    await HandleDecryptByRecoveryKeyRequestAsync(root);
                    break;

                case "decryptBatch":
                    await HandleDecryptBatchRequestAsync(root);
                    break;

                case "windowMinimize":
                    WindowState = WindowState.Minimized;
                    break;

                case "windowMaximizeToggle":
                    WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                    break;

                case "windowClose":
                    Close();
                    break;

                case "restartApp":
                    // 密碼庫部件安裝/更新完成後請使用者重啟才會生效（見規劃文件第 2.2 節「不做熱重載」）
                    // ——這裡直接重啟，不是關掉後要求使用者自己重新打開，降低「裝完卻沒生效」的困惑。
                    Process.Start(Environment.ProcessPath!);
                    Application.Current.Shutdown();
                    break;

                case "filesDroppedFromWebView":
                    HandleFilesDroppedFromWebView(e);
                    break;

                case "getPathSizes":
                    await HandleGetPathSizesRequestAsync(root);
                    break;

                case "checkNestedLocks":
                    await HandleCheckNestedLocksRequestAsync(root);
                    break;

                case "saveRecoveryKeyToFile":
                    HandleSaveRecoveryKeyToFileRequest(root);
                    break;

                case "inspectLockedFile":
                    HandleInspectLockedFileRequest(root);
                    break;

                case "getSettings":
                    HandleGetSettingsRequest();
                    break;

                case "setupCriticalAction":
                    await HandleSetupCriticalActionRequestAsync();
                    break;

                case "verifyCriticalAction":
                    await HandleVerifyCriticalActionRequestAsync();
                    break;

                case "clearHistory":
                    HandleClearHistoryRequest();
                    break;

                case "disableCriticalAction":
                    await HandleDisableCriticalActionRequestAsync();
                    break;

                case "pickVaultFolder":
                    HandlePickVaultFolder();
                    break;

                case "changeVaultPath":
                    await HandleChangeVaultPathRequestAsync(root);
                    break;

                case "updateSetting":
                    HandleUpdateSettingRequest(root);
                    break;

                case "pickFile":
                    HandlePickFile(root);
                    break;

                case "pickFolder":
                    HandlePickFolder(root);
                    break;

                case "listVault":
                    await HandleListVaultRequestAsync();
                    break;

                case "listHistory":
                    HandleListHistoryRequest();
                    break;

                case "deleteRecord":
                    await HandleDeleteRecordRequestAsync(root);
                    break;

                case "verifyPasswordForDelete":
                    await HandleVerifyPasswordForDeleteRequestAsync(root);
                    break;

                case "lockFolders":
                    await HandleLockFoldersRequestAsync(root);
                    break;

                case "unlockFolder":
                    await HandleUnlockFolderRequestAsync(root);
                    break;

                case "unlockAllFolders":
                    await HandleUnlockAllFoldersRequestAsync(root);
                    break;

                case "listFolderGuard":
                    await HandleListFolderGuardRequestAsync();
                    break;

                case "removeFolderGuardEntry":
                    await HandleRemoveFolderGuardEntryRequestAsync(root);
                    break;

                case "setupFolderGuardCredential":
                    await HandleSetupFolderGuardCredentialRequestAsync(root);
                    break;

                case "setupFolderGuardPasskey":
                    await HandleSetupFolderGuardPasskeyRequestAsync();
                    break;

                case "disableFolderGuardPasskey":
                    await HandleDisableFolderGuardPasskeyRequestAsync(root);
                    break;

                case "disableFolderGuard":
                    await HandleDisableFolderGuardRequestAsync(root);
                    break;

                case "setFolderGuardDoubleClickUnlock":
                    await HandleSetFolderGuardDoubleClickUnlockRequestAsync(root);
                    break;

                case "setFolderGuardAutoRelock":
                    await HandleSetFolderGuardAutoRelockRequestAsync(root);
                    break;

                case "openFolderInExplorer":
                    HandleOpenFolderInExplorer(root);
                    break;

                case "checkForUpdates":
                    await HandleCheckForUpdatesRequestAsync();
                    break;

                case "downloadAndInstallUpdate":
                    await HandleDownloadAndInstallUpdateRequestAsync();
                    break;

                case "openReleasesPage":
                    Process.Start(new ProcessStartInfo { FileName = "https://github.com/lx-kvn/FileLocker/releases", UseShellExecute = true });
                    break;

                case "unlockFoldersForEncryption":
                    await HandleUnlockFoldersForEncryptionRequestAsync(root);
                    break;

                default:
                    Console.WriteLine($"未知的訊息類型：{type}");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendToFrontend(new { type = "error", message = ex.Message });
        }
    }

    private async Task HandleEncryptRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var password = request.GetProperty("password").GetString() ?? "";
        var hint = request.TryGetProperty("hint", out var hintProp) ? hintProp.GetString() : null;
        var enablePasskey = request.TryGetProperty("enablePasskey", out var passkeyProp) && passkeyProp.GetBoolean();
        var enableRecoveryKey = request.TryGetProperty("enableRecoveryKey", out var recoveryProp) && recoveryProp.GetBoolean();

        // 視窗控制代碼是 WPF 平台細節，只有這裡（呼叫端）拿得到，往下傳給協定分派層當一般參數，
        // 那一層不需要知道「視窗」這個概念存在。
        var ownerWindowHandle = enablePasskey ? new WindowInteropHelper(this).Handle : IntPtr.Zero;

        SendToFrontend(new { type = "encryptBatchStarted", totalCount = paths.Count });

        var successCount = 0;

        // 每完成一個項目就馬上回報，前端可以即時更新清單，不用等全部跑完才看到結果——
        // 這裡只負責「收到一筆就送一次 WebView2 訊息」，逐項的業務邏輯在 EncryptBatchAsync 裡。
        await foreach (var item in _protocolHandlers.EncryptBatchAsync(
            paths, password, hint, enablePasskey, enableRecoveryKey, ownerWindowHandle,
            verifying => SendToFrontend(new { type = "encryptPasskeyVerifying", verifying })))
        {
            if (item.Success)
            {
                successCount++;
            }

            // 前端讀的是攤平的欄位（data.path／data.success／...），不是巢狀的 data.item.xxx，
            // 這裡要攤平回去，維持既有的線上協定格式不變。
            SendToFrontend(new
            {
                type = "encryptItemResult",
                item.Path,
                item.Success,
                item.Uuid,
                item.LockedMarkerPath,
                item.ErrorMessage,
                item.ErrorCode,
                item.ErrorDetail,
                item.PasskeyRequested,
                item.PasskeyEnabled,
                item.RecoveryKey
            });
        }

        SendToFrontend(new { type = "encryptBatchDone", totalCount = paths.Count, successCount });
    }

    /// <summary>
    /// 對應信封加密流程 Phase 2b：跟 HandleEncryptRequestAsync 平行的版本，走 pending 交易模型
    /// （只把加密內容安全寫進 Vault，不寫 marker、不刪原始檔），完成後前端會先播「郵戳／確認」
    /// 畫面，使用者確認才會另外送 commitEncrypt。
    ///
    /// encryptProgress 是這裡新增的推播訊息——比照既有 encryptPasskeyVerifying 的做法，用
    /// IProgress&lt;double&gt; 的 callback 直接呼叫 SendToFrontend，VaultProtocolHandlers／
    /// LockService 那幾層完全不知道「WebView2 訊息」這件事存在，只負責把百分比 Report 出來。
    /// </summary>
    private async Task HandleEncryptPendingRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var password = request.GetProperty("password").GetString() ?? "";
        var hint = request.TryGetProperty("hint", out var hintProp) ? hintProp.GetString() : null;
        var enablePasskey = request.TryGetProperty("enablePasskey", out var passkeyProp) && passkeyProp.GetBoolean();
        var enableRecoveryKey = request.TryGetProperty("enableRecoveryKey", out var recoveryProp) && recoveryProp.GetBoolean();

        var ownerWindowHandle = enablePasskey ? new WindowInteropHelper(this).Handle : IntPtr.Zero;

        SendToFrontend(new { type = "encryptPendingBatchStarted", totalCount = paths.Count });

        var successCount = 0;
        var progress = new Progress<double>(percent => SendToFrontend(new { type = "encryptProgress", percent = percent * 100 }));

        await foreach (var item in _protocolHandlers.EncryptPendingBatchAsync(
            paths, password, hint, enablePasskey, enableRecoveryKey, ownerWindowHandle, progress,
            verifying => SendToFrontend(new { type = "encryptPasskeyVerifying", verifying })))
        {
            if (item.Success)
            {
                successCount++;
            }

            SendToFrontend(new
            {
                type = "encryptPendingItemResult",
                item.Path,
                item.Success,
                item.Uuid,
                item.ErrorMessage,
                item.ErrorCode,
                item.ErrorDetail,
                item.PasskeyRequested,
                item.PasskeyEnabled,
                item.RecoveryKey
            });
        }

        SendToFrontend(new { type = "encryptPendingBatchDone", totalCount = paths.Count, successCount });
    }

    private async Task HandleCommitEncryptRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var result = await _protocolHandlers.CommitEncryptAsync(uuid);

        SendToFrontend(new
        {
            type = "commitEncryptResult",
            result.Success,
            result.Uuid,
            result.LockedMarkerPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    private async Task HandleRollbackPendingEncryptRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        await _protocolHandlers.RollbackPendingEncryptAsync(uuid);

        SendToFrontend(new { type = "rollbackPendingEncryptResult", uuid });
    }

    private async Task HandleDecryptRequestAsync(JsonElement request)
    {
        var lockedMarkerPath = request.GetProperty("path").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";

        var result = await _protocolHandlers.DecryptAsync(lockedMarkerPath, password);

        SendToFrontend(new
        {
            type = "decryptResult",
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    /// <summary>
    /// 對應「已加密清單」頁直接選項目解密，不需要使用者先手動找到 .locked 檔案。
    /// </summary>
    private async Task HandleDecryptByUuidRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";
        var destinationDir = request.TryGetProperty("destinationDir", out var destProp) && destProp.ValueKind == JsonValueKind.String
            ? destProp.GetString()
            : null;

        var result = await _protocolHandlers.DecryptByUuidAsync(uuid, password, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByUuidResult",
            uuid,
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    /// <summary>對應「已加密清單」頁的 Passkey 解鎖按鈕：不需要密碼，走 Windows Hello 驗證。</summary>
    private async Task HandleDecryptByPasskeyRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var destinationDir = VaultProtocolHandlers.ResolveDestinationDirFromRequest(request);

        var hwnd = new WindowInteropHelper(this).Handle;
        var result = await _protocolHandlers.DecryptByPasskeyAsync(uuid, hwnd, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByPasskeyResult",
            uuid,
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    /// <summary>對應「已加密清單」頁的恢復金鑰解鎖按鈕：不需要密碼、不需要 Windows Hello。</summary>
    private async Task HandleDecryptByRecoveryKeyRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var recoveryKeyInput = request.GetProperty("recoveryKey").GetString() ?? "";
        var destinationDir = VaultProtocolHandlers.ResolveDestinationDirFromRequest(request);

        var result = await _protocolHandlers.DecryptByRecoveryKeyAsync(uuid, recoveryKeyInput, destinationDir);

        SendToFrontend(new
        {
            type = "decryptByRecoveryKeyResult",
            uuid,
            result.Success,
            result.RestoredPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    /// <summary>
    /// 對應「已加密清單」頁摺疊群組的「全部解鎖」按鈕：跟批次加密一樣只支援密碼，
    /// 逐一解密、每完成一個就馬上回報，不用等全部跑完才看到結果。還原位置固定用各自的原始位置，
    /// 不像單獨解鎖那樣可以問「原始位置還是自訂位置」——批次情境下每個項目分別問一次太打擾人。
    /// </summary>
    private async Task HandleDecryptBatchRequestAsync(JsonElement request)
    {
        var uuids = request.GetProperty("uuids").EnumerateArray()
            .Select(u => u.GetString() ?? "")
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();
        var password = request.GetProperty("password").GetString() ?? "";

        SendToFrontend(new { type = "decryptBatchStarted", totalCount = uuids.Count });

        var successCount = 0;

        await foreach (var item in _protocolHandlers.DecryptBatchAsync(uuids, password))
        {
            if (item.Success)
            {
                successCount++;
            }

            SendToFrontend(new
            {
                type = "decryptBatchItemResult",
                item.Uuid,
                item.Success,
                item.RestoredPath,
                item.ErrorMessage,
                item.ErrorCode,
                item.ErrorDetail
            });
        }

        SendToFrontend(new { type = "decryptBatchDone", totalCount = uuids.Count, successCount });
    }

    /// <summary>
    /// 對應恢復金鑰顯示畫面的「存成檔案」選項：跳原生存檔對話框，把恢復金鑰文字寫進使用者選的檔案。
    /// </summary>
    /// <summary>
    /// 拖放檔案支援：JS 端用 postMessageWithAdditionalObjects 把拖進來的 File 物件連同這則
    /// 訊息一起送過來，這裡收到的每個物件會是 CoreWebView2File——這是 WebView2 官方專門為了
    /// 「從拖放進來的網頁 File 物件反查真正磁碟路徑」設計的機制，讀 .Path 屬性就是真正路徑，
    /// 不是瀏覽器沙盒化、拿不到路徑的一般 File 物件。
    /// </summary>
    private void HandleFilesDroppedFromWebView(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.AdditionalObjects is null)
        {
            return;
        }

        var paths = new List<string>();
        foreach (var obj in e.AdditionalObjects)
        {
            if (obj is CoreWebView2File file && !string.IsNullOrWhiteSpace(file.Path))
            {
                paths.Add(file.Path);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        Activate();
        // 拖放是在已經開著的視窗裡追加檔案，使用者可能已經選了一些東西，前端會把這個訊息
        // 合併進現有清單，不是整份取代（見 App.vue 的 filesDropped 處理）。
        SendToFrontend(new { type = "filesDropped", paths });
    }

    private void HandleSaveRecoveryKeyToFileRequest(JsonElement request)
    {
        var content = request.GetProperty("content").GetString() ?? "";
        var suggestedFileName = request.TryGetProperty("suggestedFileName", out var nameProp)
            ? nameProp.GetString() ?? "FileLocker-恢復金鑰.txt"
            : "FileLocker-恢復金鑰.txt";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "儲存恢復金鑰",
            FileName = suggestedFileName,
            Filter = "文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            DefaultExt = ".txt"
        };

        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, content);
                SendToFrontend(new { type = "saveRecoveryKeyToFileResult", success = true, path = dialog.FileName });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SendToFrontend(new { type = "saveRecoveryKeyToFileResult", success = false, errorMessage = ex.Message, errorCode = ErrorCodes.RecoveryKeySaveError, errorDetail = ex.Message });
            }
        }
        else
        {
            SendToFrontend(new { type = "saveRecoveryKeyToFileResult", success = false, cancelled = true });
        }
    }

    /// <summary>
    /// 對應「解密」頁籤：使用者選好 .locked 檔案後，查一下這個項目除了密碼之外，
    /// 還有沒有開 Passkey／恢復金鑰，讓前端可以動態顯示對應的按鈕，不用每次都固定只能輸密碼。
    /// 這裡只讀 marker 拿 UUID、查 metadata，不驗證簽章——純粹是為了顯示資訊，
    /// 真正的安全驗證在使用者實際選擇某條解鎖路徑時才會發生。
    /// </summary>
    private void HandleInspectLockedFileRequest(JsonElement request)
    {
        var path = request.GetProperty("path").GetString() ?? "";
        var result = _protocolHandlers.InspectLockedFile(path);

        SendToFrontend(new
        {
            type = "inspectLockedFileResult",
            result.Success,
            result.Uuid,
            result.OriginalName,
            result.Hint,
            result.PasskeyEnabled,
            result.RecoveryKeyEnabled,
            result.CreatedAtUtc
        });
    }

    /// <summary>
    /// 純粹給前端「假的進度條」估算時間用——不是真正的加解密進度回報，只是先問一次每個項目
    /// 的大小跟型別（檔案/資料夾），讓前端可以依大小/數量/型別分類決定進度動畫要跑多久、
    /// 資料夾項目要不要多顯示一段「壓縮中」的階段。抓不到大小（例如檔案剛好被移走、資料夾
    /// 存取被拒）就當作 0，這只是體驗用的估算功能，不該讓錯誤影響到後面真正的加密流程能不能跑。
    /// 資料夾大小用遞迴列舉加總，可能要花一點時間，所以丟到背景執行緒。
    /// </summary>
    private async Task HandleGetPathSizesRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .ToList();

        var items = await _protocolHandlers.GetPathSizesAsync(paths);

        SendToFrontend(new { type = "pathSizesResult", items });
    }

    /// <summary>
    /// 加密前的巢狀鎖定掃描——純資訊性用途，前端拿到數量後只會顯示一個不擋流程的提示，
    /// 不是像 getPathSizes 那樣影響進度條估算，也不需要因為抓不到資料而特別處理錯誤情境。
    /// </summary>
    private async Task HandleCheckNestedLocksRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .ToList();

        var count = await _protocolHandlers.CheckNestedLockCountAsync(paths);

        SendToFrontend(new { type = "nestedLockCheckResult", count });
    }

    private void HandleGetSettingsRequest()
    {
        var settings = _protocolHandlers.GetSettings();
        SendToFrontend(new
        {
            type = "settingsResult",
            settings.VaultPath,
            settings.Language,
            settings.Theme,
            settings.CriticalActionConfigured,
            settings.MinimizeToTrayEnabled,
            settings.LaunchAtStartupEnabled
        });
    }

    private async Task HandleSetupCriticalActionRequestAsync()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var success = await _protocolHandlers.SetupCriticalActionAsync(hwnd);
        SendToFrontend(new { type = "setupCriticalActionResult", success });
    }

    private async Task HandleVerifyCriticalActionRequestAsync()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var success = await _protocolHandlers.VerifyCriticalActionAsync(hwnd);
        SendToFrontend(new { type = "verifyCriticalActionResult", success });
    }

    private void HandleClearHistoryRequest()
    {
        _protocolHandlers.ClearHistory();
        SendToFrontend(new { type = "clearHistoryResult", success = true });
    }

    private async Task HandleDisableCriticalActionRequestAsync()
    {
        await _protocolHandlers.DisableCriticalActionAsync();
        SendToFrontend(new { type = "disableCriticalActionResult", success = true });
    }

    private void HandlePickVaultFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "選擇要搬移到的新 Vault 位置（建議選一個空資料夾）"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SendToFrontend(new { type = "pathPicked", purpose = "vaultFolder", path = dialog.FolderName });
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled", purpose = "vaultFolder" });
        }
    }

    /// <summary>
    /// 搬移 Vault：把目前 Vault 資料夾底下所有檔案搬到新位置、更新設定檔。
    /// 刻意不嘗試在同一個執行中的 App 裡「熱替換」正在使用的 VaultManager（怕跟正在進行中的
    /// 加密/解密操作互相干擾），搬完之後請使用者自己重新啟動 App 讓變更生效，比較單純可靠。
    /// </summary>
    private async Task HandleChangeVaultPathRequestAsync(JsonElement request)
    {
        var newPath = request.GetProperty("newPath").GetString() ?? "";

        var result = await _protocolHandlers.ChangeVaultPathAsync(newPath);

        SendToFrontend(new
        {
            type = "changeVaultPathResult",
            result.Success,
            newPath = result.NewPath,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail,
            result.RequiresRestart
        });
    }

    private void HandleUpdateSettingRequest(JsonElement request)
    {
        var key = request.GetProperty("key").GetString() ?? "";
        var value = request.GetProperty("value").GetString() ?? "";

        var result = _protocolHandlers.UpdateSetting(key, value);
        if (!result.Success)
        {
            return;
        }

        // 套用到這個視窗本身的背景色、系統匣圖示的建立/移除、Run 機碼的登記/反登記，都是
        // WPF/系統層級的即時副作用，設定值有沒有存成功是業務邏輯——UpdateSetting 只管持久化，
        // 這些副作用留在這裡處理。系統匣／開機啟動兩個設定的即時生效邏輯在 App（不是
        // MainWindow）身上，因為系統匣圖示本來就是 App 層級、不屬於任何一個視窗的東西。
        switch (key)
        {
            case "theme":
                ApplyWindowBackgroundForTheme(value);
                break;
            case "minimizeToTrayEnabled":
                ((App)Application.Current).ApplyMinimizeToTraySetting(value == "true");
                break;
            case "launchAtStartupEnabled":
                ((App)Application.Current).ApplyLaunchAtStartupSetting(value == "true");
                break;
        }

        SendToFrontend(new { type = "updateSettingResult", result.Success, result.Key, result.Value });
    }

    private async Task HandleListVaultRequestAsync()
    {
        var items = await _protocolHandlers.ListVaultAsync();
        SendToFrontend(new { type = "vaultList", items });
    }

    /// <summary>對應「使用紀錄」子頁籤：跟 Vault 目前狀態無關，單純把本機累積的操作日誌全部讀出來。</summary>
    private void HandleListHistoryRequest()
    {
        var items = _protocolHandlers.ListHistory();
        SendToFrontend(new { type = "historyList", items });
    }

    private async Task HandleDeleteRecordRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";

        var result = await _protocolHandlers.DeleteRecordAsync(uuid);

        SendToFrontend(new
        {
            type = "deleteRecordResult",
            uuid,
            result.Success,
            result.BlockedByNestedLocks,
            result.NestedUuids,
            result.ErrorMessage,
            result.ErrorCode
        });
    }

    /// <summary>對應「已加密清單」頁永久刪除前的密碼再驗證，驗證通過前端才會真的送出 deleteRecord。</summary>
    private async Task HandleVerifyPasswordForDeleteRequestAsync(JsonElement request)
    {
        var uuid = request.GetProperty("uuid").GetString() ?? "";
        var password = request.GetProperty("password").GetString() ?? "";

        var result = await _protocolHandlers.VerifyPasswordAsync(uuid, password);

        SendToFrontend(new
        {
            type = "verifyPasswordForDeleteResult",
            uuid,
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    // ---- 資料夾防護（Folder Guard）：見 FileLocker_資料夾防護_功能規劃.md。獨立於 Vault/加密的
    // 平行子系統，這裡直接呼叫 _folderGuardService，不透過 VaultProtocolHandlers。 ----

    private async Task HandleLockFoldersRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();

        var results = await _folderGuardService.LockFoldersAsync(paths);

        SendToFrontend(new
        {
            type = "lockFoldersResult",
            items = paths.Zip(results, (path, result) => new
            {
                path,
                result.Success,
                result.ErrorMessage,
                result.ErrorCode,
                result.ErrorDetail
            })
        });
    }

    private async Task HandleUnlockFolderRequestAsync(JsonElement request)
    {
        var path = request.GetProperty("path").GetString() ?? "";
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var keepInListAsUnlocked = request.TryGetProperty("keepInListAsUnlocked", out var keepProp) && keepProp.GetBoolean();
        var hwnd = new WindowInteropHelper(this).Handle;

        var result = await _folderGuardService.UnlockFolderAsync(path, password, hwnd, keepInListAsUnlocked);

        SendToFrontend(new
        {
            type = "unlockFolderResult",
            path,
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    private async Task HandleUnlockAllFoldersRequestAsync(JsonElement request)
    {
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var hwnd = new WindowInteropHelper(this).Handle;

        var result = await _folderGuardService.UnlockAllAsync(password, hwnd);

        SendToFrontend(new
        {
            type = "unlockAllFoldersResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    private async Task HandleListFolderGuardRequestAsync()
    {
        var entries = await _folderGuardService.ListAsync();

        SendToFrontend(new
        {
            type = "folderGuardListResult",
            configured = _folderGuardService.IsConfigured,
            passkeyEnabled = _folderGuardService.IsPasskeyEnabled,
            doubleClickUnlockEnabled = _folderGuardService.IsDoubleClickUnlockEnabled,
            autoRelockEnabled = _folderGuardService.IsAutoRelockEnabled,
            autoRelockMinutes = _folderGuardService.AutoRelockMinutes,
            items = entries.Select(e => new
            {
                e.Path,
                status = e.Status.ToString(),
                e.LockedAtUtc,
                e.UnlockedAtUtc
            })
        });
    }

    /// <summary>設定頁「雙擊已上鎖資料夾直接解鎖」開關——沒有身份驗證要求（跟上鎖一樣，
    /// 這只是操作體驗開關，不是需要驗證的動作），切換完直接把最新狀態回報給前端更新畫面。</summary>
    private async Task HandleSetFolderGuardDoubleClickUnlockRequestAsync(JsonElement request)
    {
        var enabled = request.GetProperty("enabled").GetBoolean();
        await _folderGuardService.SetDoubleClickUnlockEnabledAsync(enabled);

        SendToFrontend(new
        {
            type = "setFolderGuardDoubleClickUnlockResult",
            success = true,
            enabled
        });
    }

    /// <summary>設定頁「解鎖後閒置自動重新上鎖」開關——同樣沒有身份驗證要求，理由跟
    /// HandleSetFolderGuardDoubleClickUnlockRequestAsync 一樣。minutes 的互動式驗證（例如非數字
    /// 輸入）留給前端擋，這裡只負責把 Core 已經會 clamp 到最小 1 的結果原樣回報。</summary>
    private async Task HandleSetFolderGuardAutoRelockRequestAsync(JsonElement request)
    {
        var enabled = request.GetProperty("enabled").GetBoolean();
        var minutes = request.GetProperty("minutes").GetInt32();
        await _folderGuardService.SetAutoRelockAsync(enabled, minutes);

        SendToFrontend(new
        {
            type = "setFolderGuardAutoRelockResult",
            success = true,
            enabled,
            minutes = _folderGuardService.AutoRelockMinutes
        });
    }

    private async Task HandleRemoveFolderGuardEntryRequestAsync(JsonElement request)
    {
        var path = request.GetProperty("path").GetString() ?? "";
        await _folderGuardService.RemoveFromListAsync(path);
        SendToFrontend(new { type = "removeFolderGuardEntryResult", success = true, path });
    }

    private async Task HandleSetupFolderGuardCredentialRequestAsync(JsonElement request)
    {
        var password = request.GetProperty("password").GetString() ?? "";
        await _folderGuardService.SetupCredentialAsync(password);
        SendToFrontend(new { type = "setupFolderGuardCredentialResult", success = true });
    }

    private async Task HandleSetupFolderGuardPasskeyRequestAsync()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var success = await _folderGuardService.SetupPasskeyAsync(hwnd);
        SendToFrontend(new { type = "setupFolderGuardPasskeyResult", success });
    }

    private static void HandleOpenFolderInExplorer(JsonElement request)
    {
        var path = request.GetProperty("path").GetString() ?? "";
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
    }

    /// <summary>installer_config.json 是 mac-style-windows-installer 安裝時放進安裝資料夾的，
    /// 跟 FileLocker.exe 同一層——開發環境用 dotnet run 執行時不會有這個檔案，屬於正常情況，
    /// 不是錯誤。internal（不是 private）是因為 App.xaml.cs 的 --updated 啟動旗標處理也需要
    /// 讀目前版本號顯示在更新完成的提示裡，不重複寫一份同樣的邏輯。</summary>
    internal static string? ReadInstalledVersion()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "installer_config.json");
        if (!File.Exists(configPath))
        {
            return null;
        }
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("version").GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>查 GitHub 最新 release：tag、更新內容（release 的 body 欄位）、安裝檔下載連結
    /// （assets 裡副檔名是 .exe 的那個）。downloadUrl 每次都重新查、不快取、不讓前端傳回來——
    /// 前端只被允許「知道有沒有下載連結」，實際網址由後端自己決定，避免前端能左右下載目標。</summary>
    private async Task<(string? Tag, string? ReleaseNotes, string? DownloadUrl)> FetchLatestGitHubReleaseAsync()
    {
        s_updateCheckHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FileLocker-UpdateCheck");
        var response = await s_updateCheckHttpClient.GetAsync("https://api.github.com/repos/lx-kvn/FileLocker/releases/latest");
        if (!response.IsSuccessStatusCode)
        {
            return (null, null, null);
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString();
        var releaseNotes = doc.RootElement.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;

        string? downloadUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
        }
        return (tag, releaseNotes, downloadUrl);
    }

    private async Task HandleCheckForUpdatesRequestAsync()
    {
        var currentVersion = ReadInstalledVersion();
        if (currentVersion is null)
        {
            SendToFrontend(new { type = "checkForUpdatesResult", success = false, errorCode = ErrorCodes.UpdateCheckNotInstalled });
            return;
        }

        try
        {
            var (latestTag, releaseNotes, downloadUrl) = await FetchLatestGitHubReleaseAsync();
            if (latestTag is null)
            {
                SendToFrontend(new { type = "checkForUpdatesResult", success = false, errorCode = ErrorCodes.UpdateCheckFailed });
                return;
            }

            SendToFrontend(new
            {
                type = "checkForUpdatesResult",
                success = true,
                currentVersion,
                latestVersion = latestTag,
                updateAvailable = VersionComparer.IsNewerVersionAvailable(currentVersion, latestTag),
                releaseNotes,
                hasDownloadUrl = downloadUrl is not null
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            SendToFrontend(new { type = "checkForUpdatesResult", success = false, errorCode = ErrorCodes.UpdateCheckFailed });
        }
    }

    /// <summary>下載安裝檔到暫存資料夾，啟動 FileLocker.UpdateRelauncher 協助行程，交給它
    /// 「等 FileLocker 結束 → 跑靜默安裝 → 依結果重啟 FileLocker」，本體確認協助行程真的啟動
    /// 成功才關閉自己——先關本體再嘗試啟動協助行程的話，萬一啟動失敗（例如被防毒攔截）使用者
    /// 就完全沒有退路了；反過來，啟動成功後不關閉本體，靜默安裝會因為 FileLocker.exe 還在跑
    /// 而失敗（mac-style-windows-installer 的 process_running 檢查沒有旗標能跳過），所以順序
    /// 很重要。改用 /S 靜默安裝＋協助行程重啟，取代原本開出互動安裝視窗的做法——UAC 提權畫面
    /// 還是會跳一次（CreateProcess 不會觸發提權，只有 ShellExecute 會，協助行程那邊已經用
    /// UseShellExecute=true 啟動安裝檔），這是 Windows 系統層級的限制，不是這裡能繞過的。</summary>
    private async Task HandleDownloadAndInstallUpdateRequestAsync()
    {
        try
        {
            var (_, _, downloadUrl) = await FetchLatestGitHubReleaseAsync();
            if (downloadUrl is null)
            {
                SendToFrontend(new { type = "downloadAndInstallUpdateResult", success = false, errorCode = ErrorCodes.UpdateDownloadFailed });
                return;
            }

            var fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
            var installerPath = Path.Combine(Path.GetTempPath(), fileName);

            using var response = await s_updateCheckHttpClient.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();
            await using (var fileStream = File.Create(installerPath))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            var relauncherPath = Path.Combine(AppContext.BaseDirectory, "FileLocker.UpdateRelauncher.exe");
            if (!File.Exists(relauncherPath))
            {
                // 理論上不該發生：這支協助行程只在 Release 建置才會被複製到這裡（跟這個方法
                // 本身一樣，靠 installer_config.json 存不存在間接把 Debug 建置擋在外面），
                // 但找不到的話要明確回報，不要靜默失敗——不然使用者只會看到「下載完但什麼都
                // 沒發生」。
                SendToFrontend(new { type = "downloadAndInstallUpdateResult", success = false, errorCode = ErrorCodes.UpdateDownloadFailed });
                return;
            }

            var appExePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileLocker.exe");
            var logPath = Path.Combine(Path.GetTempPath(), "FileLocker_update_install_log.txt");
            Process.Start(new ProcessStartInfo
            {
                FileName = relauncherPath,
                Arguments = $"{Environment.ProcessId} \"{installerPath}\" \"{appExePath}\" \"{logPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            SendToFrontend(new { type = "downloadAndInstallUpdateResult", success = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            SendToFrontend(new { type = "downloadAndInstallUpdateResult", success = false, errorCode = ErrorCodes.UpdateDownloadFailed });
        }
    }

    private async Task HandleDisableFolderGuardPasskeyRequestAsync(JsonElement request)
    {
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var hwnd = new WindowInteropHelper(this).Handle;

        var result = await _folderGuardService.DisablePasskeyAsync(password, hwnd);

        SendToFrontend(new
        {
            type = "disableFolderGuardPasskeyResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    private async Task HandleDisableFolderGuardRequestAsync(JsonElement request)
    {
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var hwnd = new WindowInteropHelper(this).Handle;

        var result = await _folderGuardService.DisableAsync(password, hwnd);

        SendToFrontend(new
        {
            type = "disableFolderGuardResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    /// <summary>對應規劃文件第 8 節：加密流程掃描到巢狀防護中的資料夾而中止（見 LockService.EncryptAsync
    /// 的 FolderGuardContainsNestedGuarded 錯誤碼）時，前端跳彈窗列出這些子資料夾，使用者確認後
    /// 呼叫這裡解鎖（不留清單記錄，見 UnlockForEncryptionAsync 說明），前端收到成功結果後要自己
    /// 重新送一次原本的 encrypt 請求——這裡只負責解鎖，不負責重試加密。</summary>
    private async Task HandleUnlockFoldersForEncryptionRequestAsync(JsonElement request)
    {
        var paths = request.GetProperty("paths").EnumerateArray()
            .Select(p => p.GetString() ?? "")
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        var password = request.TryGetProperty("password", out var passwordProp) ? passwordProp.GetString() : null;
        var hwnd = new WindowInteropHelper(this).Handle;

        var result = await _folderGuardService.UnlockForEncryptionAsync(paths, password, hwnd);

        SendToFrontend(new
        {
            type = "unlockFoldersForEncryptionResult",
            result.Success,
            result.ErrorMessage,
            result.ErrorCode,
            result.ErrorDetail
        });
    }

    // ---- 密碼庫（Password Locker）：可選配部件，見 FileLocker_密碼庫_功能規劃.md 第 2 節。
    // MainWindow 不知道部件內部的 IPC 方法長什麼樣，只負責「轉發訊息」跟「回答部件裝了沒」
    // 這兩件事，實際的解析/呼叫/組裝回應全部搬進 FileLocker.PasswordLocker 專案的
    // PasswordLockerPlugin 裡了（見 OnWebMessageReceived 開頭的攔截判斷）。----

    private void HandleGetPasswordLockerModuleStatusRequest()
    {
        SendToFrontend(new
        {
            type = "passwordLockerModuleStatusResult",
            status = _passwordLockerModuleStatus switch
            {
                PasswordLockerModuleStatus.Ok => "ok",
                PasswordLockerModuleStatus.Broken => "broken",
                _ => "notInstalled"
            }
        });
    }

    /// <summary>第二階段（規劃文件第 2.4 節）：查 FileLocker 本體 GitHub Release 的資產列表，
    /// 找有沒有相容的 PasswordLocker zip 可以裝——跟 ReadInstalledVersion／s_updateCheckHttpClient
    /// 共用同一套「目前安裝版本」讀法，跟軟體本身的更新檢查是平行、互不影響的兩條查詢。</summary>
    private async Task HandleCheckForPasswordLockerModuleUpdateRequestAsync()
    {
        var currentVersion = ReadInstalledVersion();
        if (currentVersion is null)
        {
            SendToFrontend(new { type = "checkForPasswordLockerModuleUpdateResult", success = false, errorCode = ErrorCodes.UpdateCheckNotInstalled });
            return;
        }

        try
        {
            var (assetName, downloadUrl) = await PasswordLockerModuleInstaller.FindCompatibleReleaseAsync(s_updateCheckHttpClient, currentVersion);
            SendToFrontend(new
            {
                type = "checkForPasswordLockerModuleUpdateResult",
                success = true,
                available = downloadUrl is not null,
                assetName
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            SendToFrontend(new { type = "checkForPasswordLockerModuleUpdateResult", success = false, errorCode = ErrorCodes.UpdateCheckFailed });
        }
    }

    /// <summary>下載相容的 zip、解壓到暫存資料夾——不會立刻生效，前端要提示使用者重新啟動
    /// FileLocker（見 PasswordLockerModuleInstaller.SwapPendingInstallIfPresent 在下次啟動時
    /// 才真正切換）。這裡重新查一次相容資產而不是信任前端傳回來的網址，理由跟軟體本身更新
    /// 的 HandleDownloadAndInstallUpdateRequestAsync 一致：下載目標由後端自己決定。</summary>
    private async Task HandleInstallPasswordLockerModuleUpdateRequestAsync()
    {
        var currentVersion = ReadInstalledVersion();
        if (currentVersion is null)
        {
            SendToFrontend(new { type = "installPasswordLockerModuleUpdateResult", success = false, errorCode = ErrorCodes.UpdateCheckNotInstalled });
            return;
        }

        try
        {
            var (_, downloadUrl) = await PasswordLockerModuleInstaller.FindCompatibleReleaseAsync(s_updateCheckHttpClient, currentVersion);
            if (downloadUrl is null)
            {
                SendToFrontend(new { type = "installPasswordLockerModuleUpdateResult", success = false, errorCode = ErrorCodes.UpdateDownloadFailed });
                return;
            }

            await PasswordLockerModuleInstaller.DownloadAndStageAsync(s_updateCheckHttpClient, downloadUrl);
            SendToFrontend(new { type = "installPasswordLockerModuleUpdateResult", success = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            SendToFrontend(new { type = "installPasswordLockerModuleUpdateResult", success = false, errorCode = ErrorCodes.UpdateDownloadFailed });
        }
    }

    /// <summary>設定頁「解除安裝密碼庫部件」按鈕——只寫標記，這個 session 裡部件繼續照常運作，
    /// 下次啟動才真正刪除（見規劃文件第 9.1 節、PasswordLockerModuleInstaller.MarkForUninstall）。</summary>
    private void HandleUninstallPasswordLockerModuleRequest()
    {
        PasswordLockerModuleInstaller.MarkForUninstall();
        SendToFrontend(new { type = "uninstallPasswordLockerModuleResult", success = true });
    }

    private async Task HandlePasswordLockerModuleRequestAsync(string messageType, JsonElement request)
    {
        if (_passwordLockerPlugin is null)
        {
            // 部件未安裝或載入失敗：前端理論上會先呼叫 getPasswordLockerModuleStatus 判斷該顯示
            // 哪個畫面、不該再送其他密碼庫訊息過來。這裡不強行猜測每種訊息對應的回應 "type"
            // 該叫什麼名字，改送通用的 error 訊息——理由見下面 response is null 那段。
            SendToFrontend(new { type = "error", message = $"密碼庫部件未安裝或載入失敗，無法處理：{messageType}" });
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var response = await _passwordLockerPlugin.HandleRequestAsync(messageType, request, hwnd);
        if (response is not null)
        {
            SendToFrontend(response);
            return;
        }

        // 部件收到自己不認得的訊息名稱（HandleRequestAsync 的 _ => null 分支）——實務上這代表
        // plugins/PasswordLocker/ 裡的部件版本比主體舊，主體已經會送新訊息、部件還不認得。
        // 這裡絕對不能安靜地什麼都不送：前端的 requestMessage() 是「送出去、等一個特定回應
        // 類型」的手刻 Promise（見 useIpc.js），等不到就永遠卡住，畫面上不會有任何反應，
        // 連 console 都不會有錯誤——這正是實作 CSV 匯出時真的踩到的坑，花了很久才定位到
        // 「原來是部件 DLL 沒更新」。改送通用 error 訊息，前端的 error handler 會把所有
        // 等待中的請求解開並顯示訊息（見 App.vue 的 rejectAllPending），讓版本不同步這件事
        // 立刻看得見，而不是變成一個查不出原因的「按了沒反應」。
        SendToFrontend(new { type = "error", message = $"密碼庫部件不認得這個操作：{messageType}（部件版本可能過舊，請重新安裝密碼庫部件）" });
    }

    /// <summary>密碼庫 CSV 匯出（規劃文件第 7 節）的最後一段：部件只負責把明文 CSV 內容組好、
    /// 送回來，實際寫進磁碟的原生存檔對話框由 MainWindow 處理，比照
    /// <see cref="HandleSaveRecoveryKeyToFileRequest"/> 既有的分工慣例。</summary>
    private void HandleSavePasswordLockerCsvToFileRequest(JsonElement request)
    {
        // 這裡整個包一層 try/catch，不只是包 File.WriteAllText 那一段——GetProperty／
        // SaveFileDialog 建構／ShowDialog 任何一步萬一丟出未預期的例外，都要送回一個
        // savePasswordLockerCsvToFileResult，不能讓例外掉到最外層的通用 catch。通用 catch
        // 送回的是完全不同的 "error" 訊息類型，前端等的是 savePasswordLockerCsvToFileResult，
        // 對不到就會一直卡住、畫面沒有任何反應（見 rejectAllPending 在 useIpc.js 的說明，
        // 那是最後一道防線，這裡先盡量把錯誤原因精準送回去）。
        try
        {
            var content = request.GetProperty("content").GetString() ?? "";

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "匯出密碼庫",
                FileName = "FileLocker-密碼庫.csv",
                Filter = "CSV 檔 (*.csv)|*.csv|所有檔案 (*.*)|*.*",
                DefaultExt = ".csv"
            };

            // 這個對話框通常緊接在密碼庫驗證（可能是 Passkey／Windows Hello）之後彈出——Windows Hello
            // 是獨立的系統 UI，關閉後前景焦點不一定會自動還給 MainWindow（跟已知的 Mutex 搶焦點問題
            // 是同一類成因，見 CLAUDE.md「已知的坑」），沒搶到前景的話 ShowDialog 開出來的視窗會被
            // 擋在背後，使用者會覺得「按了完全沒反應」。開對話框前先強制搶一次前景。
            WindowActivation.ForceToForeground(this);
            if (dialog.ShowDialog(this) == true)
            {
                File.WriteAllText(dialog.FileName, content);
                SendToFrontend(new { type = "savePasswordLockerCsvToFileResult", success = true, path = dialog.FileName });
            }
            else
            {
                SendToFrontend(new { type = "savePasswordLockerCsvToFileResult", success = false, cancelled = true });
            }
        }
        catch (Exception ex)
        {
            SendToFrontend(new { type = "savePasswordLockerCsvToFileResult", success = false, errorMessage = ex.Message, errorCode = ErrorCodes.RecoveryKeySaveError, errorDetail = ex.Message });
        }
    }

    /// <summary>密碼庫 CSV 匯入的第一段：原生開檔對話框選 CSV 檔案、讀取內容，再轉發給部件的
    /// importPasswordLockerCsv 做實際解析／加密／寫入——跟匯出對稱，檔案系統操作留在
    /// MainWindow，CSV 解析與資料寫入邏輯留在部件裡。使用者取消選檔時安靜回傳 cancelled，
    /// 不當成錯誤。</summary>
    private async Task HandlePickAndImportPasswordLockerCsvRequestAsync()
    {
        // 整段包 try/catch 的理由跟 HandleSavePasswordLockerCsvToFileRequest 一致——任何一步
        // 未預期的例外都要送回 importPasswordLockerCsvResult，不能讓前端等的回應類型對不上，
        // 卡住不動。
        try
        {
            await HandlePickAndImportPasswordLockerCsvCoreAsync();
        }
        catch (Exception ex)
        {
            SendToFrontend(new { type = "importPasswordLockerCsvResult", success = false, errorMessage = ex.Message, errorCode = ErrorCodes.RecoveryKeySaveError, errorDetail = ex.Message });
        }
    }

    private async Task HandlePickAndImportPasswordLockerCsvCoreAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "匯入密碼庫 CSV",
            CheckFileExists = true,
            Filter = "CSV 檔 (*.csv)|*.csv|所有檔案 (*.*)|*.*"
        };

        // 理由同 HandleSavePasswordLockerCsvToFileRequest：這個對話框常緊接在密碼庫驗證之後彈出。
        WindowActivation.ForceToForeground(this);
        if (dialog.ShowDialog(this) != true)
        {
            SendToFrontend(new { type = "importPasswordLockerCsvResult", success = false, cancelled = true });
            return;
        }

        if (_passwordLockerPlugin is null)
        {
            return;
        }

        var csv = File.ReadAllText(dialog.FileName);

        var hwnd = new WindowInteropHelper(this).Handle;
        var request = JsonDocument.Parse(JsonSerializer.Serialize(new { csv })).RootElement;
        var response = await _passwordLockerPlugin.HandleRequestAsync("importPasswordLockerCsv", request, hwnd);
        if (response is not null)
        {
            SendToFrontend(response);
        }
    }

    private void HandlePickFile(JsonElement request)
    {
        var purpose = request.TryGetProperty("purpose", out var purposeProp) ? purposeProp.GetString() : null;
        var allowMultiselect = purpose == "encryptPath";

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = purpose == "decryptPath" ? "選擇要解密的 .locked 檔案" : "選擇要加密的檔案",
            CheckFileExists = true,
            Multiselect = allowMultiselect,
            Filter = purpose == "decryptPath"
                ? "FileLocker 鎖定檔 (*.locked)|*.locked|所有檔案 (*.*)|*.*"
                : "所有檔案 (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            if (allowMultiselect)
            {
                SendToFrontend(new { type = "pathsPicked", purpose, paths = dialog.FileNames });
            }
            else
            {
                SendToFrontend(new { type = "pathPicked", purpose, path = dialog.FileName });
            }
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled", purpose });
        }
    }

    private void HandlePickFolder(JsonElement request)
    {
        var purpose = request.TryGetProperty("purpose", out var purposeProp) ? purposeProp.GetString() : "encryptPath";

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = purpose == "decryptDestination" ? "選擇要還原到哪個資料夾" : "選擇要加密的資料夾"
        };

        if (dialog.ShowDialog(this) == true)
        {
            SendToFrontend(new { type = "pathPicked", purpose, path = dialog.FolderName });
        }
        else
        {
            SendToFrontend(new { type = "pathPickCancelled", purpose });
        }
    }

    // 既有的匿名型別訊息（type = "..." 這種寫法）本來就直接把 C# 屬性名稱寫成 camelCase，
    // 這個命名策略對它們是不動作（"type" 已經是小寫開頭）；VaultProtocolHandlers 回傳的
    // response record 用慣例的 PascalCase 屬性名稱，靠這個策略轉成前端預期的 camelCase JSON，
    // 兩種寫法可以在同一個 SendToFrontend 底下並存，不用回頭把舊的匿名型別全部改名。
    private static readonly JsonSerializerOptions SendToFrontendJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private void SendToFrontend(object message)
    {
        // CoreWebView2 還沒初始化完成，或頁面還沒跑到能處理 postMessage 的程度（見
        // _pendingFrontendMessages 上的說明）——先排隊，NavigationCompleted 時再補送。
        if (!_frontendReady || MainWebView.CoreWebView2 is null)
        {
            _pendingFrontendMessages.Add(message);
            return;
        }
        MainWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, SendToFrontendJsonOptions));
    }
}