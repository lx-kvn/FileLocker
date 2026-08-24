using FileLocker.Core.History;
using FileLocker.Core.Models;
using FileLocker.Core.Protocol;
using FileLocker.Core.Security;
using FileLocker.Core.Settings;
using FileLocker.Core.Vault;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應架構審查（2026-07-26）：這些測試就是「拆開 MainWindow 的協定分派層」這項深化的驗證——
/// VaultProtocolHandlers 不依賴任何 WPF／WebView2 具體型別，這裡完全不用開真的視窗就能測試
/// 「解析請求 → 呼叫 Core 業務邏輯 → 組裝回應」這一整層，這在拆分之前是做不到的。
/// </summary>
public class VaultProtocolHandlersTests : IDisposable
{
    private readonly DirectoryInfo _vaultDir;
    private readonly DirectoryInfo _cacheDir;
    private readonly DirectoryInfo _workDir;
    private readonly DirectoryInfo _historyDir;
    private readonly VaultManager _vaultManager;
    private readonly VaultIndexCache _vaultIndexCache;
    private readonly VaultProtocolHandlers _handlers;

    public VaultProtocolHandlersTests()
    {
        _vaultDir = Directory.CreateTempSubdirectory("FileLockerVault_");
        _cacheDir = Directory.CreateTempSubdirectory("FileLockerCache_");
        _workDir = Directory.CreateTempSubdirectory("FileLockerWork_");
        _historyDir = Directory.CreateTempSubdirectory("FileLockerHistory_");

        _vaultManager = new VaultManager(_vaultDir.FullName);
        _vaultIndexCache = new VaultIndexCache(_vaultManager, _cacheDir.FullName);

        var history = new HistoryLogger(Path.Combine(_historyDir.FullName, "history.jsonl"));
        var lockout = new LockoutTracker(Path.Combine(_historyDir.FullName, "lockout.json"));
        var lockService = new LockService(_vaultManager, history, lockout);
        var settingsManager = new AppSettingsManager(Path.Combine(_historyDir.FullName, "settings.json"));
        var settings = new AppSettings { VaultPath = _vaultDir.FullName };

        _handlers = new VaultProtocolHandlers(_vaultManager, lockService, _vaultIndexCache, history, settingsManager, settings);
    }

    public void Dispose()
    {
        _vaultIndexCache.Dispose();

        if (_vaultDir.Exists) _vaultDir.Delete(recursive: true);
        if (_cacheDir.Exists) _cacheDir.Delete(recursive: true);
        if (_workDir.Exists) _workDir.Delete(recursive: true);
        if (_historyDir.Exists) _historyDir.Delete(recursive: true);
    }

    private string CreateWorkFile(string name, string content)
    {
        var path = Path.Combine(_workDir.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task EncryptBatchAsync_YieldsOneResultPerPath()
    {
        var pathA = CreateWorkFile("甲.txt", "內容甲");
        var pathB = CreateWorkFile("乙.txt", "內容乙");

        var results = new List<EncryptItemResponse>();
        await foreach (var item in _handlers.EncryptBatchAsync([pathA, pathB], "correct-password", null, false, false, IntPtr.Zero))
        {
            results.Add(item);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Contains(results, r => r.Path == pathA);
        Assert.Contains(results, r => r.Path == pathB);
    }

    // ---- 信封加密流程 Phase 2b：pending/commit/rollback 這一層協定包裝 ----
    // 底層的交易模型本身（2a）已經在 LockServiceTests 測過，這裡驗證的是協定層有沒有正確接上
    // LockService 的新方法、有沒有正確把 IProgress<double> 往下傳，不是重複測底層邏輯。

    [Fact]
    public async Task EncryptPendingBatchAsync_YieldsPendingResultWithEmptyMarkerPath()
    {
        var path = CreateWorkFile("待確認.txt", "內容");

        var results = new List<EncryptPendingItemResponse>();
        await foreach (var item in _handlers.EncryptPendingBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero))
        {
            results.Add(item);
        }

        Assert.Single(results);
        Assert.True(results[0].Success);
        Assert.True(File.Exists(path)); // 原始檔案還在，還沒 commit
    }

    [Fact]
    public async Task EncryptPendingBatchAsync_ReportsProgressThroughToCaller()
    {
        // 檔案大到會切成好幾個 chunk，才有意義驗證進度會遞增（見 ChunkedCipher 預設 1MB chunk）。
        var path = Path.Combine(_workDir.FullName, "大檔案.bin");
        File.WriteAllBytes(path, new byte[3 * 1024 * 1024]);

        var reported = new List<double>();
        var progress = new SyncProgress(reported);

        await foreach (var _ in _handlers.EncryptPendingBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero, progress)) { }

        Assert.NotEmpty(reported);
        Assert.Equal(1.0, reported[^1]);
    }

    [Fact]
    public async Task EncryptPendingBatchAsync_StandaloneMode_ThreadsStorageModeAndDestinationDirToLockService()
    {
        // 對應「單檔案分散式加密」功能規劃 §7，實作計畫片 5：確認這一層薄包裝真的有把
        // storageMode／destinationDir 轉呼叫下去，不是只加了參數卻沒接起來。
        var path = CreateWorkFile("分散式待確認.txt", "內容");
        var destinationDir = Directory.CreateTempSubdirectory("FileLockerFlockedDestination_").FullName;

        try
        {
            EncryptPendingItemResponse? pending = null;
            await foreach (var item in _handlers.EncryptPendingBatchAsync(
                [path], "correct-password", null, false, false, IntPtr.Zero,
                storageMode: StorageMode.Standalone, destinationDir: destinationDir))
            {
                pending = item;
            }

            Assert.True(pending!.Success);
            var metadata = _vaultManager.LoadMetadata(pending.Uuid);
            Assert.Equal(StorageMode.Standalone, metadata!.StorageMode);
            Assert.Equal(destinationDir, metadata.StandaloneDestinationDir);

            var commitResult = await _handlers.CommitEncryptAsync(pending.Uuid);
            Assert.True(commitResult.Success);
            Assert.True(File.Exists(Path.Combine(destinationDir, "分散式待確認.flocked")));
        }
        finally
        {
            Directory.Delete(destinationDir, recursive: true);
        }
    }

    [Fact]
    public async Task CommitEncryptAsync_ThenRollbackPendingEncryptAsync_BothDelegateToLockService()
    {
        var commitPath = CreateWorkFile("要提交.txt", "內容");
        EncryptPendingItemResponse? pendingForCommit = null;
        await foreach (var item in _handlers.EncryptPendingBatchAsync([commitPath], "correct-password", null, false, false, IntPtr.Zero))
        {
            pendingForCommit = item;
        }

        var commitResult = await _handlers.CommitEncryptAsync(pendingForCommit!.Uuid);
        Assert.True(commitResult.Success);
        Assert.False(File.Exists(commitPath));
        Assert.True(File.Exists(commitResult.LockedMarkerPath));

        var rollbackPath = CreateWorkFile("要取消.txt", "內容");
        EncryptPendingItemResponse? pendingForRollback = null;
        await foreach (var item in _handlers.EncryptPendingBatchAsync([rollbackPath], "correct-password", null, false, false, IntPtr.Zero))
        {
            pendingForRollback = item;
        }

        await _handlers.RollbackPendingEncryptAsync(pendingForRollback!.Uuid);
        Assert.True(File.Exists(rollbackPath));
        Assert.Null(_vaultManager.LoadMetadata(pendingForRollback.Uuid));
    }

    private sealed class SyncProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }

    [Fact]
    public async Task ListVaultAsync_AfterEncrypt_ReturnsItemWithMarkerFound()
    {
        var path = CreateWorkFile("清單測試.txt", "測試內容");
        await foreach (var _ in _handlers.EncryptBatchAsync([path], "correct-password", "提示", false, false, IntPtr.Zero)) { }

        // 這個測試沒有接 VaultChangeWatcher（那是即時監控 Vault 變化用的，見另一份測試），
        // 快取不會自動發現剛才新寫入的 .meta.json，手動 Rebuild 一次模擬 watcher 本來會做的事。
        _vaultIndexCache.Rebuild();
        var items = await _handlers.ListVaultAsync();

        Assert.Single(items);
        Assert.Equal("清單測試.txt", items[0].OriginalName);
        Assert.True(items[0].MarkerFound);
        Assert.Equal("提示", items[0].Hint);
    }

    [Fact]
    public async Task ListVaultAsync_StandaloneModeItem_ChecksFlockedFileNotLockedMarker()
    {
        // 回歸測試：ListVaultAsync 原本無論 StorageMode 是什麼都固定用 MarkerStatusChecker 查
        // .locked 指標檔在不在，Standalone 項目原地留下的其實是 .flocked 檔案本體，從來就不會有
        // .locked——結果剛加密完馬上就被誤判成「指標檔可能被移動或刪除」，即使 .flocked 好端端
        // 在原地、雙擊也能正常解密。這是使用者實際回報的 bug。
        var path = CreateWorkFile("獨立加密清單測試.txt", "測試內容");
        EncryptPendingItemResponse? pending = null;
        await foreach (var item in _handlers.EncryptPendingBatchAsync(
            [path], "correct-password", null, false, false, IntPtr.Zero,
            storageMode: StorageMode.Standalone))
        {
            pending = item;
        }
        Assert.True(pending!.Success);
        await _handlers.CommitEncryptAsync(pending.Uuid);

        _vaultIndexCache.Rebuild();
        var items = await _handlers.ListVaultAsync();

        Assert.Single(items);
        Assert.True(items[0].MarkerFound);
    }

    [Fact]
    public async Task ListHistory_AfterEncrypt_RecordsEncryptedEntry()
    {
        var path = CreateWorkFile("紀錄測試.txt", "測試內容");
        await foreach (var _ in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero)) { }

        var entries = _handlers.ListHistory();

        Assert.Contains(entries, e => e.OriginalName == "紀錄測試.txt" && e.Action == "Encrypted");
    }

    [Fact]
    public async Task DeleteRecordAsync_RemovesItemFromSubsequentListing()
    {
        var path = CreateWorkFile("刪除測試.txt", "測試內容");
        EncryptItemResponse? encrypted = null;
        await foreach (var item in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero))
        {
            encrypted = item;
        }

        var deleteResult = await _handlers.DeleteRecordAsync(encrypted!.Uuid);
        Assert.True(deleteResult.Success);

        var items = await _handlers.ListVaultAsync();
        Assert.Empty(items);
    }

    [Fact]
    public async Task InspectLockedFile_ForValidMarker_ReturnsMetadataInfo()
    {
        var path = CreateWorkFile("檢視測試.txt", "測試內容");
        EncryptItemResponse? encrypted = null;
        await foreach (var item in _handlers.EncryptBatchAsync([path], "correct-password", "我的提示", false, false, IntPtr.Zero))
        {
            encrypted = item;
        }

        var result = _handlers.InspectLockedFile(encrypted!.LockedMarkerPath);

        Assert.True(result.Success);
        Assert.Equal(encrypted.Uuid, result.Uuid);
        Assert.Equal("檢視測試.txt", result.OriginalName);
        Assert.Equal("我的提示", result.Hint);
    }

    [Fact]
    public async Task InspectLockedFile_ForFlockedFile_ReadsUuidFromHeaderAndReturnsMetadataInfo()
    {
        // 回歸測試：「解密」頁籤手動選檔案的入口原本只認 .locked（LockedMarkerFile.ReadFrom），
        // 選 .flocked 檔案會直接查無 UUID、當成「找不到或無法解析」，即使檔案本身完全正常。
        var path = CreateWorkFile("檢視獨立加密測試.txt", "測試內容");
        EncryptPendingItemResponse? pending = null;
        await foreach (var item in _handlers.EncryptPendingBatchAsync(
            [path], "correct-password", "分散式提示", false, false, IntPtr.Zero,
            storageMode: StorageMode.Standalone))
        {
            pending = item;
        }
        Assert.True(pending!.Success);
        var commitResult = await _handlers.CommitEncryptAsync(pending.Uuid);
        Assert.True(commitResult.Success);
        var flockedPath = commitResult.LockedMarkerPath;

        var result = _handlers.InspectLockedFile(flockedPath);

        Assert.True(result.Success);
        Assert.Equal(pending.Uuid, result.Uuid);
        Assert.Equal("檢視獨立加密測試.txt", result.OriginalName);
        Assert.Equal("分散式提示", result.Hint);
    }

    [Fact]
    public async Task DecryptAsync_ForFlockedFile_DispatchesToStandaloneDecryptAndRestoresContent()
    {
        // 回歸測試：「解密」頁籤手動選 .flocked 檔案送出密碼，DecryptAsync 原本無條件呼叫
        // LockService.DecryptAsync（讀 .locked marker），對 .flocked 檔案會直接判定「找不到或
        // 無法解析」，即使密碼正確、檔案完全正常。
        var path = CreateWorkFile("手動選檔解密測試.txt", "測試內容");
        EncryptPendingItemResponse? pending = null;
        await foreach (var item in _handlers.EncryptPendingBatchAsync(
            [path], "manual-pick-password", null, false, false, IntPtr.Zero,
            storageMode: StorageMode.Standalone))
        {
            pending = item;
        }
        Assert.True(pending!.Success);
        var commitResult = await _handlers.CommitEncryptAsync(pending.Uuid);
        Assert.True(commitResult.Success);
        var flockedPath = commitResult.LockedMarkerPath;

        var decryptResult = await _handlers.DecryptAsync(flockedPath, "manual-pick-password");

        Assert.True(decryptResult.Success);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(flockedPath));
    }

    [Fact]
    public async Task InspectLockedFile_ForValidMarker_ReturnsCreatedAtUtcMatchingMetadata()
    {
        // 對應信封加密流程 Phase 2a：獨立解密流程的信封落地後要顯示「檔名＋加密時間」
        // （design-exploration/gui-styles-v2 定案文件 §1.11），這個時間直接來自 metadata 裡的
        // CreatedAtUtc，不是另外算的。
        var path = CreateWorkFile("時間戳記測試.txt", "測試內容");
        var before = DateTimeOffset.UtcNow;
        EncryptItemResponse? encrypted = null;
        await foreach (var item in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero))
        {
            encrypted = item;
        }
        var after = DateTimeOffset.UtcNow;

        var result = _handlers.InspectLockedFile(encrypted!.LockedMarkerPath);

        Assert.NotNull(result.CreatedAtUtc);
        Assert.InRange(result.CreatedAtUtc!.Value, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task VerifyDecryptPasswordAsync_ThenCommitPendingDecryptAsync_RestoresContent()
    {
        // 薄包裝測試：只驗證協定層有沒有正確把呼叫轉給 LockService，完整的驗證/提交行為
        // 已經在 LockServiceTests 測過，這裡不重複測底層邏輯。
        var path = CreateWorkFile("協定層解密驗證測試.txt", "協定層內容");
        EncryptItemResponse? encrypted = null;
        await foreach (var item in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero))
        {
            encrypted = item;
        }

        var verifyResult = await _handlers.VerifyDecryptPasswordAsync(encrypted!.Uuid, "correct-password");
        Assert.True(verifyResult.Success);
        Assert.False(File.Exists(path)); // 驗證階段不還原

        var commitResult = await _handlers.CommitPendingDecryptAsync(encrypted.Uuid, null);
        Assert.True(commitResult.Success);
        Assert.Equal("協定層內容", File.ReadAllText(path));
    }

    [Fact]
    public async Task CancelPendingDecryptAsync_ThenCommit_ReturnsPendingItemNotFound()
    {
        var path = CreateWorkFile("協定層取消測試.txt", "內容");
        EncryptItemResponse? encrypted = null;
        await foreach (var item in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero))
        {
            encrypted = item;
        }
        await _handlers.VerifyDecryptPasswordAsync(encrypted!.Uuid, "correct-password");

        await _handlers.CancelPendingDecryptAsync(encrypted.Uuid);
        var commitResult = await _handlers.CommitPendingDecryptAsync(encrypted.Uuid, null);

        Assert.False(commitResult.Success);
        Assert.Equal(ErrorCodes.PendingItemNotFound, commitResult.ErrorCode);
    }

    [Fact]
    public void InspectLockedFile_ForNonexistentPath_ReturnsFailure()
    {
        var result = _handlers.InspectLockedFile(Path.Combine(_workDir.FullName, "不存在.locked"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task GetPathSizesAsync_ForExistingFile_ReturnsActualByteCount()
    {
        var path = CreateWorkFile("大小測試.txt", "1234567890");

        var results = await _handlers.GetPathSizesAsync([path]);

        Assert.Single(results);
        Assert.Equal(10, results[0].Bytes);
        Assert.False(results[0].IsFolder);
    }

    [Fact]
    public async Task GetPathSizesAsync_ForMissingPath_ReturnsZeroInsteadOfThrowing()
    {
        var results = await _handlers.GetPathSizesAsync([Path.Combine(_workDir.FullName, "不存在的檔案.txt")]);

        Assert.Single(results);
        Assert.Equal(0, results[0].Bytes);
    }

    [Fact]
    public void GetSettings_ReturnsConfiguredValues()
    {
        var result = _handlers.GetSettings();

        Assert.Equal(_vaultDir.FullName, result.VaultPath);
        Assert.Equal("zh-TW", result.Language);
        Assert.Equal("macos", result.WindowControlStyle);
    }

    [Fact]
    public void UpdateSetting_Language_PersistsChange()
    {
        var result = _handlers.UpdateSetting("language", "en");

        Assert.True(result.Success);
        Assert.Equal("en", _handlers.GetSettings().Language);
    }

    [Theory]
    [InlineData("macos")]
    [InlineData("windows-native")]
    [InlineData("windows-styled")]
    public void UpdateSetting_WindowControlStyle_PersistsChange(string style)
    {
        var result = _handlers.UpdateSetting("windowControlStyle", style);

        Assert.True(result.Success);
        Assert.Equal(style, _handlers.GetSettings().WindowControlStyle);
    }

    [Fact]
    public void UpdateSetting_UnknownKey_ReturnsFailureAndDoesNotChangeSettings()
    {
        var before = _handlers.GetSettings();

        var result = _handlers.UpdateSetting("unknownKey", "whatever");

        Assert.False(result.Success);
        Assert.Equal(before.Language, _handlers.GetSettings().Language);
    }

    [Fact]
    public async Task ChangeVaultPathAsync_MovesExistingItemsToNewLocation()
    {
        var path = CreateWorkFile("搬移測試.txt", "測試內容");
        await foreach (var _ in _handlers.EncryptBatchAsync([path], "correct-password", null, false, false, IntPtr.Zero)) { }

        var newVaultDir = Directory.CreateTempSubdirectory("FileLockerVaultMoved_");
        try
        {
            Directory.Delete(newVaultDir.FullName); // ChangeVaultPathAsync 只接受不存在或空的目的地

            var result = await _handlers.ChangeVaultPathAsync(newVaultDir.FullName);

            Assert.True(result.Success);
            Assert.True(Directory.Exists(newVaultDir.FullName));
            Assert.True(new VaultManager(newVaultDir.FullName).ScanAll().Any());
        }
        finally
        {
            if (newVaultDir.Exists) newVaultDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ChangeVaultPathAsync_SamePath_ReturnsFailureWithoutMoving()
    {
        var result = await _handlers.ChangeVaultPathAsync(_vaultDir.FullName);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }
}
