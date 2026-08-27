namespace FileLocker.Core.Io;

/// <summary>
/// 唯讀包裝：從底層串流「目前的位置」開始，最多只讓呼叫端讀到指定的位元組數，之後一律回報
/// 串流已結束。
///
/// 存在的理由是 `.flocked` v2 的檔案結構（見 <see cref="FlockedFileFormat"/>）：解密所需的
/// 驗證材料放在密文之後的檔尾，所以密文不再是「header 之後一路到檔案結束」。
/// ChunkedCipher.DecryptStream 沒有長度參數、是讀到串流結束為止的，如果直接把整個檔案交給它，
/// 它會把檔尾 metadata 的開頭四個位元組當成下一個區塊的長度前綴去解析，變成一個很難懂的
/// 「內容損毀」錯誤。
///
/// 不改 ChunkedCipher 自己去收一個長度參數，是因為「密文到哪裡結束」是 `.flocked` 這個容器
/// 格式的問題，不是分塊加密演算法的問題——ChunkedCipher 也被 Vault 模式（整個 .enc 檔案就是
/// 密文）使用，那邊沒有這個概念。用一層薄包裝把差異擋在容器這一側。
/// </summary>
public sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private long _position;

    public BoundedReadStream(Stream inner, long length)
    {
        _inner = inner;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    /// <summary>回報的是「這段密文有多長」，不是底層檔案有多長。</summary>
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, remaining);
        var read = _inner.Read(buffer, offset, toRead);
        _position += read;
        return read;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// 底層串流一併關閉——呼叫端是用 using 包住這個包裝物件的，如果底層的檔案控制代碼沒跟著
    /// 關掉，解密完成後接著要刪除或搬移那個檔案時會被自己鎖住。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
