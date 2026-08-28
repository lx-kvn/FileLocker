using FileLocker.Cli;

namespace FileLocker.Cli.Tests;

/// <summary>
/// <see cref="BundleTrimming"/> 是 GUI 安裝檔瘦身用的建置期工具（見規劃：GUI 安裝檔內建的
/// cli/ 子資料夾原本整份重複帶一份跟外層一模一樣的巨大共用套件——Microsoft.Windows.SDK.NET.dll
/// 跟 runtimes/ 加起來快 57MB，GUI 跟 CLI 是同一次建置產出、位元組完全相同，沒有理由重複打包
/// 兩份）。這裡只測純邏輯（雜湊比對決定要不要跳過複製、組出備援載入路徑），不牽涉真的檔案
/// 系統 I/O——真正的檔案複製/刪除、Vault 有沒有需要，這兩件事完全無關，純函式化才能在不
/// 建置整個安裝檔的情況下驗證這個決策邏輯。
///
/// **獨立發布的 CLI_setup／CLI_zip 不能套用這套瘦身邏輯**——那個情境下 CLI 旁邊沒有 GUI
/// 的共用套件可以借，勢必要維持自帶完整套件；這套工具只給「GUI 安裝檔內嵌 cli/ 子資料夾」
/// 這個情境用，呼叫端（MSBuild 的 Exec 步驟）要注意不要對 CLI 獨立發布產物的來源資料夾
/// 跑同一套邏輯，否則裝出來的獨立 CLI 會因為找不到 DLL 而無法執行。
/// </summary>
public class BundleTrimmingTests
{
    [Fact]
    public void GetFilesToSkip_FileIdenticalInMainRoot_IsSkipped()
    {
        var cliFiles = new[] { ("Microsoft.Windows.SDK.NET.dll", "abc123") };
        var mainRootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.Windows.SDK.NET.dll"] = "abc123"
        };

        var result = BundleTrimming.GetFilesToSkip(cliFiles, mainRootFiles);

        Assert.Contains("Microsoft.Windows.SDK.NET.dll", result);
    }

    [Fact]
    public void GetFilesToSkip_SameRelativePathButDifferentHash_IsNotSkipped()
    {
        // 版本不一致時寧可重複打包也不要冒著載到錯版本的風險——雜湊比對就是這道安全網，
        // 只有真的位元組相同才敢判斷成「重複、可以省略」。
        var cliFiles = new[] { ("FileLocker.Core.dll", "cli-hash") };
        var mainRootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FileLocker.Core.dll"] = "different-hash"
        };

        var result = BundleTrimming.GetFilesToSkip(cliFiles, mainRootFiles);

        Assert.DoesNotContain("FileLocker.Core.dll", result);
    }

    [Fact]
    public void GetFilesToSkip_FileOnlyExistsInCli_IsNotSkipped()
    {
        // FileLocker.Cli.exe／.dll 這類 CLI 專屬檔案，外層 GUI 資料夾裡本來就找不到同名檔案，
        // 一定要照樣複製，不然 CLI 自己都不見了。
        var cliFiles = new[] { ("FileLocker.Cli.exe", "cli-only-hash") };
        var mainRootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var result = BundleTrimming.GetFilesToSkip(cliFiles, mainRootFiles);

        Assert.DoesNotContain("FileLocker.Cli.exe", result);
    }

    [Fact]
    public void GetFilesToSkip_NestedRuntimesPathMatchesByFullRelativePath()
    {
        // 用完整相對路徑（不是只比對檔名）當比對鍵——runtimes/win-x64/native/e_sqlite3.dll
        // 跟 runtimes/win-arm64/native/e_sqlite3.dll 檔名相同、內容不同，只比檔名會誤判成
        // 重複，比對完整相對路徑才不會把不同架構的原生庫搞混。
        var cliFiles = new[]
        {
            ("runtimes/win-x64/native/e_sqlite3.dll", "x64-hash"),
            ("runtimes/win-arm64/native/e_sqlite3.dll", "arm64-hash")
        };
        var mainRootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["runtimes/win-x64/native/e_sqlite3.dll"] = "x64-hash"
            // win-arm64 版本外層 GUI 資料夾裡沒有（例如 GUI 只帶自己執行的那個架構）。
        };

        var result = BundleTrimming.GetFilesToSkip(cliFiles, mainRootFiles);

        Assert.Contains("runtimes/win-x64/native/e_sqlite3.dll", result);
        Assert.DoesNotContain("runtimes/win-arm64/native/e_sqlite3.dll", result);
    }

    [Fact]
    public void GetFilesToSkip_PathComparisonIsCaseInsensitive()
    {
        var cliFiles = new[] { ("Microsoft.Windows.SDK.NET.dll", "abc123") };
        var mainRootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["microsoft.windows.sdk.net.dll"] = "abc123"
        };

        var result = BundleTrimming.GetFilesToSkip(cliFiles, mainRootFiles);

        Assert.Contains("Microsoft.Windows.SDK.NET.dll", result);
    }

    [Fact]
    public void GetFallbackAssemblyPath_CombinesDirectoryAndDllExtension()
    {
        var path = BundleTrimming.GetFallbackAssemblyPath("Microsoft.Windows.SDK.NET", "cli/..");

        Assert.Equal(Path.Combine("cli/..", "Microsoft.Windows.SDK.NET.dll"), path);
    }
}
