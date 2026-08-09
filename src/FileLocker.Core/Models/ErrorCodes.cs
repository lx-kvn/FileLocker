namespace FileLocker.Core.Models;

/// <summary>
/// 固定的錯誤代碼字串常數，給 LockResult／UnlockResult 的 ErrorCode 欄位用（見 OperationResults.cs 說明）。
/// 前端依這個代碼查對應語言的翻譯句子範本，找不到就退回 ErrorMessage 那份固定繁體中文文字。
/// 新增錯誤情境時，這裡加一個新常數，同時要記得在 src/FileLocker.Web/src/locales/*.json
/// 補上對應的 "error.XXX" 翻譯，兩邊要一起維護。
/// </summary>
public static class ErrorCodes
{
    public const string SourceNotFound = "SOURCE_NOT_FOUND";
    public const string MarkerAlreadyExists = "MARKER_ALREADY_EXISTS";
    public const string EncryptError = "ENCRYPT_ERROR";
    public const string EncryptUnexpectedError = "ENCRYPT_UNEXPECTED_ERROR";

    public const string InvalidMarker = "INVALID_MARKER";
    public const string MarkerSignatureInvalid = "MARKER_SIGNATURE_INVALID";
    public const string VaultContentMissing = "VAULT_CONTENT_MISSING";
    public const string CannotDetermineFolder = "CANNOT_DETERMINE_FOLDER";
    public const string RecordNotFound = "RECORD_NOT_FOUND";
    public const string ResolveDestinationError = "RESOLVE_DESTINATION_ERROR";

    public const string PasskeyNotEnabled = "PASSKEY_NOT_ENABLED";
    public const string PasskeyVerificationFailed = "PASSKEY_VERIFICATION_FAILED";
    public const string PasskeyUnwrapFailed = "PASSKEY_UNWRAP_FAILED";

    public const string RecoveryKeyNotEnabled = "RECOVERY_KEY_NOT_ENABLED";
    public const string RecoveryKeyInvalidFormat = "RECOVERY_KEY_INVALID_FORMAT";
    public const string RecoveryKeyIncorrect = "RECOVERY_KEY_INCORRECT";

    public const string LockedOut = "LOCKED_OUT";
    public const string PasswordIncorrect = "PASSWORD_INCORRECT";

    public const string UnsafeFileName = "UNSAFE_FILENAME";
    public const string DestinationFolderExists = "DESTINATION_FOLDER_EXISTS";
    public const string DestinationFileExists = "DESTINATION_FILE_EXISTS";
    public const string ContentCorrupted = "CONTENT_CORRUPTED";
    public const string ContentCorruptedWithDetail = "CONTENT_CORRUPTED_WITH_DETAIL";
    public const string DecryptError = "DECRYPT_ERROR";
    public const string DecryptUnexpectedError = "DECRYPT_UNEXPECTED_ERROR";

    public const string MarkerNotFound = "MARKER_NOT_FOUND";
    public const string MarkerParseFailed = "MARKER_PARSE_FAILED";
    public const string MarkerReplacedByOther = "MARKER_REPLACED_BY_OTHER";
    public const string MarkerReplacedByOtherNamed = "MARKER_REPLACED_BY_OTHER_NAMED";
    public const string MarkerPackedIntoContainer = "MARKER_PACKED_INTO_CONTAINER";

    public const string VaultMoveSamePath = "VAULT_MOVE_SAME_PATH";
    public const string VaultMoveDestinationNotEmpty = "VAULT_MOVE_DESTINATION_NOT_EMPTY";
    public const string VaultMoveIoError = "VAULT_MOVE_IO_ERROR";
    public const string RecoveryKeySaveError = "RECOVERY_KEY_SAVE_ERROR";

    // 對應「資料夾防護」（Folder Guard）：純 ACL 存取限制，不加密，見 FileLocker_資料夾防護_功能規劃.md。
    public const string FolderGuardNotConfigured = "FOLDER_GUARD_NOT_CONFIGURED";
    public const string FolderGuardPasswordIncorrect = "FOLDER_GUARD_PASSWORD_INCORRECT";
    public const string FolderGuardPasskeyFailed = "FOLDER_GUARD_PASSKEY_FAILED";
    public const string FolderGuardLockedOut = "FOLDER_GUARD_LOCKED_OUT";
    public const string FolderGuardAclApplyFailed = "FOLDER_GUARD_ACL_APPLY_FAILED";
    public const string FolderGuardAclRemoveFailed = "FOLDER_GUARD_ACL_REMOVE_FAILED";
    public const string FolderGuardAlreadyLocked = "FOLDER_GUARD_ALREADY_LOCKED";
    public const string FolderGuardPathNotFolder = "FOLDER_GUARD_PATH_NOT_FOLDER";
    public const string FolderGuardContainsNestedGuarded = "FOLDER_GUARD_CONTAINS_NESTED_GUARDED";

    // 對應「密碼庫」（Password Locker）：獨立於加密 Vault、資料夾防護之外的第三套憑證儲存，
    // 見 FileLocker_密碼庫_功能規劃.md。跟 FolderGuard* 一樣不重用 Vault 既有的通用錯誤代碼，
    // 因為前端要顯示的訊息文案（「密碼庫密碼錯誤」而不是泛用的「密碼錯誤」）需要各自獨立的翻譯詞條。
    public const string PasswordLockerNotConfigured = "PASSWORD_LOCKER_NOT_CONFIGURED";
    public const string PasswordLockerPasswordIncorrect = "PASSWORD_LOCKER_PASSWORD_INCORRECT";
    public const string PasswordLockerPasskeyNotEnabled = "PASSWORD_LOCKER_PASSKEY_NOT_ENABLED";
    public const string PasswordLockerPasskeyFailed = "PASSWORD_LOCKER_PASSKEY_FAILED";
    public const string PasswordLockerRecoveryKeyNotEnabled = "PASSWORD_LOCKER_RECOVERY_KEY_NOT_ENABLED";
    public const string PasswordLockerRecoveryKeyInvalidFormat = "PASSWORD_LOCKER_RECOVERY_KEY_INVALID_FORMAT";
    public const string PasswordLockerRecoveryKeyIncorrect = "PASSWORD_LOCKER_RECOVERY_KEY_INCORRECT";
    public const string PasswordLockerLockedOut = "PASSWORD_LOCKER_LOCKED_OUT";
    public const string PasswordLockerEntryNotFound = "PASSWORD_LOCKER_ENTRY_NOT_FOUND";
    public const string PasswordLockerNotVerified = "PASSWORD_LOCKER_NOT_VERIFIED";
    public const string PasswordLockerCsvInvalidFormat = "PASSWORD_LOCKER_CSV_INVALID_FORMAT";

    // 對應軟體更新檢查（installer_config.json 版本比對 GitHub release）。
    public const string UpdateCheckNotInstalled = "UPDATE_CHECK_NOT_INSTALLED";
    public const string UpdateCheckFailed = "UPDATE_CHECK_FAILED";
    public const string UpdateDownloadFailed = "UPDATE_DOWNLOAD_FAILED";
}