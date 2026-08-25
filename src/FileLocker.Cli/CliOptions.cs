namespace FileLocker.Cli;

/// <summary>某個命令列呼叫解析後的所有靜默批次模式旗標——純資料，跟 Console/檔案系統/
/// LockService 完全無關，方便單元測試（見 CliArgumentParserTests）。Program.cs 只負責
/// 「把解析結果接到 Console.Write/ReadPassword/service 呼叫」，決策邏輯全部在這裡。</summary>
public sealed record CliOptions(
    bool PasswordFromStdin,
    string? PasswordFilePath,
    bool RecoveryKeyEnabled,
    string? Hint,
    bool SkipConfirmation,
    bool StandaloneEnabled,
    string? DestinationDir,
    bool DryRunEnabled);

/// <summary>旗標解析失敗（缺值、旗標互斥）時丟出，Program.cs 接住後印訊息＋PrintUsage()，
/// 用既有的 CliExitCode.UsageError 結束——不是未預期的例外，是使用者輸入錯誤的正常回報方式。</summary>
public sealed class CliArgumentException(string message) : Exception(message);

public static class CliArgumentParser
{
    private const string PasswordStdinFlag = "--password-stdin";
    private const string PasswordFileFlag = "--password-file";
    private const string RecoveryKeyFlag = "--recovery-key";
    private const string HintFlag = "--hint";
    private const string YesFlag = "--yes";
    private const string StandaloneFlag = "--standalone";
    private const string DestinationFlag = "--destination";
    private const string DryRunFlag = "--dry-run";
    private const string DryRunShortFlag = "-n";
    private const string YesShortFlag = "-y";

    /// <summary>從 --encrypt/--unlock/--delete 之後的參數區段解析出 CliOptions，同時把剩下
    /// 不是旗標的參數（路徑/uuid 列表）分離出來回傳，保留原始順序。旗標順序不拘、可以跟位置
    /// 參數混雜——用具名旗標而不是位置參數，避免未來新增旗標時位置參數的順序爆炸。</summary>
    public static (CliOptions Options, List<string> RemainingArgs) Parse(IReadOnlyList<string> args)
    {
        var passwordFromStdin = false;
        string? passwordFilePath = null;
        var recoveryKeyEnabled = false;
        string? hint = null;
        var skipConfirmation = false;
        var standaloneEnabled = false;
        string? destinationDir = null;
        var dryRunEnabled = false;
        var remaining = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case PasswordStdinFlag:
                    passwordFromStdin = true;
                    break;
                case PasswordFileFlag:
                    passwordFilePath = RequireValue(args, ref i, PasswordFileFlag);
                    break;
                case RecoveryKeyFlag:
                    recoveryKeyEnabled = true;
                    break;
                case HintFlag:
                    hint = RequireValue(args, ref i, HintFlag);
                    break;
                case YesFlag:
                case YesShortFlag:
                    skipConfirmation = true;
                    break;
                case StandaloneFlag:
                    standaloneEnabled = true;
                    break;
                case DestinationFlag:
                    destinationDir = RequireValue(args, ref i, DestinationFlag);
                    break;
                case DryRunFlag:
                case DryRunShortFlag:
                    dryRunEnabled = true;
                    break;
                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

        if (passwordFromStdin && passwordFilePath is not null)
        {
            throw new CliArgumentException($"{PasswordStdinFlag} 跟 {PasswordFileFlag} 不能同時使用，請只選一種密碼來源。");
        }

        // --destination 只在「不進 Vault」的獨立加密下才有意義（Vault 模式的存放位置本來就是
        // 固定的），單獨出現視為使用者輸入錯誤，比照上面密碼旗標互斥檢查的做法擋下來。
        if (destinationDir is not null && !standaloneEnabled)
        {
            throw new CliArgumentException($"{DestinationFlag} 只能搭配 {StandaloneFlag} 使用。");
        }

        var options = new CliOptions(
            passwordFromStdin, passwordFilePath, recoveryKeyEnabled, hint, skipConfirmation,
            standaloneEnabled, destinationDir, dryRunEnabled);
        return (options, remaining);
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int i, string flagName)
    {
        if (i + 1 >= args.Count)
        {
            throw new CliArgumentException($"{flagName} 後面必須接一個值。");
        }
        i++;
        return args[i];
    }

    private const string LangFlag = "--lang";
    private const string OutputFlag = "--output";
    private const string OutputShortFlag = "-o";

    /// <summary>
    /// --lang／--output 是僅有的兩個「全域」旗標（CLI 英文化＋機器可讀輸出）——跟 --hint／
    /// --recovery-key 這類只在特定指令（--encrypt 等）下才有意義的旗標不同，連 Program.cs
    /// 最開頭「沒帶任何指令，直接印用法說明」那條路徑都要吃 --lang（使用者可能只是想看英文版
    /// 的用法說明）。因為這樣，不能沿用上面 Parse()——那個方法只解析「某個指令之後」的參數
    /// 區段，這裡改成從最原始、完整的 args 陣列（args[0] 就是指令本身，也在掃描範圍內）
    /// 一次性把這兩個旗標連同各自的值抽掉，抽完剩下的 args 陣列原封不動交給既有的指令分派／
    /// Parse() 邏輯，彼此互不影響、各自處理自己那一份參數。
    /// </summary>
    public static (string? Lang, string? Output, string[] RemainingArgs) ExtractGlobalFlags(IReadOnlyList<string> args)
    {
        string? lang = null;
        string? output = null;
        var remaining = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == LangFlag)
            {
                lang = RequireValue(args, ref i, LangFlag);
            }
            else if (args[i] == OutputFlag || args[i] == OutputShortFlag)
            {
                output = RequireValue(args, ref i, OutputFlag);
            }
            else
            {
                remaining.Add(args[i]);
            }
        }

        return (lang, output, remaining.ToArray());
    }
}

/// <summary>
/// --output/-o 的解析結果——預設 Text（現有的人類可讀輸出，逐字不變），Json 讓 --list／
/// --encrypt／--unlock／--unlock-recovery／--delete 改印一份結構化 JSON 到 stdout，方便腳本
/// 直接 parse，不用土法煉鋼剖析人類可讀文字。JSON 模式下 stdout 只會有這一份 JSON 文件，
/// 其餘資訊性文字（Vault 位置、互動提示等）改印到 stderr，保持 stdout 是乾淨、單一的 JSON，
/// 這是主流工具（docker/kubectl/gh 的 -o json）的既有慣例，不是這個專案自己發明的規則。
/// Resolve() 獨立放在 CliOutputFormatParser（不是這個 enum 本身——C# enum 不能有靜態方法成員）。
/// </summary>
public enum CliOutputFormat
{
    Text,
    Json
}

public static class CliOutputFormatParser
{
    public static CliOutputFormat Resolve(string? flagValue) => flagValue switch
    {
        null => CliOutputFormat.Text,
        "text" => CliOutputFormat.Text,
        "json" => CliOutputFormat.Json,
        _ => throw new CliArgumentException($"不支援的輸出格式／Unsupported output format：{flagValue}（可用值／available values：text、json）")
    };
}

/// <summary>
/// 新的子命令寫法（encrypt/unlock/unlock-recovery/list/delete，跟 git/docker/kubectl/gh 的
/// 「動詞當子命令」慣例看齊）跟舊的「旗標當動詞」寫法（--encrypt 等）並存——這裡只負責把
/// 兩種寫法都換算成內部沿用的舊 --xxx canonical 形式（Program.cs 的 switch 完全不用改），
/// 同時標記出「這次呼叫用的是不是舊寫法」，讓呼叫端決定要不要印過時提醒（純判斷邏輯跟印
/// 到 stderr 的副作用分開，才能不靠真的執行整支程式就測完這個決策）。
/// </summary>
public static class CliCommandNormalizer
{
    private static readonly Dictionary<string, string> SubcommandToLegacyFlag = new()
    {
        ["encrypt"] = "--encrypt",
        ["unlock"] = "--unlock",
        ["unlock-recovery"] = "--unlock-recovery",
        ["list"] = "--list",
        ["delete"] = "--delete",
    };

    public static (string Canonical, bool IsLegacyForm, string? RecommendedForm) Normalize(string raw)
    {
        if (SubcommandToLegacyFlag.TryGetValue(raw, out var canonical))
        {
            return (canonical, false, null);
        }

        if (SubcommandToLegacyFlag.ContainsValue(raw))
        {
            return (raw, true, raw.TrimStart('-'));
        }

        return (raw, false, null);
    }
}

/// <summary>批次執行結果 → 對外行程結束碼的唯一決策點，Program.cs 跟測試都呼叫同一個方法，
/// 保證行為一致。</summary>
public static class CliExitCode
{
    public const int Success = 0;
    public const int UsageError = 1;
    // 批次中至少一筆失敗（含全部失敗）——刻意不細分「部分失敗」跟「全部失敗」，腳本幾乎都只看
    // exit code 是否非 0，細分沒有實際需求。
    public const int PartialOrTotalFailure = 2;
    // 使用者/腳本主動取消（例如刪除確認被拒絕、沒帶 --yes）：這跟「有嘗試但失敗」意義不同，
    // 腳本可能想分開處理「我沒做」跟「我做了但失敗」，所以獨立一個代碼。
    public const int Cancelled = 3;

    public static int ForBatch(int successCount, int totalCount)
        => successCount == totalCount ? Success : PartialOrTotalFailure;
}
