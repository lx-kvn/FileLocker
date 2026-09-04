using System.IO;
using Microsoft.Win32;

namespace FileLocker.App;

/// <summary>
/// 密碼庫瀏覽器擴充功能的 Native Messaging Host 註冊（見 已完成/密碼庫_功能規劃.md 第 5 節）
/// ——比照 <see cref="ShellExtensionRegistrar"/> 的自我修復模式：每次啟動都比對目前狀態，跟現況
/// 不一致才重寫，不需要獨立的解除安裝步驟。全程只動 <c>HKCU</c>，不需要系統管理員權限。
///
/// 這裡的 manifest「path」欄位不指向 <c>plugins/PasswordLocker/</c> 這個部件自帶的轉接程式
/// 副本，改指向 <see cref="SharedExePath"/>（兩邊 App 共用的實體檔案，見
/// PasswordVault_獨立化_規劃.md 第 8.1 節）——根因是 <c>FileLocker.App</c> 跟 <c>PasswordVault.exe</c>
/// 過去各自帶一份自己的轉接程式副本、各自指向自己的路徑，登錄檔（每次啟動都自我修復覆寫，
/// 最後啟動的一方贏）跟 Named Pipe（先搶到的一方持有連線，最先啟動的一方贏）這兩套「贏家」判斷
/// 邏輯不一致時，Chrome 被登錄檔指向 A 的轉接程式，但 Pipe 被 B 持有，B 的
/// <c>VerifyClientIsExpectedHost</c> 拿自己認得的路徑一比對，發現對不上就直接切斷連線
/// （「Pipe is broken」）。改成兩邊都認同一個實體檔案、同一個路徑之後，不管誰贏得 Pipe、誰贏得
/// 登錄檔，雙方講的都是同一個地址，不會再對不上。
///
/// **manifest 檔案本身也放在共用位置**，不放在 <c>%LocalAppData%\FileLocker\</c> 底下。理由跟
/// 轉接程式同一條：登錄機碼只有一個值，兩邊都會寫，先前是「最後啟動的一方贏」。兩邊寫同一個
/// 檔案路徑、而且內容逐位元組相同（見 <see cref="NativeHostRegistration"/>）之後，誰贏就不再
/// 有影響。放在 FileLocker 自己的資料夾還有一個實際的洞：使用者移除 FileLocker、只留
/// PasswordVault 時，登錄機碼會指著一個已經被刪掉的 manifest，而 PasswordVault 不會去修它。
/// </summary>
public static class PasswordLockerNativeHostRegistrar
{
    private const string RegistryKeyPath =
        @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostRegistration.HostName;

    // PasswordVault.NativeHost.exe（部件遷出獨立 repo 後的檔名，見
    // PasswordVault_獨立化_規劃.md 第 17 節第 3 點）。
    private const string HostExeFileName = "PasswordVault.NativeHost.exe";

    /// <summary>轉接程式的相依檔案跟它同名不同副檔名（.dll／.deps.json／.runtimeconfig.json），
    /// 用前綴一起搬——少複製任何一個到共用位置，執行檔就會啟動失敗（見 CLAUDE.md「已知的坑」
    /// 關於複製部件時容易漏掉這幾個檔案的說明）。</summary>
    private const string HostFilePattern = "PasswordVault.NativeHost.*";

    /// <summary>兩邊 App 共用的存放位置——選在不屬於任一邊安裝資料夾的
    /// <c>%LocalAppData%\PasswordVault\NativeHost\</c>，跟第 7 節「密碼庫資料改指向共用路徑」
    /// 同一個理由：兩邊安裝、解除安裝、版本升級都不會動到它。</summary>
    internal static string SharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PasswordVault", "NativeHost");

    /// <summary>兩邊 Pipe Server 建構時的 expectedClientExePath、manifest 的 path 欄位，
    /// 都指向同一個值——即使部件還沒安裝、共用位置還沒有實體檔案，這裡也回傳固定路徑字串，
    /// 不做檔案是否存在的檢查（比對用的是路徑字串本身，不要求檔案當下存在）。</summary>
    public static string SharedExePath => Path.Combine(SharedDirectory, HostExeFileName);

    /// <summary>manifest 也放共用位置，兩邊寫的是同一個檔案（見類別開頭說明）。</summary>
    internal static string SharedManifestPath =>
        Path.Combine(SharedDirectory, $"{NativeHostRegistration.HostName}.json");

    /// <summary>擴充功能 ID 是 Chrome 指派給這個擴充功能的固定識別碼——開發階段用「載入未封裝
    /// 項目」測試時，這個 ID 由 Chrome 依載入路徑（或 manifest.json 的 "key" 欄位，如果固定了
    /// 公鑰）決定，第一次載入後要去 chrome://extensions 複製貼到部件資料夾裡的這個檔案。
    /// 找不到這個檔案就代表還沒準備好要接瀏覽器（例如密碼庫部件的舊版本沒帶這個檔案、或這個
    /// 功能還沒設定完），安靜略過、不註冊，不當成錯誤——密碼庫其餘功能完全不受影響。</summary>
    private const string ExtensionIdFileName = "extension-id.txt";

    /// <summary>先前 manifest 的存放位置。搬到共用位置之後，舊檔案沒有任何東西指向它，
    /// 但擴充功能的排查說明曾經要使用者去看這個路徑，留著一份內容過時的檔案會讓排查時
    /// 判斷錯誤，因此順手清掉（清不掉就算了，那只是一份沒人讀的殘留）。</summary>
    private static string LegacyManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileLocker", "NativeMessagingHost", $"{NativeHostRegistration.HostName}.json");

    /// <summary><paramref name="pluginDirectory"/> 是 plugins/PasswordLocker/ 這個資料夾——
    /// Native Host exe、extension-id.txt 都跟 PasswordVault.Core.dll 放在一起（同一個部件 zip
    /// 的內容）。</summary>
    public static void EnsureRegistered(string pluginDirectory)
    {
        var extensionIdPath = Path.Combine(pluginDirectory, ExtensionIdFileName);
        var hostExePath = Path.Combine(pluginDirectory, HostExeFileName);
        if (!File.Exists(extensionIdPath) || !File.Exists(hostExePath))
        {
            return;
        }

        string extensionId;
        try
        {
            extensionId = File.ReadAllText(extensionIdPath).Trim();
        }
        catch (IOException)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return;
        }

        EnsureSharedHostUpToDate(pluginDirectory);
        WriteManifestAndRegistry(extensionId);
    }

    /// <summary>寫入 manifest 與登錄機碼。兩邊的實作要算出相同的內容，因此這裡不加任何
    /// 帶自己品牌或安裝路徑的欄位（見 <see cref="NativeHostRegistration"/>）。</summary>
    private static void WriteManifestAndRegistry(string extensionId)
    {
        try
        {
            Directory.CreateDirectory(SharedDirectory);

            var existing = TryReadAllText(SharedManifestPath);
            var ids = NativeHostRegistration.MergeAllowedExtensionIds(existing, extensionId);
            var manifestJson = NativeHostRegistration.BuildManifest(SharedExePath, ids);

            // 內容沒變就不重寫，避免每次啟動都動到檔案時間戳記；路徑或擴充功能 ID 換過才
            // 真的更新。兩邊算出來的內容相同時，這個比對也順便讓後啟動的一方什麼都不做。
            if (existing != manifestJson)
            {
                File.WriteAllText(SharedManifestPath, manifestJson);
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (key.GetValue(null) as string != SharedManifestPath)
            {
                key.SetValue(null, SharedManifestPath);
            }

            TryDeleteLegacyManifest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void TryDeleteLegacyManifest()
    {
        try
        {
            if (File.Exists(LegacyManifestPath))
            {
                File.Delete(LegacyManifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
        }
    }

    /// <summary>共用位置沒有轉接程式、或那份比手上這份舊時，複製過去。
    ///
    /// 這裡原本的規則是「有就不動、不比對版本」，後果是共用位置永遠停在最早安裝的那個版本，
    /// 之後任何一邊修好了轉接程式都送不過去（判斷邏輯與理由見
    /// <see cref="NativeHostRegistration.ShouldReplaceSharedHost"/>）。
    ///
    /// 複製失敗安靜放棄——最常見的原因是 Chrome 正好把轉接程式叫起來、檔案被占用。轉接程式
    /// 是「用完就結束」的短命行程，下次啟動再試就會成功；這次失敗也不影響 Pipe Server 照常
    /// 監聽，共用位置留著的仍然是一份可用的舊版。</summary>
    private static void EnsureSharedHostUpToDate(string pluginDirectory)
    {
        var incoming = Path.Combine(pluginDirectory, HostExeFileName);
        var shouldReplace = NativeHostRegistration.ShouldReplaceSharedHost(
            File.Exists(SharedExePath),
            NativeHostRegistration.ReadFileVersion(SharedExePath),
            NativeHostRegistration.ReadFileVersion(incoming));

        if (!shouldReplace)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(SharedDirectory);
            foreach (var sourceFile in Directory.EnumerateFiles(pluginDirectory, HostFilePattern))
            {
                var destinationFile = Path.Combine(SharedDirectory, Path.GetFileName(sourceFile));
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
        }
    }
}
