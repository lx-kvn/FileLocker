using FileLocker.Core.Io;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應通盤檢討改善計畫第 2 輪：`.flocked` v2 把解密所需的驗證材料放在檔尾，於是密文不再是
/// 「從 header 之後一路到檔案結束」——ChunkedCipher.DecryptStream 是讀到串流結束為止的，
/// 沒有把範圍框住的話，它會把檔尾 metadata 的開頭當成下一個區塊的長度前綴去解析。
/// 這個包裝負責讓底層檔案在指定的位元組數之後就回報結束。
/// </summary>
public class BoundedReadStreamTests
{
    private static MemoryStream SourceOf(params byte[] bytes) => new(bytes);

    [Fact]
    public void Read_StopsAtDeclaredLength_EvenThoughInnerStreamHasMore()
    {
        var inner = SourceOf(1, 2, 3, 4, 5, 6, 7, 8);
        using var bounded = new BoundedReadStream(inner, 5);

        var buffer = new byte[100];
        var read = bounded.Read(buffer, 0, buffer.Length);

        Assert.Equal(5, read);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer[..5]);
        Assert.Equal(0, bounded.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Read_AcrossMultipleCalls_TotalNeverExceedsDeclaredLength()
    {
        var inner = SourceOf(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        using var bounded = new BoundedReadStream(inner, 7);

        var buffer = new byte[3];
        var total = 0;
        int read;
        while ((read = bounded.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
        }

        Assert.Equal(7, total);
    }

    [Fact]
    public void Read_StartsFromInnerStreamCurrentPosition_NotFromTheBeginning()
    {
        // 實際用法就是這樣：呼叫端先讀掉 header，才把剩下的交給這個包裝，
        // 不能自作主張把底層串流倒回開頭。
        var inner = SourceOf(9, 9, 1, 2, 3, 4);
        inner.Position = 2;
        using var bounded = new BoundedReadStream(inner, 3);

        var buffer = new byte[10];
        var read = bounded.Read(buffer, 0, buffer.Length);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..3]);
    }

    [Fact]
    public void Length_ReportsDeclaredLength_NotInnerStreamLength()
    {
        var inner = SourceOf(1, 2, 3, 4, 5, 6, 7, 8);
        using var bounded = new BoundedReadStream(inner, 5);

        Assert.Equal(5, bounded.Length);
    }

    [Fact]
    public void Dispose_AlsoDisposesInnerStream()
    {
        // 呼叫端用 using 包住這個物件，底層那份檔案控制代碼必須跟著關掉，
        // 否則解密完成後接著要刪除／搬移那個檔案時會被自己鎖住。
        var inner = SourceOf(1, 2, 3);
        var bounded = new BoundedReadStream(inner, 2);

        bounded.Dispose();

        Assert.Throws<ObjectDisposedException>(() => inner.ReadByte());
    }

    [Fact]
    public void ZeroLength_ReportsEndOfStreamImmediately()
    {
        var inner = SourceOf(1, 2, 3);
        using var bounded = new BoundedReadStream(inner, 0);

        Assert.Equal(0, bounded.Read(new byte[10], 0, 10));
    }
}
