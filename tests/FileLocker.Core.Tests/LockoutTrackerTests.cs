using FileLocker.Core.Security;
using Xunit;

namespace FileLocker.Core.Tests;

public class LockoutTrackerTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly LockoutTracker _tracker;

    public LockoutTrackerTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("FileLockerLockoutTests_");
        _tracker = new LockoutTracker(Path.Combine(_tempDir.FullName, "lockout.json"));
    }

    public void Dispose()
    {
        if (_tempDir.Exists) _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void CheckStatus_ForNeverSeenUuid_IsNotLockedOut()
    {
        var status = _tracker.CheckStatus(Guid.NewGuid().ToString());

        Assert.False(status.IsLockedOut);
        Assert.Null(status.RemainingLockout);
    }

    [Fact]
    public void RecordFailedAttempt_BelowThreshold_DoesNotLockOut()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 4; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }

        Assert.False(_tracker.CheckStatus(uuid).IsLockedOut);
    }

    [Fact]
    public void RecordFailedAttempt_ReachingThreshold_LocksOut()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }

        var status = _tracker.CheckStatus(uuid);

        Assert.True(status.IsLockedOut);
        Assert.True(status.RemainingLockout > TimeSpan.Zero);
    }

    [Fact]
    public void RecordSuccess_ClearsLockoutState()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }

        _tracker.RecordSuccess(uuid);

        Assert.False(_tracker.CheckStatus(uuid).IsLockedOut);
    }

    [Fact]
    public void RecordFailedAttempt_RepeatedLockouts_EscalatesDuration()
    {
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }
        var firstLockout = _tracker.CheckStatus(uuid).RemainingLockout!.Value;

        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuid);
        }
        var secondLockout = _tracker.CheckStatus(uuid).RemainingLockout!.Value;

        Assert.True(secondLockout > firstLockout);
    }

    [Fact]
    public void DifferentUuids_AreLockedOutIndependently()
    {
        var uuidA = Guid.NewGuid().ToString();
        var uuidB = Guid.NewGuid().ToString();

        for (var i = 0; i < 5; i++)
        {
            _tracker.RecordFailedAttempt(uuidA);
        }

        Assert.True(_tracker.CheckStatus(uuidA).IsLockedOut);
        Assert.False(_tracker.CheckStatus(uuidB).IsLockedOut);
    }

    // ---- 通盤檢討改善計畫第 3 輪：退避上限可以依用途調整 ----
    //
    // 加密維持既有的 30 秒起跳、最長 1 小時。資料夾防護改成 5 秒起跳、最長 60 秒——那個功能的
    // 威脅模型是「同一台裝置上的其他人隨手嘗試」，而且忘記密碼時本來就可以透過檔案總管的
    // 安全性設定自行取回存取權（見 ADR-0001），鎖一小時擋不住知道這條路的人，只會把輸入
    // 錯誤的擁有者關在門外。

    private LockoutTracker NewTracker(int baseSeconds, int maxSeconds)
        => new(Path.Combine(_tempDir.FullName, $"lockout-{Guid.NewGuid():N}.json"), baseSeconds, maxSeconds);

    [Fact]
    public void DefaultPolicy_MatchesTheExistingEncryptionBehaviour()
    {
        // 沒有指定政策時維持原本的參數，既有呼叫端（加密）不會因為這次改動而改變行為。
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++) _tracker.RecordFailedAttempt(uuid);

        var remaining = _tracker.CheckStatus(uuid).RemainingLockout!.Value;

        Assert.InRange(remaining.TotalSeconds, 29, 30);
    }

    [Fact]
    public void CustomPolicy_FirstLockoutUsesTheGivenBaseSeconds()
    {
        var tracker = NewTracker(baseSeconds: 5, maxSeconds: 60);
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++) tracker.RecordFailedAttempt(uuid);

        var remaining = tracker.CheckStatus(uuid).RemainingLockout!.Value;

        Assert.InRange(remaining.TotalSeconds, 4, 5);
    }

    [Fact]
    public void CustomPolicy_NeverExceedsTheGivenMaxSeconds()
    {
        var tracker = NewTracker(baseSeconds: 5, maxSeconds: 60);
        var uuid = Guid.NewGuid().ToString();

        // 連續錯 40 次：沒有上限的話 5 × 2^35 是個荒謬的數字，這裡要驗證它確實被封頂。
        for (var i = 0; i < 40; i++) tracker.RecordFailedAttempt(uuid);

        var remaining = tracker.CheckStatus(uuid).RemainingLockout!.Value;

        Assert.InRange(remaining.TotalSeconds, 59, 60);
    }

    [Fact]
    public void CustomPolicy_StillEscalatesBetweenSuccessiveLockouts()
    {
        // 上限壓低不代表放棄遞增——連續錯得愈多還是等愈久，只是天花板從一小時降到一分鐘。
        var tracker = NewTracker(baseSeconds: 5, maxSeconds: 60);
        var uuid = Guid.NewGuid().ToString();
        for (var i = 0; i < 5; i++) tracker.RecordFailedAttempt(uuid);
        var first = tracker.CheckStatus(uuid).RemainingLockout!.Value;

        tracker.RecordFailedAttempt(uuid);
        var second = tracker.CheckStatus(uuid).RemainingLockout!.Value;

        Assert.True(second > first);
    }
}
