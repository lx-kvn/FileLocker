using FileLocker.Core.Models;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應「單檔案分散式加密」功能規劃 §6.2／§8：跟 MarkerStatusChecker 平行的一組行為，
/// 差別只在檢查的是 .flocked 檔案本體（片 2 的 FlockedFileFormat 讀出來的 header UUID）
/// 而不是 .locked 指標檔內容——不需要 VaultManager／簽章金鑰，直接手動寫一份 .flocked
/// 檔案就能測試全部行為，比照 MarkerStatusCheckerTests 的既有寫法。
/// </summary>
public class FlockedStatusCheckerTests : IDisposable
{
    private readonly DirectoryInfo _workDir;

    public FlockedStatusCheckerTests()
    {
        _workDir = Directory.CreateTempSubdirectory("FileLockerWork_");
    }

    public void Dispose()
    {
        if (_workDir.Exists) _workDir.Delete(recursive: true);
    }

    private static LockedItemMetadata CreateSampleMetadata(string uuid, string originalPath, ItemType type = ItemType.File) => new()
    {
        Uuid = uuid,
        OriginalName = Path.GetFileName(originalPath.TrimEnd(Path.DirectorySeparatorChar)),
        OriginalPath = originalPath,
        PasswordVerificationHash = "dummyHashBase64==",
        Salt = "dummySaltBase64==",
        Argon2TimeCost = 3,
        Argon2MemoryCostKb = 65536,
        Argon2Parallelism = 2,
        Type = type,
        StorageMode = StorageMode.Standalone,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    private static void WriteFlockedAt(string flockedPath, string uuid)
    {
        using var stream = File.Create(flockedPath);
        FlockedFileFormat.WriteHeader(stream, uuid);
        stream.Write([1, 2, 3, 4]); // 隨便塞幾個 byte 模擬後面接的密文串流，內容不重要
    }

    [Fact]
    public void CheckFlockedStatus_ForFileStillAtOriginalLocation_ReturnsFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "沒被搬動的檔案.txt");
        var flockedPath = FlockedStatusChecker.ComputeFlockedPath(originalPath, isFolder: false);
        WriteFlockedAt(flockedPath, uuid);

        var status = FlockedStatusChecker.CheckFlockedStatus(CreateSampleMetadata(uuid, originalPath));

        Assert.True(status.Found);
        Assert.Equal(flockedPath, status.MarkerPath);
    }

    [Fact]
    public void CheckFlockedStatus_ForFolderStillAtOriginalLocation_ReturnsFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "沒被搬動的資料夾");
        var flockedPath = FlockedStatusChecker.ComputeFlockedPath(originalPath, isFolder: true);
        WriteFlockedAt(flockedPath, uuid);

        var status = FlockedStatusChecker.CheckFlockedStatus(CreateSampleMetadata(uuid, originalPath, ItemType.Folder));

        Assert.True(status.Found);
    }

    [Fact]
    public void CheckFlockedStatus_WhenFlockedFileMissing_ReturnsNotFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "從來沒建立過.flocked檔案的檔案.txt");

        var status = FlockedStatusChecker.CheckFlockedStatus(CreateSampleMetadata(uuid, originalPath));

        Assert.False(status.Found);
        Assert.Null(status.MarkerPath);
        Assert.Equal(ErrorCodes.FlockedNotFound, status.Code);
    }

    [Fact]
    public void CheckFlockedStatus_WhenOriginalPositionReplacedByDifferentUuid_ReturnsNotFound()
    {
        var uuid = Guid.NewGuid().ToString();
        var otherUuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "位置被別的項目取代.txt");
        var flockedPath = FlockedStatusChecker.ComputeFlockedPath(originalPath, isFolder: false);

        // 同一個位置的 .flocked 實際是另一個 UUID——例如使用者刪掉舊項目後，在原地重新用
        // 分散式加密加密了別的東西。
        WriteFlockedAt(flockedPath, otherUuid);

        var status = FlockedStatusChecker.CheckFlockedStatus(CreateSampleMetadata(uuid, originalPath));

        Assert.False(status.Found);
        Assert.Equal(ErrorCodes.FlockedReplacedByOther, status.Code);
        Assert.Equal(otherUuid, status.ConflictingUuid);
    }

    [Fact]
    public void CheckFlockedStatus_WhenFileAtPositionIsNotAFlockedFile_ReturnsParseFailed()
    {
        // 原本位置有檔案，但不是合法的 .flocked（例如被別的程式覆蓋、或使用者手動塞了個
        // 同名檔案），跟「找不到」是兩種不同情境，要分開回報，比照 MarkerParseFailed 的既有慣例。
        var uuid = Guid.NewGuid().ToString();
        var originalPath = Path.Combine(_workDir.FullName, "位置被非法內容取代.txt");
        var flockedPath = FlockedStatusChecker.ComputeFlockedPath(originalPath, isFolder: false);
        File.WriteAllBytes(flockedPath, [0x00, 0x01, 0x02]);

        var status = FlockedStatusChecker.CheckFlockedStatus(CreateSampleMetadata(uuid, originalPath));

        Assert.False(status.Found);
        Assert.Equal(ErrorCodes.FlockedParseFailed, status.Code);
    }

    [Fact]
    public void ComputeFlockedPath_ForFile_UsesNameWithoutExtensionPlusFlockedSuffix()
    {
        var originalPath = Path.Combine(_workDir.FullName, "報告.docx");

        var flockedPath = FlockedStatusChecker.ComputeFlockedPath(originalPath, isFolder: false);

        Assert.Equal(Path.Combine(_workDir.FullName, "報告.flocked"), flockedPath);
    }

    [Fact]
    public void ComputeFlockedPath_ForFolder_UsesFolderNamePlusFlockedSuffix()
    {
        var originalPath = Path.Combine(_workDir.FullName, "我的資料夾");

        var flockedPath = FlockedStatusChecker.ComputeFlockedPath(originalPath, isFolder: true);

        Assert.Equal(Path.Combine(_workDir.FullName, "我的資料夾.flocked"), flockedPath);
    }
}
