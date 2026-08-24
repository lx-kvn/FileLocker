namespace FileLocker.Core.Settings;

/// <summary>
/// 對應 GUI 設定頁：Vault 位置、語言、主題。存在固定位置（不像 Vault 本身可以搬），
/// 因為這是「這個 App 安裝在這台裝置上」的設定，不是要跟著 Vault 內容搬走的東西。
/// </summary>
public class AppSettings
{
    /// <summary>null 代表還沒設定過，交由呼叫端決定預設值（見 AppSettingsManager）。</summary>
    public string? VaultPath { get; set; }

    /// <summary>目前只有繁體中文一個選項，先把欄位定出來，未來加語言時前端直接多一個選項即可，不用動這裡的格式。</summary>
    public string Language { get; set; } = "zh-TW";

    /// <summary>light 或 dark。目前只存偏好、按鈕看得到，實際套用畫面主題要等 GUI 美化階段才會真的生效。</summary>
    public string Theme { get; set; } = "light";

    /// <summary>null 代表使用者還沒設定過「關鍵操作」的 Windows Hello 驗證，見
    /// VaultProtocolHandlers.SetupCriticalActionAsync／VerifyCriticalActionAsync。</summary>
    public string? CriticalActionCredentialName { get; set; }

    /// <summary>開啟後關閉所有視窗不會結束程式，改成留在系統匣（見 TrayIconManager），資料夾防護
    /// 的閒置自動重新上鎖計時器才能持續運作。跟 LaunchAtStartupEnabled 是兩個獨立的開關——
    /// 使用者可能只想要其中一個效果，不強制綁在一起。預設開啟。</summary>
    public bool MinimizeToTrayEnabled { get; set; } = true;

    /// <summary>開啟後登記 FileLocker 跟隨 Windows 啟動（見 StartupRegistrar，HKEY_CURRENT_USER
    /// 底下，不需要系統管理員權限），開機後不用手動開一次 FileLocker 就有保護。跟
    /// MinimizeToTrayEnabled 是兩個獨立的開關。預設開啟。</summary>
    public bool LaunchAtStartupEnabled { get; set; } = true;

    /// <summary>標題列視窗控制鈕（關閉／最小化／最大化）的造型，三選一：
    /// "macos"（圓點、左上角，預設）、"windows-native"（方形貼邊、右上角，貼近 Windows 11
    /// 原生行為）、"windows-styled"（方角但圓角、右上角，質感跟 macOS 燈號同一套，用 App
    /// 自己的強調色/危險色而不是 OS 原生紅/灰）。三種都仍是 Vue 畫的（.traffic-light／
    /// .win-btn／.win-btn-styled 這幾組元件），不是換成原生系統控制項，只是換一套造型。
    /// 沒有做值驗證（跟 Theme 一樣的既有慣例），前端只會送這三個字串之一。</summary>
    public string WindowControlStyle { get; set; } = "macos";
}