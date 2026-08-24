using FileLocker.Core.Models;

namespace FileLocker.Core;

/// <summary>
/// 對應「單檔案分散式加密」功能規劃 §6.2／§8：跟 MarkerStatusChecker 平行的一組行為，只回答
/// 「這個項目原本位置的 .flocked 檔案還在不在、UUID 對不對」，純粹是檔案系統查詢。跟
/// MarkerStatusChecker 的差別只在：.flocked 沒有簽章（完整性由後面的密文串流自己的 AES-GCM
/// Auth Tag 保護，見 FlockedFileFormat 上的說明），這裡只需要讀 header 裡的 UUID 比對，
/// 不需要驗證任何簽章。
/// </summary>
public static class FlockedStatusChecker
{
    public static MarkerStatus CheckFlockedStatus(LockedItemMetadata metadata)
        => CheckFlockedStatus(metadata.Uuid, metadata.OriginalPath, metadata.Type);

    /// <summary>
    /// 只吃清單頁實際需要的三個欄位，讓呼叫端不需要為了呼叫這個方法，硬湊一個帶假資料的完整
    /// LockedItemMetadata——跟 MarkerStatusChecker 的三參數多載同一個理由。
    /// </summary>
    public static MarkerStatus CheckFlockedStatus(string uuid, string originalPath, ItemType type)
    {
        var expectedPath = ComputeFlockedPath(originalPath, type == ItemType.Folder);

        if (!File.Exists(expectedPath))
        {
            return new MarkerStatus(false, null, ".flocked 檔案可能被移動或刪除", Code: ErrorCodes.FlockedNotFound);
        }

        if (!FlockedFileFormat.TryReadUuid(expectedPath, out var foundUuid))
        {
            return new MarkerStatus(false, null, "原本位置的檔案無法解析為 .flocked 檔案", Code: ErrorCodes.FlockedParseFailed);
        }

        if (foundUuid != uuid)
        {
            return new MarkerStatus(false, null, "原本的位置已經被別的加密項目取代", Code: ErrorCodes.FlockedReplacedByOther, ConflictingUuid: foundUuid);
        }

        return new MarkerStatus(true, expectedPath, null);
    }

    /// <summary>跟 MarkerStatusChecker.ComputeMarkerPath 同一套命名規則，只是副檔名換成 .flocked。</summary>
    public static string ComputeFlockedPath(string originalPath, bool isFolder)
    {
        var trimmedPath = originalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parentDir = isFolder
            ? Directory.GetParent(trimmedPath)?.FullName ?? throw new IOException($"無法判斷父資料夾：{originalPath}")
            : Path.GetDirectoryName(Path.GetFullPath(trimmedPath)) ?? throw new IOException($"無法判斷父資料夾：{originalPath}");

        var baseName = isFolder
            ? Path.GetFileName(trimmedPath)
            : Path.GetFileNameWithoutExtension(trimmedPath);

        return Path.Combine(parentDir, $"{baseName}.flocked");
    }
}
