using FileLocker.Cli;

namespace FileLocker.Cli.Tests;

/// <summary>
/// CliLocalization 是 CLI 英文化的核心：語言判斷（--lang 旗標優先，沒帶就跟著系統語言走）
/// 跟訊息查表（純資料進、純資料出）都不需要真的印到 Console 就能測完整。
/// ResolveLanguage 刻意接受一個可選的 systemTwoLetterIso 參數，不直接讀
/// CultureInfo.CurrentUICulture——那是行程層級的可變狀態，xUnit 測試方法可能被排到同一個
/// 執行緒重複使用，直接在測試裡改它會有跨測試互相污染的風險，用參數注入取代真的去動系統文化。
/// </summary>
public class CliLocalizationTests
{
    [Fact]
    public void ResolveLanguage_ExplicitLangFlagZhTw_ReturnsZhTw()
    {
        var result = CliLocalization.ResolveLanguage("zh-TW", systemTwoLetterIso: "en");
        Assert.Equal(CliLanguage.ZhTw, result);
    }

    [Fact]
    public void ResolveLanguage_ExplicitLangFlagEn_ReturnsEn()
    {
        var result = CliLocalization.ResolveLanguage("en", systemTwoLetterIso: "zh");
        Assert.Equal(CliLanguage.En, result);
    }

    [Fact]
    public void ResolveLanguage_InvalidLangFlag_ThrowsUsageError()
    {
        Assert.Throws<CliArgumentException>(() => CliLocalization.ResolveLanguage("fr", systemTwoLetterIso: "en"));
    }

    [Fact]
    public void ResolveLanguage_NoFlag_ChineseSystemCulture_FollowsSystemAndReturnsZhTw()
    {
        var result = CliLocalization.ResolveLanguage(null, systemTwoLetterIso: "zh");
        Assert.Equal(CliLanguage.ZhTw, result);
    }

    [Fact]
    public void ResolveLanguage_NoFlag_NonChineseSystemCulture_FollowsSystemAndReturnsEn()
    {
        var result = CliLocalization.ResolveLanguage(null, systemTwoLetterIso: "ja");
        Assert.Equal(CliLanguage.En, result);
    }

    [Fact]
    public void T_KnownKey_ReturnsTextInCurrentLanguage()
    {
        CliLocalization.SetLanguage(CliLanguage.En);
        try
        {
            Assert.Equal("Encrypting...", CliLocalization.T("encrypting"));
        }
        finally
        {
            CliLocalization.SetLanguage(CliLanguage.ZhTw);
        }
    }

    [Fact]
    public void T_KnownKey_SwitchingLanguageChangesResult()
    {
        CliLocalization.SetLanguage(CliLanguage.ZhTw);
        var zhText = CliLocalization.T("encrypting");
        CliLocalization.SetLanguage(CliLanguage.En);
        var enText = CliLocalization.T("encrypting");
        CliLocalization.SetLanguage(CliLanguage.ZhTw);

        Assert.NotEqual(zhText, enText);
    }

    [Fact]
    public void T_KeyWithPlaceholder_InterpolatesArgument()
    {
        CliLocalization.SetLanguage(CliLanguage.En);
        try
        {
            var text = CliLocalization.T("encryptSuccess", "C:\\a.txt");
            Assert.Contains("C:\\a.txt", text);
        }
        finally
        {
            CliLocalization.SetLanguage(CliLanguage.ZhTw);
        }
    }

    [Fact]
    public void AllZhTwKeys_ExistInEnglishDictionary_AndViceVersa()
    {
        // 防呆：兩份語言字典的 key 集合一定要完全一樣，新增訊息時很容易漏掉補另一份語言，
        // 這個測試在漏補的當下就會紅燈，不用等實際跑到那個訊息才發現英文版缺一句。
        var (missingFromEn, missingFromZhTw) = CliLocalization.DiffKeysForTest();
        Assert.True(missingFromEn.Count == 0, $"英文字典缺少這些 key：{string.Join(", ", missingFromEn)}");
        Assert.True(missingFromZhTw.Count == 0, $"中文字典缺少這些 key：{string.Join(", ", missingFromZhTw)}");
    }

    [Fact]
    public void TranslateError_KnownCode_ReturnsLocalizedTemplate_NotRawFallback()
    {
        CliLocalization.SetLanguage(CliLanguage.En);
        try
        {
            var text = CliLocalization.TranslateError(FileLocker.Core.Models.ErrorCodes.PasswordIncorrect, null, "密碼錯誤");
            Assert.NotEqual("密碼錯誤", text);
        }
        finally
        {
            CliLocalization.SetLanguage(CliLanguage.ZhTw);
        }
    }

    [Fact]
    public void TranslateError_UnknownCode_FallsBackToProvidedMessage()
    {
        var text = CliLocalization.TranslateError("SOME_CODE_THAT_DOES_NOT_EXIST", null, "原始的中文錯誤訊息");
        Assert.Equal("原始的中文錯誤訊息", text);
    }

    [Fact]
    public void TranslateError_NullCode_FallsBackToProvidedMessage()
    {
        var text = CliLocalization.TranslateError(null, null, "原始的中文錯誤訊息");
        Assert.Equal("原始的中文錯誤訊息", text);
    }
}
