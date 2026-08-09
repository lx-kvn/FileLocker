using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using FileLocker.PluginContracts;

namespace FileLocker.App;

/// <summary>
/// 密碼庫是可選配部件（見 FileLocker_密碼庫_功能規劃.md 第 2 節）：偵測固定子資料夾
/// <c>plugins/PasswordLocker/</c> 底下有沒有 dll，有就用 <see cref="AssemblyLoadContext"/>
/// 動態載入，主體完全不在編譯期依賴 FileLocker.PasswordLocker.dll。
/// </summary>
public enum PasswordLockerModuleStatus
{
    /// <summary>沒偵測到部件 dll。</summary>
    NotInstalled,

    /// <summary>偵測到 dll，但載入或初始化失敗（檔案損毀、缺依賴等）——跟未安裝分開回報，
    /// 使用者需要知道自己「曾經裝過但現在壞了」，不能被誤導成「從沒裝過」。</summary>
    Broken,

    /// <summary>已安裝且正常運作。</summary>
    Ok
}

public static class PasswordLockerPluginLoader
{
    private const string PluginAssemblyFileName = "FileLocker.PasswordLocker.dll";

    /// <summary>
    /// <paramref name="dataDirectory"/>／<paramref name="vaultItemExists"/> 是部件 Initialize
    /// 需要的初始化資訊（見 <see cref="PasswordLockerPluginContext"/>），跟載入 dll 這件事本身
    /// 無關但一起傳進來，呼叫端不用再另外呼叫一次 Initialize。
    /// </summary>
    public static (PasswordLockerModuleStatus Status, IPasswordLockerPlugin? Plugin) Load(
        string dataDirectory, Func<string, bool> vaultItemExists)
    {
        var pluginDllPath = Path.Combine(AppContext.BaseDirectory, "plugins", "PasswordLocker", PluginAssemblyFileName);
        if (!File.Exists(pluginDllPath))
        {
            return (PasswordLockerModuleStatus.NotInstalled, null);
        }

        try
        {
            var loadContext = new PasswordLockerLoadContext(pluginDllPath);
            var assembly = loadContext.LoadFromAssemblyPath(pluginDllPath);

            var pluginType = assembly.GetTypes()
                .FirstOrDefault(t => typeof(IPasswordLockerPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
            if (pluginType is null || Activator.CreateInstance(pluginType) is not IPasswordLockerPlugin plugin)
            {
                return (PasswordLockerModuleStatus.Broken, null);
            }

            plugin.Initialize(new PasswordLockerPluginContext(dataDirectory, vaultItemExists));
            return (PasswordLockerModuleStatus.Ok, plugin);
        }
        catch (Exception ex)
        {
            // 這裡刻意接最基底的 Exception，不是接特定幾種載入失敗的例外類型——dll 損毀、
            // 型別載入失敗、Initialize 內部邏輯丟出的任何例外都要在這裡被擋下來，絕對不能讓
            // 一個壞掉的可選配部件把整個 FileLocker 啟動流程拖垮。細節寫進主控台方便除錯，
            // 前端只需要知道「壞了」這個狀態本身（見 PasswordLockerModuleStatus.Broken）。
            Console.WriteLine($"PasswordLocker 部件載入失敗：{ex}");
            return (PasswordLockerModuleStatus.Broken, null);
        }
    }
}

/// <summary>
/// 隔離部件自己的相依 DLL，但 <see cref="FileLocker.PluginContracts"/> 跟
/// <c>FileLocker.Core</c> 一定要退回主體已經載入的那一份——不然 <see cref="IPasswordLockerPlugin"/>
/// 這個介面型別會在兩個 AssemblyLoadContext 裡各自存在一份，變成兩個不同的 Type 物件，
/// 部件實作出來的類別會被判定成「沒有實作這個介面」，強制轉型直接失敗。
/// </summary>
internal sealed class PasswordLockerLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PasswordLockerLoadContext(string pluginPath) : base("PasswordLockerPlugin", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is "FileLocker.PluginContracts" or "FileLocker.Core")
        {
            return null;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is not null ? LoadFromAssemblyPath(assemblyPath) : null;
    }
}
