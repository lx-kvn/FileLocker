using FileLocker.Core.UpdateCheck;

namespace FileLocker.Core.Tests;

public class PasswordLockerAssetSelectorTests
{
    [Fact]
    public void SelectBestAssetName_CurrentVersionWithinRange_ReturnsAsset()
    {
        var assets = new[] { "PasswordLocker_v0.1.0_1.0.0-1.9.9.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0");

        Assert.Equal("PasswordLocker_v0.1.0_1.0.0-1.9.9.zip", result);
    }

    [Fact]
    public void SelectBestAssetName_CurrentVersionBelowRange_ReturnsNull()
    {
        var assets = new[] { "PasswordLocker_v0.1.0_1.5.0-1.9.9.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.0.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_CurrentVersionAboveRange_ReturnsNull()
    {
        var assets = new[] { "PasswordLocker_v0.1.0_1.0.0-1.5.0.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "2.0.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_CurrentVersionAtRangeBoundary_IsInclusive()
    {
        var assets = new[] { "PasswordLocker_v0.1.0_1.0.0-1.5.0.zip" };

        Assert.NotNull(PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.0.0"));
        Assert.NotNull(PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0"));
    }

    [Fact]
    public void SelectBestAssetName_MultipleMatches_PicksHighestPasswordLockerVersion()
    {
        var assets = new[]
        {
            "PasswordLocker_v0.1.0_1.0.0-2.0.0.zip",
            "PasswordLocker_v0.2.0_1.0.0-2.0.0.zip",
            "PasswordLocker_v0.1.5_1.0.0-2.0.0.zip"
        };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0");

        Assert.Equal("PasswordLocker_v0.2.0_1.0.0-2.0.0.zip", result);
    }

    [Fact]
    public void SelectBestAssetName_IgnoresUnrelatedAssets()
    {
        var assets = new[] { "FileLocker_v1.5.0_setup.exe", "readme.txt" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_InvalidCurrentVersion_ReturnsNull()
    {
        var assets = new[] { "PasswordLocker_v0.1.0_1.0.0-2.0.0.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "not-a-version");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_EmptyAssetList_ReturnsNull()
    {
        var result = PasswordLockerAssetSelector.SelectBestAssetName([], "1.5.0");

        Assert.Null(result);
    }
}
