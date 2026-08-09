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
/// </summary>
public static class PasswordLockerNativeHostRegistrar
{
    private const string HostName = "com.filelocker.passwordlocker";
    private const string RegistryKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\" + HostName;
    private const string HostExeFileName = "FileLocker.PasswordLockerNativeHost.exe";

    /// <summary>擴充功能 ID 是 Chrome 指派給這個擴充功能的固定識別碼——開發階段用「載入未封裝
    /// 項目」測試時，這個 ID 由 Chrome 依載入路徑（或 manifest.json 的 "key" 欄位，如果固定了
    /// 公鑰）決定，第一次載入後要去 chrome://extensions 複製貼到部件資料夾裡的這個檔案。
    /// 找不到這個檔案就代表還沒準備好要接瀏覽器（例如密碼庫部件的舊版本沒帶這個檔案、或這個
    /// 功能還沒設定完），安靜略過、不註冊，不當成錯誤——密碼庫其餘功能完全不受影響。</summary>
    private const string ExtensionIdFileName = "extension-id.txt";

    /// <summary><paramref name="pluginDirectory"/> 是 plugins/PasswordLocker/ 這個資料夾——
    /// Native Host exe、extension-id.txt 都跟 FileLocker.PasswordLocker.dll 放在一起（同一個
    /// 部件 zip 的內容，見規劃文件第 2.2 節）。</summary>
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
                path = hostExePath,
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
}
