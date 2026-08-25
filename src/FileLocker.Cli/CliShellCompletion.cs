namespace FileLocker.Cli;

/// <summary>
/// `FileLocker.Cli completion <bash|zsh|pwsh>` 印出來的自動完成腳本產生器——跟主流 CLI 工具
/// （kubectl/gh/docker 的 `completion` 子命令）同一個使用模式：使用者自己把印出來的內容
/// source 進 shell 設定檔（bash：`source &lt;(FileLocker.Cli completion bash)`，或寫進
/// `/etc/bash_completion.d/`；pwsh：加進 `$PROFILE`）。這裡只做「子命令／旗標名稱」這一層
/// 靜態補全，不做「幫你補 UUID／檔案路徑」這種動態補全——檔案路徑本來就有 shell 內建的補全，
/// UUID 需要即時查 Vault 才補得出來，複雜度／效益不成比例，不在這輪範圍內。
///
/// 樣板刻意不用 C# 字串插值（$"""..."""）——bash/pwsh 腳本本身滿滿都是 ${...}／$_ 這種語法，
/// 跟 C# 插值的 { } 撞在一起會整段解析錯誤，改用純逐字字串 + Replace() 塞動態內容進去。
/// </summary>
public static class CliShellCompletion
{
    public static readonly string[] Subcommands = ["encrypt", "unlock", "unlock-recovery", "list", "delete", "completion"];

    public static readonly string[] Flags =
    [
        "--lang", "--output", "-o", "-h", "--help", "--version",
        "--password-stdin", "--password-file", "--recovery-key", "--hint",
        "--yes", "-y", "--standalone", "--destination", "--dry-run", "-n",
    ];

    public static string Generate(string shell) => shell switch
    {
        "bash" => GenerateBash(),
        "zsh" => GenerateZsh(),
        "pwsh" or "powershell" => GeneratePwsh(),
        _ => throw new CliArgumentException($"不支援的 shell／Unsupported shell：{shell}（可用值／available values：bash、zsh、pwsh）")
    };

    private const string BashTemplate = """
        # FileLocker.Cli bash 自動完成——把這行加進 ~/.bashrc（或存成檔案放進
        # /etc/bash_completion.d/）：source <(FileLocker.Cli completion bash)
        _filelocker_cli_complete() {
            local cur="${COMP_WORDS[COMP_CWORD]}"
            COMPREPLY=($(compgen -W "__WORDS__" -- "$cur"))
        }
        complete -F _filelocker_cli_complete FileLocker.Cli
        """;

    private const string ZshTemplate = """
        #compdef FileLocker.Cli
        # FileLocker.Cli zsh 自動完成——把這行加進 ~/.zshrc：source <(FileLocker.Cli completion zsh)
        _filelocker_cli() {
            local -a opts
            opts=(__WORDS__)
            _describe 'command' opts
        }
        _filelocker_cli
        """;

    private const string PwshTemplate = """
        # FileLocker.Cli PowerShell 自動完成——把這段加進 $PROFILE：
        # FileLocker.Cli completion pwsh | Out-String | Invoke-Expression
        Register-ArgumentCompleter -Native -CommandName FileLocker.Cli -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $candidates = @(__WORDS__)
            $candidates | Where-Object { $_ -like "$wordToComplete*" } | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;

    private static string GenerateBash()
        => BashTemplate.Replace("__WORDS__", string.Join(" ", Subcommands.Concat(Flags)));

    private static string GenerateZsh()
        => ZshTemplate.Replace("__WORDS__", string.Join(" ", Subcommands.Concat(Flags)));

    private static string GeneratePwsh()
        => PwshTemplate.Replace("__WORDS__", string.Join(",", Subcommands.Concat(Flags).Select(w => $"'{w}'")));
}
