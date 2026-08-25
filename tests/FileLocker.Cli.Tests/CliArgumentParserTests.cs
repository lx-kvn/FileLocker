using FileLocker.Cli;

namespace FileLocker.Cli.Tests;

/// <summary>
/// CliArgumentParser.Parse 是 CLI 靜默批次模式的唯一決策點——把「--password-stdin 這種旗標
/// 出現了沒有」跟「Console/檔案系統/LockService 怎麼用密碼」這兩件事分開，純資料進、純資料出，
/// 不需要真的去讀 Console 或建立 LockService 就能測完全部分支。
/// </summary>
public class CliArgumentParserTests
{
    [Fact]
    public void Parse_PasswordStdinFlag_SetsPasswordFromStdinTrue()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt", "--password-stdin"]);

        Assert.True(options.PasswordFromStdin);
    }

    [Fact]
    public void Parse_PasswordFileFlag_SetsPasswordFilePath()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt", "--password-file", "pw.txt"]);

        Assert.Equal("pw.txt", options.PasswordFilePath);
    }

    [Fact]
    public void Parse_NoPasswordFlags_BothStdinAndFileAreDefaultOff()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt"]);

        Assert.False(options.PasswordFromStdin);
        Assert.Null(options.PasswordFilePath);
    }

    [Fact]
    public void Parse_RecoveryKeyFlag_SetsRecoveryKeyEnabledTrue_DefaultIsFalse()
    {
        var (withFlag, _) = CliArgumentParser.Parse(["file.txt", "--recovery-key"]);
        var (withoutFlag, _) = CliArgumentParser.Parse(["file.txt"]);

        Assert.True(withFlag.RecoveryKeyEnabled);
        Assert.False(withoutFlag.RecoveryKeyEnabled);
    }

    [Fact]
    public void Parse_HintFlag_CapturesFollowingValue()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt", "--hint", "my hint"]);

        Assert.Equal("my hint", options.Hint);
    }

    [Fact]
    public void Parse_HintFlag_MissingValue_ThrowsUsageError()
    {
        Assert.Throws<CliArgumentException>(() => CliArgumentParser.Parse(["file.txt", "--hint"]));
    }

    [Fact]
    public void Parse_YesFlag_SetsSkipConfirmationTrue()
    {
        var (options, _) = CliArgumentParser.Parse(["uuid1", "--yes"]);

        Assert.True(options.SkipConfirmation);
    }

    [Fact]
    public void Parse_FlagsMixedWithPositionalPaths_SeparatesFlagsFromRemainingArgs()
    {
        var (options, remaining) = CliArgumentParser.Parse(
            ["file1.txt", "--password-stdin", "--recovery-key", "file2.txt"]);

        Assert.True(options.PasswordFromStdin);
        Assert.True(options.RecoveryKeyEnabled);
        Assert.Equal(["file1.txt", "file2.txt"], remaining);
    }

    [Fact]
    public void Parse_PasswordStdinAndPasswordFileBothPresent_IsRejectedAsConflicting()
    {
        Assert.Throws<CliArgumentException>(() =>
            CliArgumentParser.Parse(["file.txt", "--password-stdin", "--password-file", "pw.txt"]));
    }

    [Fact]
    public void Parse_StandaloneFlag_SetsStandaloneEnabledTrue_DefaultIsFalse()
    {
        var (withFlag, _) = CliArgumentParser.Parse(["file.txt", "--standalone"]);
        var (withoutFlag, _) = CliArgumentParser.Parse(["file.txt"]);

        Assert.True(withFlag.StandaloneEnabled);
        Assert.False(withoutFlag.StandaloneEnabled);
    }

    [Fact]
    public void Parse_DestinationFlag_WithStandalone_CapturesFollowingValue()
    {
        var (options, _) = CliArgumentParser.Parse(["file.txt", "--standalone", "--destination", "D:\\out"]);

        Assert.Equal("D:\\out", options.DestinationDir);
    }

    [Fact]
    public void Parse_DestinationFlag_MissingValue_ThrowsUsageError()
    {
        Assert.Throws<CliArgumentException>(() => CliArgumentParser.Parse(["file.txt", "--standalone", "--destination"]));
    }

    [Fact]
    public void Parse_DestinationFlag_WithoutStandalone_IsRejectedAsMeaningless()
    {
        // --destination 只有搭配 --standalone 才有意義（原地取代或指定他處都只對「不進 Vault」
        // 的獨立加密有意義，Vault 模式的存放位置本來就是固定的）——單獨出現是使用者輸入錯誤，
        // 比照 --password-stdin/--password-file 互斥檢查的做法，一律當參數錯誤擋下來。
        Assert.Throws<CliArgumentException>(() =>
            CliArgumentParser.Parse(["file.txt", "--destination", "D:\\out"]));
    }

    // ---- --lang 是全域旗標（CLI 英文化），不像 --hint/--recovery-key 那樣限定某個指令才有意義——
    // 連沒帶任何指令時印出的 PrintUsage() 都要吃它，所以不能用上面 Parse()（只解析某個指令
    // 之後的參數區段）處理，改成從最原始、完整的 args 陣列一次性抽掉，抽完剩下的 args 才交給
    // 既有的指令分派／Parse() 邏輯，兩者互不影響。 ----

    [Fact]
    public void ExtractGlobalFlags_LangFlagAtStart_ExtractsValueAndRemovesFromRemaining()
    {
        var (lang, _, remaining) = CliArgumentParser.ExtractGlobalFlags(["--lang", "en", "--encrypt", "file.txt"]);

        Assert.Equal("en", lang);
        Assert.Equal(["--encrypt", "file.txt"], remaining);
    }

    [Fact]
    public void ExtractGlobalFlags_LangFlagInMiddle_ExtractsValueRegardlessOfPosition()
    {
        var (lang, _, remaining) = CliArgumentParser.ExtractGlobalFlags(["--encrypt", "file.txt", "--lang", "zh-TW"]);

        Assert.Equal("zh-TW", lang);
        Assert.Equal(["--encrypt", "file.txt"], remaining);
    }

    [Fact]
    public void ExtractGlobalFlags_NoLangFlag_ReturnsNullAndOriginalArgsUnchanged()
    {
        var (lang, _, remaining) = CliArgumentParser.ExtractGlobalFlags(["--encrypt", "file.txt"]);

        Assert.Null(lang);
        Assert.Equal(["--encrypt", "file.txt"], remaining);
    }

    [Fact]
    public void ExtractGlobalFlags_LangFlagMissingValue_ThrowsUsageError()
    {
        Assert.Throws<CliArgumentException>(() => CliArgumentParser.ExtractGlobalFlags(["--lang"]));
    }

    [Fact]
    public void ExtractGlobalFlags_EmptyArgs_ReturnsNullAndEmptyRemaining()
    {
        var (lang, _, remaining) = CliArgumentParser.ExtractGlobalFlags([]);

        Assert.Null(lang);
        Assert.Empty(remaining);
    }

    // ---- --output/-o（跟 --lang 一樣是全域旗標）：讓 --list/--encrypt/--unlock/--delete 可以
    // 吐結構化 JSON，給腳本／自動化用，不用土法煉鋼剖析人類可讀輸出。 ----

    [Fact]
    public void ExtractGlobalFlags_OutputFlagAnywhere_ExtractsValue()
    {
        var (_, output, remaining) = CliArgumentParser.ExtractGlobalFlags(["--list", "--output", "json"]);

        Assert.Equal("json", output);
        Assert.Equal(["--list"], remaining);
    }

    [Fact]
    public void ExtractGlobalFlags_OutputShortAlias_SameAsLongForm()
    {
        var (_, output, remaining) = CliArgumentParser.ExtractGlobalFlags(["-o", "json", "--list"]);

        Assert.Equal("json", output);
        Assert.Equal(["--list"], remaining);
    }

    [Fact]
    public void ExtractGlobalFlags_NoOutputFlag_ReturnsNull()
    {
        var (_, output, _) = CliArgumentParser.ExtractGlobalFlags(["--list"]);
        Assert.Null(output);
    }

    [Fact]
    public void ExtractGlobalFlags_LangAndOutputTogether_BothExtracted()
    {
        var (lang, output, remaining) = CliArgumentParser.ExtractGlobalFlags(["--lang", "en", "--list", "-o", "json"]);

        Assert.Equal("en", lang);
        Assert.Equal("json", output);
        Assert.Equal(["--list"], remaining);
    }

    [Fact]
    public void ResolveOutputFormat_NullFlag_DefaultsToText()
    {
        Assert.Equal(CliOutputFormat.Text, CliOutputFormatParser.Resolve(null));
    }

    [Fact]
    public void ResolveOutputFormat_Text_ReturnsText()
    {
        Assert.Equal(CliOutputFormat.Text, CliOutputFormatParser.Resolve("text"));
    }

    [Fact]
    public void ResolveOutputFormat_Json_ReturnsJson()
    {
        Assert.Equal(CliOutputFormat.Json, CliOutputFormatParser.Resolve("json"));
    }

    [Fact]
    public void ResolveOutputFormat_InvalidValue_ThrowsUsageError()
    {
        Assert.Throws<CliArgumentException>(() => CliOutputFormatParser.Resolve("xml"));
    }

    // ---- --dry-run（--delete 適用）跟短旗標別名：CLI 體驗改善的一部分，比照主流 CLI 工具
    // （rsync/make 的 -n、apt/npm 的 -y）的既有慣例挑短旗標，不是自己發明。 ----

    [Fact]
    public void Parse_DryRunFlag_SetsDryRunEnabledTrue_DefaultIsFalse()
    {
        var (withFlag, _) = CliArgumentParser.Parse(["uuid1", "--dry-run"]);
        var (withoutFlag, _) = CliArgumentParser.Parse(["uuid1"]);

        Assert.True(withFlag.DryRunEnabled);
        Assert.False(withoutFlag.DryRunEnabled);
    }

    [Fact]
    public void Parse_DryRunShortAlias_SameAsLongForm()
    {
        var (options, _) = CliArgumentParser.Parse(["uuid1", "-n"]);
        Assert.True(options.DryRunEnabled);
    }

    [Fact]
    public void Parse_YesShortAlias_SameAsLongForm()
    {
        var (options, _) = CliArgumentParser.Parse(["uuid1", "-y"]);
        Assert.True(options.SkipConfirmation);
    }

    // ---- 子命令（encrypt/unlock/...）跟舊的 --xxx 旗標寫法並存：新寫法是主推、舊寫法完整
    // 支援但用到時要能標記出「這是舊寫法」讓呼叫端決定要不要印過時提醒。 ----

    [Theory]
    [InlineData("encrypt", "--encrypt")]
    [InlineData("unlock", "--unlock")]
    [InlineData("unlock-recovery", "--unlock-recovery")]
    [InlineData("list", "--list")]
    [InlineData("delete", "--delete")]
    public void Normalize_NewSubcommandForm_MapsToLegacyFlagForm_NotFlaggedAsLegacy(string subcommand, string expectedCanonical)
    {
        var (canonical, isLegacyForm, recommendedForm) = CliCommandNormalizer.Normalize(subcommand);

        Assert.Equal(expectedCanonical, canonical);
        Assert.False(isLegacyForm);
        Assert.Null(recommendedForm);
    }

    [Theory]
    [InlineData("--encrypt", "encrypt")]
    [InlineData("--unlock", "unlock")]
    [InlineData("--unlock-recovery", "unlock-recovery")]
    [InlineData("--list", "list")]
    [InlineData("--delete", "delete")]
    public void Normalize_OldFlagForm_PassesThroughUnchanged_ButFlaggedAsLegacyWithRecommendedForm(string legacyFlag, string expectedRecommendation)
    {
        var (canonical, isLegacyForm, recommendedForm) = CliCommandNormalizer.Normalize(legacyFlag);

        Assert.Equal(legacyFlag, canonical);
        Assert.True(isLegacyForm);
        Assert.Equal(expectedRecommendation, recommendedForm);
    }

    [Fact]
    public void Normalize_UnrecognizedCommand_PassesThroughUnchanged_NotFlaggedAsLegacy()
    {
        var (canonical, isLegacyForm, recommendedForm) = CliCommandNormalizer.Normalize("--totally-not-a-command");

        Assert.Equal("--totally-not-a-command", canonical);
        Assert.False(isLegacyForm);
        Assert.Null(recommendedForm);
    }
}
