namespace FileLocker.Core.UpdateCheck;

/// <summary>
/// FileLocker 本體自動更新（見「軟體更新檢查」功能）挑選 GitHub Release 附件的邏輯——真實抓到
/// 的 bug：v2.1.0 這輪同一次 release 新增了 CLI 獨立發布產物（FileLocker_CLI_vX.Y.Z_setup.exe／
/// _portable.zip，見 CONTEXT.md「CLI 獨立發布產物」詞條），原本「掃 assets、抓第一個副檔名是
/// .exe 的」這條邏輯完全沒排除 CLI 版安裝檔——GitHub Release 附件清單的順序不保證跟上傳順序
/// 一致（實測 v2.1.0 這輪 CLI_setup.exe 排在 GUI 版 setup.exe 前面），選到 CLI 版安裝檔、
/// 靜默裝到完全不同的資料夾（`FileLocker-CLI`，`no_admin_install: true`），GUI 版本體從頭到尾
/// 沒被更新，卻回報「安裝成功」——因為 CLI 版安裝真的成功了，只是裝錯了東西，使用者重開程式
/// 後版本號完全沒變。
///
/// 排除條件用檔名裡有沒有 <c>_CLI_</c> 這個固定字串判斷，跟 CLI 獨立發布產物的命名慣例
/// （<c>FileLocker_CLI_vX.Y.Z_setup.exe</c>／<c>FileLocker_CLI_vX.Y.Z_portable.zip</c>）綁在
/// 一起——兩邊如果之後改了命名規則要一起改，不是各自獨立維護的巧合。
/// </summary>
public static class SelfUpdateAssetSelector
{
    private const string CliMarker = "_CLI_";

    /// <summary>掃過候選資產檔名，回傳第一個「副檔名是 .exe、且不是 CLI 獨立發布產物」的
    /// 檔名——GUI 安裝檔的命名慣例固定是 <c>FileLocker_vX.Y.Z_setup.exe</c>，不含
    /// <c>_CLI_</c> 這個字串，只靠這個標記排除就夠，不需要另外寫一個正向比對 GUI 檔名格式的
    /// 正則表達式（正向比對反而更脆弱：發布慣例本身還可能演變，排除已知不要的比正向鎖死格式
    /// 更寬容）。找不到符合的資產回傳 null，不強行猜測。</summary>
    public static string? SelectGuiInstallerAssetName(IEnumerable<string> assetNames)
    {
        foreach (var name in assetNames)
        {
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !name.Contains(CliMarker, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return null;
    }
}
