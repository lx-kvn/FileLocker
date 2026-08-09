using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// Chrome Native Messaging Host（見 FileLocker_密碼庫_功能規劃.md 第 5 節）：純轉接層，不含任何
// 密碼庫業務邏輯。Chrome 每次 chrome.runtime.connectNative() 就會啟動一個這支程式的新進程，
// 透過 stdin/stdout 溝通（4-byte little-endian 長度前綴 + UTF-8 JSON，Chrome 官方標準格式）；
// 這裡原封不動把訊息轉發到 FileLocker.App 已經在監聽的 Named Pipe（framing 刻意用同一套格式，
// 見 PasswordLockerNativePipeServer 的說明），回應也原封不動轉發回 stdout。真正的驗證/加解密/
// UI 全部留在 FileLocker.App 那一側，這支程式沒有、也不該有任何那類邏輯。

const string PipeName = "FileLocker-PasswordLocker-Pipe";
const int MaxMessageBytes = 10 * 1024 * 1024;

using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();

while (true)
{
    var message = await ReadFramedAsync(stdin);
    if (message is null)
    {
        // Chrome 關閉了這條連線（分頁關掉、擴充功能重載、瀏覽器結束等）——stdin 讀到 EOF，
        // 這支進程的任務就結束了，直接退出，不用自己做任何清理，生命週期完全交給 Chrome。
        return;
    }

    byte[] response;
    try
    {
        response = await ForwardToAppAsync(message);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
    {
        response = ErrorPayload(ex.Message);
    }

    await WriteFramedAsync(stdout, response);
}

static async Task<byte[]?> ReadFramedAsync(Stream stream)
{
    var lengthBuffer = new byte[4];
    if (!await ReadExactAsync(stream, lengthBuffer))
    {
        return null;
    }

    var length = BitConverter.ToInt32(lengthBuffer, 0);
    if (length <= 0 || length > MaxMessageBytes)
    {
        return null;
    }

    var buffer = new byte[length];
    return await ReadExactAsync(stream, buffer) ? buffer : null;
}

static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset));
        if (read == 0)
        {
            return false;
        }
        offset += read;
    }
    return true;
}

static async Task WriteFramedAsync(Stream stream, byte[] payload)
{
    await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
    await stream.WriteAsync(payload);
    await stream.FlushAsync();
}

// 原本用字串插值手刻 JSON，只把雙引號換成單引號，沒有跳脫反斜線／控制字元——這裡最常見的
// 錯誤來源正是管道例外，訊息裡固定含有 "\\.\pipe\FileLocker-PasswordLocker-Pipe" 這種路徑，
// 反斜線沒跳脫會產生格式不正確的 JSON，Chrome 收到後直接 parse error，真正的錯誤原因反而
// 被蓋掉（2026-08-09 這輪稽核發現）。改用 JsonSerializer 保證輸出永遠是合法 JSON。
static byte[] ErrorPayload(string message)
    => JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message });

/// <summary>連不上 Named Pipe 就代表 FileLocker.App 沒在跑——自動在背景安靜啟動它（沿用既有的
/// --startup 旗標，不開視窗、只留系統匣圖示，這是規劃階段明確定案的行為：使用者不需要自己記得
/// 先開 FileLocker 才能用瀏覽器自動填入），重試幾次等它把 WebView2／Named Pipe 初始化完成
/// （實測開機到能接受連線通常在幾秒內，20 次 × 500ms 的重試窗留了充裕餘裕）。</summary>
static async Task<byte[]> ForwardToAppAsync(byte[] message)
{
    using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    var connected = await TryConnectAsync(pipe, TimeSpan.FromMilliseconds(500));
    if (!connected)
    {
        TryLaunchFileLockerInBackground();

        for (var attempt = 0; attempt < 20 && !connected; attempt++)
        {
            await Task.Delay(500);
            connected = await TryConnectAsync(pipe, TimeSpan.FromMilliseconds(500));
        }
    }

    if (!connected)
    {
        return ErrorPayload("無法連線到 FileLocker，請確認已安裝並可以正常啟動");
    }

    await WriteFramedAsync(pipe, message);

    var response = await ReadFramedAsync(pipe);
    return response ?? ErrorPayload("FileLocker 沒有回應");
}

static async Task<bool> TryConnectAsync(NamedPipeClientStream pipe, TimeSpan timeout)
{
    try
    {
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds);
        return true;
    }
    catch (Exception ex) when (ex is TimeoutException or IOException)
    {
        return false;
    }
}

/// <summary>這支程式跟 FileLocker.exe 一起解壓到 plugins/PasswordLocker/（見規劃文件第 2.2 節，
/// 部件的 zip 內容），FileLocker.exe 在安裝目錄根目錄，往上兩層就是。</summary>
static void TryLaunchFileLockerInBackground()
{
    try
    {
        var exePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "FileLocker.exe"));
        if (!File.Exists(exePath))
        {
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = exePath, Arguments = "--startup", UseShellExecute = true });
    }
    catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
    {
    }
}
