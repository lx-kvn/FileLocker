using System.Globalization;

namespace FileLocker.Cli;

public enum CliLanguage
{
    ZhTw,
    En
}

/// <summary>
/// CLI 英文化的唯一入口：語言判斷（--lang 旗標優先，沒帶就跟著系統顯示語言走，只有 zh-TW／en
/// 兩種語言，不做更細的地區判斷）＋訊息查表。跟 GUI 端 t('key')／zh-TW.json／en.json 那套機制
/// 精神一致，但這裡沒有另外拉一個 JSON 檔案讀取機制進來——CLI 訊息量遠比 GUI 少，用兩個純
/// C# Dictionary 常數就夠，不需要為了這個規模另外處理檔案 I/O／打包路徑問題。
///
/// Program.cs 只呼叫 T()／TranslateError()，不直接碰這兩份字典——決策邏輯（有沒有這個 key、
/// 找不到要退回哪裡）全部收斂在這裡，方便單元測試（見 CliLocalizationTests），不用真的印到
/// Console 就能測完。
/// </summary>
public static class CliLocalization
{
    public static CliLanguage Current { get; private set; } = CliLanguage.ZhTw;

    public static void SetLanguage(CliLanguage language) => Current = language;

    /// <summary>
    /// --lang 旗標值優先；沒帶就用 systemTwoLetterIso 判斷（null 代表要呼叫端自己去讀
    /// CultureInfo.CurrentUICulture，這裡不直接讀）——刻意接受外部傳入的兩碼語系字串而不是
    /// 自己讀 CultureInfo.CurrentUICulture，因為那是行程層級的可變狀態，直接在這裡讀會讓
    /// 這個方法沒辦法在單元測試裡穩定重現「系統語言是什麼」這件事（xUnit 測試方法可能被排到
    /// 同一個執行緒重複使用，測試之間互改全域文化設定有互相污染的風險）。
    /// </summary>
    public static CliLanguage ResolveLanguage(string? langFlag, string? systemTwoLetterIso = null)
    {
        if (langFlag is not null)
        {
            return langFlag switch
            {
                "zh-TW" => CliLanguage.ZhTw,
                "en" => CliLanguage.En,
                _ => throw new CliArgumentException($"不支援的語言代碼／Unsupported language code：{langFlag}（可用值／available values：zh-TW、en）")
            };
        }

        var iso = systemTwoLetterIso ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return iso == "zh" ? CliLanguage.ZhTw : CliLanguage.En;
    }

    public static string T(string key, params object[] args)
    {
        var dict = Current == CliLanguage.ZhTw ? ZhTw : En;
        if (!dict.TryGetValue(key, out var template))
        {
            // 純防呆：正常情況下不該發生（AllZhTwKeys_ExistInEnglishDictionary_AndViceVersa
            // 這個測試會在漏加 key 的當下就紅燈），真的漏掉的話印 key 本身總比整個丟例外、
            // 讓使用者完全看不到任何訊息好。
            return key;
        }
        return args.Length > 0 ? string.Format(template, args) : template;
    }

    /// <summary>
    /// 跟 GUI 端 App.vue 的 translateError() 同一套邏輯：依 ErrorCode 查對應語言的翻譯範本，
    /// 找不到就退回呼叫端提供的 fallbackMessage（也就是 LockResult/UnlockResult.ErrorMessage
    /// 那份固定繁體中文文字）——不是每個 ErrorCodes 常數都在下面的 ErrorMessages 字典裡有對應
    /// 詞條，只收錄 CLI 實際會走到的路徑（encrypt／unlock／unlock-recovery／delete）可能回傳
    /// 的錯誤代碼，跟 GUI 那 56 個 error.* 詞條不是一比一對應——CLI 不支援 Passkey、不會碰到
    /// Folder Guard／Password Locker／軟體更新這些功能，那些代碼的翻譯詞條在這裡沒有意義。
    /// LOCKED_OUT 額外處理：ErrorDetail 是後端給的剩餘秒數字串，跟 GUI 的 formatRemainingTime
    /// 一樣依目前語言換算成「X 分鐘／X minute(s)」再帶進範本，不是直接印原始秒數。
    /// </summary>
    public static string TranslateError(string? errorCode, string? errorDetail, string fallbackMessage)
    {
        if (errorCode is null)
        {
            return fallbackMessage;
        }

        var dict = Current == CliLanguage.ZhTw ? ZhTwErrors : EnErrors;
        if (!dict.TryGetValue(errorCode, out var template))
        {
            return fallbackMessage;
        }

        var detail = errorCode == "LOCKED_OUT" && errorDetail is not null && int.TryParse(errorDetail, out var seconds)
            ? FormatRemainingTime(seconds)
            : errorDetail;

        return detail is not null ? string.Format(template, detail) : template;
    }

    private static string FormatRemainingTime(int seconds)
    {
        if (Current == CliLanguage.En)
        {
            return seconds >= 60 ? $"{Math.Ceiling(seconds / 60.0)} minute(s)" : $"{seconds} second(s)";
        }
        return seconds >= 60 ? $"{Math.Ceiling(seconds / 60.0)} 分鐘" : $"{seconds} 秒";
    }

    /// <summary>只給 CliLocalizationTests 用的一致性檢查——兩份語言字典（訊息＋錯誤代碼）的
    /// key 集合要完全一樣，回傳 (缺少的英文 key, 缺少的中文 key)。</summary>
    public static (List<string> MissingFromEn, List<string> MissingFromZhTw) DiffKeysForTest()
    {
        var zhKeys = ZhTw.Keys.Concat(ZhTwErrors.Keys.Select(k => $"error.{k}")).ToHashSet();
        var enKeys = En.Keys.Concat(EnErrors.Keys.Select(k => $"error.{k}")).ToHashSet();
        return (zhKeys.Except(enKeys).ToList(), enKeys.Except(zhKeys).ToList());
    }

    private static readonly Dictionary<string, string> ZhTw = new()
    {
        ["vaultLocation"] = "Vault 位置：{0}",
        ["argumentError"] = "參數錯誤：{0}",
        ["notFound"] = "錯誤：找不到 {0}",
        ["markerNotFound"] = "錯誤：找不到指標檔 {0}",
        ["flockedFileNotFound"] = "錯誤：找不到 .flocked 檔案 {0}",
        ["enterPassword"] = "請輸入密碼：",
        ["enterPasswordConfirm"] = "\n請再輸入一次密碼確認：",
        ["passwordMismatch"] = "兩次輸入的密碼不一致，取消加密。",
        ["generateRecoveryKeyPrompt"] = "要順便產生恢復金鑰嗎？(y/N)：",
        ["hintPrompt"] = "密碼提示（可留空，直接按 Enter）：",
        ["passwordEmpty"] = "沒有輸入密碼，已取消這次操作。",
        ["encrypting"] = "加密中...",
        ["encryptSuccess"] = "加密成功：{0}",
        ["uuidLabel"] = "  UUID：{0}",
        ["markerLocationLabel"] = "  指標檔位置：{0}",
        ["flockedLocationLabel"] = "  獨立密文（.flocked）位置：{0}",
        ["recoveryKeyLabel"] = "  恢復金鑰（請妥善保存，不會再顯示第二次）：{0}",
        ["encryptFailed"] = "加密失敗：{0}",
        ["batchSummary"] = "完成：{0} 筆成功、{1} 筆失敗。",
        ["decrypting"] = "解密中...",
        ["decryptSuccess"] = "解密成功！",
        ["restoredToLabel"] = "  已還原至：{0}",
        ["decryptFailed"] = "解密失敗：{0}",
        ["vaultEmpty"] = "Vault 目前是空的。",
        ["originalPathLabel"] = "    原始路徑：{0}",
        ["sizeCreatedLabel"] = "    大小：{0}  建立時間：{1}",
        ["passkeyRecoveryLabel"] = "    Passkey：{0}  恢復金鑰：{1}{2}",
        ["yes"] = "是",
        ["no"] = "否",
        ["nestedLockSuffix"] = "  內含 {0} 個巢狀加密項目",
        ["deleteConfirmMultiple"] = "確定要永久刪除以下項目嗎？此動作無法復原：",
        ["deleteConfirmSingle"] = "確定要永久刪除 {0} 嗎？此動作無法復原 (y/N)：",
        ["yesNoPrompt"] = "(y/N)：",
        ["cancelled"] = "已取消。",
        ["recordNotFoundForUuid"] = "找不到 UUID 為 {0} 的加密紀錄。",
        ["deleteSuccess"] = "刪除成功：{0}",
        ["deleteFailedNested"] = "刪除失敗：{0}（資料夾內還有巢狀加密項目，請先個別處理）：",
        ["deleteFailed"] = "刪除失敗：{0}",
        ["dryRunHeader"] = "預演模式（--dry-run），不會真的刪除任何東西：",
        ["dryRunWouldDelete"] = "  會刪除：{0}  {1}",

        ["usageHeader"] = "用法：",
        ["usageEncrypt"] = "  FileLocker.Cli encrypt <檔案或資料夾路徑> [路徑2 ...]",
        ["usageUnlock"] = "  FileLocker.Cli unlock <.locked 或 .flocked 檔案路徑> [路徑2 ...]",
        ["usageUnlockRecovery"] = "  FileLocker.Cli unlock-recovery <uuid 或 .flocked 路徑> <恢復金鑰> [還原目的地資料夾]",
        ["usageList"] = "  FileLocker.Cli list",
        ["usageDelete"] = "  FileLocker.Cli delete <uuid> [uuid2 ...]",
        ["usageCompletion"] = "  FileLocker.Cli completion <bash|zsh|pwsh>   印出對應 shell 的自動完成腳本",
        ["usageLegacyFlagNote"] = "舊寫法 --encrypt／--unlock／--unlock-recovery／--list／--delete（開頭多兩個 -）仍然完整支援、行為完全一樣，只是用到時會印一行過時提醒到標準錯誤，不影響任何功能。",
        ["deprecatedFlagStyleWarning"] = "提醒：{0} 是舊寫法，建議改用不帶 -- 的子命令 {1}（例如 FileLocker.Cli {1} ...），這不影響本次執行的結果。",
        ["usageBatchNote"] = "--encrypt／--unlock／--delete 都支援一次傳多個路徑或 uuid：密碼（或刪除確認）只問一次，套用到所有項目，個別項目的成功/失敗各自列出。",
        ["usageVaultPathNote"] = "環境變數 FILELOCKER_VAULT_PATH 可以覆寫預設 Vault 位置（未設定時跟主程式共用同一個預設路徑）。",
        ["usageLangNote"] = "--lang <zh-TW|en> 可以指定介面語言（任何指令都適用，放在參數任何位置都可以）；沒帶的話跟著作業系統顯示語言走，系統語言不是中文就一律用英文。",
        ["usageOutputNote"] = "--output/-o <text|json> 指定輸出格式，預設 text；json 模式下 --list／--encrypt／--unlock／--unlock-recovery／--delete 會改印一份結構化 JSON 到標準輸出，方便腳本解析，其餘資訊性文字（Vault 位置、進度提示等）改印到標準錯誤，不會混進 JSON 裡。",
        ["usageSilentModeHeader"] = "靜默批次模式（供腳本使用，不會有任何互動提示）：",
        ["usagePasswordStdin"] = "  --password-stdin          從標準輸入讀一行當密碼（--encrypt／--unlock 適用，出現即觸發非互動模式）",
        ["usagePasswordFile"] = "  --password-file <路徑>     從檔案第一行讀密碼（跟 --password-stdin 互斥，只能擇一）",
        ["usageRecoveryKey"] = "  --recovery-key             非互動模式下順便產生恢復金鑰（--encrypt 適用，預設不產生）",
        ["usageHint"] = "  --hint <文字>              非互動模式下設定密碼提示（--encrypt 適用，預設留空）",
        ["usageYes"] = "  --yes, -y                  跳過 --delete 的確認提示，直接刪除",
        ["usageDryRun"] = "  --dry-run, -n              搭配 --delete，只預覽會刪除哪些項目，不會真的執行",
        ["usageStandalone"] = "  --standalone               獨立加密：加密結果不進 Vault，產生可獨立攜帶的 .flocked 檔（--encrypt 適用）",
        ["usageDestination"] = "  --destination <資料夾>      搭配 --standalone，指定 .flocked 檔要存到哪個資料夾（不指定就原地取代原始檔案）",
        ["usageHelp"] = "  -h, --help                 顯示這份用法說明並結束（放在任何位置都認得）",
        ["usageVersion"] = "  --version                  顯示版本號並結束",
        ["usageExitCodes"] = "結束碼：0 = 全部成功，1 = 參數錯誤，2 = 批次中至少一筆失敗，3 = 使用者/腳本取消（例如 --delete 沒帶 --yes 又回答非 y）。",
        ["versionLabel"] = "FileLocker.Cli {0}",
        ["versionDev"] = "開發版本（非正式安裝，找不到 installer_config.json）",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["vaultLocation"] = "Vault location: {0}",
        ["argumentError"] = "Argument error: {0}",
        ["notFound"] = "Error: not found: {0}",
        ["markerNotFound"] = "Error: marker file not found: {0}",
        ["flockedFileNotFound"] = "Error: .flocked file not found: {0}",
        ["enterPassword"] = "Enter password: ",
        ["enterPasswordConfirm"] = "\nConfirm password: ",
        ["passwordMismatch"] = "The two passwords don't match. Encryption cancelled.",
        ["generateRecoveryKeyPrompt"] = "Also generate a recovery key? (y/N): ",
        ["hintPrompt"] = "Password hint (optional, press Enter to skip): ",
        ["passwordEmpty"] = "No password entered; the operation was cancelled.",
        ["encrypting"] = "Encrypting...",
        ["encryptSuccess"] = "Encrypted successfully: {0}",
        ["uuidLabel"] = "  UUID: {0}",
        ["markerLocationLabel"] = "  Marker file location: {0}",
        ["flockedLocationLabel"] = "  Standalone (.flocked) file location: {0}",
        ["recoveryKeyLabel"] = "  Recovery key (keep it safe, it will not be shown again): {0}",
        ["encryptFailed"] = "Encryption failed: {0}",
        ["batchSummary"] = "Done: {0} succeeded, {1} failed.",
        ["decrypting"] = "Decrypting...",
        ["decryptSuccess"] = "Decrypted successfully!",
        ["restoredToLabel"] = "  Restored to: {0}",
        ["decryptFailed"] = "Decryption failed: {0}",
        ["vaultEmpty"] = "The Vault is currently empty.",
        ["originalPathLabel"] = "    Original path: {0}",
        ["sizeCreatedLabel"] = "    Size: {0}  Created: {1}",
        ["passkeyRecoveryLabel"] = "    Passkey: {0}  Recovery key: {1}{2}",
        ["yes"] = "Yes",
        ["no"] = "No",
        ["nestedLockSuffix"] = "  Contains {0} nested encrypted item(s)",
        ["deleteConfirmMultiple"] = "Are you sure you want to permanently delete the following items? This cannot be undone:",
        ["deleteConfirmSingle"] = "Are you sure you want to permanently delete {0}? This cannot be undone (y/N): ",
        ["yesNoPrompt"] = "(y/N): ",
        ["cancelled"] = "Cancelled.",
        ["recordNotFoundForUuid"] = "No encrypted record found for UUID {0}.",
        ["deleteSuccess"] = "Deleted successfully: {0}",
        ["deleteFailedNested"] = "Delete failed: {0} (this folder still contains nested encrypted items — handle them individually first):",
        ["deleteFailed"] = "Delete failed: {0}",
        ["dryRunHeader"] = "Dry run (--dry-run) — nothing will actually be deleted:",
        ["dryRunWouldDelete"] = "  Would delete: {0}  {1}",

        ["usageHeader"] = "Usage:",
        ["usageEncrypt"] = "  FileLocker.Cli encrypt <file or folder path> [path2 ...]",
        ["usageUnlock"] = "  FileLocker.Cli unlock <.locked or .flocked file path> [path2 ...]",
        ["usageUnlockRecovery"] = "  FileLocker.Cli unlock-recovery <uuid or .flocked path> <recovery key> [restore destination folder]",
        ["usageList"] = "  FileLocker.Cli list",
        ["usageDelete"] = "  FileLocker.Cli delete <uuid> [uuid2 ...]",
        ["usageCompletion"] = "  FileLocker.Cli completion <bash|zsh|pwsh>   Print a shell completion script",
        ["usageLegacyFlagNote"] = "The old --encrypt/--unlock/--unlock-recovery/--list/--delete form (with the extra --) is still fully supported and behaves identically — using it just prints a one-line deprecation notice to stderr, with no effect on functionality.",
        ["deprecatedFlagStyleWarning"] = "Notice: {0} is the old form — consider switching to the subcommand {1} (e.g. FileLocker.Cli {1} ...). This does not affect the result of this run.",
        ["usageBatchNote"] = "--encrypt/--unlock/--delete all accept multiple paths or UUIDs at once: the password (or delete confirmation) is only asked once and applied to every item, with each item's success/failure listed individually.",
        ["usageVaultPathNote"] = "The FILELOCKER_VAULT_PATH environment variable can override the default Vault location (shares the same default as the main app when unset).",
        ["usageLangNote"] = "--lang <zh-TW|en> selects the interface language (works with any command, in any argument position); when omitted, it follows the OS display language, defaulting to English unless the system language is Chinese.",
        ["usageOutputNote"] = "--output/-o <text|json> selects the output format (default: text); in json mode, --list/--encrypt/--unlock/--unlock-recovery/--delete print a structured JSON document to stdout for scripts to parse, while other informational text (Vault location, progress messages, etc.) goes to stderr instead, so it never mixes into the JSON.",
        ["usageSilentModeHeader"] = "Silent batch mode (for scripts — no interactive prompts):",
        ["usagePasswordStdin"] = "  --password-stdin          Read one line from stdin as the password (--encrypt/--unlock only; its presence alone triggers non-interactive mode)",
        ["usagePasswordFile"] = "  --password-file <path>    Read the password from the first line of a file (mutually exclusive with --password-stdin)",
        ["usageRecoveryKey"] = "  --recovery-key            Also generate a recovery key in non-interactive mode (--encrypt only, off by default)",
        ["usageHint"] = "  --hint <text>             Set a password hint in non-interactive mode (--encrypt only, blank by default)",
        ["usageYes"] = "  --yes, -y                 Skip the --delete confirmation prompt and delete immediately",
        ["usageDryRun"] = "  --dry-run, -n             With --delete, only preview what would be deleted — nothing is actually removed",
        ["usageStandalone"] = "  --standalone              Standalone encryption: the result isn't stored in the Vault, producing a portable .flocked file (--encrypt only)",
        ["usageDestination"] = "  --destination <folder>    With --standalone, choose which folder the .flocked file should be saved to (defaults to replacing the original file in place)",
        ["usageHelp"] = "  -h, --help                Show this usage text and exit (recognized in any position)",
        ["usageVersion"] = "  --version                 Show the version number and exit",
        ["usageExitCodes"] = "Exit codes: 0 = all succeeded, 1 = argument error, 2 = at least one item in the batch failed, 3 = cancelled by the user/script (e.g. --delete without --yes, answered anything other than y).",
        ["versionLabel"] = "FileLocker.Cli {0}",
        ["versionDev"] = "development build (installer_config.json not found)",
    };

    // 對應 App.vue 的 error.* 詞條——同一份文案，只是格式從 JSON 換成 C# Dictionary，
    // 佔位符從 GUI 慣用的 {detail} 改成 .NET string.Format 慣用的 {0}（只有這裡引用時的
    // 語法不同，實際顯示文字跟 GUI 逐字一致，維持兩邊使用者看到的措辭統一）。
    private static readonly Dictionary<string, string> ZhTwErrors = new()
    {
        [FileLocker.Core.Models.ErrorCodes.MarkerAlreadyExists] = "目標位置已經有一個指標檔了：{0}",
        [FileLocker.Core.Models.ErrorCodes.EncryptError] = "加密過程發生錯誤：{0}",
        [FileLocker.Core.Models.ErrorCodes.EncryptUnexpectedError] = "加密過程發生未預期的錯誤：{0}",
        [FileLocker.Core.Models.ErrorCodes.InvalidMarker] = "找不到或無法解析這個 .locked 檔案",
        [FileLocker.Core.Models.ErrorCodes.MarkerSignatureInvalid] = "指標檔驗證失敗，內容可能已被竄改",
        [FileLocker.Core.Models.ErrorCodes.VaultContentMissing] = "在集中管理區找不到對應的加密內容",
        [FileLocker.Core.Models.ErrorCodes.CannotDetermineFolder] = "無法判斷指標檔所在的資料夾",
        [FileLocker.Core.Models.ErrorCodes.RecordNotFound] = "找不到對應的加密紀錄",
        [FileLocker.Core.Models.ErrorCodes.ResolveDestinationError] = "無法判斷還原目的地：{0}",
        [FileLocker.Core.Models.ErrorCodes.RecoveryKeyNotEnabled] = "這個項目沒有啟用恢復金鑰",
        [FileLocker.Core.Models.ErrorCodes.RecoveryKeyInvalidFormat] = "恢復金鑰格式不正確，請確認有沒有打錯或漏掉字元",
        [FileLocker.Core.Models.ErrorCodes.RecoveryKeyIncorrect] = "恢復金鑰不正確",
        [FileLocker.Core.Models.ErrorCodes.LockedOut] = "密碼錯誤次數過多，請在 {0} 後再試",
        [FileLocker.Core.Models.ErrorCodes.PasswordIncorrect] = "密碼錯誤",
        [FileLocker.Core.Models.ErrorCodes.UnsafeFileName] = "這筆紀錄的檔名資訊看起來不正常（可能已損毀或被竄改），為了安全拒絕還原",
        [FileLocker.Core.Models.ErrorCodes.DestinationFolderExists] = "還原失敗，目的地已經有同名資料夾：{0}",
        [FileLocker.Core.Models.ErrorCodes.DestinationFileExists] = "還原失敗，目的地已經有同名檔案：{0}",
        [FileLocker.Core.Models.ErrorCodes.ContentCorrupted] = "解密失敗，加密內容可能已損毀",
        [FileLocker.Core.Models.ErrorCodes.ContentCorruptedWithDetail] = "解密失敗，加密內容已損毀：{0}",
        [FileLocker.Core.Models.ErrorCodes.DecryptError] = "解密過程發生錯誤：{0}",
        [FileLocker.Core.Models.ErrorCodes.DecryptUnexpectedError] = "解密過程發生未預期的錯誤：{0}",
    };

    private static readonly Dictionary<string, string> EnErrors = new()
    {
        [FileLocker.Core.Models.ErrorCodes.MarkerAlreadyExists] = "A marker file already exists at the target location: {0}",
        [FileLocker.Core.Models.ErrorCodes.EncryptError] = "An error occurred during encryption: {0}",
        [FileLocker.Core.Models.ErrorCodes.EncryptUnexpectedError] = "An unexpected error occurred during encryption: {0}",
        [FileLocker.Core.Models.ErrorCodes.InvalidMarker] = "This .locked file could not be found or parsed",
        [FileLocker.Core.Models.ErrorCodes.MarkerSignatureInvalid] = "Marker file verification failed — the content may have been tampered with",
        [FileLocker.Core.Models.ErrorCodes.VaultContentMissing] = "The corresponding encrypted content could not be found in the vault",
        [FileLocker.Core.Models.ErrorCodes.CannotDetermineFolder] = "Could not determine the folder containing the marker file",
        [FileLocker.Core.Models.ErrorCodes.RecordNotFound] = "No matching encrypted record was found",
        [FileLocker.Core.Models.ErrorCodes.ResolveDestinationError] = "Could not determine the restore destination: {0}",
        [FileLocker.Core.Models.ErrorCodes.RecoveryKeyNotEnabled] = "Recovery key is not enabled for this item",
        [FileLocker.Core.Models.ErrorCodes.RecoveryKeyInvalidFormat] = "The recovery key format is invalid — please check for typos or missing characters",
        [FileLocker.Core.Models.ErrorCodes.RecoveryKeyIncorrect] = "Incorrect recovery key",
        [FileLocker.Core.Models.ErrorCodes.LockedOut] = "Too many incorrect password attempts. Please try again in {0}",
        [FileLocker.Core.Models.ErrorCodes.PasswordIncorrect] = "Incorrect password",
        [FileLocker.Core.Models.ErrorCodes.UnsafeFileName] = "This record's filename information looks abnormal (it may be corrupted or tampered with) — restore was blocked for safety",
        [FileLocker.Core.Models.ErrorCodes.DestinationFolderExists] = "Restore failed — a folder with the same name already exists at the destination: {0}",
        [FileLocker.Core.Models.ErrorCodes.DestinationFileExists] = "Restore failed — a file with the same name already exists at the destination: {0}",
        [FileLocker.Core.Models.ErrorCodes.ContentCorrupted] = "Decryption failed — the encrypted content may be corrupted",
        [FileLocker.Core.Models.ErrorCodes.ContentCorruptedWithDetail] = "Decryption failed — the encrypted content is corrupted: {0}",
        [FileLocker.Core.Models.ErrorCodes.DecryptError] = "An error occurred during decryption: {0}",
        [FileLocker.Core.Models.ErrorCodes.DecryptUnexpectedError] = "An unexpected error occurred during decryption: {0}",
    };
}
