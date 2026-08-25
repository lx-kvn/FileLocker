using FileLocker.Core.UpdateCheck;

namespace FileLocker.Core.Tests;

public class PasswordLockerAssetSelectorTests
{
    [Fact]
    public void SelectBestAssetName_CurrentVersionWithinRange_ReturnsAsset()
    {
        var assets = new[] { "PasswordVault_v0.1.0_for-FileLocker-1.0.0-to-1.9.9.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0");

        Assert.Equal("PasswordVault_v0.1.0_for-FileLocker-1.0.0-to-1.9.9.zip", result);
    }

    [Fact]
    public void SelectBestAssetName_CurrentVersionBelowRange_ReturnsNull()
    {
        var assets = new[] { "PasswordVault_v0.1.0_for-FileLocker-1.5.0-to-1.9.9.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.0.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_CurrentVersionAboveRange_ReturnsNull()
    {
        var assets = new[] { "PasswordVault_v0.1.0_for-FileLocker-1.0.0-to-1.5.0.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "2.0.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_CurrentVersionAtRangeBoundary_IsInclusive()
    {
        var assets = new[] { "PasswordVault_v0.1.0_for-FileLocker-1.0.0-to-1.5.0.zip" };

        Assert.NotNull(PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.0.0"));
        Assert.NotNull(PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0"));
    }

    [Fact]
    public void SelectBestAssetName_MultipleMatches_PicksHighestPasswordVaultVersion()
    {
        var assets = new[]
        {
            "PasswordVault_v0.1.0_for-FileLocker-1.0.0-to-2.0.0.zip",
            "PasswordVault_v0.2.0_for-FileLocker-1.0.0-to-2.0.0.zip",
            "PasswordVault_v0.1.5_for-FileLocker-1.0.0-to-2.0.0.zip"
        };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0");

        Assert.Equal("PasswordVault_v0.2.0_for-FileLocker-1.0.0-to-2.0.0.zip", result);
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
        var assets = new[] { "PasswordVault_v0.1.0_for-FileLocker-1.0.0-to-2.0.0.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "not-a-version");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_EmptyAssetList_ReturnsNull()
    {
        var result = PasswordLockerAssetSelector.SelectBestAssetName([], "1.5.0");

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestAssetName_OldPasswordLockerNamingConvention_NoLongerMatches()
    {
        // 舊命名（PasswordLocker_vX.Y.Z_min-max.zip，FileLocker.PasswordLocker 遷出前的格式）
        // 刻意不相容——切換消費來源之後，FileLocker 自己的 Release 不會再產出這種資產，這裡
        // 固定住「新格式上線後舊格式就是不認得」這個行為，避免以後改動又悄悄兩種都收。
        var assets = new[] { "PasswordLocker_v0.1.0_1.0.0-2.0.0.zip" };

        var result = PasswordLockerAssetSelector.SelectBestAssetName(assets, "1.5.0");

        Assert.Null(result);
    }
}
