using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Protocol;

/// <summary>
/// 對應「加密」訊息的單筆回報：批次加密每完成一個項目就回報一次，讓前端可以即時更新，
/// 不用等全部跑完才看到結果。PasskeyRequested 是使用者當初有沒有勾選要開 Passkey，
/// PasskeyEnabled 是實際查詢 Vault 後真的有沒有開成功——兩者可能不一致（例如使用者
/// 取消了 Windows Hello 驗證），前端要分開顯示。
/// </summary>
public sealed record EncryptItemResponse(
    string Path, bool Success, string Uuid, string LockedMarkerPath,
    string? ErrorMessage, string? ErrorCode, string? ErrorDetail,
    bool PasskeyRequested, bool PasskeyEnabled, string? RecoveryKey)
{
    public EncryptItemResponse(string path, LockResult result, bool passkeyRequested, bool actuallyPasskeyEnabled)
        : this(
            path, result.Success, result.Uuid, result.LockedMarkerPath, result.ErrorMessage, result.ErrorCode,
            result.ErrorDetail, passkeyRequested, actuallyPasskeyEnabled, result.RecoveryKey)
    {
    }
}

/// <summary>
/// 對應信封加密流程 Phase 2b 的「pending」訊息：跟 EncryptItemResponse 形狀幾乎一樣，只是
/// LockedMarkerPath 這階段一定是空字串（EncryptPendingAsync 本來就不寫 marker），前端不該
/// 依賴這個欄位判斷加密完成與否——真正完成要等對應的 commitEncryptResult。
/// </summary>
public sealed record EncryptPendingItemResponse(
    string Path, bool Success, string Uuid,
    string? ErrorMessage, string? ErrorCode, string? ErrorDetail,
    bool PasskeyRequested, bool PasskeyEnabled, string? RecoveryKey)
{
    public EncryptPendingItemResponse(string path, LockResult result, bool passkeyRequested, bool actuallyPasskeyEnabled)
        : this(
            path, result.Success, result.Uuid, result.ErrorMessage, result.ErrorCode,
            result.ErrorDetail, passkeyRequested, actuallyPasskeyEnabled, result.RecoveryKey)
    {
    }
}

/// <summary>對應「全部解鎖」批次解密的單筆回報，還原位置固定用各自的原始位置。</summary>
public sealed record DecryptBatchItemResponse(
    string Uuid, bool Success, string RestoredPath, string? ErrorMessage, string? ErrorCode, string? ErrorDetail)
{
    public DecryptBatchItemResponse(string uuid, UnlockResult result)
        : this(uuid, result.Success, result.RestoredPath, result.ErrorMessage, result.ErrorCode, result.ErrorDetail)
    {
    }
}

/// <summary>
/// CreatedAtUtc 是信封加密流程 Phase 2a 補上的欄位（design-exploration/gui-styles-v2 定案文件
/// §1.11：獨立解密流程的信封落地後要顯示「檔名＋加密時間」）——metadata 找不到（marker 存在但
/// 沒有對應的 metadata，或根本不是合法的 .locked 檔案）時是 null，呼叫端不需要另外判斷。
/// </summary>
public sealed record InspectLockedFileResponse(
    bool Success, string? Uuid, string? OriginalName, string? Hint, bool PasskeyEnabled, bool RecoveryKeyEnabled,
    DateTimeOffset? CreatedAtUtc = null);

public sealed record PathSizeInfo(long Bytes, bool IsFolder);

public sealed record SettingsResponse(string? VaultPath, string Language, string Theme, bool CriticalActionConfigured, bool MinimizeToTrayEnabled, bool LaunchAtStartupEnabled);

public sealed record UpdateSettingResponse(bool Success, string Key, string Value);

/// <summary>RequiresRestart 只有在搬移成功時才有意義，失敗時前端不會去看這個欄位。</summary>
public sealed record ChangeVaultPathResponse(bool Success, string? NewPath, string? ErrorMessage, string? ErrorCode = null, string? ErrorDetail = null)
{
    public bool RequiresRestart => Success;
}

public sealed record VaultListItemResponse(
    string Uuid, string OriginalName, string OriginalPath, string Type,
    bool PasskeyEnabled, bool RecoveryKeyEnabled, string? BatchId, long OriginalSizeBytes,
    string? Hint, DateTimeOffset CreatedAtUtc, bool HasNestedLocks, int NestedLockCount,
    bool MarkerFound, string? MarkerStatusMessage, IReadOnlyList<string> NestedLockItemNames,
    string? MarkerStatusCode, string? MarkerStatusDetail)
{
    public VaultListItemResponse(VaultIndexEntry entry, MarkerStatus markerStatus, IReadOnlyList<string> nestedLockItemNames)
        : this(
            entry.Uuid, entry.OriginalName, entry.OriginalPath, entry.Type.ToString(),
            entry.PasskeyEnabled, entry.RecoveryKeyEnabled, entry.BatchId, entry.OriginalSizeBytes,
            entry.Hint, entry.CreatedAtUtc, entry.NestedLockCount > 0, entry.NestedLockCount,
            markerStatus.Found, markerStatus.Message, nestedLockItemNames,
            markerStatus.Code, markerStatus.Detail)
    {
    }
}

public sealed record HistoryListItemResponse(
    string Uuid, string OriginalName, string Action, DateTimeOffset TimestampUtc, string? Detail,
    string? SourcePath, bool? PasskeyEnabled, bool? RecoveryKeyEnabled, string? UnlockMethod, string? RestoredPath)
{
    public HistoryListItemResponse(HistoryEntry entry)
        : this(
            entry.Uuid, entry.OriginalName, entry.Action.ToString(), entry.TimestampUtc, entry.Detail,
            entry.SourcePath, entry.PasskeyEnabled, entry.RecoveryKeyEnabled, entry.UnlockMethod, entry.RestoredPath)
    {
    }
}
