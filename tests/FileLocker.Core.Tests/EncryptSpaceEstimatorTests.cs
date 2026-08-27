using FileLocker.Core.Protocol;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應技術規格文件第 5 節記載、但一直沒實作的「加密前顯示預估所需空間」。
///
/// 資料夾加密是「先打包成暫存 zip、再把那顆 zip 加密進 Vault」，所以過程中同時存在三份資料：
/// 原始資料夾、暫存 zip、密文——峰值用量遠高於使用者直覺以為的「跟原始檔差不多」。數十 GB 的
/// 資料夾很容易在中途把磁碟塞爆，而且失敗的時機點很尷尬（壓縮到一半或加密到一半）。
///
/// 兩份輸出可能落在不同的磁碟區（暫存 zip 在 %TEMP%、密文在 Vault，Vault 位置使用者可以自訂），
/// 所以充足與否要分開檢查，不能只看總量對單一磁碟區的可用空間。
/// </summary>
public class EncryptSpaceEstimatorTests
{
    private const long Mb = 1024L * 1024L;
    private const long Gb = 1024L * Mb;

    [Fact]
    public void SingleFile_NeedsSpaceForTheCiphertextOnly()
    {
        // 檔案不需要打包，過程中只多出一份密文。
        var estimate = EncryptSpaceEstimator.Estimate(
            [new PathSizeInfo(100 * Mb, IsFolder: false)],
            vaultFreeBytes: 10 * Gb, tempFreeBytes: 10 * Gb, vaultAndTempShareVolume: false);

        Assert.Equal(100 * Mb, estimate.VaultRequiredBytes);
        Assert.Equal(0, estimate.TempRequiredBytes);
        Assert.Equal(100 * Mb, estimate.TotalRequiredBytes);
    }

    [Fact]
    public void SingleFolder_AlsoNeedsSpaceForTheTemporaryArchive()
    {
        // 資料夾要先打包成暫存 zip（壓縮等級是 NoCompression，見規格第 5 節，所以大小約等於
        // 原始資料夾），再加密成密文——兩份都要算。
        var estimate = EncryptSpaceEstimator.Estimate(
            [new PathSizeInfo(2 * Gb, IsFolder: true)],
            vaultFreeBytes: 10 * Gb, tempFreeBytes: 10 * Gb, vaultAndTempShareVolume: false);

        Assert.Equal(2 * Gb, estimate.VaultRequiredBytes);
        Assert.Equal(2 * Gb, estimate.TempRequiredBytes);
        Assert.Equal(4 * Gb, estimate.TotalRequiredBytes);
    }

    [Fact]
    public void MixedSelection_SumsBothKinds()
    {
        var estimate = EncryptSpaceEstimator.Estimate(
            [
                new PathSizeInfo(500 * Mb, IsFolder: false),
                new PathSizeInfo(1 * Gb, IsFolder: true),
            ],
            vaultFreeBytes: 10 * Gb, tempFreeBytes: 10 * Gb, vaultAndTempShareVolume: false);

        Assert.Equal(500 * Mb + 1 * Gb, estimate.VaultRequiredBytes);
        Assert.Equal(1 * Gb, estimate.TempRequiredBytes);
    }

    [Fact]
    public void EnoughSpaceOnBothVolumes_IsSufficient()
    {
        var estimate = EncryptSpaceEstimator.Estimate(
            [new PathSizeInfo(1 * Gb, IsFolder: true)],
            vaultFreeBytes: 2 * Gb, tempFreeBytes: 2 * Gb, vaultAndTempShareVolume: false);

        Assert.True(estimate.Sufficient);
    }

    [Fact]
    public void VaultVolumeTooSmall_IsNotSufficient()
    {
        var estimate = EncryptSpaceEstimator.Estimate(
            [new PathSizeInfo(1 * Gb, IsFolder: true)],
            vaultFreeBytes: 500 * Mb, tempFreeBytes: 10 * Gb, vaultAndTempShareVolume: false);

        Assert.False(estimate.Sufficient);
    }

    [Fact]
    public void TempVolumeTooSmall_IsNotSufficient()
    {
        // 只看 Vault 那一側會漏掉這種情況：Vault 放在大硬碟、但系統暫存在快滿的系統碟上。
        var estimate = EncryptSpaceEstimator.Estimate(
            [new PathSizeInfo(1 * Gb, IsFolder: true)],
            vaultFreeBytes: 10 * Gb, tempFreeBytes: 500 * Mb, vaultAndTempShareVolume: false);

        Assert.False(estimate.Sufficient);
    }

    [Fact]
    public void SameVolume_ChecksTheCombinedTotalAgainstItOnce()
    {
        // Vault 跟暫存在同一顆磁碟時，兩份輸出是在搶同一份可用空間——分開檢查會各自過關，
        // 合起來卻放不下。這是預設情境（Vault 預設在 %LocalAppData%，跟 %TEMP% 同一顆碟）。
        var estimate = EncryptSpaceEstimator.Estimate(
            [new PathSizeInfo(1 * Gb, IsFolder: true)],
            vaultFreeBytes: (long)(1.5 * Gb), tempFreeBytes: (long)(1.5 * Gb), vaultAndTempShareVolume: true);

        Assert.False(estimate.Sufficient);
    }

    [Fact]
    public void EmptySelection_RequiresNothingAndIsSufficient()
    {
        var estimate = EncryptSpaceEstimator.Estimate(
            [], vaultFreeBytes: 0, tempFreeBytes: 0, vaultAndTempShareVolume: true);

        Assert.Equal(0, estimate.TotalRequiredBytes);
        Assert.True(estimate.Sufficient);
    }

    [Fact]
    public void UnknownFreeSpace_IsTreatedAsSufficient()
    {
        // 拿不到可用空間（磁碟區查詢失敗，用 null 表示）時不要跳假警報——這是輔助性的提示，
        // 寧可不提醒，也不要因為查不到資訊就擋在使用者面前說空間不足。
        var estimate = EncryptSpaceEstimator.Estimate(
            [new PathSizeInfo(100 * Gb, IsFolder: true)],
            vaultFreeBytes: null, tempFreeBytes: null, vaultAndTempShareVolume: true);

        Assert.True(estimate.Sufficient);
        Assert.Equal(200 * Gb, estimate.TotalRequiredBytes);
    }
}
