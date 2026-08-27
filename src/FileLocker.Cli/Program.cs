using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using FileLocker.Cli;
using FileLocker.Core;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;

// --lang 是全域旗標，連「沒帶任何指令、只印用法說明」這條路徑都要吃它（使用者可能只是想看
// 英文版的用法說明），所以要在最開頭、任何指令分派之前就先抽出來、解析語言、設定好——下面
// 所有 Console 輸出（含 PrintUsage）才能立刻套用正確的語言。抽取失敗（--lang 後面沒接值）
// 屬於使用者輸入錯誤，跟下面既有的 CliArgumentException catch 共用同一套處理。
CliOutputFormat outputFormat;
try
{
    var (langFlag, outputFlag, remainingAfterGlobalFlags) = CliArgumentParser.ExtractGlobalFlags(args);
    CliLocalization.SetLanguage(CliLocalization.ResolveLanguage(langFlag));
    outputFormat = CliOutputFormatParser.Resolve(outputFlag);
    args = remainingAfterGlobalFlags;
}
catch (CliArgumentException ex)
{
    // 這個時間點語言還沒解析成功，固定印中文——比起因為語言判斷本身失敗而整段吞掉不出聲，
    // 印中文總比什麼都不印好，而且這種輸入錯誤本來就少見（--lang／--output 後面忘記接值）。
    Console.WriteLine($"參數錯誤：{ex.Message}");
    Environment.Exit(CliExitCode.UsageError);
    return;
}
var jsonOutput = outputFormat == CliOutputFormat.Json;

// --output json 模式下 stdout 只留給最終那一份 JSON 文件，其餘資訊性文字（Vault 位置、
// 「加密中...」這類進度提示）改印到 stderr——腳本用 `| jq` 之類的工具接手 stdout 時，
// 不會被這些人類可讀的雜訊污染，這是主流工具（docker/kubectl/gh 的 -o json）的既有慣例。
TextWriter chatOut = jsonOutput ? Console.Error : Console.Out;
var jsonSerializerOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

// ---- 顏色／忙碌指示器：只在真的接到終端機時開啟，被導向檔案／管線或 --output json 時
// 一律不開——ANSI 顏色碼跟 \r 覆寫游標這兩種手法混進被腳本解析的輸出裡都是污染，不是加分。
// 尊重 NO_COLOR（https://no-color.org）這個跨工具通用的環境變數慣例，設定了就不上色，
// 不是這個專案自創的規則。 ----
var noColorEnv = Environment.GetEnvironmentVariable("NO_COLOR") is not null;
var stdoutIsTty = !noColorEnv && !Console.IsOutputRedirected;
var stderrIsTty = !noColorEnv && !Console.IsErrorRedirected;
var ansiEsc = "\u001b";
string Green(string s) => stdoutIsTty ? $"{ansiEsc}[32m{s}{ansiEsc}[0m" : s;
string Red(string s) => stdoutIsTty ? $"{ansiEsc}[31m{s}{ansiEsc}[0m" : s;
string Yellow(string s) => stderrIsTty ? $"{ansiEsc}[33m{s}{ansiEsc}[0m" : s;

var showSpinner = !jsonOutput && !Console.IsOutputRedirected;

/// <summary>
/// LockService.EncryptAsync／DecryptAsync 這類方法簽章上雖然收 IProgress&lt;double&gt;，但
/// 全專案（含 GUI）從來沒有任何地方真的呼叫過 .Report(...)——那個參數是預留的死插座，GUI
/// 端看起來會動的進度條其實是依檔案大小估算出來的動畫時間，不是真的加解密進度回報（見
/// MainWindow.xaml.cs GetPathSizesAsync 上的既有說明）。與其在 CLI 這裡假裝算得出真正的
/// 百分比，不如老實用一個「還在跑，不是當機」的忙碌旋轉指示器——不承諾任何虛假的精確度。
/// 只在真的接到終端機、且不是 --output json 時才顯示，跑完（無論成功/失敗）用空白蓋掉那一行，
/// 不會在 stdout 留下殘影字元。
/// </summary>
async Task<T> WithSpinnerAsync<T>(Func<Task<T>> operation, string label)
{
    if (!showSpinner)
    {
        return await operation();
    }

    using var cts = new CancellationTokenSource();
    var spinnerTask = Task.Run(async () =>
    {
        char[] frames = ['|', '/', '-', '\\'];
        var i = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            Console.Write($"\r{label} {frames[i % frames.Length]}");
            i++;
            try
            {
                await Task.Delay(120, cts.Token);
            }
            catch (TaskCanceledException)
            {
                // 正常收尾路徑（下面 finally 取消），不是錯誤。
            }
        }
    });

    try
    {
        return await operation();
    }
    finally
    {
        await cts.CancelAsync();
        try { await spinnerTask; } catch (OperationCanceledException) { }
        Console.Write($"\r{new string(' ', label.Length + 2)}\r");
    }
}

// -h／--help／--version 是使用者找說明的第一反射動作（比「不帶任何參數」更直覺），放在
// Vault 路徑設定之前處理——這兩個查詢不需要、也不該去建立 Vault 資料夾或碰任何檔案系統
// 狀態，純粹印一行資訊就結束。掃全部 args（不限定 args[0]），跟 --lang 一樣不管出現在
// 哪個位置都認得，例如 `--encrypt file.txt --help` 也會被接住優先處理。
if (args.Any(a => a is "-h" or "--help"))
{
    PrintUsage();
    return;
}
if (args.Any(a => a == "--version"))
{
    PrintVersion();
    return;
}

if (args.Length < 1)
{
    PrintUsage();
    return;
}

// completion 也不需要碰 Vault——放在跟 -h/--version 同一層，趁 Vault 路徑還沒設定、
// 「Vault location: ...」banner 還沒印出來之前就處理掉。這裡曾經漏掉這一步，導致
// `FileLocker.Cli completion bash` 的 stdout 混進了那行 banner，使用者直接
// `source <(FileLocker.Cli completion bash)` 會把 banner 那行文字當成指令執行、直接出錯——
// 這是本輪加完 --output json 的 chatOut 機制後才浮現的既有坑，一併在這裡修掉。
if (args[0] == "completion")
{
    if (args.Length < 2)
    {
        PrintUsage();
        Environment.Exit(CliExitCode.UsageError);
        return;
    }
    try
    {
        PrintCompletionScript(args[1]);
    }
    catch (CliArgumentException ex)
    {
        Console.Error.WriteLine(CliLocalization.T("argumentError", ex.Message));
        Environment.Exit(CliExitCode.UsageError);
    }
    return;
}

// 允許用環境變數指定 Vault 路徑，方便無 GUI 環境（排程工作、遠端伺服器）指到跟主程式
// 相同或不同的 Vault，不用寫死路徑或改程式碼——沒有設定的話就跟主程式一樣退回預設路徑。
var vaultPath = Environment.GetEnvironmentVariable("FILELOCKER_VAULT_PATH");
if (string.IsNullOrWhiteSpace(vaultPath))
{
    vaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileLocker", "Vault");
}
Directory.CreateDirectory(vaultPath);
chatOut.WriteLine(CliLocalization.T("vaultLocation", vaultPath));

var vault = new VaultManager(vaultPath);
var service = new LockService(vault);

// 這裡刻意不用 VaultIndexCache（GUI 用的 SQLite 加速層）——那層資料只靠一個常駐的
// FileSystemWatcher 保持最新，CLI 每次執行都是全新短命的行程，沒有常駐監看，
// 快取會立刻變成過時的殘影（實測：encrypt 完馬上在下一次呼叫 --list 完全看不到剛加密的項目）。
// VaultManager.ScanAll() 每次直接掃 Vault 資料夾裡的 .meta.json，慢一點但保證即時正確，
// 對一個「用完就結束」的行程來說這才是對的取捨。
// 子命令（encrypt/unlock/unlock-recovery/list/delete，不帶開頭的 --）是現在推薦的新語法，
// 跟主流 CLI 工具（git/docker/kubectl/gh 的「動詞當子命令」慣例）看齊；舊的 --encrypt 這類
// 「旗標當動詞」寫法繼續完整支援、行為完全不變，只在用到的當下印一行過時提醒到 stderr——
// 不影響任何功能或結束碼，純粹是引導使用者換寫法，不強制、不設移除時間表。
var command = NormalizeCommand(args[0]);

try
{
    switch (command)
    {
        case "--encrypt":
            RequireArgs(2);
            {
                var (options, paths) = CliArgumentParser.Parse(args[1..]);
                await EncryptCommandAsync(paths.ToArray(), options);
            }
            break;
        case "--unlock":
            RequireArgs(2);
            {
                var (options, paths) = CliArgumentParser.Parse(args[1..]);
                await UnlockCommandAsync(paths.ToArray(), options);
            }
            break;
        case "--unlock-recovery":
            RequireArgs(3);
            await UnlockByRecoveryKeyCommandAsync(args[1], args[2], args.Length > 3 ? args[3] : null);
            break;
        case "--list":
            ListCommand();
            break;
        case "--delete":
            RequireArgs(2);
            {
                var (options, uuids) = CliArgumentParser.Parse(args[1..]);
                await DeleteCommandAsync(uuids.ToArray(), options);
            }
            break;
        default:
            PrintUsage();
            break;
    }
}
catch (CliArgumentException ex)
{
    Console.WriteLine(CliLocalization.T("argumentError", ex.Message));
    PrintUsage();
    Environment.Exit(CliExitCode.UsageError);
}

/// <summary>薄包裝：呼叫可測試的 CliCommandNormalizer，副作用（印過時提醒到 stderr）留在
/// 這裡——Program.cs 本身不能被單元測試直接呼叫，但邏輯判斷（該不該正規化、該不該提醒）
/// 已經抽進 CliCommandNormalizer，那邊有完整測試覆蓋。</summary>
string NormalizeCommand(string raw)
{
    var (canonical, isLegacyForm, recommendedForm) = CliCommandNormalizer.Normalize(raw);
    if (isLegacyForm && recommendedForm is not null)
    {
        Console.Error.WriteLine(Yellow(CliLocalization.T("deprecatedFlagStyleWarning", raw, recommendedForm)));
    }
    return canonical;
}

void RequireArgs(int minCount)
{
    if (args.Length < minCount)
    {
        PrintUsage();
        Environment.Exit(1);
    }
}

/// <summary>
/// 架構檢視候選 B：encrypt／unlock／unlock-recovery／delete 這四個指令函式，扣掉各自
/// 「事前準備」（密碼提示、確認提示、缺檔檢查）跟「怎麼把一個結果變成一行文字／一個 JSON
/// 物件」之後，剩下的殼子完全一樣——逐項跑（帶忙碌指示器）、累計成功數、json 模式收集陣列
/// 或 text 模式逐項印、最後印批次摘要（或跳過，單一項目時）、決定結束碼。這個殼子以前
/// 手抄了四遍，其中一份（unlock-recovery，單一項目、不印摘要）連寫法都跟其他三份長得不一樣
/// ——收斂到這裡之後，只有一份「怎麼跑一批、怎麼收尾」的邏輯，四個指令函式各自剩下的部分
/// 縮成呼叫端只需要提供「這一項要怎麼跑」「這一項的結果怎麼印成文字／JSON」兩組委派。
/// </summary>
async Task RunBatchCommandAsync<TItem, TResult>(
    IReadOnlyList<TItem> items,
    Func<TItem, Task<TResult>> runItem,
    Func<TResult, bool> isSuccess,
    Func<TItem, TResult, object> toJson,
    Action<TItem, TResult> printText)
{
    var successCount = 0;
    var jsonResults = new List<object>();
    foreach (var item in items)
    {
        // 忙碌指示器要不要顯示、顯示什麼字，由呼叫端自己決定要不要在 runItem 裡包
        // WithSpinnerAsync——Delete 是純 metadata 操作、原本就沒有 spinner，這裡不強加。
        var result = await runItem(item);
        if (isSuccess(result))
        {
            successCount++;
        }

        if (jsonOutput)
        {
            jsonResults.Add(toJson(item, result));
        }
        else
        {
            printText(item, result);
        }
    }

    if (jsonOutput)
    {
        Console.WriteLine(JsonSerializer.Serialize(jsonResults, jsonSerializerOptions));
    }
    else if (items.Count > 1)
    {
        Console.WriteLine(CliLocalization.T("batchSummary", successCount, items.Count - successCount));
    }

    Environment.Exit(CliExitCode.ForBatch(successCount, items.Count));
}

async Task EncryptCommandAsync(string[] targetPaths, CliOptions options)
{
    var missing = targetPaths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
    if (missing.Count > 0)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                missing.Select(path => new { path, success = false, errorCode = "SOURCE_NOT_FOUND", errorMessage = CliLocalization.T("notFound", path) }),
                jsonSerializerOptions));
        }
        else
        {
            foreach (var path in missing)
            {
                Console.WriteLine(Red(CliLocalization.T("notFound", path)));
            }
        }
        Environment.Exit(CliExitCode.PartialOrTotalFailure);
        return;
    }

    // --password-stdin／--password-file 出現本身就是「非互動模式」的觸發條件（見規劃：不另外
    // 設一個全域 --non-interactive 開關，避免兩者互相矛盾）。非互動模式下確認密碼／恢復金鑰
    // y-N／密碼提示這三個互動問題全部跳過，直接用旗標值，不會像原本互動流程那樣依序讀好幾行
    // stdin（腳本很難保證順序正確）。
    var nonInteractive = options.PasswordFromStdin || options.PasswordFilePath is not null;

    string password;
    bool enableRecoveryKey;
    string? hint;

    if (nonInteractive)
    {
        password = ReadPasswordFromFlag(options);
        enableRecoveryKey = options.RecoveryKeyEnabled;
        hint = options.Hint;
    }
    else
    {
        chatOut.Write(CliLocalization.T("enterPassword"));
        password = ReadPassword();
        chatOut.Write(CliLocalization.T("enterPasswordConfirm"));
        var confirmPassword = ReadPassword();
        chatOut.WriteLine();

        if (password != confirmPassword)
        {
            chatOut.WriteLine(CliLocalization.T("passwordMismatch"));
            Environment.Exit(CliExitCode.PartialOrTotalFailure);
            return;
        }

        chatOut.Write(CliLocalization.T("generateRecoveryKeyPrompt"));
        enableRecoveryKey = (Console.ReadLine() ?? "").Trim().Equals("y", StringComparison.OrdinalIgnoreCase);

        chatOut.Write(CliLocalization.T("hintPrompt"));
        hint = Console.ReadLine();
    }

    if (string.IsNullOrEmpty(password))
    {
        chatOut.WriteLine(CliLocalization.T("passwordEmpty"));
        Environment.Exit(CliExitCode.PartialOrTotalFailure);
        return;
    }

    // 選了不只一個項目才需要分組——單一項目沒有「摺疊」的意義，維持 batchId = null，
    // 跟 GUI 端 VaultProtocolHandlers.EncryptBatchAsync 同一套邏輯。
    var batchId = targetPaths.Length > 1 ? Guid.NewGuid().ToString() : null;

    // Passkey 刻意不在 CLI 提供——WinRT KeyCredentialManager 會跳出 Windows Hello 系統 UI，
    // 這是無 GUI 環境的存在意義相衝突的功能，之後如果要支援也應該是另一個獨立指令，不是這裡硬塞。
    chatOut.WriteLine(CliLocalization.T("encrypting"));

    await RunBatchCommandAsync(
        targetPaths,
        runItem: targetPath => WithSpinnerAsync(
            () => service.EncryptAsync(
                targetPath, password, string.IsNullOrWhiteSpace(hint) ? null : hint,
                enablePasskey: false, ownerWindowHandle: IntPtr.Zero,
                enableRecoveryKey: enableRecoveryKey, batchId: batchId,
                storageMode: options.StandaloneEnabled ? StorageMode.Standalone : StorageMode.Vault,
                destinationDir: options.DestinationDir),
            CliLocalization.T("encrypting") + " " + Path.GetFileName(targetPath)),
        isSuccess: result => result.Success,
        // Standalone 模式沒有 .locked 指標檔，LockResult.LockedMarkerPath 這個欄位借用來裝
        // 實際落腳的 .flocked 檔案路徑（見 LockService.CommitStandaloneEncryptAsync）——
        // JSON 輸出額外用 storageMode 欄位讓腳本自己判斷該把 location 當指標檔還是密文檔看待，
        // 不像文字模式那樣只能靠切換過的標籤文字表達。兩個分支刻意產生完全一樣的欄位集合
        // （成功時 errorCode/errorMessage 是 null，失敗時 uuid/storageMode/location/recoveryKey
        // 是 null），讓陣列裡每個元素的 JSON 形狀一致，腳本不用先判斷 success 才知道該找哪些鍵。
        toJson: (targetPath, result) => new
        {
            path = targetPath,
            success = result.Success,
            uuid = result.Success ? result.Uuid : null,
            storageMode = result.Success ? (options.StandaloneEnabled ? "Standalone" : "Vault") : null,
            location = result.Success ? result.LockedMarkerPath : null,
            recoveryKey = result.Success && !string.IsNullOrEmpty(result.RecoveryKey) ? result.RecoveryKey : null,
            errorCode = result.Success ? null : result.ErrorCode,
            errorMessage = result.Success ? null : CliLocalization.TranslateError(result.ErrorCode, result.ErrorDetail, result.ErrorMessage ?? ""),
        },
        printText: (targetPath, result) =>
        {
            if (result.Success)
            {
                Console.WriteLine(Green(CliLocalization.T("encryptSuccess", targetPath)));
                Console.WriteLine(CliLocalization.T("uuidLabel", result.Uuid));
                var locationKey = options.StandaloneEnabled ? "flockedLocationLabel" : "markerLocationLabel";
                Console.WriteLine(CliLocalization.T(locationKey, result.LockedMarkerPath));
                if (!string.IsNullOrEmpty(result.RecoveryKey))
                {
                    Console.WriteLine(CliLocalization.T("recoveryKeyLabel", result.RecoveryKey));
                }
            }
            else
            {
                Console.WriteLine(Red(CliLocalization.T("encryptFailed", targetPath)));
                Console.WriteLine($"  {CliLocalization.TranslateError(result.ErrorCode, result.ErrorDetail, result.ErrorMessage ?? "")}");
            }
        });
}

async Task UnlockCommandAsync(string[] markerPaths, CliOptions options)
{
    var missing = markerPaths.Where(p => !File.Exists(p)).ToList();
    if (missing.Count > 0)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                missing.Select(path => new
                {
                    path, success = false, restoredPath = (string?)null,
                    errorCode = string.Equals(Path.GetExtension(path), ".flocked", StringComparison.OrdinalIgnoreCase) ? "FLOCKED_NOT_FOUND" : "MARKER_NOT_FOUND",
                    errorMessage = CliLocalization.T(string.Equals(Path.GetExtension(path), ".flocked", StringComparison.OrdinalIgnoreCase) ? "flockedFileNotFound" : "markerNotFound", path),
                }),
                jsonSerializerOptions));
        }
        else
        {
            foreach (var path in missing)
            {
                // .flocked 本身就是密文檔（不是「指標檔」），找不到的訊息用詞要跟著換，不然
                // 使用者會被「指標檔」這個詞誤導，以為自己選錯了檔案類型。
                var key = string.Equals(Path.GetExtension(path), ".flocked", StringComparison.OrdinalIgnoreCase)
                    ? "flockedFileNotFound"
                    : "markerNotFound";
                Console.WriteLine(Red(CliLocalization.T(key, path)));
            }
        }
        Environment.Exit(CliExitCode.PartialOrTotalFailure);
        return;
    }

    var nonInteractive = options.PasswordFromStdin || options.PasswordFilePath is not null;
    string password;
    if (nonInteractive)
    {
        password = ReadPasswordFromFlag(options);
    }
    else
    {
        chatOut.Write(CliLocalization.T("enterPassword"));
        password = ReadPassword();
        chatOut.WriteLine();
    }

    // 沒輸入任何東西（互動時直接按 Enter、腳本情境 stdin 是空的）就明確講出來——底層驗證
    // 現在會安全地回報「密碼不正確」而不是崩潰（見 Argon2KeyDerivation.VerifyPassword），
    // 但那句話會讓使用者以為自己打錯字，而不是根本沒輸入到。比照 encrypt 的既有處理。
    if (string.IsNullOrEmpty(password))
    {
        chatOut.WriteLine(CliLocalization.T("passwordEmpty"));
        Environment.Exit(CliExitCode.PartialOrTotalFailure);
        return;
    }

    chatOut.WriteLine(CliLocalization.T("decrypting"));

    await RunBatchCommandAsync(
        markerPaths,
        // 架構檢視後下移：.locked／.flocked 該用哪個方法解密，這個判斷本身收斂進
        // LockService.DecryptFileAsync（GUI 的 VaultProtocolHandlers.DecryptAsync 也呼叫
        // 同一個方法）——這裡以前手刻過一份一模一樣的判斷，漏改導致 .flocked 檔案跑
        // --unlock 曾經失敗，現在只有一份實作，不會再各自漏改。
        runItem: markerPath => WithSpinnerAsync(
            () => service.DecryptFileAsync(markerPath, password),
            CliLocalization.T("decrypting") + " " + Path.GetFileName(markerPath)),
        isSuccess: result => result.Success,
        toJson: (markerPath, result) => new
        {
            path = markerPath,
            success = result.Success,
            restoredPath = result.Success ? result.RestoredPath : null,
            errorCode = result.Success ? null : result.ErrorCode,
            errorMessage = result.Success ? null : CliLocalization.TranslateError(result.ErrorCode, result.ErrorDetail, result.ErrorMessage ?? ""),
        },
        printText: (markerPath, result) =>
        {
            if (markerPaths.Length > 1)
            {
                Console.WriteLine(markerPath);
            }
            PrintUnlockResult(result);
        });
}

/// <summary>
/// 第一個參數可以是 uuid，也可以是一顆 `.flocked` 檔案的路徑。
///
/// 接受路徑是「獨立可攜」這件事在 CLI 這邊的對應（見通盤檢討改善計畫第 2 輪）：`.flocked` v2
/// 把解密所需的驗證材料嵌在檔案本身，別人給你一顆檔案、或這台機器的集中管理區不在了，手上就
/// 只有這顆檔案，沒有任何紀錄可以先查出 uuid。GUI 端雙擊 `.flocked` 改用恢復金鑰解鎖走的是同
/// 一條路（PasswordPromptWindow），兩邊維持一致。
/// </summary>
async Task UnlockByRecoveryKeyCommandAsync(string uuidOrFlockedPath, string recoveryKey, string? destinationDir)
{
    chatOut.WriteLine(CliLocalization.T("decrypting"));

    // 傳進來的是 `.flocked` 路徑時，uuid 直接從檔頭讀（JSON 輸出的 uuid 欄位仍然是真正的 uuid，
    // 不會變成一個路徑字串），解密改走路徑式入口——那條路在集中管理區查不到紀錄時會改讀檔尾
    // 嵌入的驗證材料。傳 uuid 的既有用法完全不變。
    var isFlockedPath = string.Equals(Path.GetExtension(uuidOrFlockedPath), ".flocked", StringComparison.OrdinalIgnoreCase);
    var uuid = uuidOrFlockedPath;
    if (isFlockedPath)
    {
        if (!FlockedFileFormat.TryReadUuid(uuidOrFlockedPath, out var flockedUuid) || flockedUuid is null)
        {
            Console.Error.WriteLine(Red(CliLocalization.T("flockedFileNotFound", uuidOrFlockedPath)));
            Environment.ExitCode = (int)CliExitCode.PartialOrTotalFailure;
            return;
        }
        uuid = flockedUuid;
    }

    // 單一項目、不是清單——一樣走 RunBatchCommandAsync：items 只有一個元素時，批次摘要行
    // 本來就不會印（items.Count > 1 才印），CliExitCode.ForBatch(1,1)/(0,1) 換算出來
    // 也跟原本手寫的 result.Success ? Success : PartialOrTotalFailure 完全等價，不需要
    // 為了「只有一項」另外寫一份殼子。
    await RunBatchCommandAsync(
        [uuid],
        runItem: _ => WithSpinnerAsync(
            () => isFlockedPath
                ? service.DecryptFlockedFileByRecoveryKeyAsync(uuidOrFlockedPath, recoveryKey)
                : service.DecryptByRecoveryKeyAsync(uuid, recoveryKey, destinationDir),
            CliLocalization.T("decrypting")),
        isSuccess: result => result.Success,
        toJson: (u, result) => new
        {
            uuid = u,
            success = result.Success,
            restoredPath = result.Success ? result.RestoredPath : null,
            errorCode = result.Success ? null : result.ErrorCode,
            errorMessage = result.Success ? null : CliLocalization.TranslateError(result.ErrorCode, result.ErrorDetail, result.ErrorMessage ?? ""),
        },
        printText: (_, result) => PrintUnlockResult(result));
}

void PrintUnlockResult(UnlockResult result)
{
    if (result.Success)
    {
        Console.WriteLine(Green(CliLocalization.T("decryptSuccess")));
        Console.WriteLine(CliLocalization.T("restoredToLabel", result.RestoredPath));
    }
    else
    {
        Console.WriteLine(Red(CliLocalization.T("decryptFailed",
            CliLocalization.TranslateError(result.ErrorCode, result.ErrorDetail, result.ErrorMessage ?? ""))));
    }
}

void ListCommand()
{
    var entries = vault.ScanAll().ToList();

    if (jsonOutput)
    {
        // 只投影使用者/腳本用得到的欄位，不是把整個 LockedItemMetadata 直接序列化出去——
        // 那個型別上還帶著 Salt／PasswordVerificationHash／Argon2 參數這些密碼學內部細節，
        // 不該透過 --list 的輸出外流，跟 GUI 端 VaultListItemResponse 刻意只挑欄位投影
        // 給前端的理由一樣。
        Console.WriteLine(JsonSerializer.Serialize(
            entries.Select(entry => new
            {
                uuid = entry.Uuid,
                type = entry.Type.ToString(),
                originalName = entry.OriginalName,
                originalPath = entry.OriginalPath,
                storageMode = entry.StorageMode.ToString(),
                sizeBytes = entry.OriginalSizeBytes,
                createdAtUtc = entry.CreatedAtUtc,
                passkeyEnabled = entry.PasskeyEnabled,
                recoveryKeyEnabled = entry.RecoveryKeyEnabled,
                nestedLockUuids = entry.ContainsNestedLocks,
            }),
            jsonSerializerOptions));
        return;
    }

    if (entries.Count == 0)
    {
        Console.WriteLine(CliLocalization.T("vaultEmpty"));
        return;
    }

    foreach (var entry in entries)
    {
        Console.WriteLine($"{entry.Uuid}  [{entry.Type}]  {entry.OriginalName}");
        Console.WriteLine(CliLocalization.T("originalPathLabel", entry.OriginalPath));
        Console.WriteLine(CliLocalization.T("sizeCreatedLabel",
            FormatSize(entry.OriginalSizeBytes),
            entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        var nestedSuffix = entry.ContainsNestedLocks.Count > 0
            ? CliLocalization.T("nestedLockSuffix", entry.ContainsNestedLocks.Count)
            : "";
        Console.WriteLine(CliLocalization.T("passkeyRecoveryLabel",
            entry.PasskeyEnabled ? CliLocalization.T("yes") : CliLocalization.T("no"),
            entry.RecoveryKeyEnabled ? CliLocalization.T("yes") : CliLocalization.T("no"),
            nestedSuffix));
        Console.WriteLine();
    }
}

string FormatSize(long bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double size = bytes;
    var unitIndex = 0;
    while (size >= 1024 && unitIndex < units.Length - 1)
    {
        size /= 1024;
        unitIndex++;
    }
    return $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {units[unitIndex]}";
}

async Task DeleteCommandAsync(string[] uuids, CliOptions options)
{
    // --dry-run／-n：只預覽會刪掉哪些項目，不真的呼叫 TryDeleteRecordAsync，也不需要使用者
    // 確認（本來就沒有任何動作會發生）——比照 rsync -n／make -n 的既有慣例，永久刪除這種
    // 不可逆動作特別需要這種「先看會發生什麼事，再決定要不要真的下手」的預覽路徑。
    if (options.DryRunEnabled)
    {
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                uuids.Select(uuid =>
                {
                    var metadata = vault.LoadMetadata(uuid);
                    return new { uuid, wouldDelete = metadata is not null, originalName = metadata?.OriginalName };
                }),
                jsonSerializerOptions));
        }
        else
        {
            Console.WriteLine(CliLocalization.T("dryRunHeader"));
            foreach (var uuid in uuids)
            {
                var metadata = vault.LoadMetadata(uuid);
                Console.WriteLine(metadata is null
                    ? CliLocalization.T("recordNotFoundForUuid", uuid)
                    : CliLocalization.T("dryRunWouldDelete", uuid, metadata.OriginalName));
            }
        }
        Environment.Exit(CliExitCode.Success);
        return;
    }

    // 永久刪除在 GUI 端是 T3（要密碼、要關鍵操作驗證，見前端 protectionTiers.js 與通盤檢討
    // 改善計畫第 3 輪），CLI 這邊不設密碼那道門，只要一個 y／n 確認——不是漏做，是那道門在
    // 這個介面上擋不到任何人：能執行 CLI 的人本來就能直接開 Vault 資料夾把 {uuid}.enc 跟
    // {uuid}.meta.json 刪掉，效果完全一樣。要求輸入密碼只會擋到照規矩用的自己人，順便讓
    // 排程工作、遠端伺服器這些「無 GUI 環境可操作」的存在目的（見技術規格文件第 15 節）失效。
    if (!options.SkipConfirmation)
    {
        if (uuids.Length > 1)
        {
            chatOut.WriteLine(CliLocalization.T("deleteConfirmMultiple"));
            foreach (var id in uuids)
            {
                chatOut.WriteLine($"  {id}");
            }
            chatOut.Write(CliLocalization.T("yesNoPrompt"));
        }
        else
        {
            chatOut.Write(CliLocalization.T("deleteConfirmSingle", uuids[0]));
        }
        var confirm = (Console.ReadLine() ?? "").Trim();
        if (!confirm.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            chatOut.WriteLine(CliLocalization.T("cancelled"));
            Environment.Exit(CliExitCode.Cancelled);
            return;
        }
    }

    await RunBatchCommandAsync(
        uuids,
        // CLI 沒有 GUI 那層 VaultIndexCache（SQLite 加速索引），每次都是直接掃 .meta.json，
        // 沒有「快取殘留孤兒紀錄」這個問題可言——RecordNotFound 這裡就是單純的「查無此 uuid」，
        // 跟其他失敗情境走同一條 isSuccess=false 路徑（見 toJson/printText 各自的特別處理）。
        runItem: uuid => service.TryDeleteRecordAsync(uuid),
        isSuccess: result => result.Success,
        toJson: (uuid, result) => result.ErrorCode == ErrorCodes.RecordNotFound
            ? new
            {
                uuid, success = false, errorCode = (string?)ErrorCodes.RecordNotFound,
                errorMessage = (string?)CliLocalization.TranslateError(ErrorCodes.RecordNotFound, null, CliLocalization.T("recordNotFoundForUuid", uuid)),
                blockedByNestedLocks = false, nestedUuids = (IReadOnlyList<string>?)null,
            }
            : new
            {
                uuid,
                success = result.Success,
                errorCode = result.Success ? null : result.ErrorCode,
                errorMessage = result.Success ? null : CliLocalization.TranslateError(result.ErrorCode, null, result.ErrorMessage ?? ""),
                blockedByNestedLocks = result.BlockedByNestedLocks,
                nestedUuids = result.BlockedByNestedLocks ? result.NestedUuids : null,
            },
        printText: (uuid, result) =>
        {
            if (result.ErrorCode == ErrorCodes.RecordNotFound)
            {
                Console.WriteLine(Red(CliLocalization.T("recordNotFoundForUuid", uuid)));
            }
            else if (result.Success)
            {
                Console.WriteLine(Green(CliLocalization.T("deleteSuccess", uuid)));
            }
            else if (result.BlockedByNestedLocks)
            {
                Console.WriteLine(Red(CliLocalization.T("deleteFailedNested", uuid)));
                foreach (var nestedUuid in result.NestedUuids ?? [])
                {
                    Console.WriteLine($"  {nestedUuid}");
                }
            }
            else
            {
                Console.WriteLine(Red(CliLocalization.T("deleteFailed", uuid)));
                Console.WriteLine($"  {CliLocalization.TranslateError(result.ErrorCode, null, result.ErrorMessage ?? "")}");
            }
        });
}

// 主控台沒有內建的密碼遮罩輸入，自己用 Console.ReadKey 逐字元讀取，
// 顯示 * 取代實際字元，支援 Backspace 修改，Enter 結束輸入。
//
// Console.ReadKey 在標準輸入被重新導向時（腳本管線、排程工作丟進去的批次輸入）會直接丟例外，
// 不是回傳不正確的值——這正是「無 GUI 環境」預期會遇到的用法，所以這裡必須先偵測
// Console.IsInputRedirected，退回 Console.ReadLine()（沒有遮罩，但至少能動）。
string ReadPassword()
{
    if (Console.IsInputRedirected)
    {
        return Console.ReadLine() ?? "";
    }

    var password = new StringBuilder();
    ConsoleKeyInfo key;

    while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (password.Length > 0)
            {
                password.Remove(password.Length - 1, 1);
                Console.Write("\b \b");
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            password.Append(key.KeyChar);
            Console.Write("*");
        }
    }

    return password.ToString();
}

// --password-stdin／--password-file 共用同一套「拿到一行文字當密碼、不互動」的管線——呼叫端
// 只在 nonInteractive（其中一個旗標有值）時才呼叫這個方法，所以 PasswordFilePath 為 null
// 代表一定是走 stdin 這條路。跟既有的 ReadPassword() 是刻意分開的兩個方法：ReadPassword()
// 是「互動遮罩輸入，但輸入被重導向時順便還能動」，這裡是「明確、唯一讀一行，不做任何遮罩／
// 二次確認」，語意不同，共用同一個方法容易讓兩種情境的行為互相污染。
string ReadPasswordFromFlag(CliOptions options)
    => options.PasswordFromStdin
        ? Console.In.ReadLine() ?? ""
        : File.ReadLines(options.PasswordFilePath!).FirstOrDefault() ?? "";

void PrintCompletionScript(string shell)
{
    Console.WriteLine(CliShellCompletion.Generate(shell));
}

void PrintVersion()
{
    Console.WriteLine(CliLocalization.T("versionLabel", ReadVersion()));
}

/// <summary>
/// 跟 MainWindow.ReadInstalledVersion() 同一份資料來源（installer_config.json 的 "version"
/// 欄位），但路徑要多試一層——那個檔案放在安裝根目錄，CLI 本體在往下一層的 cli/ 子資料夾
/// （見技術規格文件 §19「CLI 打包」），AppContext.BaseDirectory 對 CLI 來說已經是 cli/ 這層，
/// 直接沿用 GUI 那份邏輯的相對路徑會找不到檔案。開發環境用 dotnet run 執行時兩個位置都不會
/// 有這個檔案，這是正常情況（不是安裝出來的），退回「開發版本」字樣，不是拋例外或印一個
/// 容易誤導使用者的假版本號。
/// </summary>
string ReadVersion()
{
    string[] candidates =
    [
        Path.Combine(AppContext.BaseDirectory, "installer_config.json"),
        Path.Combine(AppContext.BaseDirectory, "..", "installer_config.json"),
    ];

    foreach (var path in candidates)
    {
        if (!File.Exists(path))
        {
            continue;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("version", out var versionProp) && versionProp.GetString() is { } version)
            {
                return version;
            }
        }
        catch (JsonException)
        {
            // 損毀的設定檔跟「檔案不存在」用同一套退回行為，不特別區分——這裡只是想顯示
            // 版本號，不是安裝完整性檢查，沒必要為了這個目的另外設計錯誤回報。
        }
    }

    return CliLocalization.T("versionDev");
}

void PrintUsage()
{
    Console.WriteLine(CliLocalization.T("usageHeader"));
    Console.WriteLine(CliLocalization.T("usageEncrypt"));
    Console.WriteLine(CliLocalization.T("usageUnlock"));
    Console.WriteLine(CliLocalization.T("usageUnlockRecovery"));
    Console.WriteLine(CliLocalization.T("usageList"));
    Console.WriteLine(CliLocalization.T("usageDelete"));
    Console.WriteLine(CliLocalization.T("usageCompletion"));
    Console.WriteLine();
    Console.WriteLine(CliLocalization.T("usageLegacyFlagNote"));
    Console.WriteLine(CliLocalization.T("usageBatchNote"));
    Console.WriteLine(CliLocalization.T("usageVaultPathNote"));
    Console.WriteLine(CliLocalization.T("usageLangNote"));
    Console.WriteLine(CliLocalization.T("usageOutputNote"));
    Console.WriteLine();
    Console.WriteLine(CliLocalization.T("usageSilentModeHeader"));
    Console.WriteLine(CliLocalization.T("usagePasswordStdin"));
    Console.WriteLine(CliLocalization.T("usagePasswordFile"));
    Console.WriteLine(CliLocalization.T("usageRecoveryKey"));
    Console.WriteLine(CliLocalization.T("usageHint"));
    Console.WriteLine(CliLocalization.T("usageYes"));
    Console.WriteLine(CliLocalization.T("usageDryRun"));
    Console.WriteLine(CliLocalization.T("usageStandalone"));
    Console.WriteLine(CliLocalization.T("usageDestination"));
    Console.WriteLine(CliLocalization.T("usageHelp"));
    Console.WriteLine(CliLocalization.T("usageVersion"));
    Console.WriteLine();
    Console.WriteLine(CliLocalization.T("usageExitCodes"));
}
