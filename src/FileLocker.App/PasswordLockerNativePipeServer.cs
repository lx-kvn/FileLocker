using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using FileLocker.PluginContracts;
using Microsoft.Win32.SafeHandles;

namespace FileLocker.App;

/// <summary>
/// 密碼庫瀏覽器擴充功能的本機端點（見 FileLocker_密碼庫_功能規劃.md 第 5 節）：Native Messaging
/// Host（獨立、短命的 <c>FileLocker.PasswordLockerNativeHost.exe</c>，由 Chrome 每次連線時啟動）
/// 透過這條 Named Pipe 把訊息轉進來，直接重用跟 WebView2 完全一樣的 <see cref="IPasswordLockerPlugin"/>
/// 實例——這條管線本身不含任何密碼庫業務邏輯，只負責「收 JSON → 呼叫 plugin → 回傳 JSON」，
/// 跟 <c>MainWindow.HandlePasswordLockerModuleRequestAsync</c> 是同一個角色的另一份實作，只是
/// 對外的通道不同（Named Pipe 而不是 WebView2 的 postMessage）。
///
/// Framing 刻意跟 Chrome Native Messaging 標準格式一致（4-byte little-endian 長度前綴 + UTF-8
/// JSON）——這樣 Native Host 那一端幾乎是原封不動轉發位元組，不需要另外設計、另外測試一套
/// 轉譯邏輯，降低那個「純轉接層」進程本身出錯的機會。
///
/// 這條管線本身沒有前端 WebView2 那種「只有自己的行程才能 postMessage」的天然邊界——任何
/// 以同一個 Windows 使用者身分執行的本機程式，只要知道管線名稱就能連上來。2026-08-09 這輪
/// 安全稽核發現原本的實作完全信任連線端、也完全信任訊息內容，等同把整個密碼庫（包含
/// exportPasswordLockerCsv 這種一次吐出全部明文的訊息）暴露成一個無認證的本機 IPC 端點。
/// 這裡加上三層防線，缺一不可：
/// 1. <see cref="BuildPipeSecurity"/>：DACL 只允許目前使用者連線，Everyone／匿名登入被排除。
/// 2. <see cref="VerifyClientIsExpectedHost"/>：連線建立後用 GetNamedPipeClientProcessId 反查
///    對方的行程路徑，必須是我們自己的 Native Host exe——擋掉「同一個使用者身分下、其他也在跑
///    的本機程式」湊巧或刻意連上這條管線的情況（DACL 只能限制「誰」，擋不了「同一個誰底下的
///    哪一支程式」）。
/// 3. <see cref="AllowedMessageTypes"/>：即使前兩層都通過，也只放行擴充功能真正需要的訊息
///    類型——密碼庫部件本身認得的訊息遠多於瀏覽器情境需要的（見 PasswordLockerPlugin 的完整
///    switch），像 exportPasswordLockerCsv／deletePasswordLockerCredentials／
///    changePasswordLockerPassword 這些不該從這條管線觸發。
/// </summary>
public sealed class PasswordLockerNativePipeServer
{
    public const string PipeName = "FileLocker-PasswordLocker-Pipe";

    // 這條管線的實際名稱——正式執行永遠用上面那個固定常數，測試（見 FileLocker.App.Tests）
    // 才會傳一個各自獨立、帶隨機後綴的名稱進來，避免跟同一台機器上真的在跑的 FileLocker.exe
    // 搶同一個具名管道、或多個測試案例互相干擾。
    private readonly string _pipeName;

    private const int MaxMessageBytes = 10 * 1024 * 1024;

    /// <summary>連線被拒絕、或訊息格式壞掉時的重試延遲——避免任何形式的緊迫重試迴圈燒 CPU
    /// （見 AcceptLoopAsync 的說明：這裡曾經因為 maxNumberOfServerInstances 設成 1、每次
    /// 都建立一個新的 NamedPipeServerStream 立刻撞見「所有管道例項都在使用中」，被吞掉的
    /// IOException 導致迴圈在整段連線期間零延遲空轉，實測會佔滿一顆 CPU 核心）。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>擴充功能實際會透過這條管線送出的訊息類型——比對 content-script.js／
    /// popup.js／background.js 目前所有會轉發給 Native Host 的 chrome.runtime.sendMessage
    /// 呼叫整理出來（stashPending*／takePending* 這幾個由 background.js 自己攔截處理，
    /// 從不會真的送到這裡，不用列入）。密碼庫部件本身認得的訊息類型遠多於這份清單（見
    /// PasswordLockerPlugin.HandleRequestAsync 的完整 switch），像匯出 CSV、刪除、改密碼
    /// 這種操作只應該經由 App 本體的 WebView2 postMessage 通道觸發，不該讓瀏覽器擴充功能
    /// （進而任何連得上這條本機管線的程式）也能觸發。openPasswordLockerApp 在
    /// HandleMessageAsync 裡更早就被攔截，不會走到這份白名單檢查，但一起列在這裡方便對照。</summary>
    private static readonly HashSet<string> AllowedMessageTypes = new(StringComparer.Ordinal)
    {
        "openPasswordLockerApp",
        "listPasswordLocker",
        "findPasswordLockerCredentialsForDomain",
        "revealPasswordLockerCredentialForSite",
        "addOrUpdatePasswordLockerCredential",
        "generatePasswordLockerPassword"
    };

    /// <summary>「缺驗證就自動跳視窗重試」這個機制只對這兩種訊息開放——見 HandleMessageAsync
    /// 內的說明：判斷條件如果只看「請求裡有沒有 domain 欄位」，任何白名單內的訊息只要順手帶一個
    /// domain 欄位就能觸發驗證視窗、通過後重打一次自己，等於拿使用者的驗證動作幫任何訊息開後門
    /// （2026-08-09 稽核發現：例如 listPasswordLocker 本身不需要驗證，但只要帶 domain 就能藉由
    /// 這個機制間接繞過去）。這兩個是唯一真的需要「先驗證、通過再重試」流程的訊息。</summary>
    private static readonly HashSet<string> RetryAfterVerificationMessageTypes = new(StringComparer.Ordinal)
    {
        "revealPasswordLockerCredentialForSite",
        "addOrUpdatePasswordLockerCredential"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Func<IPasswordLockerPlugin?> _getPlugin;
    private readonly Func<string, string?, Task<bool>> _requestBrowserVerification;
    private readonly Func<Task> _openPasswordLockerApp;
    private readonly string _expectedClientExePath;
    private CancellationTokenSource? _cts;

    public PasswordLockerNativePipeServer(
        Func<IPasswordLockerPlugin?> getPlugin, Func<string, string?, Task<bool>> requestBrowserVerification,
        Func<Task> openPasswordLockerApp, string expectedClientExePath, string? pipeName = null)
    {
        _getPlugin = getPlugin;
        _requestBrowserVerification = requestBrowserVerification;
        _openPasswordLockerApp = openPasswordLockerApp;
        _expectedClientExePath = expectedClientExePath;
        _pipeName = pipeName ?? PipeName;
    }

    /// <summary>從 App.xaml.cs 的 OnStartup（UI 執行緒）同步呼叫，故意用 Task.Run 把整個接受迴圈
    /// 丟到執行緒集區——AcceptLoopAsync 內部的 await 沒有加 ConfigureAwait(false)，如果直接在
    /// UI 執行緒呼叫（不經過 Task.Run），第一次 await 之後的所有延續（包含下一輪迴圈重新建立
    /// NamedPipeServerStream）都會透過 WPF 的 DispatcherSynchronizationContext 排回 UI 執行緒
    /// 處理——這在實測中真的把整個視窗訊息迴圈卡死在 CreateNamedPipe 裡動彈不得（App 整個沒回應，
    /// 不是單純變慢）。Task.Run 確保這條迴圈從一開始就活在沒有 SynchronizationContext 的執行緒
    /// 集區執行緒上，所有延續自然留在執行緒集區，不會意外跑回 UI 執行緒。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>只允許目前這個 Windows 使用者連線——Everyone／ANONYMOUS LOGON 預設對具名管道
    /// 有讀取權（見類別開頭稽核說明的實測結果），雖然讀取權本身送不出請求，但密碼管理器的
    /// 存在意義就是「就算是以使用者身分執行的其他程式，沒有主密碼也拿不到明文」，這條管線
    /// 不該是這道邊界唯一的破口。DACL 只給目前使用者 ReadWrite＋CreateNewInstance（後者是
    /// 建立「不是第一個」的管道例項所必須的權限，AcceptLoopAsync 迴圈裡每接完一個連線就會
    /// 建立下一個）。</summary>
    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("無法取得目前使用者的 SID");
        security.AddAccessRule(new PipeAccessRule(
            currentUser, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return security;
    }

    /// <summary>每個 Native Host 進程的存活期間只會建立一次連線（Chrome 幫它管生命週期，進程
    /// 結束前這條連線都開著），但這裡還是用迴圈接受一個又一個連線——不假設同時間只會有一個
    /// 瀏覽器分頁在用，也不假設某次連線一定會正常關閉。單一連線內部再用另一層迴圈讀多個請求
    /// （見 HandleConnectionAsync），呼應同一個 Native Host 進程存活期間可能問好幾次的情境。
    ///
    /// maxNumberOfServerInstances 改成 NamedPipeServerStream.MaxAllowedServerInstances（原本
    /// 寫死 1）：寫死 1 時，只要有一條連線還開著，這裡建立下一個 NamedPipeServerStream 準備
    /// 接受「下一個」連線就會立刻撞見 IOException（所有管道例項都在使用中），被下面的空 catch
    /// 吞掉、迴圈立刻重來——整段連線期間（例如驗證視窗開著等使用者輸入密碼的那幾十秒）零延遲
    /// 空轉，2026-08-09 這輪稽核用探針程式實測證實會佔滿一顆 CPU 核心。改成不限制數量之後這個
    /// IOException 不會再發生，但 catch 裡還是留一段短延遲當保險，避免任何其他原因造成的重試
    /// 迴圈失控。</summary>
    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var pipeSecurity = BuildPipeSecurity();

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, pipeSecurity);
                await pipe.WaitForConnectionAsync(token);

                if (!VerifyClientIsExpectedHost(pipe))
                {
                    pipe.Dispose();
                    continue;
                }

                _ = HandleConnectionAsync(pipe, token);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
            }
            catch (IOException)
            {
                pipe?.Dispose();
                try
                {
                    await Task.Delay(RetryDelay, token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>DACL 限制的是「哪個 Windows 使用者」，擋不住「同一個使用者身分下，剛好也在跑
    /// 的其他本機程式」——這裡額外反查連線端的行程路徑，必須跟我們自己安裝的 Native Host exe
    /// 完全一致（大小寫不拘、走 Path.GetFullPath 正規化）。任何一步查不到（行程剛好結束、
    /// 沒有權限讀取模組路徑等）一律視為不通過、拒絕連線——這是本機 IPC 的信任邊界，查不清楚
    /// 就不該假設對方是自己人。</summary>
    private bool VerifyClientIsExpectedHost(NamedPipeServerStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
            {
                return false;
            }

            using var process = Process.GetProcessById((int)clientProcessId);
            var clientPath = process.MainModule?.FileName;
            if (clientPath is null)
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(clientPath), Path.GetFullPath(_expectedClientExePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // ArgumentException：行程 id 已經不存在（Native Host 是短命進程，查詢當下剛好結束）。
            // InvalidOperationException／Win32Exception：沒有權限讀取對方的模組資訊。
            // 任何一種都當作驗證失敗，不放行。
            return false;
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            while (pipe.IsConnected && !token.IsCancellationRequested)
            {
                var request = await ReadMessageAsync(pipe, token);
                if (request is null)
                {
                    break;
                }

                var response = await HandleMessageAsync(request.Value);
                await WriteMessageAsync(pipe, response, token);
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            pipe.Dispose();
        }
    }

    /// <summary>轉發給 plugin，收到「尚未驗證」的錯誤時（且請求裡帶了 domain，代表這是瀏覽器
    /// 情境的請求）先跑一次驗證流程再重打一次原本的請求——這個「先驗證、通過再重試」的組合
    /// 動作留在這裡而不是塞進 PasswordLockerPlugin，因為部件本身完全不知道「跳出 FileLocker
    /// 視窗、等使用者在畫面上完成驗證」這種 WPF 層級的事，只有 App 這一側辦得到。
    ///
    /// 白名單檢查（AllowedMessageTypes）跟重試資格檢查（RetryAfterVerificationMessageTypes）
    /// 是兩層獨立的防線，見兩個常數宣告處的說明——白名單管「這條管線准不准轉發這則訊息」，
    /// 重試資格管「就算准轉發，通不通過身份驗證後可以自動重試」，混在一起判斷會讓任何白名單
    /// 內的訊息只要順手帶個 domain 欄位就能借用使用者的驗證動作。</summary>
    private async Task<object> HandleMessageAsync(JsonElement request)
    {
        var type = request.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
        if (type is null)
        {
            return new { type = "error", message = "缺少 type 欄位" };
        }

        if (!AllowedMessageTypes.Contains(type))
        {
            return new { type = "error", message = "這條管線不接受這個訊息類型" };
        }

        // 「管理密碼」（擴充功能 popup 最下面的按鈕）：使用者這次是明確要求叫出 FileLocker
        // 主視窗、不是背景自動填入流程，跟 RequestBrowserVerificationAsync 刻意不叫視窗的
        // 設計完全相反，不走 plugin，直接請 App.xaml.cs 開窗、切分頁。
        if (type == "openPasswordLockerApp")
        {
            await _openPasswordLockerApp();
            return new { type = "openPasswordLockerAppResult", success = true };
        }

        var plugin = _getPlugin();
        if (plugin is null)
        {
            return new { type = "error", message = "密碼庫部件未安裝或載入失敗" };
        }

        object? response;
        try
        {
            response = await plugin.HandleRequestAsync(type, request, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or JsonException)
        {
            // 請求格式不符部件預期（缺欄位、型別不對、Enum.Parse 失敗等）——不能讓例外往上炸穿
            // HandleConnectionAsync 唯一有接的 IOException/OperationCanceledException，那樣會
            // 變成沒人觀察到的 Task 例外，直接把這條連線整個切斷（使用者只看到「FileLocker 沒有
            // 回應」，比明確的錯誤訊息更難排查）。轉成一般錯誤回應，讓連線可以繼續處理下一則請求。
            return new { type = "error", message = "請求格式不正確" };
        }

        if (response is not null
            && TryGetStringProperty(response, "errorCode") == "PASSWORD_LOCKER_NOT_VERIFIED"
            && RetryAfterVerificationMessageTypes.Contains(type)
            && request.TryGetProperty("domain", out var domainProp))
        {
            var domain = domainProp.GetString() ?? "";
            // targetDomain：只用來讓驗證視窗如實告知使用者「這組密碼實際上會被用在哪個網站」，
            // 跟 domain（這筆密碼歸屬、拿來驗證用的網域）是兩回事——見
            // PasswordLockerBrowserVerifyWindow 開頭關於雙網域顯示的說明。沒有這個欄位（domain
            // 就是目的地本身）或跟 domain 相同時視窗不需要多顯示一行。
            var targetDomain = request.TryGetProperty("targetDomain", out var targetDomainProp)
                ? targetDomainProp.GetString()
                : null;
            var verified = await _requestBrowserVerification(domain, targetDomain);
            if (verified)
            {
                response = await plugin.HandleRequestAsync(type, request, IntPtr.Zero);
            }
        }

        return response ?? new { type = $"{type}Result", success = false, errorMessage = "密碼庫部件不認得這個請求" };
    }

    private static string? TryGetStringProperty(object response, string propertyName)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions));
        return doc.RootElement.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static async Task<JsonElement?> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        var lengthBuffer = new byte[4];
        if (!await ReadExactAsync(stream, lengthBuffer, token))
        {
            return null;
        }

        var length = BitConverter.ToInt32(lengthBuffer, 0);
        if (length <= 0 || length > MaxMessageBytes)
        {
            return null;
        }

        var buffer = new byte[length];
        if (!await ReadExactAsync(stream, buffer, token))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(buffer);
        return doc.RootElement.Clone();
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token);
            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static async Task WriteMessageAsync(Stream stream, object message, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await stream.WriteAsync(BitConverter.GetBytes(json.Length), token);
        await stream.WriteAsync(json, token);
        await stream.FlushAsync(token);
    }
}
