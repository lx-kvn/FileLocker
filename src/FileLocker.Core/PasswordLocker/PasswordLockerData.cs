namespace FileLocker.Core.PasswordLocker;

/// <summary>
/// 對應規劃文件（FileLocker_密碼庫_功能規劃.md）：獨立於加密 Vault、資料夾防護之外的第三套
/// 憑證儲存，credentials.json 的完整內容。三條解鎖路徑（密碼／Passkey／恢復金鑰）最終都是為了
/// 拿到同一把 Locker 主金鑰，用來加解密 <see cref="PasswordCredentialEntry"/> 裡的密碼／備註。
/// </summary>
public class PasswordLockerData
{
    /// <summary>密碼驗證用（Argon2id 分割金鑰模式，跟 Vault／資料夾防護同一套）。</summary>
    public string? PasswordSaltBase64 { get; set; }
    public string? PasswordVerificationHashBase64 { get; set; }

    /// <summary>Passkey（裝置綁定），重用 PasskeyProtector 的完整 wrap/unwrap 流程——
    /// 密碼庫存的是真正要加密的內容，不是資料夾防護那種純驗證用法，需要真的把 Locker 主金鑰包起來。</summary>
    public bool PasskeyEnabled { get; set; }
    public string? PasskeyCredentialName { get; set; }
    public string? PasskeyWrappedMasterKeyBase64 { get; set; }

    /// <summary>恢復金鑰，重用 RecoveryKeyProtector 的 wrap/unwrap 模式，第三條獨立解鎖路徑。</summary>
    public bool RecoveryKeyEnabled { get; set; }
    public string? RecoveryKeyWrappedMasterKeyBase64 { get; set; }

    /// <summary>自動填入的驗證有效期：每個網站獨立計時、滑動視窗，這裡只存逾時分鐘數，
    /// 實際的「網站→上次驗證時間」對應表是執行期記憶體狀態，不持久化（見 PasswordLockerService）。</summary>
    public int SessionTimeoutMinutes { get; set; } = 5;

    public List<PasswordCredentialEntry> Entries { get; set; } = new();
}

public enum CredentialCategory
{
    Website,
    EncryptedFile
}

/// <summary>
/// 密碼庫裡的一筆憑證。AssociatedDomains／Username／Title 刻意不加密——瀏覽器分頁載入網站時
/// 要能在使用者驗證身份之前就比對「有沒有存過這個網站的憑證」，否則每個網站都得先驗證才知道
/// 有沒有存過，變相強迫每次都要驗證，違背「不打擾」的設計（見規劃文件第 5 節）。
/// EncryptedPasswordBase64／EncryptedNotesBase64 才是需要保護的機密內容。
/// </summary>
public class PasswordCredentialEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public CredentialCategory Category { get; set; }

    /// <summary>Website 類別是使用者自訂標題；EncryptedFile 類別是對應 Vault 項目的檔名，
    /// 來源項目消失後這個欄位變成唯一的識別依據（見規劃文件第 4 節「已加密檔案」類別）。</summary>
    public string Title { get; set; } = "";

    public List<string> AssociatedDomains { get; set; } = new();
    public string Username { get; set; } = "";

    public string EncryptedPasswordBase64 { get; set; } = "";
    public string? EncryptedNotesBase64 { get; set; }

    /// <summary>只有 EncryptedFile 類別使用，對應 Vault 項目的 UUID。</summary>
    public string? LinkedVaultItemUuid { get; set; }

    /// <summary>由 PasswordLockerService.CheckLinkedVaultItemsAsync 維護——LinkedVaultItemUuid
    /// 對應的 Vault 項目已經消失時設為 true，UI 層依此顯示刪除線＋標示來源消失，不刪除這筆憑證
    /// （見規劃文件第 4 節）。</summary>
    public bool SourceDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
