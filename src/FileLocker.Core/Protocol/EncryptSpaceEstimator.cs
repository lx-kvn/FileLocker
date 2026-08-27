namespace FileLocker.Core.Protocol;

/// <summary>
/// 加密過程中「除了原始資料以外還要額外佔用多少磁碟空間」的估算結果。
/// </summary>
/// <param name="VaultRequiredBytes">寫進 Vault（或獨立加密的目的地）的密文大小。</param>
/// <param name="TempRequiredBytes">
/// 資料夾打包成暫存 zip 需要的空間，位置固定在 <c>%TEMP%\FileLocker\</c>（見規格第 5.2 節）。
/// 選取項目全是檔案時是 0——檔案不需要打包。
/// </param>
/// <param name="Sufficient">
/// 目前的可用空間夠不夠。查不到可用空間時一律回 true，見 <see cref="EncryptSpaceEstimator"/> 的說明。
/// </param>
public sealed record EncryptSpaceEstimate(long VaultRequiredBytes, long TempRequiredBytes, bool Sufficient)
{
    /// <summary>顯示給使用者看的總量——兩份輸出加起來就是他直覺想知道的「還要多少空間」。</summary>
    public long TotalRequiredBytes => VaultRequiredBytes + TempRequiredBytes;
}

/// <summary>
/// 對應技術規格文件第 5 節記載的「加密前顯示預估所需空間」。
///
/// 需要這個估算，是因為資料夾加密的峰值磁碟用量遠高於直覺：流程是「先打包成暫存 zip、再把
/// 那顆 zip 加密進 Vault、成功後才刪掉暫存 zip 與原始資料夾」，中途同時存在三份資料。數十 GB
/// 的資料夾很容易在中途把磁碟塞爆，而且失敗的時機點很尷尬（壓縮到一半或加密到一半）。
///
/// 暫存 zip 用 CompressionLevel.NoCompression（見規格第 5 節：那顆 zip 的用途是「把資料夾打包
/// 成一份東西」，不是省空間），所以它的大小約等於原始資料夾；密文因為是分塊 AEAD，每塊多一個
/// nonce 與 tag，比明文略大但差距在千分之一等級，估算時不特別加成。
///
/// 兩份輸出可能落在不同磁碟區（暫存 zip 在 %TEMP%、密文在 Vault，而 Vault 位置使用者可以自訂），
/// 所以充足與否要分開檢查；但兩者在同一顆磁碟時（預設情境：Vault 在 %LocalAppData%，跟 %TEMP%
/// 同一顆碟）是在搶同一份可用空間，必須改成用合計去比，否則會出現「分開各自過關、合起來卻放
/// 不下」的誤判。
///
/// 拿不到可用空間時（磁碟區查詢失敗）一律視為足夠：這是輔助性的提示功能，寧可不提醒，
/// 也不要因為查不到資訊就擋在使用者面前說空間不足。
/// </summary>
public static class EncryptSpaceEstimator
{
    public static EncryptSpaceEstimate Estimate(
        IReadOnlyList<PathSizeInfo> sizes,
        long? vaultFreeBytes,
        long? tempFreeBytes,
        bool vaultAndTempShareVolume)
    {
        var vaultRequired = sizes.Sum(s => s.Bytes);
        var tempRequired = sizes.Where(s => s.IsFolder).Sum(s => s.Bytes);

        var sufficient = vaultAndTempShareVolume
            ? IsEnough(vaultFreeBytes, vaultRequired + tempRequired)
            : IsEnough(vaultFreeBytes, vaultRequired) && IsEnough(tempFreeBytes, tempRequired);

        return new EncryptSpaceEstimate(vaultRequired, tempRequired, sufficient);
    }

    private static bool IsEnough(long? freeBytes, long requiredBytes)
        => freeBytes is null || freeBytes.Value >= requiredBytes;
}
