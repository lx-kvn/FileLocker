using FileLocker.Core;
using FileLocker.Core.Models;
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

    // ---- v2：把解密所需的驗證材料嵌進檔案本身（通盤檢討改善計畫第 2 輪）----
    //
    // v1 的 header 只有 UUID，解密時鹽值／Argon2 參數／密碼驗證雜湊全部要回 Vault 查
    // {uuid}.meta.json——也就是說 .flocked 檔案並不是它宣稱的「獨立可攜」：換一台裝置打不開，
    // Vault 遺失或重建之後所有既存的 .flocked 也一起打不開。v2 把這份 metadata 附進檔案本身。
    //
    // metadata 放在「檔尾」而不是接在 header 後面，是因為寫入時機對不上：header 在加密開始
    // 之前就要寫（密文緊接在它後面），但 Passkey／恢復金鑰的包裝金鑰是加密完成之後才產生的。
    // 放檔尾的話，commit 階段直接 append 再 File.Move 即可，不需要把整份密文重讀重寫一次
    // （大型項目差別很大）。

    private static LockedItemMetadata SampleMetadata(string uuid) => new()
    {
        Uuid = uuid,
        OriginalName = "機密報告.txt",
        OriginalPath = @"C:\Users\someone\Documents\機密報告.txt",
        PasswordVerificationHash = Convert.ToBase64String(new byte[32]),
        Salt = Convert.ToBase64String(new byte[16]),
        Argon2TimeCost = 3,
        Argon2MemoryCostKb = 65536,
        Argon2Parallelism = 2,
        Hint = "生日",
        Type = ItemType.File,
        OriginalSizeBytes = 1234,
        StorageMode = StorageMode.Standalone,
    };

    [Fact]
    public void AppendMetadataTrailer_ThenTryReadLayout_RoundTripsVerificationMaterial()
    {
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        stream.Write(new byte[] { 9, 9, 9 }); // 假裝是密文
        FlockedFileFormat.AppendMetadataTrailer(stream, SampleMetadata(uuid));
        stream.Position = 0;

        var success = FlockedFileFormat.TryReadLayout(stream, out var layout);

        Assert.True(success);
        Assert.Equal(uuid, layout!.Uuid);
        Assert.NotNull(layout.EmbeddedMetadata);
        Assert.Equal("機密報告.txt", layout.EmbeddedMetadata!.OriginalName);
        Assert.Equal(Convert.ToBase64String(new byte[16]), layout.EmbeddedMetadata.Salt);
        Assert.Equal(Convert.ToBase64String(new byte[32]), layout.EmbeddedMetadata.PasswordVerificationHash);
        Assert.Equal(3, layout.EmbeddedMetadata.Argon2TimeCost);
        Assert.Equal(65536, layout.EmbeddedMetadata.Argon2MemoryCostKb);
        Assert.Equal(2, layout.EmbeddedMetadata.Argon2Parallelism);
        Assert.Equal("生日", layout.EmbeddedMetadata.Hint);
        Assert.Equal(ItemType.File, layout.EmbeddedMetadata.Type);
    }

    [Fact]
    public void AppendMetadataTrailer_DoesNotEmbedOriginalPath()
    {
        // 原始完整路徑對解密沒有任何作用（還原位置是看 .flocked 檔案現在放在哪），留著只會讓
        // 這顆「設計上就是要拿給別人／帶去別台裝置」的檔案順便洩漏使用者的資料夾結構。
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        FlockedFileFormat.AppendMetadataTrailer(stream, SampleMetadata(uuid));

        var rawText = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("someone", rawText);
        Assert.DoesNotContain("Documents", rawText);

        stream.Position = 0;
        FlockedFileFormat.TryReadLayout(stream, out var layout);
        Assert.True(string.IsNullOrEmpty(layout!.EmbeddedMetadata!.OriginalPath));
    }

    [Fact]
    public void AppendMetadataTrailer_EmbedsRecoveryKeyWrappedContentKey()
    {
        // 恢復金鑰是純資料、不綁裝置，是「密碼忘了」時唯一的救命繩——它必須跟著檔案走，
        // 否則帶到另一台裝置就只剩密碼一條路，等於少了一個宣稱有提供的解鎖方式。
        var uuid = Guid.NewGuid().ToString();
        var metadata = SampleMetadata(uuid);
        metadata.RecoveryKeyEnabled = true;
        metadata.RecoveryKeyWrappedContentKey = "wrapped-recovery-key-base64";

        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        FlockedFileFormat.AppendMetadataTrailer(stream, metadata);
        stream.Position = 0;

        FlockedFileFormat.TryReadLayout(stream, out var layout);

        Assert.True(layout!.EmbeddedMetadata!.RecoveryKeyEnabled);
        Assert.Equal("wrapped-recovery-key-base64", layout.EmbeddedMetadata.RecoveryKeyWrappedContentKey);
    }

    [Fact]
    public void TryReadLayout_ReportsCiphertextLengthExcludingTrailer()
    {
        // ChunkedCipher.DecryptStream 是讀到串流結束為止的——檔尾多了 metadata 區塊之後，
        // 一定要把密文的範圍框出來，否則它會把 metadata 當成下一個區塊的長度前綴去解析。
        var uuid = Guid.NewGuid().ToString();
        var ciphertext = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        stream.Write(ciphertext);
        FlockedFileFormat.AppendMetadataTrailer(stream, SampleMetadata(uuid));
        stream.Position = 0;

        FlockedFileFormat.TryReadLayout(stream, out var layout);

        Assert.Equal(ciphertext.Length, layout!.CiphertextLength);
    }

    [Fact]
    public void TryReadLayout_WithoutTrailer_ReportsNoMetadataAndCiphertextToEndOfStream()
    {
        // Pending 階段的檔案（還在 Vault 裡當暫存密文，commit 才會補上檔尾）走的就是這條路，
        // 此時 Vault 一定查得到 metadata，退回原本的行為是正確的。
        var uuid = Guid.NewGuid().ToString();
        var ciphertext = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        stream.Write(ciphertext);
        stream.Position = 0;

        var success = FlockedFileFormat.TryReadLayout(stream, out var layout);

        Assert.True(success);
        Assert.Null(layout!.EmbeddedMetadata);
        Assert.Equal(ciphertext.Length, layout.CiphertextLength);
    }

    [Fact]
    public void TryReadLayout_TrailerMagicCorrupted_FallsBackToNoEmbeddedMetadata()
    {
        // 檔尾被截斷／改寫時不猜測，一律當作沒有嵌入 metadata——退回查 Vault 這條既有路徑，
        // 至少在原本那台裝置上還救得回來，比直接判定整個檔案損毀好。
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        stream.Write(new byte[] { 1, 2, 3 });
        FlockedFileFormat.AppendMetadataTrailer(stream, SampleMetadata(uuid));
        var bytes = stream.ToArray();
        bytes[^1] ^= 0xFF; // 破壞檔尾 magic 的最後一個位元組
        using var corrupted = new MemoryStream(bytes);

        var success = FlockedFileFormat.TryReadLayout(corrupted, out var layout);

        Assert.True(success);
        Assert.Null(layout!.EmbeddedMetadata);
    }

    [Fact]
    public void TryReadHeader_Version1File_StillReadable()
    {
        // v1 檔案（沒有檔尾 metadata）在使用者磁碟上已經存在，升版之後仍然要讀得出 UUID、
        // 走回查 Vault 的既有解密路徑，不能因為版本號不是最新就整份拒絕。
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        var bytes = stream.ToArray();
        bytes[4] = 1; // 版本號改回 1
        using var v1Stream = new MemoryStream(bytes);

        var success = FlockedFileFormat.TryReadHeader(v1Stream, out var readUuid);

        Assert.True(success);
        Assert.Equal(uuid, readUuid);
    }

    [Fact]
    public void TryReadHeader_UnknownFutureVersion_ReturnsFalse()
    {
        // 認不得的版本一律失敗，不猜測著解析——欄位順序或意義改變的格式變動才會跳版本號。
        var uuid = Guid.NewGuid().ToString();
        using var stream = new MemoryStream();
        FlockedFileFormat.WriteHeader(stream, uuid);
        var bytes = stream.ToArray();
        bytes[4] = 99;
        using var futureStream = new MemoryStream(bytes);

        Assert.False(FlockedFileFormat.TryReadHeader(futureStream, out _));
    }
}
