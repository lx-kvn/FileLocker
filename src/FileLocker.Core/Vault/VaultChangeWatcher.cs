using System.Collections.Concurrent;

namespace FileLocker.Core.Vault;

/// <summary>
/// 監控 Vault 資料夾內 *.meta.json 的新增/變更/刪除，把變化即時同步進 VaultIndexCache，
/// 並在一輪變化處理完後觸發 Changed 事件，讓呼叫端（App 層）可以推送通知給前端清單頁。
///
/// 兩層 debounce，關注點分離：
/// 1. 單檔 debounce——同一個路徑短時間內收到再多次事件，都只在「安靜下來」後處理一次。
/// 2. 全域通知 debounce——任何一次單檔處理完成都會重置這個計時器，批次加密/解密幾十個
///    檔案時只對外觸發一次 Changed，不會連環轟炸前端。
/// </summary>
public sealed class VaultChangeWatcher : IDisposable
{
    private readonly VaultIndexCache _indexCache;
    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _perFileDebounce;
    private readonly TimeSpan _notifyDebounce;
    private readonly ConcurrentDictionary<string, Timer> _perFileTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _notifyTimerLock = new();
    private Timer? _notifyTimer;

    // 追蹤「目前正在執行中」的事件處理常式／ProcessFile／通知回呼數量，Dispose() 靠這個
    // 數字歸零來確認真的沒有任何回呼還在存取 _indexCache，而不是靠 Timer.Dispose(WaitHandle)
    // （見下面 Dispose() 內的說明，這個 BCL 內建寫法在這裡會跟 ProcessFile 自己「移除並釋放
    // 自己」的既有邏輯互相打架，實測會讓 Dispose() 卡死）。
    private int _inFlightCallbacks;

    // _watcher.Dispose() 只保證「以後不會再有新事件」，不保證「已經被 OS 通知、已經排進執行緒
    // 集區、但還沒真正執行到」的事件處理常式不會再跑——這種已經在飛的事件處理常式如果在
    // Dispose() 清空 _perFileTimers 之後才真正執行到 ScheduleProcessing，會建立一顆全新的
    // Timer，完全不在清空迴圈的涵蓋範圍內，之後這顆孤兒 Timer 自己觸發時一樣會去存取
    // 這時候可能已經被呼叫端 Dispose 掉的 _indexCache（實測真的撞到過這個當機，見下面
    // Dispose() 的完整說明）。_lifecycleLock 保護 _disposed 旗標本身的讀寫跟
    // ScheduleProcessing 是否要真的建立 Timer 這兩件事，確保是同一個原子操作，不會有
    // 「檢查的時候還沒 disposed、真正建立的時候已經 disposed」這種存在於檢查跟動作之間的縫隙。
    private readonly object _lifecycleLock = new();
    private bool _disposed;

    /// <summary>快取已經處理完一輪變化。從背景計時器執行緒觸發，呼叫端要自己切回 UI 執行緒。</summary>
    public event EventHandler? Changed;

    public VaultChangeWatcher(
        string vaultPath,
        VaultIndexCache indexCache,
        TimeSpan? perFileDebounce = null,
        TimeSpan? notifyDebounce = null)
    {
        _indexCache = indexCache;
        _perFileDebounce = perFileDebounce ?? TimeSpan.FromMilliseconds(300);
        _notifyDebounce = notifyDebounce ?? TimeSpan.FromMilliseconds(750);

        _watcher = new FileSystemWatcher(vaultPath, "*.meta.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            // 預設 8KB 太容易在批次加密/解密大量檔案時溢位（溢位會漏事件，見 OnError）。
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnRenamedEvent;
        _watcher.Error += OnError;
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    private void OnFileEvent(object sender, FileSystemEventArgs e) => RunTrackedCallback(() => ScheduleProcessing(e.FullPath));

    private void OnRenamedEvent(object sender, RenamedEventArgs e) => RunTrackedCallback(() =>
    {
        // 舊名字現在不存在了、新名字現在存在——分別排一次，自然對應到 Removed/Changed，
        // 不需要為 Renamed 額外寫一套邏輯。
        ScheduleProcessing(e.OldFullPath);
        ScheduleProcessing(e.FullPath);
    });

    private void OnError(object sender, ErrorEventArgs e) => RunTrackedCallback(() =>
    {
        // 已經確定漏事件了（通常是 InternalBufferOverflowException），唯一能保證正確的方式
        // 就是全量重掃，不值得為這個罕見情況做更複雜的處理。
        lock (_lifecycleLock)
        {
            if (_disposed) return;
        }
        _indexCache.Rebuild();
        ScheduleNotify();
    });

    /// <summary>把「遞增計數→執行→遞減計數」這個固定模式包起來，所有事件處理常式共用。</summary>
    private void RunTrackedCallback(Action action)
    {
        Interlocked.Increment(ref _inFlightCallbacks);
        try
        {
            action();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightCallbacks);
        }
    }

    private void ScheduleProcessing(string fullPath)
    {
        lock (_lifecycleLock)
        {
            // 已經在 Dispose() 流程中——不能再建立新的 Timer，否則會變成清空迴圈涵蓋不到、
            // 之後自己單獨觸發時存取到已經被呼叫端 Dispose 掉的 _indexCache 的孤兒 Timer。
            if (_disposed) return;

            // 用 Timer.Change 覆寫既有計時器來達成 debounce——每次事件都把倒數重設，
            // 而不是疊加多個計時器，安靜下來後才會真的處理一次。
            _perFileTimers.AddOrUpdate(
                fullPath,
                _ => new Timer(ProcessFile, fullPath, _perFileDebounce, Timeout.InfiniteTimeSpan),
                (_, existingTimer) =>
                {
                    existingTimer.Change(_perFileDebounce, Timeout.InfiniteTimeSpan);
                    return existingTimer;
                });
        }
    }

    private void ProcessFile(object? state)
    {
        Interlocked.Increment(ref _inFlightCallbacks);
        try
        {
            var fullPath = (string)state!;
            if (_perFileTimers.TryRemove(fullPath, out var timer))
            {
                timer.Dispose();
            }

            // 處理時重新問一次磁碟現況，而不是完全相信事件當時標記的型別——雲端同步用戶端
            // 常見「先寫暫存檔、再改名蓋掉」或「短暫建立又立刻刪除」的模式，debounce 真正
            // 觸發的當下，磁碟狀態可能已經跟事件剛發生時不一樣了。
            try
            {
                if (File.Exists(fullPath))
                {
                    _indexCache.OnMetaFileChanged(fullPath);
                }
                else
                {
                    _indexCache.OnMetaFileRemoved(fullPath);
                }
            }
            catch (IOException)
            {
                // 檔案可能還在被寫入/鎖定中，略過這次，下次事件到來再處理一次。
            }

            ScheduleNotify();
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightCallbacks);
        }
    }

    private void ScheduleNotify()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
        }
        lock (_notifyTimerLock)
        {
            _notifyTimer ??= new Timer(NotifyChanged, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _notifyTimer.Change(_notifyDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void NotifyChanged(object? state)
    {
        // 這個回呼本身不碰 _indexCache（只是轉發事件給訂閱端），但還是計進 _inFlightCallbacks——
        // 訂閱端（App 層）收到 Changed 事件時可能也會反過來呼叫回 _indexCache 讀資料，
        // 一起算進去比較保守安全，反正這個回呼通常很快就結束，不會讓 Dispose() 多等太久。
        Interlocked.Increment(ref _inFlightCallbacks);
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightCallbacks);
        }
    }

    public void Dispose()
    {
        // 先停用事件觸發（而不是只靠等一下呼叫 _watcher.Dispose()）：盡量減少「事件已經被 OS
        // 通知、但還沒真正執行到處理常式」這種在飛事件的數量。但這個步驟本身不夠：已經被
        // .NET 排進執行緒集區、只是還沒輪到執行的事件處理常式，就算之後才呼叫 Dispose()，
        // 也還是會照常執行——所以還需要下面的 _disposed 旗標跟兩階段等待。
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();

        // 第一階段：標記 _disposed，讓「已經在飛、還沒執行到 ScheduleProcessing」的事件處理
        // 常式在真正執行到的那一刻自己放棄（見 ScheduleProcessing／ScheduleNotify 開頭的
        // _disposed 檢查，跟這裡用同一個 _lifecycleLock，檢查跟「要不要建立新 Timer」這個
        // 動作是同一個原子操作，不會有「檢查時還沒 disposed、真正建立時已經 disposed」的縫隙）。
        // 標記完之後等 _inFlightCallbacks 歸零一次，確保所有「在飛」的事件處理常式都已經
        // 真正執行完畢（不管它們最後有沒有成功排到新 Timer）——這一步做完，_perFileTimers
        // 保證不會再長出新項目，才能安全地進到下一步清空迴圈。
        //
        // 這個坑是實測撞出來的：只做「清空迴圈 + 等計數器歸零」（沒有 _disposed 旗標跟這一輪
        // 提前等待）看似合理，但如果一個 FileSystemWatcher 事件剛好在清空迴圈跑完之後才真正
        // 執行到 ScheduleProcessing，會建立一顆全新、不在清空範圍內、也還沒被計進
        // _inFlightCallbacks 的孤兒 Timer——它自己的 debounce 時間到了之後才觸發，這時候
        // Dispose() 早就回傳了，呼叫端可能已經把 VaultIndexCache 也 Dispose 掉，一樣會撞到
        // 存取已關閉連線的當機（見 tests/.../VaultChangeWatcherTests.
        // BurstOfManyFileChanges_RaisesChangedEventExactlyOnce 就實際撞到過這個當機，不是
        // 只在特地寫的回歸測試裡才會發生）。
        lock (_lifecycleLock)
        {
            _disposed = true;
        }
        WaitForInFlightCallbacksToSettle();

        // 第二階段：這時候 _perFileTimers 已經是最終、不會再變動的完整集合，可以放心清空。
        // Timer.Dispose()（不管是無參數版本還是 Dispose(WaitHandle) 那個多載）都只保證「這顆
        // 計時器以後不會再觸發」，不保證「已經在執行中」的回呼會被中斷完成才返回——如果呼叫端
        // 緊接著就把 VaultIndexCache 也 Dispose 掉，一個還在飛的 ProcessFile／通知回呼繼續
        // 存取 SQLite 就會撞到同一種當機，所以清空之後還要再等一次計數器歸零。
        //
        // （原本試過改用 Timer.Dispose(WaitHandle) 這個 BCL 內建、文件上保證「等在飛的回呼跑完
        // 才 signal」的寫法，但 ProcessFile 自己在回呼一開始就會把自己從 _perFileTimers 移除、
        // 呼叫自己的 Dispose()——這是計時器 callback 內自我釋放的合法用法，跟外部再對同一個
        // Timer 物件呼叫 Dispose(WaitHandle) 並等待訊號的寫法同時發生時，實測會讓 waitHandle
        // 永遠等不到訊號、Dispose() 直接卡死，所以改用 Interlocked 計數器輪詢這個最簡單可靠
        // 的做法。）
        foreach (var timer in _perFileTimers.Values)
        {
            timer.Dispose();
        }
        _perFileTimers.Clear();

        lock (_notifyTimerLock)
        {
            _notifyTimer?.Dispose();
        }

        WaitForInFlightCallbacksToSettle();
    }

    private void WaitForInFlightCallbacksToSettle()
    {
        var spinWait = new SpinWait();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref _inFlightCallbacks) > 0 && DateTime.UtcNow < deadline)
        {
            spinWait.SpinOnce();
        }
    }
}
