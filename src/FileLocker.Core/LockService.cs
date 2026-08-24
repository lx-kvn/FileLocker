using System.Security.Cryptography;
using FileLocker.Core.Crypto;
using FileLocker.Core.FolderPackaging;
using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.SecureDelete;
using FileLocker.Core.Security;
using FileLocker.Core.Vault;

namespace FileLocker.Core;

/// <summary>
/// 對外的主要 API 入口——GUI、CLI 原型都只需要呼叫這一層，不需要知道底下 Crypto/Vault/FolderPackaging 的細節。
/// 對應規格文件 3.3（加密流程）、3.4（解密流程）、3.2 第 3 點（刪除防呆）。
/// </summary>
public class LockService
{
    private readonly VaultManager _vault;
    private readonly HistoryLogger? _history;
    private readonly LockoutTracker? _lockout;
    private readonly Func<IReadOnlyList<string>>? _getGuardedFolderPaths;

    /// <summary>
    /// 對應獨立解密流程（design-exploration/gui-styles-v2 定案文件 §1.11）的驗證/提交交易模型：
    /// Verify 階段只證明使用者有權限解密（密碼/Passkey/恢復金鑰其中一種），不寫入任何檔案；
    /// 使用者選定存檔位置後才呼叫 CommitPendingDecryptAsync 真正還原。跟信封加密流程的
    /// pending/commit（EncryptPendingAsync／CommitEncryptAsync）不同的是：加密的 pending 狀態
    /// 已經是安全的密文，可以直接持久化進 Vault；這裡 pending 的是「已經解出來但還沒用掉」的
    /// 內容金鑰或明文密碼，絕對不能落地寫進磁碟，只能留在記憶體裡，所以用一個 process 內的
    /// ConcurrentDictionary，不是 Vault 裡的 Status 欄位。
    /// 已知限制：沒有逾時自動清除機制——使用者驗證成功後放著不管（既不 commit 也不 cancel），
    /// contentKey／password 會留在記憶體直到下次同一 uuid 被重新 verify 或整個 App 關閉，
    /// 風險等級跟其他「解密到一半」的既有暫存邏輯相同，不在這輪範圍內處理。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingDecrypt> _pendingDecrypts = new();

    private sealed record PendingDecrypt(LockedItemMetadata Metadata, byte[]? ContentKey, string? Password, string UnlockMethod);

    /// <summary>
    /// historyLogger／lockoutTracker 都是選填的：CLI 原型或單元測試不一定需要，傳 null 就單純不記錄／不鎖定，
    /// 不影響加密/解密本身的行為。getGuardedFolderPaths 同樣選填——用一個委派而不是直接依賴
    /// FolderGuardService 型別，讓 LockService 不需要知道資料夾防護子系統的存在，只在有傳入時
    /// 才做巢狀防護資料夾的檢查（見 EncryptAsync）；傳 null 就完全略過這個檢查，維持既有呼叫端
    /// （Cli、既有測試）不用跟著改動。
    /// </summary>
    public LockService(
        VaultManager vault, HistoryLogger? historyLogger = null, LockoutTracker? lockoutTracker = null,
        Func<IReadOnlyList<string>>? getGuardedFolderPaths = null)
    {
        _vault = vault;
        _history = historyLogger;
        _lockout = lockoutTracker;
        _getGuardedFolderPaths = getGuardedFolderPaths;
    }

    /// <summary>
    /// 維持原本一次到位的行為（成功＝原始檔已刪除、marker 已寫入）——內部其實是依序呼叫
    /// EncryptPendingAsync 再 CommitEncryptAsync，只是把兩段合併成單一原子操作對外呈現，
    /// 讓 CLI／舊版精靈這些不需要「取消」中間態的既有呼叫端完全不用改。commit 階段失敗時
    /// 會自動把剛才寫入的 pending 項目回滾掉，維持「失敗＝什麼都沒發生過」這個既有保證
    /// （對應舊版 TryCleanupOrphanedVaultEntry 的行為）。信封加密流程（design-exploration/
    /// gui-styles-v2 定案文件 §1.8）需要在使用者確認前有一個安全的中間態，直接呼叫
    /// EncryptPendingAsync／CommitEncryptAsync／RollbackPendingEncryptAsync 三個方法，不走這個
    /// 合併版本。
    /// </summary>
    public async Task<LockResult> EncryptAsync(
        string path, string password, string? hint,
        bool enablePasskey = false, IntPtr ownerWindowHandle = default,
        bool enableRecoveryKey = false, string? batchId = null,
        IProgress<double>? progress = null,
        Action<bool>? onPasskeyVerifying = null,
        StorageMode storageMode = StorageMode.Vault,
        string? destinationDir = null)
    {
        var pending = await EncryptPendingAsync(
            path, password, hint, enablePasskey, ownerWindowHandle, enableRecoveryKey, batchId,
            progress, onPasskeyVerifying, storageMode, destinationDir);

        if (!pending.Success)
        {
            return pending;
        }

        var commit = await CommitEncryptAsync(pending.Uuid);

        if (!commit.Success)
        {
            // 維持舊版 EncryptAsync「失敗就什麼都不留」的保證——commit 失敗（例如 marker 寫入時
            // 磁碟滿了）不應該讓呼叫端看到一筆卡在 Pending、既不是成功也不是乾淨失敗的紀錄。
            await RollbackPendingEncryptAsync(pending.Uuid);
            return commit;
        }

        // RecoveryKey 明文只在 pending 階段產生過一次（衍生用的隨機值本身不會被持久化），
        // commit 階段純粹是收尾動作（寫 marker／刪原始檔／寫 History），不會重新產生，
        // 所以要沿用 pending 階段算出來的那一份，不能指望 commit 結果自己帶著。
        return commit with { RecoveryKey = pending.RecoveryKey };
    }

    /// <summary>
    /// 對應信封加密流程「取消要能安全回滾」交易模型的第一段：做完壓縮／加密／Passkey／恢復金鑰
    /// 包裝，metadata 寫入 Vault（Status=Pending），但刻意不寫 marker、不刪原始檔、不寫 History——
    /// 這個狀態下呼叫端隨時可以安全地整個放棄（見 RollbackPendingEncryptAsync），原始檔案全程
    /// 沒被動過。真正要完成這筆加密（放 marker、刪原始檔），使用者確認後另外呼叫
    /// CommitEncryptAsync。
    ///
    /// 注意：這裡刻意不整個包進 Task.Run——實測發現 Passkey 相關的 WinRT 呼叫如果整個在背景執行緒
    /// 上執行，第二次（簽章）的 Windows Hello 驗證視窗會抓不到正確的視窗焦點/啟用狀態（懷疑跟 WinRT
    /// 的執行緒環境有關）。只有純檔案 I/O／加密運算的部分（EncryptToVault）丟進背景執行緒，
    /// Passkey 相關呼叫留在呼叫端原本的執行緒（通常是 UI 執行緒）上直接 await。
    /// </summary>
    public async Task<LockResult> EncryptPendingAsync(
        string path, string password, string? hint,
        bool enablePasskey = false, IntPtr ownerWindowHandle = default,
        bool enableRecoveryKey = false, string? batchId = null,
        IProgress<double>? progress = null,
        Action<bool>? onPasskeyVerifying = null,
        StorageMode storageMode = StorageMode.Vault,
        string? destinationDir = null)
    {
        var isFolder = Directory.Exists(path);
        var isFile = File.Exists(path);

        if (!isFolder && !isFile)
        {
            return new LockResult(false, "", "", $"找不到檔案或資料夾：{path}", ErrorCode: ErrorCodes.SourceNotFound, ErrorDetail: path);
        }

        var type = isFolder ? ItemType.Folder : ItemType.File;

        var originalName = isFolder
            ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(path);

        // 先做這個便宜的檢查，才去做壓縮資料夾這種可能很花時間的工作——
        // 目標位置已經有指標檔的話，應該儘早失敗，不要白白先把整個資料夾壓縮完才發現要失敗。
        var markerPath = MarkerStatusChecker.ComputeMarkerPath(path, isFolder);
        if (File.Exists(markerPath))
        {
            return new LockResult(false, "", "", $"目標位置已經有一個指標檔了：{markerPath}", ErrorCode: ErrorCodes.MarkerAlreadyExists, ErrorDetail: markerPath);
        }

        // 單檔案分散式加密的目標是 .flocked，跟集中庫加密的 .locked 是不同檔名，理論上不會
        // 撞上面那個檢查——這裡另外檢查一次真正的目標位置（原地或 destinationDir），一樣是
        // 「先做便宜檢查、避免白白先壓縮完才發現要失敗」的理由，commit 階段也會再檢查一次
        // （pending 到 commit 之間這段時間，目標位置仍然可能被其他操作搶先卡位）。
        if (storageMode == StorageMode.Standalone)
        {
            var flockedTargetPath = ComputeStandaloneFlockedTargetPath(path, isFolder, destinationDir);
            if (File.Exists(flockedTargetPath))
            {
                return new LockResult(false, "", "", $"目標位置已經有一個獨立密文檔了：{flockedTargetPath}", ErrorCode: ErrorCodes.FlockedAlreadyExists, ErrorDetail: flockedTargetPath);
            }
        }

        // 資料夾防護的 ACL 拒絕規則掛在目前登入帳號的 SID 上，這個行程本身也是用同一個帳號跑，
        // 讀取防護中的子資料夾一樣會被拒絕存取——同一個理由，這裡也要先做這個便宜的檢查，
        // 不要等 EncryptToVault 壓縮到一半才撞見 UnauthorizedAccessException（見規劃文件第 8 節）。
        if (isFolder && _getGuardedFolderPaths is not null)
        {
            var nestedGuarded = FolderArchiver.FindNestedGuardedFolders(path, _getGuardedFolderPaths());
            if (nestedGuarded.Count > 0)
            {
                var names = string.Join("、", nestedGuarded.Select(p =>
                    Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
                return new LockResult(false, "", "",
                    $"這個資料夾內含正在上鎖的項目（子資料夾：{names}），請先解鎖才能加密",
                    ErrorCode: ErrorCodes.FolderGuardContainsNestedGuarded, ErrorDetail: string.Join("|", nestedGuarded));
            }
        }

        EncryptionResult encryptResult;
        try
        {
            encryptResult = await Task.Run(() => EncryptToVault(path, isFolder, password, storageMode, progress));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LockResult(false, "", "", $"加密過程發生錯誤：{ex.Message}", ErrorCode: ErrorCodes.EncryptError, ErrorDetail: ex.Message);
        }
        catch (Exception ex)
        {
            // EncryptToVault 內部已經自己接住所有例外，理論上這裡不會再丟出來——
            // 保留這層純粹是防禦性寫法，避免未來改動時漏接某個例外型別導致整個 App 崩潰。
            return new LockResult(false, "", "", $"加密過程發生未預期的錯誤：{ex.Message}", ErrorCode: ErrorCodes.EncryptUnexpectedError, ErrorDetail: ex.Message);
        }

        try
        {
            if (!encryptResult.Success)
            {
                return new LockResult(false, "", "", encryptResult.ErrorMessage, ErrorCode: encryptResult.ErrorCode, ErrorDetail: encryptResult.ErrorDetail);
            }

            string? passkeyCredentialName = null;
            string? passkeyChallengeBase64 = null;
            string? passkeyWrappedKeyBase64 = null;

            // 對應規格文件 8.1 節：Passkey 是「額外」的一道門，這裡失敗（不支援裝置、使用者取消、
            // 驗證失敗）都不影響密碼加密本身的成功與否，只是這個項目最終沒有啟用 Passkey 快速解鎖。
            if (enablePasskey && await PasskeyProtector.IsSupportedAsync())
            {
                // 這段期間會跳出 Windows Hello 系統 UI 並阻塞等待使用者操作，前端的假進度條完全
                // 感知不到——用這個回呼讓呼叫端（App 層）通知前端暫停動畫，避免進度條在使用者
                // 還沒完成驗證時繼續自顧自往前跑。
                onPasskeyVerifying?.Invoke(true);
                try
                {
                    var credentialName = PasskeyProtector.GenerateCredentialName();
                    if (await PasskeyProtector.CreateCredentialAsync(credentialName, ownerWindowHandle))
                    {
                        var challenge = PasskeyProtector.GenerateChallenge();
                        var signature = await PasskeyProtector.SignChallengeAsync(credentialName, challenge, ownerWindowHandle);

                        if (signature is not null)
                        {
                            var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
                            try
                            {
                                passkeyWrappedKeyBase64 = PasskeyProtector.WrapContentKey(wrappingKey, encryptResult.EncryptionKey!);
                                passkeyCredentialName = credentialName;
                                passkeyChallengeBase64 = Convert.ToBase64String(challenge);
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(wrappingKey);
                                CryptographicOperations.ZeroMemory(signature);
                            }
                        }
                        else
                        {
                            // 使用者取消或驗證失敗：清掉剛剛建立的裝置金鑰，不留下一把沒被用到的憑證。
                            await PasskeyProtector.DeleteCredentialAsync(credentialName);
                        }
                    }
                }
                finally
                {
                    onPasskeyVerifying?.Invoke(false);
                }
            }

            string? recoveryKeyWrappedBase64 = null;
            string? recoveryKeyDisplayText = null;

            // 恢復金鑰是純本機運算（產生隨機值、HKDF、AES-GCM），不牽涉任何 Windows API，
            // 不需要像 Passkey 那樣顧慮執行緒環境，直接同步做完即可。
            if (enableRecoveryKey)
            {
                var recoveryKeyBytes = RecoveryKeyProtector.GenerateRecoveryKeyBytes();
                try
                {
                    recoveryKeyDisplayText = RecoveryKeyProtector.FormatForDisplay(recoveryKeyBytes);
                    var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
                    try
                    {
                        recoveryKeyWrappedBase64 = RecoveryKeyProtector.WrapContentKey(wrappingKey, encryptResult.EncryptionKey!);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(wrappingKey);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(recoveryKeyBytes);
                }
            }

            // 這裡刻意不寫 marker、不刪原始檔、不寫 History——那三件事是「這筆加密真的完成了」
            // 的宣告，屬於 CommitEncryptAsync 的職責。到這裡為止，加密內容已經安全寫進 Vault，
            // 原始檔案完全沒被動過，是可以隨時安全放棄的中間態。
            var metadata = new LockedItemMetadata
            {
                Uuid = encryptResult.Uuid!,
                OriginalName = originalName,
                OriginalPath = path,
                PasswordVerificationHash = encryptResult.PasswordVerificationHashBase64!,
                Salt = encryptResult.SaltBase64!,
                Argon2TimeCost = KeyDerivationDefaults.TimeCost,
                Argon2MemoryCostKb = KeyDerivationDefaults.MemoryCostKb,
                Argon2Parallelism = KeyDerivationDefaults.Parallelism,
                Hint = hint,
                Type = type,
                OriginalSizeBytes = encryptResult.OriginalSizeBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ContainsNestedLocks = encryptResult.NestedUuids!,
                PasskeyEnabled = passkeyWrappedKeyBase64 is not null,
                PasskeyCredentialName = passkeyCredentialName,
                PasskeyChallenge = passkeyChallengeBase64,
                PasskeyWrappedContentKey = passkeyWrappedKeyBase64,
                RecoveryKeyEnabled = recoveryKeyWrappedBase64 is not null,
                RecoveryKeyWrappedContentKey = recoveryKeyWrappedBase64,
                BatchId = batchId,
                Status = LockStatus.Pending,
                StorageMode = storageMode,
                StandaloneDestinationDir = destinationDir
            };
            _vault.SaveMetadata(metadata);

            return new LockResult(true, encryptResult.Uuid!, "", null, recoveryKeyDisplayText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // metadata／marker 這一段如果寫到一半失敗（例如 marker.WriteTo 因為磁碟滿了而丟出例外），
            // 之前可能已經成功把 metadata 寫進 Vault 了——盡力把這個孤兒項目清掉，避免清單頁出現一筆
            // 沒有對應 .locked 指標檔、永遠打不開的幽靈紀錄。
            TryCleanupOrphanedVaultEntry(encryptResult.Uuid);
            return new LockResult(false, "", "", $"加密過程發生錯誤：{ex.Message}", ErrorCode: ErrorCodes.EncryptError, ErrorDetail: ex.Message);
        }
        catch (Exception ex)
        {
            TryCleanupOrphanedVaultEntry(encryptResult.Uuid);
            return new LockResult(false, "", "", $"加密過程發生未預期的錯誤：{ex.Message}", ErrorCode: ErrorCodes.EncryptUnexpectedError, ErrorDetail: ex.Message);
        }
        finally
        {
            if (encryptResult.EncryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(encryptResult.EncryptionKey);
            }
            if (encryptResult.TempZipPath is not null)
            {
                SecureFileEraser.OverwriteAndDelete(encryptResult.TempZipPath);
            }
        }
    }

    /// <summary>
    /// 對應信封加密流程「取消要能安全回滾」交易模型的第二段：把 EncryptPendingAsync 留下的
    /// Pending 項目真正完成——寫 marker、刪除原始檔、metadata 狀態改成 Committed、寫入 History。
    /// 呼叫端（信封 UI）要在這一步真的完成之後才播「寄出」動畫，不能先演給使用者看、背地裡
    /// 還沒做完（design-exploration/gui-styles-v2 定案文件 §1.8：正確性優先於流暢度）。
    ///
    /// 只吃 uuid——原始路徑／是不是資料夾這兩件事直接從 pending metadata 本身讀（`OriginalPath`／
    /// `Type`），不要求呼叫端另外傳一份，避免呼叫端（信封 UI）手上的值跟 pending 記錄本身不一致
    /// 這種本來不該存在的落差。
    /// </summary>
    public async Task<LockResult> CommitEncryptAsync(string uuid)
    {
        var metadata = _vault.LoadMetadata(uuid);
        if (metadata is null || metadata.Status != LockStatus.Pending)
        {
            return new LockResult(false, "", "", $"找不到待確認的加密項目：{uuid}", ErrorCode: ErrorCodes.PendingItemNotFound, ErrorDetail: uuid);
        }

        // 對應「單檔案分散式加密」功能規劃 §7：兩種模式 commit 階段實際做的事不同（寫 .locked
        // marker vs. 把暫存密文搬到最終位置的 .flocked），分成兩個方法各自負責，不要擠在同一個
        // 方法裡用一堆 if 分支——這兩段邏輯除了「都要更新 Status、寫 History」以外幾乎沒有共用
        // 部分，硬併在一起只會更難讀。
        return metadata.StorageMode == StorageMode.Standalone
            ? await CommitStandaloneEncryptAsync(metadata)
            : await CommitVaultEncryptAsync(metadata, uuid);
    }

    private async Task<LockResult> CommitVaultEncryptAsync(LockedItemMetadata metadata, string uuid)
    {
        var originalPath = metadata.OriginalPath;
        var isFolder = metadata.Type == ItemType.Folder;
        var markerPath = MarkerStatusChecker.ComputeMarkerPath(originalPath, isFolder);

        try
        {
            if (File.Exists(markerPath))
            {
                return new LockResult(false, "", "", $"目標位置已經有一個指標檔了：{markerPath}", ErrorCode: ErrorCodes.MarkerAlreadyExists, ErrorDetail: markerPath);
            }

            var vaultConfig = _vault.LoadOrCreateConfig();
            var signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);
            var marker = LockedMarkerFile.Create(uuid, signingKey);
            marker.WriteTo(markerPath);

            metadata.Status = LockStatus.Committed;
            _vault.SaveMetadata(metadata);

            // 到這裡，marker 已經寫入、metadata 已經標記完成——資料本身已經安全了。
            // 清除原始明文是「收尾」動作，這一步就算失敗，也不代表這筆加密失敗，
            // 所以特別包一層自己的 try/catch，不讓它跟著外層的 catch 把整個結果判定成失敗。
            string? cleanupWarning = null;
            try
            {
                await Task.Run(() =>
                {
                    if (isFolder)
                    {
                        SecureFileEraser.OverwriteAndDeleteFolder(originalPath);
                    }
                    else
                    {
                        SecureFileEraser.OverwriteAndDelete(originalPath);
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = $"加密已完成，但清除原始檔案時發生錯誤，請手動確認並刪除原始檔案：{ex.Message}";
            }

            _history?.Append(new HistoryEntry(
                uuid, metadata.OriginalName, HistoryAction.Encrypted, DateTimeOffset.UtcNow, metadata.Hint,
                SourcePath: originalPath,
                PasskeyEnabled: metadata.PasskeyEnabled,
                RecoveryKeyEnabled: metadata.RecoveryKeyEnabled));

            return new LockResult(true, uuid, markerPath, cleanupWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // marker 寫到一半失敗（例如磁碟滿了）——項目留在 Pending，不在這裡自動回滾：
            // 呼叫端（信封 UI）可能想讓使用者重試 commit，而不是連加密內容都一起丟掉。
            // 舊版 EncryptAsync 那種「失敗就整個回滾」的行為在合併版本的 EncryptAsync 裡處理。
            return new LockResult(false, "", "", $"完成加密時發生錯誤：{ex.Message}", ErrorCode: ErrorCodes.CommitPendingEncryptFailed, ErrorDetail: ex.Message);
        }
    }

    /// <summary>
    /// 對應「單檔案分散式加密」功能規劃 §7：不寫 .locked marker，改成把 EncryptToVault 階段
    /// 已經暫存在 Vault 裡（含 .flocked header）的密文檔案，用 File.Move 直接搬到最終位置
    /// （原地取代原始檔案，或使用者當初指定的 destinationDir）——密文內容不需要重新讀寫一次。
    /// 搬移完成後 OriginalPath 要更新成新位置，之後 FlockedStatusChecker／解密流程才會查對
    /// 地方（見 LockedItemMetadata.StorageMode 上的說明）。
    /// </summary>
    private async Task<LockResult> CommitStandaloneEncryptAsync(LockedItemMetadata metadata)
    {
        var uuid = metadata.Uuid;
        var originalPath = metadata.OriginalPath;
        var isFolder = metadata.Type == ItemType.Folder;
        var finalFlockedPath = ComputeStandaloneFlockedTargetPath(originalPath, isFolder, metadata.StandaloneDestinationDir);

        try
        {
            if (File.Exists(finalFlockedPath))
            {
                return new LockResult(false, "", "", $"目標位置已經有一個獨立密文檔了：{finalFlockedPath}", ErrorCode: ErrorCodes.FlockedAlreadyExists, ErrorDetail: finalFlockedPath);
            }

            var targetParentDir = Path.GetDirectoryName(finalFlockedPath);
            if (!string.IsNullOrEmpty(targetParentDir))
            {
                // 原地取代的情境這個資料夾理論上一定存在（就是原始檔案所在的資料夾）；
                // destinationDir 是使用者透過原生選資料夾對話框挑的，也一定存在——這裡
                // 純粹是防禦性寫法，比照 VaultManager.SaveMetadata 對 Vault 資料夾本身的
                // 既有慣例，不假設呼叫端一定先幫忙建好。
                Directory.CreateDirectory(targetParentDir);
            }
            File.Move(_vault.GetEncContentPath(uuid), finalFlockedPath);

            metadata.OriginalPath = metadata.StandaloneDestinationDir is null
                ? originalPath
                : Path.Combine(metadata.StandaloneDestinationDir, metadata.OriginalName);
            metadata.Status = LockStatus.Committed;
            _vault.SaveMetadata(metadata);

            // 到這裡，.flocked 已經落地、metadata 已經標記完成——資料本身已經安全了。
            // 清除原始明文是「收尾」動作，理由跟 CommitVaultEncryptAsync 完全一樣。
            string? cleanupWarning = null;
            try
            {
                await Task.Run(() =>
                {
                    if (isFolder)
                    {
                        SecureFileEraser.OverwriteAndDeleteFolder(originalPath);
                    }
                    else
                    {
                        SecureFileEraser.OverwriteAndDelete(originalPath);
                    }
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupWarning = $"加密已完成，但清除原始檔案時發生錯誤，請手動確認並刪除原始檔案：{ex.Message}";
            }

            _history?.Append(new HistoryEntry(
                uuid, metadata.OriginalName, HistoryAction.Encrypted, DateTimeOffset.UtcNow, metadata.Hint,
                SourcePath: originalPath,
                PasskeyEnabled: metadata.PasskeyEnabled,
                RecoveryKeyEnabled: metadata.RecoveryKeyEnabled));

            return new LockResult(true, uuid, finalFlockedPath, cleanupWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 搬移到一半失敗（例如磁碟滿了）——項目留在 Pending，不在這裡自動回滾，理由跟
            // CommitVaultEncryptAsync 完全一樣：呼叫端可能想讓使用者重試 commit。
            return new LockResult(false, "", "", $"完成加密時發生錯誤：{ex.Message}", ErrorCode: ErrorCodes.CommitPendingEncryptFailed, ErrorDetail: ex.Message);
        }
    }

    /// <summary>
    /// 算出單檔案分散式加密最終要落腳的 .flocked 路徑——沒有 destinationDir 就是原地
    /// （沿用 FlockedStatusChecker.ComputeFlockedPath 對 sourcePath 本身的計算）；有的話，
    /// 先組出「假設這個項目現在人在 destinationDir 底下」的虛擬路徑，再套用同一套副檔名
    /// 換算規則——EncryptPendingAsync（先做便宜的早期碰撞檢查）跟 CommitStandaloneEncryptAsync
    /// （真正決定搬去哪）共用同一份計算，確保兩處算出來的路徑不會不一致。
    /// </summary>
    private static string ComputeStandaloneFlockedTargetPath(string sourcePath, bool isFolder, string? destinationDir)
    {
        if (destinationDir is null)
        {
            return FlockedStatusChecker.ComputeFlockedPath(sourcePath, isFolder);
        }

        var name = isFolder
            ? Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(sourcePath);
        var virtualOriginalPath = Path.Combine(destinationDir, name);
        return FlockedStatusChecker.ComputeFlockedPath(virtualOriginalPath, isFolder);
    }

    /// <summary>
    /// 對應「按下取消」：整個放棄一筆 Pending 項目，Vault 裡的加密內容／metadata 都會被刪除，
    /// 原始檔案全程沒被動過（EncryptPendingAsync 本來就沒碰過它）。刻意做成幂等（比照
    /// VaultManager.DeleteItem 本身的幂等設計）——uuid 不存在或已經被清過都直接視為成功，
    /// 呼叫端不需要先檢查存不存在才敢呼叫。
    /// </summary>
    public Task RollbackPendingEncryptAsync(string uuid)
        => Task.Run(() => _vault.DeleteItem(uuid));

    /// <summary>
    /// 對應「中途關閉 App（pending 期間當機/關閉）」：啟動時呼叫一次，把上次留下的孤兒 Pending
    /// 項目全部安全清掉。刻意只看 metadata 裡的 Status 欄位，不用「marker 存不存在」推斷——
    /// marker 遺失可能是別的原因（例如使用者手動刪除一份合法的 Committed 紀錄），跟這裡要處理的
    /// 「使用者從頭到尾沒按過確認」是兩種不同情境，用錯判斷邏輯可能誤刪合法但 marker 遺失的紀錄。
    /// </summary>
    public async Task<int> RollbackAllPendingAsync()
    {
        var pendingUuids = await Task.Run(() =>
            _vault.ScanAll().Where(m => m.Status == LockStatus.Pending).Select(m => m.Uuid).ToList());

        foreach (var uuid in pendingUuids)
        {
            await RollbackPendingEncryptAsync(uuid);
        }

        return pendingUuids.Count;
    }

    private void TryCleanupOrphanedVaultEntry(string? uuid)
    {
        if (string.IsNullOrEmpty(uuid))
        {
            return;
        }
        try
        {
            _vault.DeleteItem(uuid);
        }
        catch (Exception)
        {
            // 盡力而為，清不掉就算了，不能讓清理失敗又拋出新的例外蓋掉原本要回報的錯誤。
        }
    }

    private sealed record EncryptionResult(
        bool Success,
        string? ErrorMessage,
        string? Uuid,
        byte[]? EncryptionKey,
        string? PasswordVerificationHashBase64,
        string? SaltBase64,
        long OriginalSizeBytes,
        List<string>? NestedUuids,
        string? TempZipPath,
        string? ErrorCode = null,
        string? ErrorDetail = null);

    /// <summary>
    /// 純粹的檔案 I/O／加密運算部分，不牽涉任何 Windows Hello / WinRT 呼叫，安全地丟進背景執行緒執行。
    /// 回傳的 EncryptionKey 刻意不在這裡清零——呼叫端（EncryptAsync）還要拿它去做 Passkey 包裝，
    /// 用完才會清零，見 EncryptAsync 的 finally 區塊。
    /// </summary>
    private EncryptionResult EncryptToVault(
        string path, bool isFolder, string password, StorageMode storageMode, IProgress<double>? progress = null)
    {
        var nestedUuids = new List<string>();
        string contentPath;
        string? tempZipToCleanup = null;

        try
        {
            if (isFolder)
            {
                foreach (var nestedPath in FolderArchiver.FindNestedLockedFiles(path))
                {
                    // .locked 指標檔跟 .flocked 獨立密文檔是完全不同的格式（見各自類別上的
                    // 說明），依副檔名分開解析，不能共用同一套讀取邏輯。
                    if (nestedPath.EndsWith(".flocked", StringComparison.OrdinalIgnoreCase))
                    {
                        if (FlockedFileFormat.TryReadUuid(nestedPath, out var flockedUuid))
                        {
                            nestedUuids.Add(flockedUuid!);
                        }
                    }
                    else
                    {
                        var nestedMarker = LockedMarkerFile.ReadFrom(nestedPath);
                        if (nestedMarker is not null)
                        {
                            nestedUuids.Add(nestedMarker.Uuid);
                        }
                    }
                }

                contentPath = FolderArchiver.CompressToTempZip(path);
                tempZipToCleanup = contentPath;
            }
            else
            {
                contentPath = path;
            }

            var originalSizeBytes = new FileInfo(contentPath).Length;

            var salt = Argon2KeyDerivation.GenerateSalt();
            var derived = Argon2KeyDerivation.DeriveKeys(password, salt);
            var uuid = Guid.NewGuid().ToString();

            // 串流處理：一次只把一個 chunk（預設 1MB）的明文留在記憶體，不管檔案多大，
            // 記憶體用量都不會跟著檔案大小線性增加（見 ChunkedCipher 的分塊加密設計）。
            using (var plaintextStream = File.OpenRead(contentPath))
            using (var encStream = _vault.OpenEncryptedContentWrite(uuid))
            {
                // Standalone 模式：Pending 階段先跟集中庫加密共用同一個 Vault 暫存位置
                // （{uuid}.enc），只是內容前面多寫一段 .flocked header——這樣 commit 時只要
                // 把這個檔案整個 File.Move 到最終位置就是一份合法的 .flocked 檔案，不需要
                // 重新讀寫整份密文；Rollback 也直接沿用既有的 _vault.DeleteItem(uuid)，
                // 不需要另外設計一套暫存檔清理機制。
                if (storageMode == StorageMode.Standalone)
                {
                    FlockedFileFormat.WriteHeader(encStream, uuid);
                }
                ChunkedCipher.EncryptStream(derived.EncryptionKey, plaintextStream, encStream, progress: progress, totalBytes: originalSizeBytes);
            }

            return new EncryptionResult(
                true, null, uuid, derived.EncryptionKey,
                Convert.ToBase64String(derived.VerificationHash), Convert.ToBase64String(salt),
                originalSizeBytes, nestedUuids, tempZipToCleanup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EncryptionResult(
                false, $"加密過程發生錯誤：{ex.Message}", null, null, null, null, 0, null, tempZipToCleanup,
                ErrorCode: ErrorCodes.EncryptError, ErrorDetail: ex.Message);
        }
        catch (Exception ex)
        {
            // 兜底：任何沒特別預期到的例外（例如底層密碼學函式庫丟出的例外）都不應該讓整個 App 崩潰，
            // 一律轉換成失敗結果回傳，讓 GUI 能顯示錯誤訊息而不是整個程式當掉。
            return new EncryptionResult(
                false, $"加密過程發生未預期的錯誤：{ex.Message}", null, null, null, null, 0, null, tempZipToCleanup,
                ErrorCode: ErrorCodes.EncryptUnexpectedError, ErrorDetail: ex.Message);
        }
    }

    /// <summary>對應原本雙擊 .locked 檔案的解密流程：先讀 marker 驗證簽章，再往下走。</summary>
    public Task<UnlockResult> DecryptAsync(string lockedMarkerPath, string password)
        => Task.Run(() => DecryptViaMarkerCore(lockedMarkerPath, password));

    private UnlockResult DecryptViaMarkerCore(string lockedMarkerPath, string password)
    {
        var marker = LockedMarkerFile.ReadFrom(lockedMarkerPath);
        if (marker is null)
        {
            return new UnlockResult(false, "", "找不到或無法解析這個 .locked 檔案", ErrorCode: ErrorCodes.InvalidMarker);
        }

        var vaultConfig = _vault.LoadOrCreateConfig();
        var signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);

        if (!marker.VerifySignature(signingKey))
        {
            return new UnlockResult(false, "", "指標檔驗證失敗，內容可能已被竄改", ErrorCode: ErrorCodes.MarkerSignatureInvalid);
        }

        var metadata = _vault.LoadMetadata(marker.Uuid);
        if (metadata is null)
        {
            return new UnlockResult(false, "", "在集中管理區找不到對應的加密內容", ErrorCode: ErrorCodes.VaultContentMissing);
        }

        var parentDir = Path.GetDirectoryName(Path.GetFullPath(lockedMarkerPath));
        if (parentDir is null)
        {
            return new UnlockResult(false, "", "無法判斷指標檔所在的資料夾", ErrorCode: ErrorCodes.CannotDetermineFolder);
        }

        var result = DecryptAndRestore(metadata, password, parentDir);

        if (result.Success)
        {
            // 這個路徑本來就是從 marker 檔案本身進來的，解密成功後直接刪除它就好，不用再檢查存不存在。
            File.Delete(lockedMarkerPath);
        }

        return result;
    }

    /// <summary>
    /// 對應「已加密清單」頁直接選項目解密：不需要事先找到 .locked 檔案，直接用 UUID 從 Vault 解密。
    /// destinationDir 為 null 時（使用者選擇「還原到原始位置」），退而求其次用加密當下記錄的原始路徑
    /// 所在的資料夾；使用者若指定了 destinationDir（自己選了另一個地方存），就用那個位置。
    /// 不論還原到哪裡，解密成功後都會反推出原本 .locked 應該在的位置，檢查那裡現在還有沒有東西——
    /// 有（而且真的是同一個 UUID）就清掉，避免留下一個已經失效、會誤導使用者的指標檔；沒有就跳過，不當成錯誤。
    /// 這個檢查永遠是根據「原始位置」判斷，跟這次實際存去哪裡無關，因為 .locked 指標檔本來就只可能出現在
    /// 原始位置，不會出現在使用者這次選的新位置。
    /// </summary>
    public Task<UnlockResult> DecryptByUuidAsync(string uuid, string password, string? destinationDir = null)
        => Task.Run(() => DecryptByUuidCore(uuid, password, destinationDir));

    private UnlockResult DecryptByUuidCore(string uuid, string password, string? destinationDir)
    {
        if (!TryLoadMetadata(uuid, out var metadata, out var notFoundResult))
        {
            return notFoundResult!;
        }

        var destinationParentDir = ResolveDestinationParentDir(metadata, destinationDir, out var resolveError);
        if (destinationParentDir is null)
        {
            return new UnlockResult(false, "", resolveError!, ErrorCode: ErrorCodes.ResolveDestinationError, ErrorDetail: resolveError);
        }

        var result = DecryptAndRestore(metadata, password, destinationParentDir);

        if (result.Success)
        {
            CleanupOriginalArtifactIfMatches(metadata, uuid);
        }

        return result;
    }

    /// <summary>
    /// 對應規格文件 8.1 節「Passkey 快速解鎖」：不需要密碼，改用 Windows Hello 簽章衍生出的
    /// 包裝金鑰解開內容金鑰。ownerWindowHandle 用來套用視窗焦點緩解（見 PasskeyProtector.SignChallengeAsync）。
    /// </summary>
    public async Task<UnlockResult> DecryptByPasskeyAsync(string uuid, IntPtr ownerWindowHandle, string? destinationDir = null)
    {
        if (!TryLoadMetadata(uuid, out var metadata, out var notFoundResult))
        {
            return notFoundResult!;
        }

        if (!metadata.PasskeyEnabled || metadata.PasskeyCredentialName is null
            || metadata.PasskeyChallenge is null || metadata.PasskeyWrappedContentKey is null)
        {
            return new UnlockResult(false, "", "這個項目沒有啟用 Passkey 快速解鎖", ErrorCode: ErrorCodes.PasskeyNotEnabled);
        }

        var challenge = Convert.FromBase64String(metadata.PasskeyChallenge);
        var signature = await PasskeyProtector.SignChallengeAsync(metadata.PasskeyCredentialName, challenge, ownerWindowHandle);
        if (signature is null)
        {
            return new UnlockResult(false, "", "Passkey 驗證失敗或已取消", ErrorCode: ErrorCodes.PasskeyVerificationFailed);
        }

        byte[] contentKey;
        try
        {
            var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
            try
            {
                contentKey = PasskeyProtector.UnwrapContentKey(wrappingKey, metadata.PasskeyWrappedContentKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }
        catch (CryptographicException)
        {
            return new UnlockResult(false, "", "Passkey 解包內容金鑰失敗，資料可能已損毀", ErrorCode: ErrorCodes.PasskeyUnwrapFailed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        // 這裡連同 ResolveDestinationParentDir（含 Directory.CreateDirectory）一起丟進背景執行緒，
        // 不只是原本的 RestoreFromKey——跟這個方法一開始特意把 Passkey 簽章步驟留在呼叫端執行緒
        // 的理由相反，這一段純粹是檔案 I/O，沒有 WinRT 呼叫，搬進背景執行緒沒有風險。
        return await Task.Run(() => FinishAfterKeyResolved(metadata, uuid, contentKey, destinationDir, "passkey"));
    }

    /// <summary>對應「恢復金鑰」備援路徑：不需要密碼、不需要 Windows Hello，用使用者自己抄下來的恢復金鑰解鎖。</summary>
    public Task<UnlockResult> DecryptByRecoveryKeyAsync(string uuid, string recoveryKeyInput, string? destinationDir = null)
        => Task.Run(() => DecryptByRecoveryKeyCore(uuid, recoveryKeyInput, destinationDir));

    private UnlockResult DecryptByRecoveryKeyCore(string uuid, string recoveryKeyInput, string? destinationDir)
    {
        if (!TryLoadMetadata(uuid, out var metadata, out var notFoundResult))
        {
            return notFoundResult!;
        }

        if (!metadata.RecoveryKeyEnabled || metadata.RecoveryKeyWrappedContentKey is null)
        {
            return new UnlockResult(false, "", "這個項目沒有啟用恢復金鑰", ErrorCode: ErrorCodes.RecoveryKeyNotEnabled);
        }

        var recoveryKeyBytes = RecoveryKeyProtector.ParseUserInput(recoveryKeyInput);
        if (recoveryKeyBytes is null)
        {
            return new UnlockResult(false, "", "恢復金鑰格式不正確，請確認有沒有打錯或漏掉字元", ErrorCode: ErrorCodes.RecoveryKeyInvalidFormat);
        }

        byte[] contentKey;
        try
        {
            var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
            try
            {
                contentKey = RecoveryKeyProtector.UnwrapContentKey(wrappingKey, metadata.RecoveryKeyWrappedContentKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }
        catch (CryptographicException)
        {
            return new UnlockResult(false, "", "恢復金鑰不正確", ErrorCode: ErrorCodes.RecoveryKeyIncorrect);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKeyBytes);
        }

        return FinishAfterKeyResolved(metadata, uuid, contentKey, destinationDir, "recoveryKey");
    }

    /// <summary>
    /// 獨立解密流程 Verify 階段（密碼路徑）：只驗證密碼對不對、把結果存進 pending 字典，
    /// 不寫入任何檔案。跟既有的 VerifyPasswordAsync（給永久刪除前重新驗證用）不同的是，那個
    /// 方法驗證完就把衍生金鑰歸零、不留東西；這裡要保留原始明文密碼，讓後續
    /// CommitPendingDecryptAsync 可以直接交給既有的 DecryptAndRestore 完成真正的還原
    /// （密碼路徑沒有一個中間態的 contentKey 可以先解出來保存，DecryptAndRestore 本身就是
    /// 「驗證＋衍生金鑰＋還原」一次做完，所以只能把密碼原樣留著等 commit 時重新驗證一次）。
    /// </summary>
    public Task<VerifyPasswordResult> VerifyDecryptPasswordAsync(string uuid, string password)
        => Task.Run(() =>
        {
            var metadata = _vault.LoadMetadata(uuid);
            if (metadata is null)
            {
                return new VerifyPasswordResult(false, "找不到對應的加密紀錄", ErrorCode: ErrorCodes.RecordNotFound);
            }

            var verification = VerifyPasswordAndDeriveKey(metadata, password);
            if (verification.EncryptionKey is not null)
            {
                // 這裡只需要證明密碼正確，不需要衍生金鑰本身（commit 階段會用密碼重新衍生一次）。
                CryptographicOperations.ZeroMemory(verification.EncryptionKey);
            }

            if (!verification.Success)
            {
                return new VerifyPasswordResult(false, verification.ErrorMessage, ErrorCode: verification.ErrorCode, ErrorDetail: verification.ErrorDetail);
            }

            _pendingDecrypts[uuid] = new PendingDecrypt(metadata, ContentKey: null, Password: password, UnlockMethod: "password");
            return new VerifyPasswordResult(true);
        });

    /// <summary>
    /// 獨立解密流程 Verify 階段（Passkey 路徑）：複製自 DecryptByPasskeyAsync 到解出 contentKey
    /// 為止的邏輯，但不呼叫 FinishAfterKeyResolved 立刻還原——把解出來的 contentKey 存進 pending
    /// 字典，等使用者選定存檔位置後才透過 CommitPendingDecryptAsync 真正寫入，這樣才能讓「驗證
    /// 成功打勾」跟「選存檔位置」是使用者可以分開、中途取消的兩個步驟，同時只跳一次 Windows
    /// Hello（不用像密碼路徑那樣在 commit 階段重新驗證一次）。
    /// </summary>
    public async Task<VerifyPasswordResult> VerifyDecryptByPasskeyAsync(string uuid, IntPtr ownerWindowHandle)
    {
        var metadata = _vault.LoadMetadata(uuid);
        if (metadata is null)
        {
            return new VerifyPasswordResult(false, "找不到對應的加密紀錄", ErrorCode: ErrorCodes.RecordNotFound);
        }

        if (!metadata.PasskeyEnabled || metadata.PasskeyCredentialName is null
            || metadata.PasskeyChallenge is null || metadata.PasskeyWrappedContentKey is null)
        {
            return new VerifyPasswordResult(false, "這個項目沒有啟用 Passkey 快速解鎖", ErrorCode: ErrorCodes.PasskeyNotEnabled);
        }

        var challenge = Convert.FromBase64String(metadata.PasskeyChallenge);
        var signature = await PasskeyProtector.SignChallengeAsync(metadata.PasskeyCredentialName, challenge, ownerWindowHandle);
        if (signature is null)
        {
            return new VerifyPasswordResult(false, "Passkey 驗證失敗或已取消", ErrorCode: ErrorCodes.PasskeyVerificationFailed);
        }

        byte[] contentKey;
        try
        {
            var wrappingKey = PasskeyProtector.DeriveWrappingKey(signature);
            try
            {
                contentKey = PasskeyProtector.UnwrapContentKey(wrappingKey, metadata.PasskeyWrappedContentKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }
        catch (CryptographicException)
        {
            return new VerifyPasswordResult(false, "Passkey 解包內容金鑰失敗，資料可能已損毀", ErrorCode: ErrorCodes.PasskeyUnwrapFailed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }

        _pendingDecrypts[uuid] = new PendingDecrypt(metadata, ContentKey: contentKey, Password: null, UnlockMethod: "passkey");
        return new VerifyPasswordResult(true);
    }

    /// <summary>獨立解密流程 Verify 階段（恢復金鑰路徑）：跟 Passkey 路徑同樣的做法，複製自 DecryptByRecoveryKeyCore。</summary>
    public Task<VerifyPasswordResult> VerifyDecryptByRecoveryKeyAsync(string uuid, string recoveryKeyInput)
        => Task.Run(() =>
        {
            var metadata = _vault.LoadMetadata(uuid);
            if (metadata is null)
            {
                return new VerifyPasswordResult(false, "找不到對應的加密紀錄", ErrorCode: ErrorCodes.RecordNotFound);
            }

            if (!metadata.RecoveryKeyEnabled || metadata.RecoveryKeyWrappedContentKey is null)
            {
                return new VerifyPasswordResult(false, "這個項目沒有啟用恢復金鑰", ErrorCode: ErrorCodes.RecoveryKeyNotEnabled);
            }

            var recoveryKeyBytes = RecoveryKeyProtector.ParseUserInput(recoveryKeyInput);
            if (recoveryKeyBytes is null)
            {
                return new VerifyPasswordResult(false, "恢復金鑰格式不正確，請確認有沒有打錯或漏掉字元", ErrorCode: ErrorCodes.RecoveryKeyInvalidFormat);
            }

            byte[] contentKey;
            try
            {
                var wrappingKey = RecoveryKeyProtector.DeriveWrappingKey(recoveryKeyBytes);
                try
                {
                    contentKey = RecoveryKeyProtector.UnwrapContentKey(wrappingKey, metadata.RecoveryKeyWrappedContentKey);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(wrappingKey);
                }
            }
            catch (CryptographicException)
            {
                return new VerifyPasswordResult(false, "恢復金鑰不正確", ErrorCode: ErrorCodes.RecoveryKeyIncorrect);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recoveryKeyBytes);
            }

            _pendingDecrypts[uuid] = new PendingDecrypt(metadata, ContentKey: contentKey, Password: null, UnlockMethod: "recoveryKey");
            return new VerifyPasswordResult(true);
        });

    /// <summary>
    /// 獨立解密流程 Commit 階段：使用者選定存檔位置後呼叫，取出對應 uuid 的 pending 驗證結果，
    /// 依當初是哪種驗證方式分別交給既有的 DecryptAndRestore（密碼）或 FinishAfterKeyResolved
    /// （Passkey／恢復金鑰）完成真正的還原——這兩個既有私有方法完全不用改，History 紀錄／
    /// 指標檔清理都是它們內部本來就會做的事，這裡不用額外處理。
    /// 找不到 pending 紀錄（沒有 verify 過，或已經 commit/cancel 過）視為呼叫端的邏輯錯誤，
    /// 回傳明確的錯誤碼而不是拋例外。
    /// </summary>
    public Task<UnlockResult> CommitPendingDecryptAsync(string uuid, string? destinationDir)
        => Task.Run(() =>
        {
            if (!_pendingDecrypts.TryRemove(uuid, out var pending))
            {
                return new UnlockResult(false, "", "找不到對應的解密驗證紀錄，請重新選擇檔案", ErrorCode: ErrorCodes.PendingItemNotFound);
            }

            if (pending.Password is not null)
            {
                var destinationParentDir = ResolveDestinationParentDir(pending.Metadata, destinationDir, out var resolveError);
                if (destinationParentDir is null)
                {
                    return new UnlockResult(false, "", resolveError!, ErrorCode: ErrorCodes.ResolveDestinationError, ErrorDetail: resolveError);
                }

                var result = DecryptAndRestore(pending.Metadata, pending.Password, destinationParentDir);
                if (result.Success)
                {
                    CleanupOriginalArtifactIfMatches(pending.Metadata, uuid);
                }
                return result;
            }

            return FinishAfterKeyResolved(pending.Metadata, uuid, pending.ContentKey!, destinationDir, pending.UnlockMethod);
        });

    /// <summary>
    /// 獨立解密流程使用者按取消（或整個信封被中途關閉）時呼叫：丟掉 pending 驗證結果，
    /// 不還原任何檔案。ContentKey 路徑（Passkey／恢復金鑰）解出來的內容金鑰要歸零，密碼路徑
    /// 沒有額外持有的金鑰可以清（密碼字串本身跟其他既有程式碼把密碼當 string 傳遞是同一種
    /// 做法，沒有引入新風險）。刻意做成幂等——重複呼叫同一個 uuid、或呼叫一個根本沒有 pending
    /// 紀錄的 uuid，都直接安全地當沒發生過，不回傳錯誤（呼叫端通常是「保險起見關閉時清一下」，
    /// 不需要知道有沒有真的清到東西）。
    /// </summary>
    public Task CancelPendingDecryptAsync(string uuid)
        => Task.Run(() =>
        {
            if (_pendingDecrypts.TryRemove(uuid, out var pending) && pending.ContentKey is not null)
            {
                CryptographicOperations.ZeroMemory(pending.ContentKey);
            }
        });

    /// <summary>
    /// DecryptByPasskeyAsync／DecryptByRecoveryKeyCore 共用：兩者都是「先透過各自的方式解出
    /// contentKey，再解目的地資料夾、還原、清掉舊標記檔」，差別只有 unlockMethod 字串跟外層
    /// 是否要包一層 Task.Run。DecryptByUuidCore 不套用這個方法——它是直接拿密碼交給
    /// DecryptAndRestore 內部同時做「驗證＋衍生金鑰＋還原」，呼叫這一層時根本沒有一個已經
    /// 解開的 contentKey 可以傳進來，硬套會讓這個方法多長出一個處理「沒有 key」的分支，
    /// 介面被迫變複雜，不划算——維持 DecryptByUuidCore 自己的序列不變。
    /// </summary>
    private UnlockResult FinishAfterKeyResolved(LockedItemMetadata metadata, string uuid, byte[] contentKey, string? destinationDir, string unlockMethod)
    {
        var destinationParentDir = ResolveDestinationParentDir(metadata, destinationDir, out var resolveError);
        if (destinationParentDir is null)
        {
            CryptographicOperations.ZeroMemory(contentKey);
            return new UnlockResult(false, "", resolveError!, ErrorCode: ErrorCodes.ResolveDestinationError, ErrorDetail: resolveError);
        }

        var result = RestoreFromKey(metadata, contentKey, destinationParentDir, unlockMethod);

        if (result.Success)
        {
            CleanupOriginalArtifactIfMatches(metadata, uuid);
        }

        return result;
    }

    /// <summary>找不到 metadata 時，三個 Decrypt*Core／Async 入口共用的「找不到對應加密紀錄」結果。</summary>
    private bool TryLoadMetadata(string uuid, out LockedItemMetadata metadata, out UnlockResult? notFoundResult)
    {
        var loaded = _vault.LoadMetadata(uuid);
        if (loaded is null)
        {
            metadata = null!;
            notFoundResult = new UnlockResult(false, "", "找不到對應的加密紀錄", ErrorCode: ErrorCodes.RecordNotFound);
            return false;
        }

        metadata = loaded;
        notFoundResult = null;
        return true;
    }

    /// <summary>DecryptByUuidCore／DecryptByPasskeyAsync 共用：算出解密後要還原到哪個資料夾。</summary>
    private static string? ResolveDestinationParentDir(LockedItemMetadata metadata, string? destinationDir, out string? errorMessage)
    {
        errorMessage = null;

        if (!string.IsNullOrWhiteSpace(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
            return destinationDir;
        }

        var originalParentDir = Path.GetDirectoryName(Path.GetFullPath(metadata.OriginalPath));
        if (originalParentDir is null)
        {
            errorMessage = "無法判斷原始路徑所在的資料夾";
            return null;
        }

        Directory.CreateDirectory(originalParentDir);
        return originalParentDir;
    }

    /// <summary>
    /// DecryptByUuidCore／DecryptByPasskeyAsync／DecryptByRecoveryKeyAsync／TryDeleteRecordAsync
    /// 共用：解密成功（或使用者直接刪除紀錄）後，把原本位置留著的「這筆加密實際的產出物」
    /// 清掉——Vault 模式是 .locked 指標檔，Standalone 模式是 .flocked 檔案本體，兩者的檔案
    /// 格式、驗證方式都不一樣（見各自類別上的說明），依 StorageMode 分派給對應的實作，
    /// 呼叫端不用自己判斷該用哪一套。
    /// </summary>
    private void CleanupOriginalArtifactIfMatches(LockedItemMetadata metadata, string uuid)
    {
        if (metadata.StorageMode == StorageMode.Standalone)
        {
            CleanupFlockedIfMatches(metadata, uuid);
            return;
        }

        CleanupMarkerIfMatches(metadata, uuid);
    }

    /// <summary>
    /// 反推原本 .locked 應該在的位置，有（而且真的是同一個 UUID、簽章也驗證通過）就清掉，
    /// 避免留下一個已經失效、會誤導使用者的指標檔；沒有就跳過。
    /// 這裡刻意額外驗證簽章、不能只看 UUID 是否相符——metadata.OriginalPath 是明文的本機資料，
    /// 沒有簽章保護，理論上可能被竄改；如果只看 UUID，攻擊者只要能在算出來的位置預先放一個
    /// UUID 對得上的假檔案，就有機會誘使這裡刪掉非預期的檔案。加上簽章驗證後，
    /// 攻擊者還得知道 Vault 的簽章金鑰才偽造得出通過驗證的假指標檔，門檻高很多。
    /// </summary>
    private void CleanupMarkerIfMatches(LockedItemMetadata metadata, string uuid)
    {
        var expectedMarkerPath = MarkerStatusChecker.ComputeMarkerPath(metadata.OriginalPath, metadata.Type == ItemType.Folder);
        if (!File.Exists(expectedMarkerPath))
        {
            return;
        }

        var marker = LockedMarkerFile.ReadFrom(expectedMarkerPath);
        if (marker is null || marker.Uuid != uuid)
        {
            return;
        }

        var vaultConfig = _vault.LoadOrCreateConfig();
        var signingKey = Convert.FromBase64String(vaultConfig.SigningKeyBase64);
        if (!marker.VerifySignature(signingKey))
        {
            return;
        }

        File.Delete(expectedMarkerPath);
    }

    /// <summary>
    /// 跟 CleanupMarkerIfMatches 平行：反推原本 .flocked 應該在的位置，UUID 對得上就刪掉。
    /// .flocked 沒有像 .locked 那樣的獨立簽章可以驗證（見 FlockedFileFormat 上的說明——完整性
    /// 由密文串流自己的 AES-GCM Auth Tag 保護），這裡的 UUID 比對已經是能做的最後一道防線。
    /// </summary>
    private void CleanupFlockedIfMatches(LockedItemMetadata metadata, string uuid)
    {
        var expectedFlockedPath = FlockedStatusChecker.ComputeFlockedPath(metadata.OriginalPath, metadata.Type == ItemType.Folder);
        if (!File.Exists(expectedFlockedPath))
        {
            return;
        }

        if (!FlockedFileFormat.TryReadUuid(expectedFlockedPath, out var foundUuid) || foundUuid != uuid)
        {
            return;
        }

        File.Delete(expectedFlockedPath);
    }

    /// <summary>密碼路徑：驗證密碼、拿到內容金鑰後，交給 RestoreFromKey 做剩下的還原工作。</summary>
    private UnlockResult DecryptAndRestore(LockedItemMetadata metadata, string password, string destinationParentDir)
    {
        var verification = VerifyPasswordAndDeriveKey(metadata, password);
        if (!verification.Success)
        {
            return new UnlockResult(false, "", verification.ErrorMessage!, ErrorCode: verification.ErrorCode, ErrorDetail: verification.ErrorDetail);
        }

        return RestoreFromKey(metadata, verification.EncryptionKey!, destinationParentDir, "password");
    }

    /// <summary>
    /// 對應「已加密清單」頁永久刪除前的密碼再驗證：跟 DecryptAndRestore 共用同一套密碼驗證＋
    /// 鎖定機制，但驗證通過後不呼叫 RestoreFromKey——永久刪除不需要、也不該碰觸加密內容本身，
    /// 只是要證明「按下永久刪除的人真的知道密碼」。
    /// </summary>
    public Task<VerifyPasswordResult> VerifyPasswordAsync(string uuid, string password)
        => Task.Run(() => VerifyPasswordCore(uuid, password));

    private VerifyPasswordResult VerifyPasswordCore(string uuid, string password)
    {
        var metadata = _vault.LoadMetadata(uuid);
        if (metadata is null)
        {
            // 這個方法唯一的呼叫端是「永久刪除前的密碼再驗證」——找不到 metadata 代表這筆紀錄
            // 已經沒有實際內容需要密碼保護，驗證的目的（證明按下刪除的人真的知道密碼）已經不成立，
            // 視同驗證通過讓使用者可以繼續走到最終確認彈窗，把這筆孤兒快取列清乾淨，而不是卡在
            // 一個永遠驗證不過的死結裡。
            return new VerifyPasswordResult(true);
        }

        var verification = VerifyPasswordAndDeriveKey(metadata, password);
        if (verification.EncryptionKey is not null)
        {
            // 這條路徑不需要內容金鑰本身（只是要證明「這個人真的知道密碼」），驗證完就清掉，
            // 不像 DecryptAndRestore 那樣把金鑰交給 RestoreFromKey 繼續用。
            CryptographicOperations.ZeroMemory(verification.EncryptionKey);
        }

        return verification.Success
            ? new VerifyPasswordResult(true)
            : new VerifyPasswordResult(false, verification.ErrorMessage, ErrorCode: verification.ErrorCode, ErrorDetail: verification.ErrorDetail);
    }

    /// <summary>DecryptAndRestore／VerifyPasswordCore 共用：檢查鎖定狀態、驗證密碼、衍生內容金鑰，並記錄成功/失敗次數。</summary>
    private readonly record struct PasswordVerification(bool Success, byte[]? EncryptionKey, string? ErrorCode, string? ErrorDetail, string? ErrorMessage);

    private PasswordVerification VerifyPasswordAndDeriveKey(LockedItemMetadata metadata, string password)
    {
        if (_lockout is not null)
        {
            var lockoutStatus = _lockout.CheckStatus(metadata.Uuid);
            if (lockoutStatus.IsLockedOut)
            {
                return new PasswordVerification(false, null, ErrorCodes.LockedOut,
                    ((int)lockoutStatus.RemainingLockout!.Value.TotalSeconds).ToString(),
                    $"密碼錯誤次數過多，請在 {FormatRemaining(lockoutStatus.RemainingLockout!.Value)}後再試");
            }
        }

        var salt = Convert.FromBase64String(metadata.Salt);
        var storedHash = Convert.FromBase64String(metadata.PasswordVerificationHash);

        var (isValid, encryptionKey) = Argon2KeyDerivation.VerifyPassword(
            password, salt, storedHash,
            metadata.Argon2TimeCost, metadata.Argon2MemoryCostKb, metadata.Argon2Parallelism);

        if (!isValid || encryptionKey is null)
        {
            _lockout?.RecordFailedAttempt(metadata.Uuid);
            return new PasswordVerification(false, null, ErrorCodes.PasswordIncorrect, null, "密碼錯誤");
        }

        _lockout?.RecordSuccess(metadata.Uuid);
        return new PasswordVerification(true, encryptionKey, null, null, null);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        return remaining.TotalMinutes >= 1
            ? $"{Math.Ceiling(remaining.TotalMinutes)} 分鐘"
            : $"{Math.Ceiling(remaining.TotalSeconds)} 秒";
    }

    /// <summary>
    /// 安全檢查：metadata.OriginalName 理論上只會是加密當下用 Path.GetFileName 取出的單純檔名，
    /// 但 .meta.json 是明文的本機檔案，沒有像 .locked 指標檔那樣有 HMAC 簽章保護，理論上可能被竄改或損毀。
    /// 如果不檢查就直接拿去 Path.Combine，一個被竄改成絕對路徑（或帶 ".." 路徑穿越片段）的檔名，
    /// 可能導致解密內容被寫到使用者指定的還原資料夾之外的任意位置——這裡在真的動筆寫檔案之前擋掉這種情況。
    /// </summary>
    private static bool IsSafeRestoreFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        if (Path.IsPathRooted(name))
        {
            return false;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        if (name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// 對應「單檔案分散式加密」功能規劃 §8：算出密文串流的來源——Vault 模式維持原樣讀
    /// {uuid}.enc；Standalone 模式改讀 .flocked 檔案本體，跳過 header 並驗證裡面的 UUID
    /// 真的對得上這筆 metadata（防禦：避免 OriginalPath 被竄改指到別的 .flocked 檔案時
    /// 誤把不相關的內容解出來）。header 無法解析或 UUID 不符都丟 InvalidDataException，
    /// 跟 RestoreFromKey 既有的「內容損毀」錯誤路徑共用同一套處理（見那裡的 catch 區塊）。
    /// </summary>
    private Stream OpenContentStreamForDecrypt(LockedItemMetadata metadata)
    {
        if (metadata.StorageMode != StorageMode.Standalone)
        {
            return _vault.OpenEncryptedContentRead(metadata.Uuid);
        }

        var flockedPath = FlockedStatusChecker.ComputeFlockedPath(metadata.OriginalPath, metadata.Type == ItemType.Folder);
        var stream = File.OpenRead(flockedPath);
        if (!FlockedFileFormat.TryReadHeader(stream, out var headerUuid) || headerUuid != metadata.Uuid)
        {
            stream.Dispose();
            throw new InvalidDataException(".flocked 檔案的 header 無法解析，或裡面的 UUID 跟這筆紀錄不符");
        }

        return stream;
    }

    /// <summary>
    /// DecryptAndRestore（密碼路徑）跟 DecryptByPasskeyAsync／DecryptByRecoveryKeyAsync 共用的核心還原邏輯：
    /// 拿到內容金鑰之後，解密內容、寫回目的地、清除 Vault 內的項目、記錄歷史紀錄。
    /// 呼叫端負責把 encryptionKey 準備好（不管是密碼衍生、Passkey 解包，還是恢復金鑰解包出來的），
    /// 這裡負責用完清零；unlockMethod 只是拿來寫進使用紀錄，不影響解密邏輯本身。
    /// </summary>
    private UnlockResult RestoreFromKey(LockedItemMetadata metadata, byte[] encryptionKey, string destinationParentDir, string unlockMethod)
    {
        if (!IsSafeRestoreFileName(metadata.OriginalName))
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            return new UnlockResult(false, "", "這筆紀錄的檔名資訊看起來不正常（可能已損毀或被竄改），為了安全拒絕還原", ErrorCode: ErrorCodes.UnsafeFileName);
        }

        var destinationPath = Path.Combine(destinationParentDir, metadata.OriginalName);

        if (metadata.Type == ItemType.Folder)
        {
            if (Directory.Exists(destinationPath))
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
                return new UnlockResult(false, "", $"還原失敗，目的地已經有同名資料夾：{destinationPath}", ErrorCode: ErrorCodes.DestinationFolderExists, ErrorDetail: destinationPath);
            }
            Directory.CreateDirectory(FolderArchiver.TempDirectory);
        }
        else if (File.Exists(destinationPath))
        {
            CryptographicOperations.ZeroMemory(encryptionKey);
            return new UnlockResult(false, "", $"還原失敗，目的地已經有同名檔案：{destinationPath}", ErrorCode: ErrorCodes.DestinationFileExists, ErrorDetail: destinationPath);
        }

        // 資料夾的話先解密寫進一個暫存 zip，再解壓縮還原成資料夾結構；檔案的話直接解密寫到目的地。
        var actualWritePath = metadata.Type == ItemType.Folder
            ? Path.Combine(FolderArchiver.TempDirectory, $"{Guid.NewGuid()}.zip")
            : destinationPath;

        try
        {
            try
            {
                // 串流解密：一次只處理一個 chunk，全程不會有「整份明文」同時存在記憶體裡。
                // StorageMode=Standalone 的項目密文不在 Vault 裡（commit 時已經搬到 .flocked
                // 檔案本體，見 CommitStandaloneEncryptAsync），改從那裡讀，讀之前要先跳過
                // 前面的 header 並確認 UUID 對得上——理由跟 FlockedStatusChecker 一致。
                using (var encStream = OpenContentStreamForDecrypt(metadata))
                using (var outputStream = File.Create(actualWritePath))
                {
                    ChunkedCipher.DecryptStream(encryptionKey, encStream, outputStream);
                }
            }
            catch
            {
                // 解密中途失敗（密碼錯誤/Passkey 解包錯誤在這裡不會發生，因為呼叫端已經先驗證過；
                // 這裡會是內容損毀/被竄改），不留下一個寫到一半、內容不完整的檔案在磁碟上誤導使用者。
                if (File.Exists(actualWritePath))
                {
                    try { File.Delete(actualWritePath); } catch (IOException) { /* 盡力而為，清不掉就算了 */ }
                }
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
            }

            if (metadata.Type == ItemType.Folder)
            {
                try
                {
                    FolderArchiver.ExtractZipToFolder(actualWritePath, destinationPath);
                }
                finally
                {
                    SecureFileEraser.OverwriteAndDelete(actualWritePath);
                }
            }

            _vault.DeleteItem(metadata.Uuid);
            _history?.Append(new HistoryEntry(
                metadata.Uuid, metadata.OriginalName, HistoryAction.Decrypted, DateTimeOffset.UtcNow, null,
                UnlockMethod: unlockMethod, RestoredPath: destinationPath));

            return new UnlockResult(true, destinationPath);
        }
        catch (CryptographicException)
        {
            return new UnlockResult(false, "", "解密失敗，加密內容可能已損毀", ErrorCode: ErrorCodes.ContentCorrupted);
        }
        catch (InvalidDataException ex)
        {
            return new UnlockResult(false, "", $"解密失敗，加密內容已損毀：{ex.Message}", ErrorCode: ErrorCodes.ContentCorruptedWithDetail, ErrorDetail: ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new UnlockResult(false, "", $"解密過程發生錯誤：{ex.Message}", ErrorCode: ErrorCodes.DecryptError, ErrorDetail: ex.Message);
        }
        catch (Exception ex)
        {
            return new UnlockResult(false, "", $"解密過程發生未預期的錯誤：{ex.Message}", ErrorCode: ErrorCodes.DecryptUnexpectedError, ErrorDetail: ex.Message);
        }
    }

    public Task<DeleteRecordResult> TryDeleteRecordAsync(string uuid, bool force = false)
        => Task.Run(() =>
        {
            var metadata = _vault.LoadMetadata(uuid);
            if (metadata is null)
            {
                return new DeleteRecordResult(false, false, null, "找不到對應的加密紀錄", ErrorCode: ErrorCodes.RecordNotFound);
            }

            if (metadata.ContainsNestedLocks.Count > 0 && !force)
            {
                return new DeleteRecordResult(false, true, metadata.ContainsNestedLocks);
            }

            _vault.DeleteItem(uuid);

            // Vault 裡的加密內容刪掉之後，原本位置留著的產出物（.locked 指標檔，或
            // Standalone 模式的 .flocked 檔案本體）會變成一個指向不存在內容的死連結／孤兒
            // 密文——順便清掉它，避免使用者之後雙擊到一個只會顯示「找不到對應的加密紀錄」
            // 的失效檔案，或留著一份已經沒有對應紀錄、卻還裝著實際密文的孤兒檔案。
            CleanupOriginalArtifactIfMatches(metadata, uuid);

            _history?.Append(new HistoryEntry(uuid, metadata.OriginalName, HistoryAction.RecordDeleted, DateTimeOffset.UtcNow, null));

            return new DeleteRecordResult(true, false);
        });

}