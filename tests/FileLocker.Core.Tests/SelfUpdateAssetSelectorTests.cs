using FileLocker.Core.UpdateCheck;

namespace FileLocker.Core.Tests;

public class SelfUpdateAssetSelectorTests
{
    [Fact]
    public void SelectGuiInstallerAssetName_CliSetupListedBeforeGuiSetup_SkipsCliAndReturnsGui()
    {
        // 真實發生過的 bug：v2.1.0 這輪 GitHub Release 附件順序剛好是 CLI 安裝檔排在
        // GUI 安裝檔前面，舊邏輯「抓第一個副檔名是 .exe 的」會選錯。
        var assets = new[]
        {
            "FileLocker_CLI_v2.1.0_portable.zip",
            "FileLocker_CLI_v2.1.0_setup.exe",
            "FileLocker_v2.1.0_setup.exe",
        };

        var result = SelfUpdateAssetSelector.SelectGuiInstallerAssetName(assets);

        Assert.Equal("FileLocker_v2.1.0_setup.exe", result);
    }

    [Fact]
    public void SelectGuiInstallerAssetName_GuiSetupListedFirst_ReturnsGui()
    {
        var assets = new[]
        {
            "FileLocker_v2.1.0_setup.exe",
            "FileLocker_CLI_v2.1.0_setup.exe",
            "FileLocker_CLI_v2.1.0_portable.zip",
        };

        var result = SelfUpdateAssetSelector.SelectGuiInstallerAssetName(assets);

        Assert.Equal("FileLocker_v2.1.0_setup.exe", result);
    }

    [Fact]
    public void SelectGuiInstallerAssetName_OnlyCliAssetsPresent_ReturnsNull()
    {
        var assets = new[]
        {
            "FileLocker_CLI_v2.1.0_setup.exe",
            "FileLocker_CLI_v2.1.0_portable.zip",
        };

        var result = SelfUpdateAssetSelector.SelectGuiInstallerAssetName(assets);

        Assert.Null(result);
    }

    [Fact]
    public void SelectGuiInstallerAssetName_NoExeAssets_ReturnsNull()
    {
        var assets = new[] { "README.md", "checksums.txt" };

        var result = SelfUpdateAssetSelector.SelectGuiInstallerAssetName(assets);

        Assert.Null(result);
    }

    [Fact]
    public void SelectGuiInstallerAssetName_EmptyList_ReturnsNull()
    {
        var result = SelfUpdateAssetSelector.SelectGuiInstallerAssetName([]);

        Assert.Null(result);
    }

    [Fact]
    public void SelectGuiInstallerAssetName_CliMarkerCaseInsensitive_StillExcluded()
    {
        var assets = new[] { "FileLocker_cli_v2.1.0_setup.exe", "FileLocker_v2.1.0_setup.exe" };

        var result = SelfUpdateAssetSelector.SelectGuiInstallerAssetName(assets);

        Assert.Equal("FileLocker_v2.1.0_setup.exe", result);
    }
}
