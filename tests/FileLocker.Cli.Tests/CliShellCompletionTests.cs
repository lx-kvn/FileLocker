using FileLocker.Cli;

namespace FileLocker.Cli.Tests;

/// <summary>
/// CliShellCompletion.Generate 純粹是字串樣板產生，不牽涉真的執行任何 shell——這裡驗證
/// 產生出來的內容有沒有涵蓋子命令／旗標清單，跟未知 shell 會不會正確報參數錯誤。
/// 真的語法對不對（bash -n 之類）留給人工驗證，單元測試不依賴本機一定裝了 bash/zsh/pwsh。
/// </summary>
public class CliShellCompletionTests
{
    [Fact]
    public void Generate_Bash_ContainsCompleteRegistrationAndAllSubcommands()
    {
        var script = CliShellCompletion.Generate("bash");

        Assert.Contains("complete -F", script);
        Assert.Contains("FileLocker.Cli", script);
        foreach (var subcommand in CliShellCompletion.Subcommands)
        {
            Assert.Contains(subcommand, script);
        }
    }

    [Fact]
    public void Generate_Zsh_ContainsCompdefAndAllSubcommands()
    {
        var script = CliShellCompletion.Generate("zsh");

        Assert.Contains("#compdef FileLocker.Cli", script);
        foreach (var subcommand in CliShellCompletion.Subcommands)
        {
            Assert.Contains(subcommand, script);
        }
    }

    [Fact]
    public void Generate_Pwsh_ContainsRegisterArgumentCompleterAndAllSubcommands()
    {
        var script = CliShellCompletion.Generate("pwsh");

        Assert.Contains("Register-ArgumentCompleter", script);
        foreach (var subcommand in CliShellCompletion.Subcommands)
        {
            Assert.Contains($"'{subcommand}'", script);
        }
    }

    [Fact]
    public void Generate_PowershellAlias_SameAsPwsh()
    {
        Assert.Equal(CliShellCompletion.Generate("pwsh"), CliShellCompletion.Generate("powershell"));
    }

    [Fact]
    public void Generate_UnknownShell_ThrowsUsageError()
    {
        Assert.Throws<CliArgumentException>(() => CliShellCompletion.Generate("fish"));
    }

    [Fact]
    public void AllGeneratedScripts_MentionEveryFlag()
    {
        foreach (var shell in new[] { "bash", "zsh", "pwsh" })
        {
            var script = CliShellCompletion.Generate(shell);
            foreach (var flag in CliShellCompletion.Flags)
            {
                Assert.True(script.Contains(flag), $"{shell} 腳本裡缺少旗標 {flag}");
            }
        }
    }
}
