using System.Security.Cryptography;
using System.Text;
using FileLocker.Core.Crypto;
using Xunit;

namespace FileLocker.Core.Tests;

public class ChunkedCipherTests
{
    [Fact]
    public void EncryptStream_ThenDecryptStream_SingleChunk_RoundTripsContent()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var original = Encoding.UTF8.GetBytes("這份內容小於一個 chunk，應該只會產生一個區塊。");

        using var plaintextInput = new MemoryStream(original);
        using var ciphertextStream = new MemoryStream();
        ChunkedCipher.EncryptStream(key, plaintextInput, ciphertextStream);

        ciphertextStream.Position = 0;
        using var plaintextOutput = new MemoryStream();
        ChunkedCipher.DecryptStream(key, ciphertextStream, plaintextOutput);

        Assert.Equal(original, plaintextOutput.ToArray());
    }

    [Fact]
    public void EncryptStream_ThenDecryptStream_MultipleChunks_RoundTripsContent()
    {
        // 用很小的 chunk size 強迫切成很多塊，不用真的產生一個很大的檔案就能驗證多區塊邏輯。
        var key = RandomNumberGenerator.GetBytes(32);
        var random = new Random(12345);
        var original = new byte[10_000];
        random.NextBytes(original);

        using var plaintextInput = new MemoryStream(original);
        using var ciphertextStream = new MemoryStream();
        ChunkedCipher.EncryptStream(key, plaintextInput, ciphertextStream, chunkSizeBytes: 777); // 刻意選一個不會整除的怪數字

        ciphertextStream.Position = 0;
        using var plaintextOutput = new MemoryStream();
        ChunkedCipher.DecryptStream(key, ciphertextStream, plaintextOutput);

        Assert.Equal(original, plaintextOutput.ToArray());
    }

    [Fact]
    public void EncryptStream_EmptyInput_ProducesEmptyOutputAndRoundTrips()
    {
        var key = RandomNumberGenerator.GetBytes(32);

        using var plaintextInput = new MemoryStream(Array.Empty<byte>());
        using var ciphertextStream = new MemoryStream();
        ChunkedCipher.EncryptStream(key, plaintextInput, ciphertextStream);

        Assert.Equal(0, ciphertextStream.Length);

        ciphertextStream.Position = 0;
        using var plaintextOutput = new MemoryStream();
        ChunkedCipher.DecryptStream(key, ciphertextStream, plaintextOutput);

        Assert.Equal(0, plaintextOutput.Length);
    }

    [Fact]
    public void DecryptStream_WithWrongKey_ThrowsCryptographicException()
    {
        var correctKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var original = Encoding.UTF8.GetBytes("secret content");

        using var plaintextInput = new MemoryStream(original);
        using var ciphertextStream = new MemoryStream();
        ChunkedCipher.EncryptStream(correctKey, plaintextInput, ciphertextStream);

        ciphertextStream.Position = 0;
        using var plaintextOutput = new MemoryStream();
        Assert.ThrowsAny<CryptographicException>(() => ChunkedCipher.DecryptStream(wrongKey, ciphertextStream, plaintextOutput));
    }

    [Fact]
    public void DecryptStream_WithTruncatedStream_ThrowsInvalidDataException()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var original = Encoding.UTF8.GetBytes("這段內容夠長，確保加密後的密文串流本身也有一定長度可以被截斷。");

        using var plaintextInput = new MemoryStream(original);
        using var fullCiphertext = new MemoryStream();
        ChunkedCipher.EncryptStream(key, plaintextInput, fullCiphertext);

        // 模擬寫入中斷：只留一半的密文內容。
        var truncatedBytes = fullCiphertext.ToArray()[..(int)(fullCiphertext.Length / 2)];
        using var truncatedStream = new MemoryStream(truncatedBytes);
        using var plaintextOutput = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => ChunkedCipher.DecryptStream(key, truncatedStream, plaintextOutput));
    }

    // ---- 信封加密流程 Phase 2a：真實進度回報 ----

    [Fact]
    public void EncryptStream_MultipleChunks_ReportsIncreasingProgressEndingAtOne()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var random = new Random(54321);
        var original = new byte[10_000];
        random.NextBytes(original);

        var reported = new List<double>();
        var progress = new Progress<double>(value => reported.Add(value));
        // Progress<T> 預設用 SynchronizationContext 非同步排程回呼——測試環境沒有真正的
        // UI 訊息迴圈可以幫忙把回呼排程執行，這裡直接用 IProgress<double> 介面型別呼叫，
        // 繞過 Progress<T> 那層排程，讓 .Report() 同步、立刻反映到 reported 清單裡。
        IProgress<double> syncProgress = new SyncProgress(reported);

        using var plaintextInput = new MemoryStream(original);
        using var ciphertextStream = new MemoryStream();
        ChunkedCipher.EncryptStream(key, plaintextInput, ciphertextStream, chunkSizeBytes: 777, progress: syncProgress, totalBytes: original.Length);

        Assert.NotEmpty(reported);
        Assert.Equal(1.0, reported[^1]);
        for (var i = 1; i < reported.Count; i++)
        {
            Assert.True(reported[i] >= reported[i - 1]); // 單調遞增，不會忽大忽小
        }
    }

    [Fact]
    public void EncryptStream_TotalBytesZeroOrNull_DoesNotReportAndDoesNotThrow()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var reported = new List<double>();
        IProgress<double> syncProgress = new SyncProgress(reported);

        using var emptyInput = new MemoryStream(Array.Empty<byte>());
        using var ciphertextStream = new MemoryStream();

        var exception = Record.Exception(() =>
            ChunkedCipher.EncryptStream(key, emptyInput, ciphertextStream, progress: syncProgress, totalBytes: 0));

        Assert.Null(exception);
        Assert.Empty(reported);
    }

    private sealed class SyncProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }

    // ---- 解密也回報真實進度（通盤檢討改善計畫第 4 輪）----
    //
    // 改版前只有加密這一半接了進度回報，解密的 DecryptStream 連參數都沒有——同一個工具裡
    // 加密看得到百分比、解密只能乾等。totalBytes 用的是「明文總位元組數」，跟加密那一半同一個
    // 語意（解密時呼叫端手上就有這個數字，它存在 metadata 的 OriginalSizeBytes 欄位）。

    [Fact]
    public void DecryptStream_MultipleChunks_ReportsIncreasingProgressEndingAtOne()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var original = new byte[777 * 3 + 100];
        RandomNumberGenerator.Fill(original);

        using var ciphertextStream = new MemoryStream();
        using (var plaintextInput = new MemoryStream(original))
        {
            ChunkedCipher.EncryptStream(key, plaintextInput, ciphertextStream, chunkSizeBytes: 777);
        }
        ciphertextStream.Position = 0;

        var reported = new List<double>();
        IProgress<double> syncProgress = new SyncProgress(reported);
        using var plaintextOutput = new MemoryStream();

        ChunkedCipher.DecryptStream(key, ciphertextStream, plaintextOutput, syncProgress, original.Length);

        Assert.Equal(original, plaintextOutput.ToArray());
        Assert.NotEmpty(reported);
        Assert.Equal(reported.OrderBy(v => v), reported); // 只會往前，不會倒退
        Assert.All(reported, v => Assert.InRange(v, 0.0, 1.0));
        Assert.Equal(1.0, reported[^1], 6);
    }

    [Fact]
    public void DecryptStream_WithoutProgress_StillDecryptsCorrectly()
    {
        // 兩個新參數都是選填的，既有呼叫端（沒有進度需求的路徑）不用跟著改。
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var original = new byte[5000];
        RandomNumberGenerator.Fill(original);

        using var ciphertextStream = new MemoryStream();
        using (var plaintextInput = new MemoryStream(original))
        {
            ChunkedCipher.EncryptStream(key, plaintextInput, ciphertextStream);
        }
        ciphertextStream.Position = 0;

        using var plaintextOutput = new MemoryStream();
        ChunkedCipher.DecryptStream(key, ciphertextStream, plaintextOutput);

        Assert.Equal(original, plaintextOutput.ToArray());
    }

    [Fact]
    public void DecryptStream_ZeroTotalBytes_SkipsReportingWithoutDividingByZero()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);

        using var ciphertextStream = new MemoryStream();
        using (var emptyInput = new MemoryStream(Array.Empty<byte>()))
        {
            ChunkedCipher.EncryptStream(key, emptyInput, ciphertextStream);
        }
        ciphertextStream.Position = 0;

        var reported = new List<double>();
        IProgress<double> syncProgress = new SyncProgress(reported);
        using var plaintextOutput = new MemoryStream();

        ChunkedCipher.DecryptStream(key, ciphertextStream, plaintextOutput, syncProgress, 0);

        Assert.Empty(reported);
    }
}
