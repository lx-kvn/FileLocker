using FileLocker.Core.Models;
using FileLocker.Core.PasswordLocker;
using FileLocker.Core.Security;
using Xunit;

namespace FileLocker.Core.Tests.PasswordLocker;

/// <summary>
/// 只測密碼與恢復金鑰路徑——Passkey 相關方法牽涉真的 Windows Hello 硬體互動，跟
/// PasskeyProtectorTests／FolderGuardServiceTests 同樣的限制，沒辦法自動化測試。
/// </summary>
public class PasswordLockerServiceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly PasswordLockerService _service;

    public PasswordLockerServiceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerPasswordLockerServiceTests_");
        var store = new PasswordLockerStore(Path.Combine(_tempDir.FullName, "credentials.json"));
        var lockoutTracker = new LockoutTracker(Path.Combine(_tempDir.FullName, "lockout.json"));
        _service = new PasswordLockerService(store, lockoutTracker);
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    // ---- 設定與驗證（密碼路徑）----

    [Fact]
    public void IsConfigured_BeforeSetup_IsFalse()
    {
        Assert.False(_service.IsConfigured);
    }

    [Fact]
    public async Task SetupCredentialAsync_ThenIsConfigured_IsTrue()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        Assert.True(_service.IsConfigured);
    }

    [Fact]
    public async Task VerifyAsync_BeforeSetup_ReturnsNotConfiguredError()
    {
        var result = await _service.VerifyAsync("anything", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerNotConfigured, result.ErrorCode);
    }

    [Fact]
    public async Task VerifyAsync_CorrectPassword_ReturnsMasterKey()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.True(result.Success);
        Assert.NotNull(result.MasterKey);
        Assert.Equal(32, result.MasterKey!.Length);
    }

    [Fact]
    public async Task VerifyAsync_WrongPassword_ReturnsPasswordIncorrectError()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _service.VerifyAsync("wrong-password", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerPasswordIncorrect, result.ErrorCode);
        Assert.Null(result.MasterKey);
    }

    [Fact]
    public async Task VerifyAsync_FiveWrongAttempts_LocksOutEvenCorrectPassword()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        for (var i = 0; i < 5; i++)
        {
            await _service.VerifyAsync("wrong-password", IntPtr.Zero);
        }

        var result = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerLockedOut, result.ErrorCode);
    }

    // ---- 恢復金鑰路徑（純函式，不牽涉 Windows Hello，可以自動化測試）----

    [Fact]
    public async Task SetupRecoveryKeyAsync_ThenVerifyByRecoveryKeyAsync_Succeeds()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var setup = await _service.SetupRecoveryKeyAsync(verify.MasterKey!);

        Assert.True(setup.Result.Success);
        Assert.NotNull(setup.RecoveryKey);

        var result = await _service.VerifyByRecoveryKeyAsync(setup.RecoveryKey!);

        Assert.True(result.Success);
        Assert.NotNull(result.MasterKey);
        Assert.Equal(verify.MasterKey, result.MasterKey);
    }

    [Fact]
    public async Task VerifyByRecoveryKeyAsync_WrongRecoveryKey_ReturnsIncorrectError()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.SetupRecoveryKeyAsync(verify.MasterKey!);

        var result = await _service.VerifyByRecoveryKeyAsync("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AA");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerRecoveryKeyIncorrect, result.ErrorCode);
    }

    [Fact]
    public async Task VerifyByRecoveryKeyAsync_NotEnabled_ReturnsNotEnabledError()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");

        var result = await _service.VerifyByRecoveryKeyAsync("AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA-AA");

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.PasswordLockerRecoveryKeyNotEnabled, result.ErrorCode);
    }

    // ---- CRUD ----

    [Fact]
    public async Task AddOrUpdateCredentialAsync_ThenGetDecryptedPasswordAsync_RoundTrips()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);

        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        Assert.True(add.Success);
        Assert.NotNull(add.EntryId);

        var decrypted = await _service.GetDecryptedPasswordAsync(add.EntryId!, verify.MasterKey!);

        Assert.True(decrypted.Success);
        Assert.Equal("hunter2", decrypted.Password);
    }

    [Fact]
    public async Task ListCredentialsMetadata_DoesNotExposeDecryptedPassword()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var list = await _service.ListCredentialsMetadataAsync();

        Assert.Single(list);
        Assert.Equal("example.com", list[0].AssociatedDomains[0]);
        Assert.Equal("user@example.com", list[0].Username);
    }

    [Fact]
    public async Task DeleteCredentialAsync_RemovesEntry()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var deleteResult = await _service.DeleteCredentialAsync(add.EntryId!);
        var list = await _service.ListCredentialsMetadataAsync();

        Assert.True(deleteResult.Success);
        Assert.Empty(list);
    }

    [Fact]
    public async Task FindCredentialsForDomain_MatchesOnlyAssociatedDomain_WithoutRequiringMasterKey()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example",
            domains: ["example.com"], username: "user@example.com",
            password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var matches = await _service.FindCredentialsForDomainAsync("example.com");
        var noMatches = await _service.FindCredentialsForDomainAsync("other.com");

        Assert.Single(matches);
        Assert.Empty(noMatches);
    }

    // ---- 已加密檔案類別的自我修復 ----

    [Fact]
    public async Task CheckLinkedVaultItemsAsync_MissingVaultItem_FlagsAsSourceDeletedWithoutRemoving()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var add = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.EncryptedFile, title: "報稅資料.zip",
            domains: [], username: "", password: "hunter2", notes: null,
            linkedVaultItemUuid: "missing-uuid", masterKey: verify.MasterKey!);

        var flagged = await _service.CheckLinkedVaultItemsAsync(_ => false);
        var list = await _service.ListCredentialsMetadataAsync();

        Assert.Contains(add.EntryId, flagged);
        Assert.Single(list);
        Assert.True(list[0].SourceDeleted);
    }

    [Fact]
    public async Task CheckLinkedVaultItemsAsync_ExistingVaultItem_NotFlagged()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.EncryptedFile, title: "報稅資料.zip",
            domains: [], username: "", password: "hunter2", notes: null,
            linkedVaultItemUuid: "existing-uuid", masterKey: verify.MasterKey!);

        var flagged = await _service.CheckLinkedVaultItemsAsync(_ => true);
        var list = await _service.ListCredentialsMetadataAsync();

        Assert.Empty(flagged);
        Assert.False(list[0].SourceDeleted);
    }

    // ---- 自動填入 session（每網站獨立、滑動視窗）----

    [Fact]
    public void IsSiteSessionValid_NeverVerified_IsFalse()
    {
        Assert.False(_service.IsSiteSessionValid("example.com"));
    }

    [Fact]
    public void RecordSiteVerified_ThenIsSiteSessionValid_WithinTimeout_IsTrue()
    {
        var now = DateTime.UtcNow;
        _service.RecordSiteVerified("example.com", now);

        Assert.True(_service.IsSiteSessionValid("example.com", now.AddMinutes(4)));
    }

    [Fact]
    public void IsSiteSessionValid_AfterTimeoutExpires_IsFalse()
    {
        var now = DateTime.UtcNow;
        _service.RecordSiteVerified("example.com", now);

        Assert.False(_service.IsSiteSessionValid("example.com", now.AddMinutes(6)));
    }

    [Fact]
    public void RecordSiteVerified_DoesNotAffectOtherDomains()
    {
        var now = DateTime.UtcNow;
        _service.RecordSiteVerified("example.com", now);

        Assert.False(_service.IsSiteSessionValid("other.com", now));
    }

    // ---- 密碼強度／重複使用提示 ----

    [Theory]
    [InlineData("123", PasswordStrength.Weak)]
    [InlineData("password1", PasswordStrength.Weak)]
    [InlineData("Tr0ub4dor", PasswordStrength.Medium)]
    [InlineData("Correct-Horse-Battery-Staple-42!", PasswordStrength.Strong)]
    public void EstimateStrength_ReturnsExpectedBucket(string password, PasswordStrength expected)
    {
        Assert.Equal(expected, PasswordLockerService.EstimateStrength(password));
    }

    [Fact]
    public async Task FindEntriesReusingPassword_TwoEntriesWithSamePassword_ReturnsBoth()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        var first = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "A", domains: ["a.com"],
            username: "u", password: "shared-password", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);
        var second = await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "B", domains: ["b.com"],
            username: "u", password: "shared-password", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "C", domains: ["c.com"],
            username: "u", password: "different-password", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var reused = await _service.FindEntriesReusingPasswordAsync("shared-password", verify.MasterKey!);

        Assert.Equal(2, reused.Count);
        Assert.Contains(first.EntryId, reused);
        Assert.Contains(second.EntryId, reused);
    }

    // ---- 密碼產生器 ----

    [Fact]
    public void GeneratePassword_RespectsRequestedLength()
    {
        var password = PasswordLockerService.GeneratePassword(20, includeSymbols: true);

        Assert.Equal(20, password.Length);
    }

    [Fact]
    public void GeneratePassword_WithoutSymbols_OnlyContainsAlphanumerics()
    {
        var password = PasswordLockerService.GeneratePassword(50, includeSymbols: false);

        Assert.All(password, c => Assert.True(char.IsLetterOrDigit(c)));
    }

    // ---- CSV 匯出 ----

    [Fact]
    public async Task ExportToCsv_IncludesDecryptedPasswordForEachEntry()
    {
        await _service.SetupCredentialAsync("correct-horse-battery-staple");
        var verify = await _service.VerifyAsync("correct-horse-battery-staple", IntPtr.Zero);
        await _service.AddOrUpdateCredentialAsync(
            id: null, CredentialCategory.Website, title: "Example", domains: ["example.com"],
            username: "user@example.com", password: "hunter2", notes: null, linkedVaultItemUuid: null,
            masterKey: verify.MasterKey!);

        var csv = await _service.ExportToCsvAsync(verify.MasterKey!);

        Assert.Contains("example.com", csv);
        Assert.Contains("user@example.com", csv);
        Assert.Contains("hunter2", csv);
    }
}
