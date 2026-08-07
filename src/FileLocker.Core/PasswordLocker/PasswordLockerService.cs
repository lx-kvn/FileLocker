using System.Security.Cryptography;
using System.Text;
using FileLocker.Core.Crypto;
using FileLocker.Core.Models;
using FileLocker.Core.Security;

namespace FileLocker.Core.PasswordLocker;

public enum PasswordStrength
{
    Weak,
    Medium,
    Strong
}

/// <summary>
/// 對外門面，整合密碼庫子系統：PasswordLockerStore（憑證持久化）、PasskeyProtector／
/// RecoveryKeyProtector（跟 Vault 一樣的完整 wrap/unwrap 流程，密碼庫存的是真的要加密的內容，
/// 不是資料夾防護那種純驗證用法）、Argon2KeyDerivation（衍生 Locker 主金鑰）、LockoutTracker
/// （暴力猜測防護，鍵值固定用 <see cref="LockoutKey"/>，只套用在密碼路徑——Passkey、恢復金鑰
/// 都沒有能被暴力猜測的「猜」的環節，跟資料夾防護的既有邏輯一致）。
///
/// 跟 LockService、FolderGuardService 都是平行、互不依賴的獨立子系統——「已加密檔案」類別要
/// 檢查對應 Vault 項目是否還存在時，透過委派（見 CheckLinkedVaultItemsAsync）而不是直接依賴
/// VaultIndexCache，避免 Core 內部子系統互相硬依賴。
/// </summary>
public class PasswordLockerService
{
    private const string LockoutKey = "password-locker-unlock";

    private readonly PasswordLockerStore _store;
    private readonly LockoutTracker _lockoutTracker;
    private readonly Dictionary<string, DateTime> _siteSessions = new(StringComparer.OrdinalIgnoreCase);

    public PasswordLockerService(PasswordLockerStore store, LockoutTracker lockoutTracker)
    {
        _store = store;
        _lockoutTracker = lockoutTracker;
    }

    public bool IsConfigured => _store.Load().PasswordVerificationHashBase64 is not null;

    // ---- 設定 ----

    public async Task<PasswordLockerResult> SetupCredentialAsync(string password)
    {
        return await Task.Run(() =>
        {
            var salt = Argon2KeyDerivation.GenerateSalt();
            var derived = Argon2KeyDerivation.DeriveKeys(password, salt);

            var data = _store.Load();
            data.PasswordSaltBase64 = Convert.ToBase64String(salt);
            data.PasswordVerificationHashBase64 = Convert.ToBase64String(derived.VerificationHash);
            _store.Save(data);

            CryptographicOperations.ZeroMemory(derived.EncryptionKey);
            CryptographicOperations.ZeroMemory(derived.VerificationHash);

            return new PasswordLockerResult(true);
        });
    }

    public async Task<PasswordLockerResult> SetupPasskeyAsync(IntPtr ownerWindowHandle, byte[] lockerMasterKey)
    {
        var credentialName = PasskeyProtector.GenerateCredentialName();
        var created = await PasskeyProtector.CreateCredentialAsync(credentialName, ownerWindowHandle);
        if (!created)
        {
            return new PasswordLockerResult(false, "Passkey 設定失敗或已取消", ErrorCode: ErrorCodes.PasswordLockerPasskeyFailed);
        }

        var challenge = PasskeyProtector.GenerateChallenge();
        var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);
        if (signature is null)
        {
            await PasskeyProtector.DeleteCredentialAsync(credentialName);
            return new PasswordLockerResult(false, "Passkey 設定失敗或已取消", ErrorCode: ErrorCodes.PasswordLockerPasskeyFailed);
        }

        string wrapped;
        var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
        try
        {
            wrapped = PasskeyProtector.WrapContentKey(wrappingKey, lockerMasterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(signature);
        }

        var data = _store.Load();
        data.PasskeyCredentialName = credentialName;
        data.PasskeyWrappedMasterKeyBase64 = wrapped;
        data.PasskeyEnabled = true;
        _store.Save(data);

        return new PasswordLockerResult(true);
    }

    /// <summary>停用前一樣先驗證身份（密碼/Passkey，Passkey 優先），跟資料夾防護既有慣例一致。</summary>
    public async Task<PasswordLockerResult> DisablePasskeyAsync(string? password, IntPtr ownerWindowHandle)
    {
        var verify = await VerifyAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return new PasswordLockerResult(false, verify.ErrorMessage, verify.ErrorCode, verify.ErrorDetail);
        }
        if (verify.MasterKey is not null)
        {
            CryptographicOperations.ZeroMemory(verify.MasterKey);
        }

        var data = _store.Load();
        if (data.PasskeyCredentialName is { } credentialName)
        {
            await PasskeyProtector.DeleteCredentialAsync(credentialName);
        }
        data.PasskeyCredentialName = null;
        data.PasskeyWrappedMasterKeyBase64 = null;
        data.PasskeyEnabled = false;
        _store.Save(data);

        return new PasswordLockerResult(true);
    }

    /// <summary>恢復金鑰只在這次呼叫回傳看得到，FileLocker 不留任何副本——呼叫端收到後要立刻
    /// 顯示給使用者，強制做出「已抄下」的確認（跟 LockResult.RecoveryKey 的既有慣例一致）。</summary>
    public async Task<(string? RecoveryKey, PasswordLockerResult Result)> SetupRecoveryKeyAsync(byte[] lockerMasterKey)
    {
        return await Task.Run(() =>
        {
            var recoveryKeyBytes = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
            var display = RecoveryKeyProtector.FormatForDisplay(recoveryKeyBytes);

            string wrapped;
            var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
            try
            {
                wrapped = RecoveryKeyProtector.WrapContentKey(wrappingKey, lockerMasterKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
                CryptographicOperations.ZeroMemory(recoveryKeyBytes);
            }

            var data = _store.Load();
            data.RecoveryKeyWrappedMasterKeyBase64 = wrapped;
            data.RecoveryKeyEnabled = true;
            _store.Save(data);

            return ((string?)display, new PasswordLockerResult(true));
        });
    }

    public async Task<PasswordLockerResult> DisableRecoveryKeyAsync(string? password, IntPtr ownerWindowHandle)
    {
        var verify = await VerifyAsync(password, ownerWindowHandle);
        if (!verify.Success)
        {
            return new PasswordLockerResult(false, verify.ErrorMessage, verify.ErrorCode, verify.ErrorDetail);
        }
        if (verify.MasterKey is not null)
        {
            CryptographicOperations.ZeroMemory(verify.MasterKey);
        }

        var data = _store.Load();
        data.RecoveryKeyWrappedMasterKeyBase64 = null;
        data.RecoveryKeyEnabled = false;
        _store.Save(data);

        return new PasswordLockerResult(true);
    }

    // ---- 驗證 ----

    /// <summary>Passkey 已設定時優先嘗試；沒設定、或呼叫端明確不想用時走密碼路徑。密碼路徑受
    /// LockoutTracker 保護，Passkey 路徑略過鎖定機制（TPM 硬體驗證沒有「猜」的環節）。成功時附帶
    /// Locker 主金鑰，呼叫端用完後要自行 CryptographicOperations.ZeroMemory 清掉。</summary>
    public async Task<PasswordLockerVerifyResult> VerifyAsync(string? password, IntPtr ownerWindowHandle, bool tryPasskeyFirst = true)
    {
        var data = _store.Load();

        if (tryPasskeyFirst && data.PasskeyEnabled && data.PasskeyCredentialName is { } credentialName)
        {
            var challenge = PasskeyProtector.GenerateChallenge();
            var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);
            if (signature is not null)
            {
                byte[] masterKey;
                var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
                try
                {
                    masterKey = PasskeyProtector.UnwrapContentKey(wrappingKey, data.PasskeyWrappedMasterKeyBase64!);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(wrappingKey);
                    CryptographicOperations.ZeroMemory(signature);
                }
                return new PasswordLockerVerifyResult(true, masterKey);
            }

            // Passkey 已設定但驗證失敗/被取消，且呼叫端沒有同時附上密碼——「Passkey 已設定就只走
            // Passkey」，不退回密碼，見資料夾防護既有的同一個設計理由。
            if (password is null)
            {
                return new PasswordLockerVerifyResult(false, null, "Passkey 驗證失敗或已取消", ErrorCode: ErrorCodes.PasswordLockerPasskeyFailed);
            }
        }

        if (data.PasswordSaltBase64 is null || data.PasswordVerificationHashBase64 is null)
        {
            return new PasswordLockerVerifyResult(false, null, "尚未設定密碼庫", ErrorCode: ErrorCodes.PasswordLockerNotConfigured);
        }

        if (password is null)
        {
            return new PasswordLockerVerifyResult(false, null, "密碼錯誤", ErrorCode: ErrorCodes.PasswordLockerPasswordIncorrect);
        }

        var lockoutStatus = _lockoutTracker.CheckStatus(LockoutKey);
        if (lockoutStatus.IsLockedOut)
        {
            var remainingSeconds = (int)Math.Ceiling(lockoutStatus.RemainingLockout!.Value.TotalSeconds);
            return new PasswordLockerVerifyResult(false, null, "嘗試次數過多，請稍後再試",
                ErrorCode: ErrorCodes.PasswordLockerLockedOut, ErrorDetail: remainingSeconds.ToString());
        }

        var salt = Convert.FromBase64String(data.PasswordSaltBase64);
        var storedHash = Convert.FromBase64String(data.PasswordVerificationHashBase64);
        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(password, salt, storedHash);

        if (!isValid)
        {
            _lockoutTracker.RecordFailedAttempt(LockoutKey);
            return new PasswordLockerVerifyResult(false, null, "密碼錯誤", ErrorCode: ErrorCodes.PasswordLockerPasswordIncorrect);
        }

        _lockoutTracker.RecordSuccess(LockoutKey);
        return new PasswordLockerVerifyResult(true, encryptionKey);
    }

    public async Task<PasswordLockerVerifyResult> VerifyByRecoveryKeyAsync(string recoveryKeyInput)
    {
        return await Task.Run(() =>
        {
            var data = _store.Load();
            if (!data.RecoveryKeyEnabled || data.RecoveryKeyWrappedMasterKeyBase64 is null)
            {
                return new PasswordLockerVerifyResult(false, null, "尚未設定密碼庫恢復金鑰", ErrorCode: ErrorCodes.PasswordLockerRecoveryKeyNotEnabled);
            }

            var recoveryKeyBytes = RecoveryKeyProtector.ParseUserInput(recoveryKeyInput);
            if (recoveryKeyBytes is null)
            {
                return new PasswordLockerVerifyResult(false, null, "恢復金鑰格式不正確", ErrorCode: ErrorCodes.PasswordLockerRecoveryKeyInvalidFormat);
            }

            var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
            try
            {
                var masterKey = RecoveryKeyProtector.UnwrapContentKey(wrappingKey, data.RecoveryKeyWrappedMasterKeyBase64);
                return new PasswordLockerVerifyResult(true, masterKey);
            }
            catch (CryptographicException)
            {
                return new PasswordLockerVerifyResult(false, null, "恢復金鑰不正確", ErrorCode: ErrorCodes.PasswordLockerRecoveryKeyIncorrect);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
                CryptographicOperations.ZeroMemory(recoveryKeyBytes);
            }
        });
    }

    // ---- CRUD ----

    public async Task<PasswordLockerEntryResult> AddOrUpdateCredentialAsync(
        string? id, CredentialCategory category, string title, IReadOnlyList<string> domains,
        string username, string password, string? notes, string? linkedVaultItemUuid, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var data = _store.Load();
            var encryptedPassword = EncryptField(masterKey, password);
            var encryptedNotes = string.IsNullOrEmpty(notes) ? null : EncryptField(masterKey, notes);
            var now = DateTime.UtcNow;

            var existing = id is not null ? data.Entries.FirstOrDefault(e => e.Id == id) : null;
            if (existing is not null)
            {
                existing.Category = category;
                existing.Title = title;
                existing.AssociatedDomains = domains.ToList();
                existing.Username = username;
                existing.EncryptedPasswordBase64 = encryptedPassword;
                existing.EncryptedNotesBase64 = encryptedNotes;
                existing.LinkedVaultItemUuid = linkedVaultItemUuid;
                existing.UpdatedAtUtc = now;
                _store.Save(data);
                return new PasswordLockerEntryResult(true, existing.Id);
            }

            var entry = new PasswordCredentialEntry
            {
                Category = category,
                Title = title,
                AssociatedDomains = domains.ToList(),
                Username = username,
                EncryptedPasswordBase64 = encryptedPassword,
                EncryptedNotesBase64 = encryptedNotes,
                LinkedVaultItemUuid = linkedVaultItemUuid,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            data.Entries.Add(entry);
            _store.Save(data);
            return new PasswordLockerEntryResult(true, entry.Id);
        });
    }

    public async Task<PasswordLockerDecryptedPasswordResult> GetDecryptedPasswordAsync(string id, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var entry = _store.Load().Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return new PasswordLockerDecryptedPasswordResult(false, null, "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound);
            }

            var plaintext = DecryptField(masterKey, entry.EncryptedPasswordBase64);
            try
            {
                return new PasswordLockerDecryptedPasswordResult(true, Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        });
    }

    public async Task<IReadOnlyList<PasswordCredentialMetadata>> ListCredentialsMetadataAsync()
        => await Task.Run(() => _store.Load().Entries.Select(ToMetadata).ToList());

    public async Task<PasswordLockerResult> DeleteCredentialAsync(string id)
    {
        return await Task.Run(() =>
        {
            var data = _store.Load();
            var removed = data.Entries.RemoveAll(e => e.Id == id);
            if (removed == 0)
            {
                return new PasswordLockerResult(false, "找不到這筆密碼紀錄", ErrorCode: ErrorCodes.PasswordLockerEntryNotFound);
            }
            _store.Save(data);
            return new PasswordLockerResult(true);
        });
    }

    /// <summary>供瀏覽器擴充功能查詢用：不需要解鎖就能查，只比對明文的 AssociatedDomains
    /// （見規劃文件第 5 節「必須在使用者驗證身份之前就能比對」）。</summary>
    public async Task<IReadOnlyList<PasswordCredentialMetadata>> FindCredentialsForDomainAsync(string domain)
        => await Task.Run(() => _store.Load().Entries
            .Where(e => e.AssociatedDomains.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)))
            .Select(ToMetadata)
            .ToList());

    /// <summary>「已加密檔案」類別的自我修復：對應項目消失時標題加刪除線＋標示來源消失
    /// （規劃文件第 4 節），不刪除這筆憑證。用委派而非直接依賴 VaultIndexCache，避免 Core
    /// 內部子系統互相硬依賴（比照 LockService 建構子接收 getGuardedFolderPaths 委派的既有模式）。</summary>
    public async Task<IReadOnlyList<string>> CheckLinkedVaultItemsAsync(Func<string, bool> vaultItemExists)
    {
        return await Task.Run(() =>
        {
            var data = _store.Load();
            var flagged = new List<string>();
            var changed = false;

            foreach (var entry in data.Entries)
            {
                if (entry.Category != CredentialCategory.EncryptedFile || entry.LinkedVaultItemUuid is null)
                {
                    continue;
                }

                var sourceDeleted = !vaultItemExists(entry.LinkedVaultItemUuid);
                if (sourceDeleted != entry.SourceDeleted)
                {
                    entry.SourceDeleted = sourceDeleted;
                    changed = true;
                }
                if (sourceDeleted)
                {
                    flagged.Add(entry.Id);
                }
            }

            if (changed)
            {
                _store.Save(data);
            }

            return flagged;
        });
    }

    // ---- 自動填入 session（每網站獨立、滑動視窗，見規劃文件第 3 節）----

    /// <summary>now 參數只給測試用來注入固定時間，正式呼叫端不用帶，預設用目前時間。</summary>
    public void RecordSiteVerified(string domain, DateTime? now = null)
        => _siteSessions[domain] = now ?? DateTime.UtcNow;

    public bool IsSiteSessionValid(string domain, DateTime? now = null)
    {
        if (!_siteSessions.TryGetValue(domain, out var lastVerified))
        {
            return false;
        }

        var current = now ?? DateTime.UtcNow;
        var timeoutMinutes = _store.Load().SessionTimeoutMinutes;
        return current - lastVerified <= TimeSpan.FromMinutes(timeoutMinutes);
    }

    // ---- 密碼強度／重複使用提示（規劃文件第 6 節，純資訊性、不阻擋儲存）----

    public static PasswordStrength EstimateStrength(string password)
    {
        var hasLower = password.Any(char.IsLower);
        var hasUpper = password.Any(char.IsUpper);
        var hasDigit = password.Any(char.IsDigit);
        var hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));
        var variety = new[] { hasLower, hasUpper, hasDigit, hasSymbol }.Count(x => x);

        if (password.Length < 8 || variety < 3)
        {
            return PasswordStrength.Weak;
        }

        return password.Length >= 16 ? PasswordStrength.Strong : PasswordStrength.Medium;
    }

    /// <summary>只比對使用者自己密碼庫裡的資料，不涉及任何連網查詢或外部外洩資料庫比對。</summary>
    public async Task<IReadOnlyList<string>> FindEntriesReusingPasswordAsync(string password, byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var matches = new List<string>();
            foreach (var entry in _store.Load().Entries)
            {
                var plaintext = DecryptField(masterKey, entry.EncryptedPasswordBase64);
                try
                {
                    if (Encoding.UTF8.GetString(plaintext) == password)
                    {
                        matches.Add(entry.Id);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
            return matches;
        });
    }

    // ---- 密碼產生器 ----

    private const string AlphanumericChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string SymbolChars = "!@#$%^&*()-_=+[]{}";

    public static string GeneratePassword(int length, bool includeSymbols)
    {
        var choices = includeSymbols ? AlphanumericChars + SymbolChars : AlphanumericChars;
        return RandomNumberGenerator.GetString(choices, length);
    }

    // ---- CSV 匯出（規劃文件第 7 節：密碼忘記＋Passkey／恢復金鑰都用不了時的最後自救手段）----

    public async Task<string> ExportToCsvAsync(byte[] masterKey)
    {
        return await Task.Run(() =>
        {
            var sb = new StringBuilder();
            sb.AppendLine("title,domains,username,password,notes");

            foreach (var entry in _store.Load().Entries)
            {
                var password = Encoding.UTF8.GetString(DecryptField(masterKey, entry.EncryptedPasswordBase64));
                var notes = entry.EncryptedNotesBase64 is not null
                    ? Encoding.UTF8.GetString(DecryptField(masterKey, entry.EncryptedNotesBase64))
                    : "";

                sb.AppendLine(string.Join(",",
                    CsvEscape(entry.Title),
                    CsvEscape(string.Join(";", entry.AssociatedDomains)),
                    CsvEscape(entry.Username),
                    CsvEscape(password),
                    CsvEscape(notes)));
            }

            return sb.ToString();
        });
    }

    private static string CsvEscape(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    // ---- 內部輔助 ----

    private static PasswordCredentialMetadata ToMetadata(PasswordCredentialEntry entry)
        => new(entry.Id, entry.Category, entry.Title, entry.AssociatedDomains, entry.Username,
            entry.LinkedVaultItemUuid, entry.SourceDeleted, entry.CreatedAtUtc, entry.UpdatedAtUtc);

    /// <summary>跟 RecoveryKeyProtector.WrapContentKey 內部用的 nonce+tag+ciphertext 串接格式一致。</summary>
    private static string EncryptField(byte[] masterKey, string plaintext)
    {
        var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(masterKey, Encoding.UTF8.GetBytes(plaintext));
        var combined = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length + tag.Length, ciphertext.Length);
        return Convert.ToBase64String(combined);
    }

    private static byte[] DecryptField(byte[] masterKey, string base64)
    {
        var combined = Convert.FromBase64String(base64);
        var nonce = combined.AsSpan(0, AesGcmCipher.NonceSizeBytes);
        var tag = combined.AsSpan(AesGcmCipher.NonceSizeBytes, AesGcmCipher.TagSizeBytes);
        var ciphertext = combined.AsSpan(AesGcmCipher.NonceSizeBytes + AesGcmCipher.TagSizeBytes);
        return AesGcmCipher.Decrypt(masterKey, nonce, ciphertext, tag);
    }
}
