using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace FileLocker.App;

/// <summary>
/// 密碼庫瀏覽器擴充功能的 Native Messaging Host 註冊（見 FileLocker_密碼庫_功能規劃.md 第 5 節）
/// ——比照 <see cref="ShellExtensionRegistrar"/> 的自我修復模式：每次啟動都比對目前狀態，跟現況
/// 不一致才重寫，不需要獨立的解除安裝步驟（指向的路徑本來就跟著部件走，部件被移除後這組登錄
/// 機碼變成空懸，風險等同 mswi 不管的 <c>%LocalAppData%</c> 軟殘留，見規劃文件第 9.2 節同一類
/// 判斷）。全程只動 <c>HKCU</c>，不需要系統管理員權限。
///
/// 這裡的 manifest「path」欄位刻意不指向 <c>plugins/PasswordLocker/</c> 這個部件自帶的轉接程式
/// 副本，改指向 <see cref="SharedExePath"/>（兩邊 App 共用的實體檔案，見
/// PasswordVault_獨立化_規劃.md 第 8.1 節）——根因是 <c>FileLocker.App</c> 跟 <c>PasswordVault.exe</c>
/// 過去各自帶一份自己的轉接程式副本、各自指向自己的路徑，登錄檔（每次啟動都自我修復覆寫，
/// 最後啟動的一方贏）跟 Named Pipe（先搶到的一方持有連線，最先啟動的一方贏）這兩套「贏家」判斷
/// 邏輯不一致時，Chrome 被登錄檔指向 A 的轉接程式，但 Pipe 被 B 持有，B 的
/// <c>VerifyClientIsExpectedHost</c> 拿自己認得的路徑一比對，發現對不上就直接切斷連線
/// （「Pipe is broken」）。改成兩邊都認同一個實體檔案、同一個路徑之後，不管誰贏得 Pipe、誰贏得
/// 登錄檔，雙方講的都是同一個地址，不會再對不上。
/// </summary>
public static class PasswordLockerNativeHostRegistrar
{
    private const string HostName = "com.filelocker.passwordlocker";
    private const string RegistryKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;
    // PasswordVault.NativeHost.exe（部件遷出獨立 repo 後的檔名，見
    // PasswordVault_獨立化_規劃.md 第 17 節第 3 點）。
    private const string HostExeFileName = "PasswordVault.NativeHost.exe";

    /// <summary>兩邊 App 共用的轉接程式存放位置——刻意選在不屬於任一邊安裝資料夾的
    /// <c>%LocalAppData%\PasswordVault\NativeHost\</c>，跟第 7 節「密碼庫資料改指向共用路徑」
    /// 同一個理由：兩邊安裝、解除安裝、版本升級都不會動到它。</summary>
    internal static string SharedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PasswordVault", "NativeHost");

    /// <summary>兩邊 Pipe Server 建構時的 expectedClientExePath、這裡 manifest 的 path 欄位，
    /// 都指向同一個值——即使部件還沒安裝、共用位置還沒有實體檔案，這裡也回傳固定路徑字串，
    /// 不做檔案是否存在的檢查（比對用的是路徑字串本身，不要求檔案當下存在，跟原本直接指向
    /// plugins 資料夾裡那份的行為一致）。</summary>
    public static string SharedExePath => Path.Combine(SharedDirectory, HostExeFileName);

    /// <summary>擴充功能 ID 是 Chrome 指派給這個擴充功能的固定識別碼——開發階段用「載入未封裝
    /// 項目」測試時，這個 ID 由 Chrome 依載入路徑（或 manifest.json 的 "key" 欄位，如果固定了
    /// 公鑰）決定，第一次載入後要去 chrome://extensions 複製貼到部件資料夾裡的這個檔案。
    /// 找不到這個檔案就代表還沒準備好要接瀏覽器（例如密碼庫部件的舊版本沒帶這個檔案、或這個
    /// 功能還沒設定完），安靜略過、不註冊，不當成錯誤——密碼庫其餘功能完全不受影響。</summary>
    private const string ExtensionIdFileName = "extension-id.txt";

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

        EnsureSharedExeCopied(pluginDirectory);

        try
        {
            var manifestDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileLocker", "NativeMessagingHost");
            Directory.CreateDirectory(manifestDir);
            var manifestPath = Path.Combine(manifestDir, $"{HostName}.json");

            var manifest = new
            {
                name = HostName,
                description = "FileLocker 密碼庫瀏覽器整合（Native Messaging Host）",
                path = SharedExePath,
                type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{extensionId}/" }
            };
            var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

            // 內容沒變就不重寫，避免每次啟動都動到檔案時間戳記；路徑或擴充功能 ID 換過
            // （部件重新安裝位置變了、擴充功能重新申請 ID）才真的更新。
            if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != manifestJson)
            {
                File.WriteAllText(manifestPath, manifestJson);
            }

            using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
            if (key.GetValue(null) as string != manifestPath)
            {
                key.SetValue(null, manifestPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>誰先啟動就負責複製，共用位置已經有轉接程式的話什麼都不做——不比對版本新舊，
    /// 用最簡單的「有就不動」規則（規劃文件第 8.1 節定案的方向：「誰先啟動，就負責把轉接程式
    /// 複製到這個共用位置（如果還沒有的話）」，沒有要求比對版本）。複製失敗（檔案被占用、權限
    /// 問題等）安靜放棄——共用位置多半已經有另一邊複製好的版本，這裡失敗不影響 Pipe Server
    /// 照常啟動監聽，只是這次連線驗證會因為共用位置真的沒有檔案而全部失敗，等下次啟動再重試。</summary>
    private static void EnsureSharedExeCopied(string pluginDirectory)
    {
        if (File.Exists(SharedExePath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(SharedDirectory);
            // 比對前綴而不是只複製 HostExeFileName 本身——Native Host 是完整的 .NET 執行檔，
            // 還帶著自己的 .dll／.deps.json／.runtimeconfig.json（見 CLAUDE.md「已知的坑」
            // 關於複製部件時容易漏掉這幾個檔案的說明），少複製任何一個到共用位置，執行檔就會
            // 啟動失敗。
            foreach (var sourceFile in Directory.EnumerateFiles(pluginDirectory, "PasswordVault.NativeHost.*"))
            {
                var destinationFile = Path.Combine(SharedDirectory, Path.GetFileName(sourceFile));
                File.Copy(sourceFile, destinationFile, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
