using System.Text.Json;
using FileLocker.Core.Models;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Tests;

public class VaultChangeWatcherTests : IDisposable
{
    // 這個類別的計時測試會跟其他測試專案（尤其是 FileLocker.PasswordLocker.Tests 裡刻意
    // CPU/記憶體密集的 Argon2id 測試）在整套 `dotnet test` 一起跑時同時執行——實測發現單獨
    // 跑這個測試檔案穩定通過，但跟其他測試專案一起跑偶爾會斷（BurstOfManyFileChanges_
    // RaisesChangedEventExactlyOnce 的 raisedCount 忽大忽小，或 WaitForChangedAsync 逾時前
    // 事件根本沒到）。追下去發現根本原因不是 debounce 視窗或輪詢邏輯（那些已經調過好幾輪，
    // 見下面 PerFileDebounce/NotifyDebounce 的說明跟 BurstOfManyFileChanges 內的輪詢註解），
    // 是 .NET ThreadPool 預設的執行緒注入節流——執行緒池忙碌時，新執行緒大約每 500ms 才會
    // 多開一顆，這裡用的 System.Threading.Timer 回呼排進去的就是 ThreadPool，節流會直接讓
    // 回呼被延後執行，跟 debounce 視窗設多寬無關。這裡在整個測試類別跑之前，把 ThreadPool
    // 的最小執行緒數拉高，讓回呼不用等節流慢慢開執行緒——這是純測試環境的緩解措施，不改變
    // production code（VaultChangeWatcher.cs）本身的計時行為或邏輯。
    static VaultChangeWatcherTests()
    {
        ThreadPool.GetMinThreads(out var minWorker, out var minIo);
        var desiredMinWorker = Math.Max(minWorker, Environment.ProcessorCount * 4);
        ThreadPool.SetMinThreads(desiredMinWorker, minIo);
    }

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
            //
            // deadline 原本是 5 秒，實際發生過整套 `dotnet test` 一起跑、系統比較忙的時候，
            // 真正的 FileSystemWatcher 送出事件本身的延遲被拉長超過這個上限，導致輪詢直接
            // 等到 deadline 就放棄、raisedCount 還停在 0（不是被多算，是連一次都還沒等到），
            // 斷言就誤判成失敗。拉長到 10 秒給系統忙的時候多一點緩衝，不是延長 debounce
            // 視窗本身（NotifyDebounce／settleWindow 不變，只是願意多等幾輪安靜視窗）。
            var settleWindow = NotifyDebounce + TimeSpan.FromMilliseconds(150);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
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
    public void DisposeCalledImmediatelyAfterFileChange_DoesNotRaceWithCacheDisposal()
    {
        // 重現一個實際發生過、把整個測試主機行程弄當機的競爭情況：VaultChangeWatcher.Dispose()
        // 原本只是把每個檔案的 Timer 呼叫 .Dispose()，但 Timer.Dispose() 不保證「已經在執行中」
        // 的回呼會被中斷完成才返回——如果呼叫端緊接著就把 VaultIndexCache（包著 SqliteConnection）
        // 也 Dispose 掉（這正是本測試類別的 IDisposable.Dispose() 在做的事，也是實際 App 層
        // shutdown 邏輯的既有順序），一個還在飛的 ProcessFile 回呼繼續存取 SQLite 就會撞到
        // ObjectDisposedException——而且是背景執行緒上未攔截的例外，直接讓整個行程當掉，不是
        // 單純的測試失敗（.NET 對執行緒集區背景執行緒上的未攔截例外，預設行為就是終止行程）。
        //
        // 用極短的 debounce（1ms）讓 debounce 計時器幾乎立刻觸發，緊接著不等待、立刻呼叫
        // watcher.Dispose() 再呼叫 cache.Dispose()，盡量重現「Dispose 那一刻回呼剛好在飛」
        // 的窗口。watcher.Dispose() 回傳之後，緊接著 Dispose cache 必須是安全的——如果修好了，
        // Dispose() 內部要真的等到所有在飛的計時器回呼完全執行完畢才能返回，而不是只是把
        // Timer 物件標記成已釋放。
        // 單一輪的時機窗口很窄（不一定每次都撞得到），重複跑 30 輪疊加機率——這是刻意的
        // 壓力測試寫法，不是隨手複製貼上：目的是讓這份回歸測試在「修復前」有夠高的機率
        // 真的複現當機，而不是矇對一次就過。
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var tempVaultDir = Directory.CreateTempSubdirectory("FileLockerVaultTests_DisposeRace_");
            var tempCacheDir = Directory.CreateTempSubdirectory("FileLockerCacheTests_DisposeRace_");
            try
            {
                var vault = new VaultManager(tempVaultDir.FullName);
                var cache = new VaultIndexCache(vault, tempCacheDir.FullName);
                var watcher = new VaultChangeWatcher(
                    tempVaultDir.FullName,
                    cache,
                    perFileDebounce: TimeSpan.FromMilliseconds(1),
                    notifyDebounce: TimeSpan.FromMilliseconds(1));
                watcher.Start();

                // 筆數故意拉高（200 筆）：每個檔案各自的 1ms debounce 幾乎都會在我們還在寫檔的
                // 這段時間內就到期，一大批 ProcessFile 回呼會同時搶 VaultIndexCache 內部那道
                // 序列化存取的鎖（見 VaultIndexCache._connectionLock 上的註解），排隊處理需要
                // 一點時間，這樣才能讓「呼叫 Dispose() 那一刻還有回呼在飛」這個窗口變得夠寬。
                for (var i = 0; i < 200; i++)
                {
                    vault.SaveMetadata(CreateSampleMetadata(Guid.NewGuid().ToString()));
                }

                // 故意不等待、不 sleep——緊接著立刻收尾，這正是會觸發競爭的順序。
                watcher.Dispose();
                cache.Dispose();
            }
            finally
            {
                if (tempVaultDir.Exists) tempVaultDir.Delete(recursive: true);
                if (tempCacheDir.Exists) tempCacheDir.Delete(recursive: true);
            }
        }
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
