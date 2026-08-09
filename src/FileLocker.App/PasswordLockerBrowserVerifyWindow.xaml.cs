using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FileLocker.Core.Crypto;

namespace FileLocker.App;

/// <summary>
/// 瀏覽器擴充功能觸發密碼庫驗證時跳出的獨立小視窗——一開啟就顯示（不是先靜默試一次
/// Passkey、失敗才叫出視窗），技術結構跟視覺樣式比照 PasswordPromptWindow，見 XAML
/// 開頭說明。有設定 Passkey 就在 Loaded 時自動觸發一次驗證，期間密碼欄位鎖住
/// （SetBusyState），沒完成才退回密碼輸入、保留「重試 Passkey」按鈕——這個視窗本身就是
/// Windows Hello 前景固定手法要用的 ownerWindowHandle，不需要另外準備一個隱形的
/// owner 視窗（那個視窗生命週期跟這個視窗脫節，驗證結束後還會賴著不消失，見規劃文件）。
///
/// 用 Show()（見 App.xaml.cs 呼叫端）而不是 ShowDialog()：這個視窗曾經被
/// WindowActivation.ForceToForeground 用 Show() 顯示一次、緊接著又被呼叫端呼叫
/// ShowDialog()，WPF 對「已經 Show() 過的視窗再呼叫 ShowDialog()」會直接丟
/// InvalidOperationException——這個例外沒被任何地方接住，一路往上炸穿整條
/// RequestBrowserVerificationAsync／PasswordLockerNativePipeServer 的呼叫鏈，變成一個
/// 沒人觀察到的 Task 例外，使用者只會看到「按了確定/取消，卡住幾秒才自己關掉，好像當機」
/// ——因為視窗本身雖然關了，但呼叫端那個等著要接結果的 await 從來沒有真的完成過，一路等到
/// 上層某個逾時或收尾機制介入才收場。改成非模態的 Show()＋這裡自己用 TaskCompletionSource
/// 通知結果，PasswordPromptWindow（雙擊 .locked 檔案跳出的那個）本來就是這樣做的，不是
/// 這個視窗特有的新設計。
/// </summary>
public partial class PasswordLockerBrowserVerifyWindow : Window
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // password 為 null 代表「試 Passkey」；非 null 代表「用這組密碼驗證」——最後一個參數是
    // 這個視窗自己的 HWND，當 Windows Hello 前景固定手法（WindowFocusHelper）的
    // ownerWindowHandle 用；跟 App.xaml.cs 裡 verifyPasswordLocker 訊息的 tryPasskeyFirst
    // 語意對齊（見該方法呼叫端 TryVerifyPasswordLockerAsync）。
    private readonly Func<string?, bool, IntPtr, Task<(bool Success, string? ErrorMessage, string? ErrorCode)>> _verify;
    private readonly bool _passkeyEnabled;
    private readonly TaskCompletionSource<bool> _resultTcs = new();
    private bool _isBusy;

    private const string SingleDomainHint = "這個網站想使用密碼庫裡的帳號密碼，請先驗證身份。";

    /// <summary>domain（密碼歸屬、拿來驗證的網域）跟 targetDomain（密碼實際會被填入的網域）
    /// 不同時，把兩者都講清楚——不然使用者只看得到 domain（例如「選擇密碼」情境下顯示的是
    /// 那筆密碼原本歸屬的網站），完全看不出密碼即將被用在當下所在的另一個網站，見
    /// App.xaml.cs.RequestBrowserVerificationAsync 上的稽核說明。</summary>
    private static string BuildHintText(string domain, string? targetDomain)
        => string.IsNullOrEmpty(targetDomain) || string.Equals(targetDomain, domain, StringComparison.OrdinalIgnoreCase)
            ? SingleDomainHint
            : $"要把「{domain}」的密碼庫帳密用在「{targetDomain}」嗎？請先驗證身份。";

    // Windows Hello 的驗證 UI 是系統層級元件，我們對它的置頂控制受作業系統的權限邊界限制
    // （見 WindowFocusHelper 上的說明），但這個視窗本身完全是我們自己的、沒有這層限制——
    // 使用者切去操作其他應用程式時，這個視窗會被正常的 z-order 規則往後推，不是 Passkey
    // 對話框造成的。定期（不是永久置頂，避免反過來擋住之後才彈出的 Windows Hello 對話框）
    // 把它拉回前景，只在「不是正忙著等 Passkey 結果」的時候做——Passkey 進行中搶回前景反而
    // 會蓋掉 Windows Hello 對話框，見 Loaded 事件處理常式裡對這個時序問題的說明。
    // 0.5 秒的檢查間隔＋輸入防抖：使用者正在打密碼的當下（最近一小段時間內有按鍵）不搶前景，
    // 不然每次拉回前景都可能打斷輸入法/游標狀態；沒有輸入的空檔（沒在打字，例如剛顯示、
    // 或不小心點到別的視窗那段時間）才積極搶回來，讓使用者不用自己手動點回去。
    private readonly DispatcherTimer _keepInFrontTimer;
    private static readonly TimeSpan KeepInFrontInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TypingDebounce = TimeSpan.FromMilliseconds(300);
    private DateTime _lastPasswordInputUtc = DateTime.MinValue;

    /// <summary>呼叫端用這個 Task 等結果，不是靠 ShowDialog() 的回傳值——見類別開頭說明。</summary>
    public Task<bool> ResultTask => _resultTcs.Task;

    public PasswordLockerBrowserVerifyWindow(
        string domain, string? targetDomain, bool passkeyEnabled, string theme,
        Func<string?, bool, IntPtr, Task<(bool Success, string? ErrorMessage, string? ErrorCode)>> verify)
    {
        InitializeComponent();
        ApplyTheme(theme);

        _verify = verify;
        _passkeyEnabled = passkeyEnabled;

        DomainText.Text = domain;
        HintText.Text = BuildHintText(domain, targetDomain);
        PasskeyButton.Visibility = passkeyEnabled ? Visibility.Visible : Visibility.Collapsed;

        VerifyButtonHost.Cursor = Cursors.No;

        _keepInFrontTimer = new DispatcherTimer { Interval = KeepInFrontInterval };
        _keepInFrontTimer.Tick += (_, _) =>
        {
            if (!_isBusy && DateTime.UtcNow - _lastPasswordInputUtc > TypingDebounce)
            {
                // 單靠搶焦點沒辦法把這個視窗疊到一個「真正置頂」的視窗（Windows Hello 對話框）
                // 上面——這是 Windows z-order 的硬規則，跟哪個視窗目前作用中無關。先把
                // WindowFocusHelper 正在維持的置頂暫時關掉，這個視窗才有機會真的浮上來，
                // 搶完前景立刻還原，不然 Hello 對話框會一直卡在非置頂狀態。這裡不判斷
                // Hello 對話框現在是不是真的還開著——SuspendPromotion／ResumePromotion 對
                // 「目前沒有任何視窗在維持置頂」的情況本來就是安全的無動作。
                WindowFocusHelper.SuspendPromotion();
                WindowActivation.ForceToForeground(this);
                WindowFocusHelper.ResumePromotion();
            }
        };
        Loaded += (_, _) => _keepInFrontTimer.Start();
        Closed += (_, _) => _keepInFrontTimer.Stop();

        Loaded += async (_, _) =>
        {
            if (_passkeyEnabled)
            {
                // 比照 PasswordPromptWindow 的既有寫法：先明確 Activate、讓出一輪 Dispatcher，
                // 確保這個剛顯示的視窗自己的作用中狀態已經穩定，才觸發 Passkey——不然緊接著
                // WPF/OS 對這個視窗自己的 pending 作用中程序完成時，反而會把焦點搶回來、
                // 蓋掉剛顯示的 Windows Hello 驗證視窗。這個視窗比 PasswordPromptWindow 多一層
                // 額外的不穩定來源：觸發鏈路長得多（Chrome → 現拉起一個全新的
                // FileLocker.PasswordLockerNativeHost.exe 進程 → Named Pipe → 這裡），比起
                // 「已經在跑的 App 收到本機 Pipe 轉送的參數」多了「啟動一個全新進程」這一大段
                // OS 排程雜訊，單一次 Dispatcher.Yield 不夠讓這個視窗自己的作用中狀態先穩定
                // 下來——實測發現視窗剛顯示時會被 Passkey 對話框蓋過去，要手動點一下才恢復
                // 正常順序。多等一段真正的時間（不只是排一次隊），讓 DWM 合成/作用中狀態確實
                // 走完，才輪到 Passkey 搶前景。
                Activate();
                await Dispatcher.Yield(DispatcherPriority.Input);
                await Task.Delay(200);
                await SubmitAsync(password: null);
            }
            else
            {
                PasswordInput.Focus();
            }
        };
        // 使用者用標題列關閉鈕以外的方式關掉視窗（Alt+F4、工作列右鍵關閉）時的保底——
        // TrySetResult 是冪等的，success/cancel 路徑已經設過結果的話這裡不會有效果。
        Closed += (_, _) => _resultTcs.TrySetResult(false);
    }

    private async void PasskeyButton_Click(object sender, RoutedEventArgs e) => await SubmitAsync(password: null);

    private async void VerifyButton_Click(object sender, RoutedEventArgs e) => await SubmitAsync(PasswordInput.Password);

    private async Task SubmitAsync(string? password)
    {
        if (_isBusy)
        {
            return;
        }
        _isBusy = true;
        SetBusyState(true);
        ErrorText.Visibility = Visibility.Collapsed;

        if (password is null)
        {
            PasskeyStatusText.Text = "正在使用 Passkey 驗證，請通過 Windows Hello…";
            PasskeyStatusText.Visibility = Visibility.Visible;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var (success, errorMessage, _) = await _verify(password, password is null, hwnd);

        if (success)
        {
            await ShowSuccessAndCloseAsync();
            return;
        }

        if (password is null)
        {
            // Passkey 沒完成（使用者取消、驗證失敗）：退回密碼輸入，保留「重試 Passkey」
            // 按鈕讓使用者可以再試一次——不代表使用者不想用 Passkey，可能只是不小心關掉。
            PasskeyStatusText.Text = "Passkey 未完成驗證，可以重試，或直接輸入密碼。";
            PasskeyStatusText.Visibility = Visibility.Visible;
            SetBusyState(false);
            PasswordInput.Focus();
            _isBusy = false;
            return;
        }

        ErrorText.Text = errorMessage ?? "驗證失敗";
        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.Clear();
        SetBusyState(false);
        PasswordInput.Focus();
        _isBusy = false;
    }

    private async Task ShowSuccessAndCloseAsync()
    {
        SuccessOverlay.Visibility = Visibility.Visible;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        SuccessOverlay.BeginAnimation(OpacityProperty, fadeIn);

        await Task.Delay(500);
        _resultTcs.TrySetResult(true);
        Close();
    }

    private void SetBusyState(bool isBusy)
    {
        PasswordInput.IsEnabled = !isBusy;
        VerifyButton.IsEnabled = !isBusy && !string.IsNullOrEmpty(PasswordInput.Password);
        VerifyButtonHost.Cursor = VerifyButton.IsEnabled ? Cursors.Hand : Cursors.No;
        CancelButton.IsEnabled = !isBusy;
        PasskeyButton.IsEnabled = !isBusy;
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _lastPasswordInputUtc = DateTime.UtcNow;
        if (_isBusy) return;
        VerifyButton.IsEnabled = !string.IsNullOrEmpty(PasswordInput.Password);
        VerifyButtonHost.Cursor = VerifyButton.IsEnabled ? Cursors.Hand : Cursors.No;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _resultTcs.TrySetResult(false);
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _isBusy) return;
        CancelButton_Click(sender, e);
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void ApplyTheme(string theme)
    {
        var isDark = theme == "dark";
        SetBrush("SurfaceBrush", isDark ? "#232428" : "#FFFFFF");
        SetBrush("WindowBorderBrush", isDark ? "#34363C" : "#E1E4EA");
        SetBrush("BorderStrongBrush", isDark ? "#454850" : "#C9CDD6");
        SetBrush("TextBrush", isDark ? "#ECEDEF" : "#1B1E24");
        SetBrush("TextSecondaryBrush", isDark ? "#B0B4BC" : "#454A54");
        SetBrush("TextTertiaryBrush", isDark ? "#82868F" : "#6B707A");
        SetBrush("AccentBrush", isDark ? "#D9A83B" : "#A8770F");
        SetBrush("DangerBrush", isDark ? "#E17153" : "#B14328");
        SetBrush("ChipBackgroundBrush", isDark ? "#2C2E33" : "#F1F2F5");
        SetBrush("SuccessBrush", isDark ? "#4EAE76" : "#2E7D4F");
    }

    private void SetBrush(string resourceKey, string colorHex)
    {
        Resources[resourceKey] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
    }
}
