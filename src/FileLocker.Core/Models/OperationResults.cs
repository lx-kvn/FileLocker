namespace FileLocker.Core.Models;

/// <summary>
/// RecoveryKey 只有在這次加密啟用了恢復金鑰時才會有值，而且只有這一次回傳的時候看得到——
/// FileLocker 本身不會把它存在任何地方，GUI 收到後要立刻顯示給使用者、強制使用者做出「存成檔案」
/// 或「已經抄下來了」的選擇，不能只是靜靜地顯示過去就算了。
///
/// ErrorCode／ErrorDetail 是給前端多語言轉譯用的（見規格文件第 10 節）：ErrorMessage 保留固定的
/// 繁體中文文字，當前端找不到 ErrorCode 對應的翻譯時當後備顯示；ErrorCode 是一組固定的英文代碼
/// （見 ErrorCodes 類別），ErrorDetail 是代碼裡需要內嵌的動態內容（例如檔案路徑、例外訊息），
/// 這個欄位本身不翻譯，直接原樣嵌進前端對應語言的句子範本裡。
/// </summary>
public record LockResult(bool Success, string Uuid, string LockedMarkerPath, string? ErrorMessage = null, string? RecoveryKey = null, string? ErrorCode = null, string? ErrorDetail = null);

public record UnlockResult(bool Success, string RestoredPath, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 對應規格文件 3.2 節防呆機制：刪除紀錄失敗時，用這個結果類型告訴呼叫端「因為裡面還有巢狀鎖定」，
/// 而不是單純回傳 bool，方便 UI 顯示對應的白話提示文字。
/// </summary>
public record DeleteRecordResult(bool Success, bool BlockedByNestedLocks, IReadOnlyList<string>? NestedUuids = null, string? ErrorMessage = null, string? ErrorCode = null);

/// <summary>
/// 對應「永久刪除」前的密碼再驗證：只確認密碼對不對，不衍生出後續動作（不解密、不還原檔案）。
/// </summary>
public record VerifyPasswordResult(bool Success, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 對應清單頁的「盡力而為」檢查：只檢查 metadata.OriginalPath 反推出來的預期位置，
/// 不是掃描整個磁碟去找 .locked 檔案實際在哪——使用者若把它搬去別的地方，這裡就檢查不到，
/// 這是設計上刻意的取捨（完整掃描成本太高、也不一定找得到）。
/// </summary>
public record MarkerStatus(bool Found, string? MarkerPath, string? Message, string? Code = null, string? Detail = null, string? ConflictingUuid = null);

/// <summary>
/// 對應「資料夾防護」上鎖操作的結果——跟 LockResult 不同，這裡沒有 Uuid／LockedMarkerPath，
/// 因為資料夾防護不搬動內容、不產生指標檔，純粹是 ACL 拒絕規則有沒有套用成功。
/// </summary>
public record FolderGuardResult(bool Success, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>對應「資料夾防護」解鎖／憑證驗證的結果，形狀比照 VerifyPasswordResult。</summary>
public record FolderGuardUnlockResult(bool Success, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 對應「密碼庫」（Password Locker）一般操作的結果，形狀比照 FolderGuardResult。
/// </summary>
public record PasswordLockerResult(bool Success, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 對應密碼庫的身份驗證：跟資料夾防護的 FolderGuardUnlockResult 不同——密碼庫存的是真的要加密的
/// 內容，驗證成功時要把 Locker 主金鑰一併回傳給呼叫端繼續做 CRUD 操作（形狀比照 LockService
/// 內部的 PasswordVerification），不是純粹的通過/沒通過。呼叫端用完 MasterKey 後要自行
/// CryptographicOperations.ZeroMemory 清掉，跟 LockService.DecryptAndRestore 的既有慣例一致。
/// </summary>
public record PasswordLockerVerifyResult(bool Success, byte[]? MasterKey = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>對應「設定恢復金鑰」：跟 LockResult.RecoveryKey 的顯示慣例一致——只在這次呼叫回傳看得到，
/// FileLocker 不會留下任何副本，呼叫端收到後要立刻顯示給使用者做「已抄下」的確認。</summary>
public record PasswordLockerRecoveryKeyResult(bool Success, string? RecoveryKey = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>新增/更新一筆密碼庫憑證的結果，附帶這筆紀錄的 Id 方便呼叫端後續操作。</summary>
public record PasswordLockerEntryResult(bool Success, string? EntryId = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>取得單筆憑證解密後密碼的結果。</summary>
public record PasswordLockerDecryptedPasswordResult(bool Success, string? Password = null, string? ErrorMessage = null, string? ErrorCode = null, string? ErrorDetail = null);

/// <summary>
/// 密碼庫清單頁用的憑證中繼資料——刻意不含解密後的密碼欄位，只有真的需要密碼本身（自動填入、
/// 編輯畫面顯示、CSV 匯出）才呼叫 GetDecryptedPasswordAsync／ExportToCsv 額外解密，清單本身
/// 不需要驗證身份就能查（AssociatedDomains／Username／Title 都是明文，見 PasswordCredentialEntry
/// 的說明）。SourceDeleted 只有 EncryptedFile 類別會是 true，代表對應的 Vault 項目已經消失。
/// </summary>
public record PasswordCredentialMetadata(
    string Id,
    FileLocker.Core.PasswordLocker.CredentialCategory Category,
    string Title,
    IReadOnlyList<string> AssociatedDomains,
    string Username,
    string? LinkedVaultItemUuid,
    bool SourceDeleted,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);