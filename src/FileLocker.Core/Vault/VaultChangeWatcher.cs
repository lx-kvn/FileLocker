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

    // 追蹤「目前正在執行中」的 ProcessFile／通知回呼數量，Dispose() 靠這個數字歸零來確認
    // 真的沒有任何回呼還在存取 _indexCache，而不是靠 Timer.Dispose(WaitHandle)（見下面
    // Dispose() 內的說明，這個 BCL 內建寫法在這裡會跟 ProcessFile 自己「移除並釋放自己」
    // 的既有邏輯互相打架，實測會讓 Dispose() 卡死）。
    private int _inFlightCallbacks;

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

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ScheduleProcessing(e.FullPath);

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        // 舊名字現在不存在了、新名字現在存在——分別排一次，自然對應到 Removed/Changed，
        // 不需要為 Renamed 額外寫一套邏輯。
        ScheduleProcessing(e.OldFullPath);
        ScheduleProcessing(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 已經確定漏事件了（通常是 InternalBufferOverflowException），唯一能保證正確的方式
        // 就是全量重掃，不值得為這個罕見情況做更複雜的處理。
        _indexCache.Rebuild();
        ScheduleNotify();
    }

    private void ScheduleProcessing(string fullPath)
    {
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
        // 先停用事件觸發（而不是只靠等一下呼叫 _watcher.Dispose()）：確保呼叫這個方法之後，
        // 不會再有新的 OnFileEvent/OnRenamedEvent 進來排一顆新的 Timer。
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();

        // Timer.Dispose()（不管是無參數版本還是 Dispose(WaitHandle) 那個多載）都只保證「這顆
        // 計時器以後不會再觸發」，不保證「已經在執行中」的回呼會被中斷完成才返回——如果呼叫端
        // 緊接著就把 VaultIndexCache（呼叫端持有的另一個 IDisposable，包著 SqliteConnection）
        // 也 Dispose 掉，一個還在飛的 ProcessFile／通知回呼繼續存取 SQLite 就會撞到
        // ObjectDisposedException（或是連線已關閉的變體），而且是背景執行緒上未攔截的例外，
        // 直接讓整個行程當掉，不是單純丟例外可以被上層 try/catch 接住（這個 crash 真實發生過，
        // 見 tests/.../VaultChangeWatcherTests.DisposeCalledImmediatelyAfterFileChange_
        // DoesNotRaceWithCacheDisposal 這份回歸測試）。
        //
        // 原本試過改用 Timer.Dispose(WaitHandle) 這個 BCL 內建、文件上保證「等在飛的回呼跑完
        // 才signal」的寫法，但這裡的 ProcessFile 自己在回呼一開始就會把自己從 _perFileTimers
        // 移除、呼叫自己的 Dispose()（見 ProcessFile 內的 timer.Dispose()）——這是計時器
        // callback 內自我釋放的合法用法，本身沒問題，但跟這裡「外部再對同一個 Timer 物件呼叫
        // Dispose(WaitHandle) 並等待訊號」的寫法同時發生時，實測會讓 waitHandle 永遠等不到
        // 訊號、Dispose() 直接卡死（用上面提到的回歸測試重現過）。改用最簡單可靠的做法：
        // 一個 Interlocked 計數器記錄「目前正在執行中」的回呼數量，ProcessFile／通知回呼各自
        // 在一開始遞增、結束時（含例外情況）遞減，Dispose() 這裡先讓所有計時器都不會再觸發，
        // 再輪詢等這個計數器歸零——不管回呼是不是自己搶先移除了自己的 Timer，計數器都準確
        // 反映「還有沒有程式碼在存取 _indexCache」，不會被計時器物件本身的生命週期細節干擾。
        foreach (var timer in _perFileTimers.Values)
        {
            timer.Dispose();
        }
        _perFileTimers.Clear();

        lock (_notifyTimerLock)
        {
            _notifyTimer?.Dispose();
        }

        var spinWait = new SpinWait();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref _inFlightCallbacks) > 0 && DateTime.UtcNow < deadline)
        {
            spinWait.SpinOnce();
        }
    }
}
