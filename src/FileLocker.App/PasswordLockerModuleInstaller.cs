using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileLocker.Core.UpdateCheck;

namespace FileLocker.App;

/// <summary>
/// 密碼庫可選配部件的自動安裝／更新（見 FileLocker_密碼庫_功能規劃.md 第 2.2／2.4 節第二階段）：
/// 掃 FileLocker 本體 GitHub Release 的資產列表找相容的 PasswordLocker zip、下載、解壓縮到
/// 暫存資料夾。跟軟體本身的更新檢查（<see cref="MainWindow"/> 裡的 FetchLatestGitHubReleaseAsync）
/// 平行、獨立，共用同一個 GitHub repo，但找的是不同的資產。
///
/// 不做熱重載：dll 可能正被 <see cref="PasswordLockerPluginLoader"/> 用 AssemblyLoadContext 載入中，
/// Windows 不允許原地覆寫，所以這裡永遠解壓到 <c>plugins/PasswordLocker.pending/</c>，真正生效
/// （把舊資料夾換成新的）要等下次啟動、在載入部件之前，見 <see cref="SwapPendingInstallIfPresent"/>。
/// </summary>
public static class PasswordLockerModuleInstaller
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/lx-kvn/FileLocker/releases/latest";

    private const string ManifestFileName = "install_manifest.json";
    private const string UninstallMarkerFileName = "PasswordLocker.uninstall-marker";

    private static string PluginsRoot => Path.Combine(AppContext.BaseDirectory, "plugins");
    private static string ActiveDir => Path.Combine(PluginsRoot, "PasswordLocker");
    private static string PendingDir => Path.Combine(PluginsRoot, "PasswordLocker.pending");
    private static string ManifestPath => Path.Combine(AppContext.BaseDirectory, ManifestFileName);
    private static string UninstallMarkerPath => Path.Combine(PluginsRoot, UninstallMarkerFileName);

    /// <summary>查目前 FileLocker 本體的 GitHub Release 資產列表，挑出跟 <paramref name="currentFileLockerVersion"/>
    /// 相容、PasswordLocker 版本最新的一筆（見 PasswordLockerAssetSelector），回傳資產名稱與下載網址。
    /// 找不到相容資產、或查詢本身失敗都回傳 (null, null)，不拋例外——呼叫端只需要知道「有沒有找到」。</summary>
    public static async Task<(string? AssetName, string? DownloadUrl)> FindCompatibleReleaseAsync(HttpClient httpClient, string currentFileLockerVersion)
    {
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FileLocker-PasswordLockerInstaller");
        var response = await httpClient.GetAsync(ReleasesApiUrl);
        if (!response.IsSuccessStatusCode)
        {
            return (null, null);
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets))
        {
            return (null, null);
        }

        var assetsByName = new Dictionary<string, string>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name is not null && url is not null)
            {
                assetsByName[name] = url;
            }
        }

        var bestName = PasswordLockerAssetSelector.SelectBestAssetName(assetsByName.Keys, currentFileLockerVersion);
        return bestName is null ? (null, null) : (bestName, assetsByName[bestName]);
    }

    /// <summary>下載 zip、解壓縮到 <c>plugins/PasswordLocker.pending/</c>——先清掉舊的暫存內容再解壓，
    /// 避免上次安裝失敗留下的殘餘檔案跟這次的內容混在一起。真正切換到生效目錄要等下次啟動
    /// （見 <see cref="SwapPendingInstallIfPresent"/>），這裡不動 <see cref="ActiveDir"/>。</summary>
    public static async Task DownloadAndStageAsync(HttpClient httpClient, string downloadUrl)
    {
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"PasswordLocker-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await httpClient.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();
            await using (var fileStream = File.Create(tempZipPath))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            if (Directory.Exists(PendingDir))
            {
                Directory.Delete(PendingDir, recursive: true);
            }
            Directory.CreateDirectory(PendingDir);
            ZipFile.ExtractToDirectory(tempZipPath, PendingDir);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }
        }
    }

    /// <summary>App 啟動時、在 <see cref="PasswordLockerPluginLoader.Load"/> 之前呼叫——暫存資料夾
    /// 存在就代表上次執行期間有下載過新版本待生效：刪掉舊的生效資料夾（如果有）、把暫存資料夾
    /// 換成生效資料夾。這一步失敗（例如檔案被其他程序鎖住）不應該讓整個 App 開不起來，安靜略過、
    /// 讓舊版本（如果有的話）繼續用，暫存資料夾留著下次啟動再試一次。</summary>
    public static void SwapPendingInstallIfPresent()
    {
        if (!Directory.Exists(PendingDir))
        {
            return;
        }

        try
        {
            if (Directory.Exists(ActiveDir))
            {
                Directory.Delete(ActiveDir, recursive: true);
            }
            Directory.Move(PendingDir, ActiveDir);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>設定頁「解除安裝密碼庫部件」按鈕呼叫——跟安裝/更新同一套「重啟才生效」機制，
    /// dll 正在被 AssemblyLoadContext 載入中，這裡只先寫標記，真正刪除在下次啟動、
    /// <see cref="ApplyPendingUninstallIfMarked"/> 裡完成（見規劃文件第 9.1 節）。</summary>
    public static void MarkForUninstall()
    {
        Directory.CreateDirectory(PluginsRoot);
        File.WriteAllText(UninstallMarkerPath, "");
    }

    /// <summary>App 啟動時、在 <see cref="PasswordLockerPluginLoader.Load"/>／<see cref="SwapPendingInstallIfPresent"/>
    /// 之前呼叫——標記檔存在就代表使用者上次執行期間按過「解除安裝」，把 <see cref="ActiveDir"/>
    /// 整個刪掉、清掉標記，並把這些檔案從 install_manifest.json 的 "files" 陣列移除（見
    /// <see cref="UnregisterFromInstallManifest"/>，跟 <see cref="SyncInstallManifest"/> 是一對
    /// 相反的操作）。刪除失敗（檔案被鎖住）安靜放棄、標記留著，下次啟動再試一次。</summary>
    public static void ApplyPendingUninstallIfMarked()
    {
        if (!File.Exists(UninstallMarkerPath))
        {
            return;
        }

        try
        {
            if (Directory.Exists(ActiveDir))
            {
                var relativePaths = GetRelativeFilePaths(ActiveDir);
                Directory.Delete(ActiveDir, recursive: true);
                UnregisterFromInstallManifest(relativePaths);
            }
            File.Delete(UninstallMarkerPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>把 plugins/PasswordLocker/ 目前的檔案清單同步進 mswi 產生的 install_manifest.json
    /// 的 "files" 陣列——詳細理由見規劃文件第 9.2 節：部件是安裝完成後才動態下載的，mswi 打包
    /// 當下寫的 manifest 不會知道這些檔案存在，不同步的話 Windows 原生解除安裝會把這個資料夾
    /// 當成「使用者自行產生的資料」保留下來，導致整個安裝目錄解除安裝後清不掉。每次啟動、
    /// 確認部件存在時都呼叫，冪等（已經同步過的檔案不會重複加入）。</summary>
    public static void SyncInstallManifest()
    {
        if (!Directory.Exists(ActiveDir))
        {
            return;
        }

        var relativePaths = GetRelativeFilePaths(ActiveDir);
        MutateManifestFiles(files =>
        {
            foreach (var path in relativePaths)
            {
                if (!files.Contains(path))
                {
                    files.Add(path);
                }
            }
        });
    }

    private static void UnregisterFromInstallManifest(IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }
        MutateManifestFiles(files => files.RemoveAll(relativePaths.Contains));
    }

    /// <summary>install_manifest.json 是 mswi（另一個獨立專案）產生的檔案，格式沒有正式文件保證
    /// 穩定性，讀寫都包在這裡、集中一處失敗處理：找不到檔案（開發環境、非 mswi 安裝）、格式跟
    /// 預期不符（mswi 版本更新改了結構）都安靜放棄，不讓 FileLocker 自己的啟動/安裝流程失敗——
    /// 這個同步本來就是錦上添花，最差的結果只是退回「解除安裝時這個資料夾不會被自動清空」這個
    /// 較保守的舊行為，不影響密碼庫部件本身能不能正常運作。</summary>
    private static void MutateManifestFiles(Action<List<string>> mutate)
    {
        if (!File.Exists(ManifestPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(ManifestPath);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null)
            {
                return;
            }

            var files = root["files"]?.AsArray().Select(n => n?.GetValue<string>()).Where(s => s is not null).Select(s => s!).ToList()
                ?? [];
            mutate(files);

            root["files"] = new JsonArray(files.Select(f => (JsonNode)JsonValue.Create(f)).ToArray());
            File.WriteAllText(ManifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }
    }

    private static List<string> GetRelativeFilePaths(string dir)
    {
        var baseDir = AppContext.BaseDirectory;
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(baseDir, f))
            .ToList();
    }
}
