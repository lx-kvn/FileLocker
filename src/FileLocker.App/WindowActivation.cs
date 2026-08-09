using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FileLocker.App;

/// <summary>
/// 單純呼叫 Activate()（本質上是 SetForegroundWindow）在「雙擊被 Mutex 擋下來的行程，
/// 透過 Named Pipe 轉送參數給已經在跑的實體」這條路徑上不可靠——轉送行程呼叫
/// AllowSetForegroundWindow(ASFW_ANY) 給的搶焦權限只在很短時間內有效（下一次使用者輸入事件、
/// 或系統認定經過太久就會失效），但從 Pipe 收到轉送參數到真正呼叫 Activate() 之間，往往還要
/// 先建構一個全新的 MainWindow（含 WebView2 初始化），這段時間常常已經足夠讓權限失效——視窗
/// 其實有被建立/顯示出來，只是沒有真的被搶到最前面，使用者會覺得「已經在系統匣裡的
/// FileLocker，再雙擊 exe 完全沒反應」。瀏覽器擴充功能透過 Native Messaging Host 轉來的
/// 「管理密碼」請求是同一類情境（觸發來源是背景的 Native Host 進程，不是這個行程剛收到的
/// 使用者輸入），實測發現連 Topmost 切換這一層都不夠，視窗開是開了，卻被吞到其他視窗後面。
/// </summary>
internal static class WindowActivation
{
    /// <summary>
    /// Topmost 切換一次是視窗管理員層級的 z-order 操作，不依賴 AllowSetForegroundWindow
    /// 給的、會過期的搶焦權限，兩個行程之間隔了多久都不受影響，能確保視窗真的被拉到最上層。
    /// 但這一步只解決「疊在最上層」，不保證真的拿到鍵盤輸入焦點（WPF 的 Activate() 內部
    /// 呼叫 SetForegroundWindow，一樣受 Windows 的防搶焦點限制，觸發來源不是這個行程剛收到
    /// 的使用者輸入時常常悄悄失敗）——所以額外用 AttachThreadInput 技巧繞過這個限制：暫時把
    /// 呼叫端執行緒的輸入佇列跟目前前景視窗的執行緒接在一起，系統就會允許這裡呼叫
    /// SetForegroundWindow 生效。跟 FileLocker.Core.Crypto.WindowFocusHelper 是同一套技巧
    /// （那邊是給 Passkey 驗證視窗用的），兩邊分屬不同專案、WindowFocusHelper 又是 internal，
    /// 這裡選擇各自獨立實作一份，不特地開 InternalsVisibleTo 換取共用幾行 P/Invoke。
    /// </summary>
    public static void ForceToForeground(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Topmost = true;
        window.Topmost = false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ForceSetForegroundWindow(hwnd);
        }

        window.Activate();
    }

    private static bool ForceSetForegroundWindow(IntPtr hWnd)
    {
        var foregroundWindow = GetForegroundWindow();
        var currentThreadId = GetCurrentThreadId();

        if (foregroundWindow == IntPtr.Zero || foregroundWindow == hWnd)
        {
            return SetForegroundWindow(hWnd);
        }

        var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        if (foregroundThreadId == currentThreadId)
        {
            return SetForegroundWindow(hWnd);
        }

        var attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
        try
        {
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);
}
