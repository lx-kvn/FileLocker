using System.Text.RegularExpressions;

namespace FileLocker.Core.UpdateCheck;

/// <summary>
/// 密碼庫可選配部件的 GitHub Release 資產挑選邏輯（見 PasswordVault_獨立化_規劃.md「資產命名規則」
/// 小節）。部件原本是同一個 repo 裡的 FileLocker.PasswordLocker，遷出獨立成 PasswordVault repo
/// 之後，檔名前綴跟著換成 <c>PasswordVault_</c>，且插入 <c>for-FileLocker-</c>／<c>-to-</c> 固定
/// 字詞當視覺分隔——原本 <c>PasswordLocker_vX.Y.Z_x.y.z-x.y.z.zip</c> 這種三組版本號緊貼在一起的
/// 寫法太容易眼花撩亂。
///
/// 檔名慣例 <c>PasswordVault_vX.Y.Z_for-FileLocker-x.y.z-to-x.y.z.zip</c>：前段是 PasswordVault
/// 自己的版本，後段是這個版本相容的 FileLocker 版本區間（含頭尾）。用區間而不是「最低版本」單向
/// 標記，是因為部件可能依賴 FileLocker 某版本才新增的介面，同時又可能在更新的 FileLocker 版本上
/// 因架構調整而失效——只標最低版本沒辦法表達「太新也不相容」。
///
/// 刻意不相容遷出前的舊命名（<c>PasswordLocker_v...</c>）——切換消費來源之後，FileLocker 自己的
/// Release 不會再產出這種資產，兩種格式都收只會讓人搞不清楚現在到底認的是哪一種。
/// </summary>
public static class PasswordLockerAssetSelector
{
    private static readonly Regex AssetNamePattern = new(
        @"^PasswordVault_v(?<pv>\d+\.\d+\.\d+)_for-FileLocker-(?<min>\d+\.\d+\.\d+)-to-(?<max>\d+\.\d+\.\d+)\.zip$",
        RegexOptions.Compiled);

    /// <summary>掃過候選資產檔名，篩出「目前執行中的 FileLocker 版本落在相容區間內」的資產，
    /// 有多筆符合時挑其中 PasswordLocker 版本最新的一筆。<paramref name="currentFileLockerVersion"/>
    /// 或任何一筆資產檔名格式解析失敗都視為不符合，不強行猜測。</summary>
    public static string? SelectBestAssetName(IEnumerable<string> assetNames, string currentFileLockerVersion)
    {
        if (!TryParseVersion(currentFileLockerVersion, out var current))
        {
            return null;
        }

        string? bestName = null;
        Version? bestPasswordLockerVersion = null;

        foreach (var name in assetNames)
        {
            var match = AssetNamePattern.Match(name);
            if (!match.Success)
            {
                continue;
            }
            if (!TryParseVersion(match.Groups["pv"].Value, out var passwordLockerVersion)
                || !TryParseVersion(match.Groups["min"].Value, out var min)
                || !TryParseVersion(match.Groups["max"].Value, out var max))
            {
                continue;
            }
            if (current < min || current > max)
            {
                continue;
            }
            if (bestPasswordLockerVersion is null || passwordLockerVersion > bestPasswordLockerVersion)
            {
                bestPasswordLockerVersion = passwordLockerVersion;
                bestName = name;
            }
        }

        return bestName;
    }

    private static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }
        return Version.TryParse(raw.Trim().TrimStart('v', 'V'), out version!);
    }
}
