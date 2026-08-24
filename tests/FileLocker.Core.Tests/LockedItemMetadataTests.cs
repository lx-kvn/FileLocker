using System.Text.Json;
using FileLocker.Core.Models;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應「單檔案分散式加密」功能規劃 §6.1：StorageMode 欄位要照抄既有 Status 欄位「給預設值、
/// 舊資料反序列化自動補上」的既有慣例（見 LockedItemMetadata.Status 上的註解），這裡直接驗證
/// 那個假設成立，不是憑印象假設 System.Text.Json 的預設值行為一定符合預期。
/// </summary>
public class LockedItemMetadataTests
{
    // 刻意手刻 JSON 字串（不透過 JsonSerializer.Serialize 反過來產生)，模擬「這份 .meta.json
    // 是舊版本寫的，根本沒有 StorageMode 這個屬性」的真實情境——如果改成用同一個型別序列化
    // 再反序列化，STorageMode 一定會被寫進去，測不出「欄位缺席時的預設值」這件事本身。
    private const string LegacyJsonWithoutStorageMode = """
        {
          "Uuid": "11111111-1111-1111-1111-111111111111",
          "OriginalName": "test.txt",
          "OriginalPath": "C:\\test.txt",
          "PasswordVerificationHash": "hash",
          "Salt": "salt",
          "Argon2TimeCost": 3,
          "Argon2MemoryCostKb": 65536,
          "Argon2Parallelism": 4,
          "Type": 0,
          "OriginalSizeBytes": 100,
          "CreatedAtUtc": "2026-01-01T00:00:00Z"
        }
        """;

    [Fact]
    public void Deserialize_LegacyJsonWithoutStorageMode_DefaultsToVault()
    {
        var metadata = JsonSerializer.Deserialize<LockedItemMetadata>(LegacyJsonWithoutStorageMode);

        Assert.NotNull(metadata);
        Assert.Equal(StorageMode.Vault, metadata!.StorageMode);
    }

    [Fact]
    public void SerializeThenDeserialize_StandaloneMode_RoundTrips()
    {
        var original = new LockedItemMetadata
        {
            StorageMode = StorageMode.Standalone,
            Uuid = "22222222-2222-2222-2222-222222222222",
            OriginalName = "test.txt",
            OriginalPath = @"C:\test.txt",
            PasswordVerificationHash = "hash",
            Salt = "salt",
            Argon2TimeCost = 3,
            Argon2MemoryCostKb = 65536,
            Argon2Parallelism = 4,
            Type = ItemType.File,
            OriginalSizeBytes = 100,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<LockedItemMetadata>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(StorageMode.Standalone, roundTripped!.StorageMode);
    }

    [Fact]
    public void NewInstance_WithoutExplicitStorageMode_DefaultsToVault()
    {
        var metadata = new LockedItemMetadata
        {
            Uuid = "33333333-3333-3333-3333-333333333333",
            OriginalName = "test.txt",
            OriginalPath = @"C:\test.txt",
            PasswordVerificationHash = "hash",
            Salt = "salt",
            Argon2TimeCost = 3,
            Argon2MemoryCostKb = 65536,
            Argon2Parallelism = 4,
            Type = ItemType.File,
        };

        Assert.Equal(StorageMode.Vault, metadata.StorageMode);
    }
}
