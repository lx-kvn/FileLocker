using System.Text.Json;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Tests;

public class VaultChangeWatcherTests : IDisposable
{
    // 覆寫成比正式環境（300ms／750ms）短的 debounce 值，搭配逾時輪詢斷言而非固定 sleep，
    // 盡量降低機器負載造成的不穩定——這類涉及計時的測試先天比純邏輯測試容易偶爾變慢，是明確
    // 接受的取捨。原本用 30ms／80ms，`dotnet test` 平行跑多個測試組時 CPU 排程延遲偶爾會讓
    // BurstOfManyFileChanges_RaisesChangedEventExactlyOnce 15 次連續寫入之間的間隔被拉長到
    // 超過這個窗口，導致同一輪 burst 被真的拆成兩次 debounce 週期（不是斷言邏輯的問題，是
    // production code 在那個間隔下本來就會如實回報兩次）——調寬到 80ms／200ms，跟正式環境
    // 的比例接近，但仍遠比 300ms／750ms 快，測試總時長還在可接受範圍。
    private static readonly TimeSpan PerFileDebounce = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan NotifyDebounce = TimeSpan.FromMilliseconds(200);

    private readonly DirectoryInfo _tempVaultDir;
    private readonly DirectoryInfo _tempCacheDir;
    private readonly VaultManager _vault;
    private readonly VaultIndexCache _cache;
    private readonly VaultChangeWatcher _watcher;

    public VaultChangeWatcherTests()
    {
        _tempVaultDir = Directory.CreateTempSubdirectory("FileLockerVaultTests_");
        _tempCacheDir = Directory.CreateTempSubdirectory("FileLockerCacheTests_");
        _vault = new VaultManager(_tempVaultDir.FullName);
        _cache = new VaultIndexCache(_vault, _tempCacheDir.FullName);
        _watcher = new VaultChangeWatcher(_tempVaultDir.FullName, _cache, PerFileDebounce, NotifyDebounce);
        _watcher.Start();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _cache.Dispose();

        if (_tempVaultDir.Exists)
        {
            _tempVaultDir.Delete(recursive: true);
        }

        if (_tempCacheDir.Exists)
        {
            _tempCacheDir.Delete(recursive: true);
        }
    }

    private static LockedItemMetadata CreateSampleMetadata(string uuid) => new()
    {
        Uuid = uuid,
        OriginalName = "測試檔案.txt",
        OriginalPath = @"C:\Users\test\Documents\測試檔案.txt",
        PasswordVerificationHash = "dummyHashBase64==",
        Salt = "dummySaltBase64==",
        Argon2TimeCost = 3,
        Argon2MemoryCostKb = 65536,
        Argon2Parallelism = 2,
        Hint = "測試提示",
        Type = ItemType.File,
        OriginalSizeBytes = 1024,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    /// <summary>等到 Changed 事件觸發、或逾時；用輪詢等待而非固定 sleep 後單次斷言。</summary>
    private async Task<bool> WaitForChangedAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>();
        void Handler(object? sender, EventArgs e) => tcs.TrySetResult(true);

        _watcher.Changed += Handler;
        try
        {
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return completed == tcs.Task;
        }
        finally
        {
            _watcher.Changed -= Handler;
        }
    }

    [Fact]
    public async Task RapidSuccessiveWritesToSameFile_OnlyProcessedOnce()
    {
        var uuid = Guid.NewGuid().ToString();
        var metaPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");

        for (var i = 0; i < 5; i++)
        {
            var metadata = CreateSampleMetadata(uuid);
            metadata.Hint = $"第 {i} 次寫入";
            File.WriteAllText(metaPath, JsonSerializer.Serialize(metadata));
            await Task.Delay(5); // 遠小於 PerFileDebounce，確保這些事件會被視為同一輪安靜下來後才處理
        }

        var raised = await WaitForChangedAsync(TimeSpan.FromSeconds(2));

        Assert.True(raised);
        var items = _cache.GetItems();
        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }

    [Fact]
    public async Task BurstOfManyFileChanges_RaisesChangedEventExactlyOnce()
    {
        var raisedCount = 0;
        void CountHandler(object? sender, EventArgs e) => Interlocked.Increment(ref raisedCount);
        _watcher.Changed += CountHandler;

        try
        {
            for (var i = 0; i < 15; i++)
            {
                _vault.SaveMetadata(CreateSampleMetadata(Guid.NewGuid().ToString()));
            }

            // 不用單一個固定 sleep 就斷言——`dotnet test` 會平行跑好幾個測試組，CPU 排程
            // 延遲偶爾會讓 15 次 SaveMetadata 之間的間隔被拉長到超過 debounce 視窗，導致
            // raisedCount 被多算，或是固定等待時間到了但 cache 還沒處理完最後幾筆。改成輪詢
            // 直到「raisedCount 連續一段安靜視窗內都沒再變化」才罷手，等待時間會隨機器負載
            // 自動拉長，不受固定時間長度綁死（實際發生過：同一份測試單獨跑穩定通過，
            // 跟其他測試組一起跑就出現 raisedCount 忽大忽小）。
            var settleWindow = NotifyDebounce + TimeSpan.FromMilliseconds(150);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var lastCount = -1;
            var lastChangeUtc = DateTime.UtcNow;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(30);
                var current = Volatile.Read(ref raisedCount);
                if (current != lastCount)
                {
                    lastCount = current;
                    lastChangeUtc = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastChangeUtc >= settleWindow)
                {
                    break;
                }
            }
        }
        finally
        {
            _watcher.Changed -= CountHandler;
        }

        Assert.Equal(1, raisedCount);
        Assert.Equal(15, _cache.GetItems().Count);
    }

    [Fact]
    public async Task DeleteThenRecreateWithinDebounceWindow_EndsUpConsistentWithFinalDiskState()
    {
        var uuid = Guid.NewGuid().ToString();
        var metaPath = Path.Combine(_tempVaultDir.FullName, $"{uuid}.meta.json");
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        await WaitForChangedAsync(TimeSpan.FromSeconds(2));
        Assert.Single(_cache.GetItems());

        // debounce 視窗內刪除又重建：處理當下重新問磁碟現況的設計，應該讓最終結果收斂到
        // 「磁碟上現在真的存在」這個狀態，而不是被中間某個瞬間的事件型別誤導。
        File.Delete(metaPath);
        _vault.SaveMetadata(CreateSampleMetadata(uuid));

        var raised = await WaitForChangedAsync(TimeSpan.FromSeconds(2));

        Assert.True(raised);
        var items = _cache.GetItems();
        Assert.Single(items);
        Assert.Equal(uuid, items[0].Uuid);
    }
}
