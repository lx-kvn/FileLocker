using System.Text.Json;

namespace FileLocker.PluginContracts;

/// <summary>
/// FileLocker.App 用來呼叫「密碼庫」可選配部件的介面契約，見
/// FileLocker_密碼庫_功能規劃.md 第 2.1 節。刻意採通用轉發式，不是把部件內部十幾個 IPC
/// 方法逐一定義成介面方法——部件版本跟 FileLocker 主體版本脫鉤（見規劃文件第 8 節），
/// 如果介面跟內部 IPC 方法一樣細，部件每次新增/修改內部訊息都得同步改這個契約專案跟主體，
/// 違背「脫鉤」的初衷。這個介面往後不應該因為部件內部邏輯變動而需要修改。
/// </summary>
public interface IPasswordLockerPlugin
{
    /// <summary>執行期呼叫一次，在第一次 <see cref="HandleRequestAsync"/> 之前完成。</summary>
    void Initialize(PasswordLockerPluginContext context);

    /// <summary>
    /// 把一個 WebView2 IPC 訊息（<paramref name="messageType"/> 對應訊息的 "type" 欄位，
    /// <paramref name="requestBody"/> 是整包訊息內容）原樣轉發進部件，部件自己決定怎麼解析、
    /// 呼叫內部邏輯、組裝回應。回傳值會直接交給 <c>SendToFrontend</c> 序列化送回前端，
    /// 回傳物件本身必須已經包含前端期待的 "type" 欄位（部件負責，主體不會另外加工）。
    /// <paramref name="ownerWindowHandle"/> 是目前主視窗的 Win32 控制代碼，Passkey／Windows Hello
    /// 對話框需要一個擁有者視窗——這是唯一一個平台相關到需要主體每次呼叫都重新提供的資訊
    /// （視窗可能被關掉重開，控制代碼會變），不適合放進 <see cref="Initialize"/> 一次性帶入的
    /// <see cref="PasswordLockerPluginContext"/> 裡。</summary>
    Task<object?> HandleRequestAsync(string messageType, JsonElement requestBody, IntPtr ownerWindowHandle);
}

/// <summary>
/// 部件初始化需要的、只有主體才知道的資訊——資料存放路徑（放在 FileLocker 現有的
/// %LocalAppData%\FileLocker\PasswordLocker 底下，部件自己不決定路徑）、查詢某個 Vault
/// 項目是否還存在的委派（「已加密檔案」分類的憑證要用，比照 PasswordLockerProtocolHandlers
/// 現有建構子已經在用的委派模式，部件不直接依賴 Vault 相關型別）。
/// </summary>
public sealed class PasswordLockerPluginContext(string dataDirectory, Func<string, bool> vaultItemExists)
{
    public string DataDirectory { get; } = dataDirectory;
    public Func<string, bool> VaultItemExists { get; } = vaultItemExists;
}
