namespace FileLocker.Core.Models;

/// <summary>
/// 對應信封加密流程「取消要能安全回滾」的交易模型（design-exploration/gui-styles-v2 定案文件
/// §1.8）：Pending 代表加密內容已經安全寫進 Vault，但原始檔案還沒被刪除、也還沒放 marker——
/// 這個狀態下取消永遠是安全的（只要刪掉 Vault 裡這筆暫存項目，原始檔案完全沒被動過）。
/// Committed 代表已經走完 marker 寫入＋原始檔刪除，是「這筆加密真的完成了」的最終狀態。
/// 不能用「marker 存不存在」推斷是不是 pending——marker 遺失可能是別的原因（例如使用者手動
/// 刪除），跟「這筆加密根本還沒 finalize」是兩種不同情境，混用會導致誤刪合法但 marker 遺失的
/// 加密紀錄。
/// </summary>
public enum LockStatus
{
    Pending,
    Committed
}

/// <summary>
/// 對應規格文件第 4 節：每個加密項目獨立一份的 {uuid}.meta.json 內容。
/// 這份物件不是「加密金鑰」本身的載體——PasswordVerificationHash 只拿來驗證密碼是否正確，
/// 真正的加密金鑰永遠是當下用密碼 + Salt 即時算出來，不會被序列化進這個檔案。
/// </summary>
public class LockedItemMetadata
{
    /// <summary>
    /// 預設 Committed，不是 Pending——這樣既有的舊資料（沒有這個欄位的舊版 .meta.json，
    /// JSON 反序列化時這個欄位會用預設值補上）跟既有測試/呼叫端（EncryptAsync 走的是原本的
    /// 一次到位流程，不產生 Pending 狀態）都自動視為「已完成」，不需要額外遷移邏輯。
    /// </summary>
    public LockStatus Status { get; set; } = LockStatus.Committed;

    /// <summary>對應 Vault 內 {Uuid}.enc 檔名。</summary>
    public required string Uuid { get; set; }

    public required string OriginalName { get; set; }

    /// <summary>加密當下的原始路徑，用來在解密時決定還原位置。</summary>
    public required string OriginalPath { get; set; }

    /// <summary>Argon2id 衍生後、用於「驗證密碼是否正確」的雜湊值（Base64）。不可逆推回密碼或加密金鑰。</summary>
    public required string PasswordVerificationHash { get; set; }

    /// <summary>本次加密使用的隨機 Salt（Base64）。</summary>
    public required string Salt { get; set; }

    public required int Argon2TimeCost { get; set; }

    public required int Argon2MemoryCostKb { get; set; }

    public required int Argon2Parallelism { get; set; }

    /// <summary>使用者設定的密碼提示，解密視窗顯示用。</summary>
    public string? Hint { get; set; }

    public required ItemType Type { get; set; }

    public long OriginalSizeBytes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? LastAccessedAtUtc { get; set; }

    /// <summary>
    /// 對應規格文件 3.2 節「巢狀 .locked 項目」設計：若此項目是資料夾，且封裝時偵測到內部
    /// 本來就含有其他 .locked 指標檔，記錄那些內層項目的 UUID。
    /// 只要這個清單不是空的，UI／LockService 在刪除這筆紀錄前必須擋下來，見 LockService.TryDeleteRecordAsync。
    /// </summary>
    public List<string> ContainsNestedLocks { get; set; } = new();

    // ---- 對應規格文件 8.1 節「Passkey 快速解鎖」，以下四個欄位只有啟用時才會有值 ----

    /// <summary>是否有為這個項目啟用 Passkey 快速解鎖。false 時，下面三個欄位一律為 null。</summary>
    public bool PasskeyEnabled { get; set; }

    /// <summary>這個項目專屬的 Windows Hello 裝置金鑰名稱（帶隨機 GUID，見 PasskeyProtector.GenerateCredentialName）。</summary>
    public string? PasskeyCredentialName { get; set; }

    /// <summary>簽章用的隨機挑戰資料（Base64）。本身不是機密，外洩也沒關係，純粹是簽章的輸入。</summary>
    public string? PasskeyChallenge { get; set; }

    /// <summary>用 Passkey 簽章衍生出的包裝金鑰加密過的內容金鑰（Base64），格式：Nonce+Tag+Ciphertext。</summary>
    public string? PasskeyWrappedContentKey { get; set; }

    // ---- 對應規格文件「恢復金鑰」，以下欄位只有啟用時才會有值 ----

    /// <summary>是否有為這個項目啟用恢復金鑰。false 時，下面的欄位為 null。</summary>
    public bool RecoveryKeyEnabled { get; set; }

    /// <summary>用恢復金鑰衍生出的包裝金鑰加密過的內容金鑰（Base64），格式同 PasskeyWrappedContentKey。</summary>
    public string? RecoveryKeyWrappedContentKey { get; set; }

    /// <summary>
    /// 一次選多個項目加密時（不管是加密頁籤多選，還是 Shell Extension 右鍵多選），
    /// 同一批全部會標上同一個隨機 ID，讓「已加密清單」頁可以把它們摺疊成一組顯示。
    /// 單一項目加密時這裡是 null，代表不需要分組。
    /// </summary>
    public string? BatchId { get; set; }
}