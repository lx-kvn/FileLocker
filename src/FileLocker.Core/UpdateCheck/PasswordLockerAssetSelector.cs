using System.Text.RegularExpressions;

namespace FileLocker.Core.UpdateCheck;

/// <summary>
/// 密碼庫可選配部件的 GitHub Release 資產挑選邏輯（見 FileLocker_密碼庫_功能規劃.md 第 2.2／8 節）。
/// 檔名慣例 <c>PasswordLocker_vX.Y.Z_x.y.z-x.y.z.zip</c>：前段是 PasswordLocker 自己的版本，
/// 後段 <c>x.y.z-x.y.z</c> 是這個版本相容的 FileLocker 版本區間（含頭尾）。用區間而不是「最低版本」
/// 單向標記，是因為部件可能依賴 FileLocker 某版本才新增的介面，同時又可能在更新的 FileLocker
/// 版本上因架構調整而失效——只標最低版本沒辦法表達「太新也不相容」。
/// </summary>
public static class PasswordLockerAssetSelector
{
    private static readonly Regex AssetNamePattern = new(
        @"^PasswordLocker_v(?<pv>\d+\.\d+\.\d+)_(?<min>\d+\.\d+\.\d+)-(?<max>\d+\.\d+\.\d+)\.zip$",
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
