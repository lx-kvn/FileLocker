using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace FileLocker.Cli;

/// <summary>
/// GUI 安裝檔內嵌的 cli/ 子資料夾建置期瘦身工具——GUI 跟 CLI 是同一次建置產出，
/// cli/ 資料夾裡有大約 57MB（Microsoft.Windows.SDK.NET.dll＋runtimes/）是跟外層 GUI
/// 資料夾一模一樣、位元組完全相同的共用套件，沒有理由在同一顆安裝檔裡重複打包兩份。
/// 這裡只放純邏輯（雜湊比對、組出備援路徑），實際的檔案系統操作（列舉檔案、算雜湊、
/// 刪除）由呼叫端（<c>--internal-trim-bundle</c>，見 Program.cs）負責。
///
/// **只有 GUI 安裝檔內嵌的 cli/ 子資料夾會真的被瘦身，獨立發布的 CLI_setup／CLI_zip
/// 完全不受影響**：那個情境下 CLI 旁邊沒有 GUI 的共用套件可以借，來源目錄
/// （<c>FileLocker.Cli</c> 自己的建置輸出）本身沒有被動過，繼續帶著完整套件。
///
/// **瘦身後怎麼還能執行**：不是靠 <c>runtimeconfig.json</c> 的 <c>additionalProbingPaths</c>
/// ——實測過那個機制是給正規 NuGet 套件快取目錄結構用的（<c>&lt;packageId&gt;/&lt;version&gt;/lib/…</c>），
/// 對著一個放滿散裝 DLL 的平面資料夾直接查會找不到，載入照樣失敗。改成掛一個
/// <see cref="AssemblyLoadContext.Resolving"/> 事件——只有在標準解析（讀 deps.json 找同
/// 資料夾內的 DLL）失敗時才會觸發，找得到就照常使用，這裡純粹是失敗後的備援，對獨立發布版
/// 完全零影響（因為它從來不會觸發到這個備援）。
///
/// **這個掛勾一定要用 <see cref="ModuleInitializerAttribute"/>，不能放在 Program.cs 的
/// Main 裡當第一行**——這裡曾經真的這樣做過，實測還是崩潰：C# top-level statements 整個
/// Main 編譯成一個方法，CLR JIT 編譯這個方法時，是把方法本體從頭到尾一次解析完才開始執行
/// （不是執行到哪行才編譯到哪行），只要 Main 的任何一行（不管多後面）用到 FileLocker.Core
/// 的型別，JIT 當下就要去解析那個組件，這發生在 Main 的第一行程式碼真正執行之前，掛在
/// Main 開頭的事件處理常式完全來不及生效。Module Initializer 在整個模組被載入時就執行，
/// 早於 Main 方法被 JIT，這個時間點才夠早。</summary>
public static class BundleTrimming
{
    /// <summary>Program.cs 的 Main 方法一定會用到 FileLocker.Core 的型別，JIT 編譯 Main
    /// 時就會需要解析該組件——這個掛勾必須在那之前完成，見類別開頭的完整說明。</summary>
    [ModuleInitializer]
    internal static void RegisterFallbackAssemblyResolver()
    {
        AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name is null)
            {
                return null;
            }
            var fallbackPath = Path.GetFullPath(
                GetFallbackAssemblyPath(assemblyName.Name, Path.Combine(AppContext.BaseDirectory, "..")));
            return File.Exists(fallbackPath) ? context.LoadFromAssemblyPath(fallbackPath) : null;
        };
    }

    /// <summary>
    /// 決定 cli/ 資料夾底下哪些檔案可以跳過複製——只有「外層 GUI 資料夾裡，同一個相對路徑
    /// 存在一個雜湊值完全相同的檔案」才判定為重複、可以省略。比對用完整相對路徑而不是只比
    /// 檔名，避免 runtimes/win-x64/native/e_sqlite3.dll 跟 runtimes/win-arm64/native/e_sqlite3.dll
    /// 這種同檔名不同架構的原生庫被誤判成同一份。雜湊不同（版本不一致）時寧可保守複製，
    /// 不冒著讓 CLI 載到跟自己編譯時不同版本 DLL 的風險。
    /// </summary>
    /// <param name="cliFiles">cli/ 資料夾底下每個檔案的（相對路徑, 雜湊值）。</param>
    /// <param name="mainRootFiles">外層 GUI 資料夾底下每個檔案的相對路徑到雜湊值對照表
    /// （呼叫端應使用忽略大小寫的 Dictionary，Windows 檔案系統不分大小寫）。</param>
    /// <returns>可以跳過複製的相對路徑集合。</returns>
    public static IReadOnlySet<string> GetFilesToSkip(
        IEnumerable<(string RelativePath, string Hash)> cliFiles,
        IReadOnlyDictionary<string, string> mainRootFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, hash) in cliFiles)
        {
            if (mainRootFiles.TryGetValue(relativePath, out var mainRootHash)
                && string.Equals(mainRootHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(relativePath);
            }
        }
        return result;
    }

    /// <summary>
    /// 組出 <see cref="System.Runtime.Loader.AssemblyLoadContext.Resolving"/> 備援載入時要
    /// 試的完整路徑——單純字串組合，不做任何檔案存在與否的檢查（呼叫端拿到路徑後自己決定
    /// 要不要真的載入），保持這裡是純函式方便測試。
    /// </summary>
    /// <param name="assemblyShortName">組件名稱（不含副檔名），來自
    /// <see cref="System.Reflection.AssemblyName.Name"/>。</param>
    /// <param name="fallbackDirectory">要去找的備援資料夾（GUI 安裝檔情境下是 cli/ 的上層）。</param>
    public static string GetFallbackAssemblyPath(string assemblyShortName, string fallbackDirectory) =>
        Path.Combine(fallbackDirectory, assemblyShortName + ".dll");
}
