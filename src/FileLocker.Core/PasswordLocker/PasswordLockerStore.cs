using System.Text.Json;
using FileLocker.Core.Io;

namespace FileLocker.Core.PasswordLocker;

/// <summary>
/// 對應規劃文件：憑證資料獨立於 Vault 之外的本機儲存層。純粹是檔案系統存取，跟 FolderGuardStore
/// 對資料夾防護的定位一致——不做加解密（PasswordLockerService 的事）也不做業務規則判斷，
/// 方便獨立做單元測試。
/// </summary>
public class PasswordLockerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;

    public PasswordLockerStore(string filePath)
    {
        _filePath = filePath;
    }

    public PasswordLockerData Load()
    {
        if (!File.Exists(_filePath))
        {
            return new PasswordLockerData();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<PasswordLockerData>(json) ?? new PasswordLockerData();
        }
        catch (JsonException)
        {
            return new PasswordLockerData();
        }
    }

    public void Save(PasswordLockerData data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicFile.WriteAllText(_filePath, json);
    }
}
