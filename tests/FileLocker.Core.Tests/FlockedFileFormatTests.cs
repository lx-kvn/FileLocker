using FileLocker.Core;
using Xunit;

namespace FileLocker.Core.Tests;

/// <summary>
/// 對應「單檔案分散式加密」功能規劃 §4：`.flocked` 檔案本身就是完整密文，沒有 Vault 可以指過去，
/// 但 FolderArchiver 掃描巢狀鎖定項目時需要能識別出裡面的 UUID（見 §4 點 2）——完整密文沒辦法
/// 直接讀出 UUID，所以在密文串流前面加一段非加密的小 header（magic bytes＋版本號＋UUID）。
/// UUID 明碼是可接受的（`.locked` 指標檔本身也是明碼 UUID＋簽章，同一個資安假設：UUID 本來
/// 就不是機密，真正的機密是加密金鑰跟密碼）。
/// </summary>
public class FlockedFileFormatTests
{
    [Fact]
    public void WriteHeader_ThenTryReadHeader_RoundTripsUuid()
    {
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();

        FlockedFileFormat.WriteHeader(stream, uuid);
        stream.Position = 0;

        var success = FlockedFileFormat.TryReadHeader(stream, out var readUuid);

        Assert.True(success);
        Assert.Equal(uuid, readUuid);
    }

    [Fact]
    public void TryReadHeader_LeavesStreamPositionedRightAfterHeader()
    {
        // 密文串流緊接在 header 後面（ChunkedCipher.EncryptStream 的輸出），TryReadHeader
        // 讀完 header 後，呼叫端要能直接從目前的 stream 位置繼續餵給 ChunkedCipher.DecryptStream，
        // 不需要另外算 offset。
        var uuid = Guid.NewGuid().ToString();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        stream.Write(payload);
        stream.Position = 0;

        FlockedFileFormat.TryReadHeader(stream, out _);
        var remaining = new byte[payload.Length];
        var bytesRead = stream.Read(remaining, 0, remaining.Length);

        Assert.Equal(payload.Length, bytesRead);
        Assert.Equal(payload, remaining);
    }

    [Fact]
    public void TryReadHeader_WrongMagicBytes_ReturnsFalseInsteadOfThrowing()
    {
        // 模擬使用者把一個普通檔案改副檔名成 .flocked（或檔案已經損毀）——不是合法的
        // FileLocker 輸出，不該丟例外炸掉呼叫端，比照 LockedMarkerFile.ReadFrom 的既有慣例，
        // 一律回傳 false 讓呼叫端自己決定要顯示什麼錯誤訊息。
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44 });
        stream.Position = 0;

        var success = FlockedFileFormat.TryReadHeader(stream, out var uuid);

        Assert.False(success);
        Assert.Null(uuid);
    }

    [Fact]
    public void TryReadHeader_TruncatedStream_ReturnsFalseInsteadOfThrowing()
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 0x46, 0x4C, 0x4B, 0x44 }); // 只寫 magic bytes，header 沒寫完
        stream.Position = 0;

        var success = FlockedFileFormat.TryReadHeader(stream, out var uuid);

        Assert.False(success);
        Assert.Null(uuid);
    }

    [Fact]
    public void TryReadHeader_UnsupportedVersion_ReturnsFalseInsteadOfThrowing()
    {
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        var bytes = stream.ToArray();
        bytes[4] = 0xFF; // 版本號欄位改成一個不存在的版本
        using var corrupted = new MemoryStream(bytes);

        var success = FlockedFileFormat.TryReadHeader(corrupted, out var readUuid);

        Assert.False(success);
        Assert.Null(readUuid);
    }

    [Fact]
    public void TryReadHeader_DeclaredHeaderLengthLargerThanCurrent_SkipsExtraReservedBytesAndStillReadsUuid()
    {
        // 對應延展性設計：以後如果保留欄位塞了真的用得到的東西，宣告的 Header 長度會比目前
        // 這個版本寫死的長度長。這裡手動構造一個「假裝來自未來、保留欄位變長」的 header，
        // 驗證讀取端是照宣告長度整段吃掉，不是寫死目前的常數，多出來的部分正確被當成
        // 無法辨識的欄位捨棄，UUID 還是讀得出來，stream 位置也正確停在密文開頭（不會把
        // 多出來的保留欄位誤當成密文的一部分）。
        var uuid = Guid.NewGuid().ToString();
        using var originalStream = new MemoryStream();
        FlockedFileFormat.WriteHeader(originalStream, uuid);
        var original = originalStream.ToArray();

        const int extraBytes = 6;
        var currentHeaderLength = (original[5] << 8) | original[6];
        var newHeaderLength = (ushort)(currentHeaderLength + extraBytes);

        var expanded = new byte[original.Length + extraBytes];
        Array.Copy(original, expanded, original.Length);
        expanded[5] = (byte)(newHeaderLength >> 8);
        expanded[6] = (byte)(newHeaderLength & 0xFF);
        // 多出來的 extraBytes 保持全 0（模擬未來版本的保留欄位，目前程式碼不認得也不需要認得）。

        var fakePlaintextAfterHeader = new byte[] { 7, 8, 9 };
        var withExtraFieldAndContent = expanded.Concat(fakePlaintextAfterHeader).ToArray();
        using var stream = new MemoryStream(withExtraFieldAndContent);

        var success = FlockedFileFormat.TryReadHeader(stream, out var readUuid);
        var remaining = new byte[fakePlaintextAfterHeader.Length];
        var remainingBytesRead = stream.Read(remaining, 0, remaining.Length);

        Assert.True(success);
        Assert.Equal(uuid, readUuid);
        Assert.Equal(fakePlaintextAfterHeader.Length, remainingBytesRead);
        Assert.Equal(fakePlaintextAfterHeader, remaining);
    }

    [Fact]
    public void TryReadHeader_DeclaredHeaderLengthTooShortForUuid_ReturnsFalse()
    {
        // 宣告的長度連 UUID 都塞不下，代表 header 本身已經不可信（損毀或惡意構造），
        // 不該嘗試繼續解析、更不該讀到超出宣告範圍的資料當成 UUID。
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        var bytes = stream.ToArray();
        bytes[5] = 0x00;
        bytes[6] = 0x05; // Header 長度改成 5（比 Magic+版本+長度欄位本身還短）
        using var corrupted = new MemoryStream(bytes);

        var success = FlockedFileFormat.TryReadHeader(corrupted, out var readUuid);

        Assert.False(success);
        Assert.Null(readUuid);
    }
}
