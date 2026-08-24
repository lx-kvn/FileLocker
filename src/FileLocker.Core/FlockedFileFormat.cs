using System.Buffers.Binary;

namespace FileLocker.Core;

/// <summary>
/// 對應「單檔案分散式加密」功能規劃 §4：`.flocked` 檔案本身就是完整密文（沒有 Vault 可以指過去），
/// 但 FolderArchiver 掃描巢狀鎖定項目時需要能識別出裡面的 UUID（§4 點 2）——完整密文沒辦法直接
/// 讀出 UUID，所以在密文串流（ChunkedCipher.EncryptStream 的輸出）前面加一段固定格式、非加密的
/// 小 header：
///
/// ┌────────────────────────────────────────────────────┐
/// │ Magic bytes (4 bytes)          "FLKD"                │
/// │ 版本號 (1 byte)                                       │
/// │ Header 總長度 (2 bytes, big-endian)                    │
/// │ UUID (16 bytes，原始 GUID 位元組，不是 36 字元字串形式)     │
/// │ 保留欄位 (8 bytes，目前全部寫 0)                          │
/// └────────────────────────────────────────────────────┘
///
/// 「Header 總長度」欄位是刻意加的（不是單純寫死一個常數）：往後如果要在保留欄位那塊加真正
/// 用得到的東西（例如壓縮旗標、chunk size 提示），只要沒有動到既有欄位的位置跟意義、只是
/// 使用原本填 0 的保留空間，並不需要跳版本號——讀取端永遠照這個欄位讀走「宣告的長度」，
/// 不用把長度寫死在程式碼裡，也就不會因為長度變了就讀錯位移、把密文串流的開頭吃掉一截。
/// 真的不相容的格式變動（欄位順序或意義改變）才需要真的跳版本號，讀到不認得的版本直接判定
/// 失敗，不猜測著解析。
///
/// UUID 明碼寫在 header 裡是刻意的：UUID 本身不是機密（`.locked` 指標檔本身也是明碼 UUID＋簽章，
/// 同一個資安假設），真正需要保護的密碼／加密金鑰完全不在這個 header 裡，只在後面
/// ChunkedCipher 產出的密文串流裡。
///
/// header 本身沒有簽章——跟 `.locked` 指標檔不同，`.flocked` 檔案的完整性由後面的密文串流自己
/// 的 AES-GCM Auth Tag 保護（header 被竄改頂多讓 UUID 讀錯／巢狀偵測誤判，不會讓人拿到明文，
/// 解密仍然需要正確密碼），不需要額外簽章機制。
/// </summary>
public static class FlockedFileFormat
{
    // "FLKD"：FileLocker standalone 的縮寫，判斷檔案類型用，不是任何加密材料。
    private static readonly byte[] MagicBytes = [0x46, 0x4C, 0x4B, 0x44];

    private const byte CurrentVersion = 1;

    private const int UuidLengthBytes = 16;

    // 保留給未來欄位的空間，目前沒有任何用途，寫入時全部填 0，讀取時直接忽略內容（不解讀、
    // 不驗證）——上面類別註解說明過為什麼寧可先留白，也不要之後為了塞一個小旗標就得跳版本號。
    private const int ReservedBytesLength = 8;

    // Magic + 版本號 + Header 長度欄位本身——這一段的長度是固定的，因為「Header 長度」這個
    // 欄位自己要放在哪裡、多長，必須是讀取端不用先知道總長度就能算出來的，不能套用同一套
    // 「照宣告長度讀」的邏輯（雞生蛋問題）。
    private const int FixedPrefixLengthBytes = 4 /* magic */ + 1 /* version */ + 2 /* header length */;

    private const int CurrentHeaderLengthBytes = FixedPrefixLengthBytes + UuidLengthBytes + ReservedBytesLength;

    /// <summary>
    /// 把 header 寫進 output 目前的位置，寫完後呼叫端接著把 ChunkedCipher.EncryptStream 的輸出
    /// 直接接在後面即可，不需要另外處理位移。
    /// </summary>
    public static void WriteHeader(Stream output, string uuid)
    {
        if (!Guid.TryParse(uuid, out var parsedUuid))
        {
            throw new ArgumentException("uuid 必須是合法的 GUID 格式", nameof(uuid));
        }

        Span<byte> headerLengthBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(headerLengthBytes, (ushort)CurrentHeaderLengthBytes);

        output.Write(MagicBytes);
        output.WriteByte(CurrentVersion);
        output.Write(headerLengthBytes);
        output.Write(parsedUuid.ToByteArray());
        output.Write(new byte[ReservedBytesLength]); // 保留欄位，這個版本全部寫 0。
    }

    /// <summary>
    /// 讀取並驗證 header；成功時 <paramref name="input"/> 的位置會停在 header 結束、密文串流
    /// 開始的地方（實際跳過的位元組數以檔案裡宣告的 Header 長度為準，不是寫死的常數——見類別
    /// 註解），呼叫端可以直接接著呼叫 ChunkedCipher.DecryptStream。
    ///
    /// 找不到合法 header（magic bytes 不對、版本不支援、宣告長度不合理、串流長度不夠）一律
    /// 回傳 false，不拋例外——比照 LockedMarkerFile.ReadFrom 的既有慣例，呼叫端（雙擊 .flocked
    /// 解密流程、FolderArchiver 巢狀掃描）只需要處理「讀得到」跟「讀不到/不是合法檔案」兩種情況。
    /// </summary>
    public static bool TryReadHeader(Stream input, out string? uuid)
    {
        uuid = null;

        var prefix = new byte[FixedPrefixLengthBytes];
        if (ReadFully(input, prefix, 0, FixedPrefixLengthBytes) != FixedPrefixLengthBytes)
        {
            return false;
        }

        if (!prefix.AsSpan(0, MagicBytes.Length).SequenceEqual(MagicBytes))
        {
            return false;
        }

        var version = prefix[MagicBytes.Length];
        if (version != CurrentVersion)
        {
            return false;
        }

        var declaredHeaderLength = BinaryPrimitives.ReadUInt16BigEndian(prefix.AsSpan(MagicBytes.Length + 1, 2));
        var remainingLength = declaredHeaderLength - FixedPrefixLengthBytes;

        // 宣告的長度連 UUID 都塞不下，代表這個 header 本身已經不合理（損毀或惡意構造），
        // 不嘗試繼續解析——這裡刻意不要求「剛好等於目前版本已知的長度」，保留欄位以後
        // 變長是允許的（見類別註解），只要求「至少放得下我們認得的欄位」。
        if (remainingLength < UuidLengthBytes)
        {
            return false;
        }

        var remaining = new byte[remainingLength];
        if (ReadFully(input, remaining, 0, remainingLength) != remainingLength)
        {
            return false;
        }

        // remaining 裡 UUID 之後的部分（保留欄位，以及未來版本可能塞的更多欄位）這個版本
        // 一律不解讀、直接捨棄——但因為上面已經照宣告長度把它們整段讀掉，stream 位置仍然正確
        // 停在密文開始的地方。
        var uuidBytes = remaining.AsSpan(0, UuidLengthBytes).ToArray();
        uuid = new Guid(uuidBytes).ToString();
        return true;
    }

    /// <summary>
    /// 判斷一個檔案是不是合法的 .flocked 檔案，不需要拿到 UUID 的情境（例如只想確認副檔名對得上
    /// 內容）可以用這個，內部就是呼叫 TryReadHeader 再丟掉結果。
    /// </summary>
    public static bool TryReadUuid(string path, out string? uuid)
    {
        uuid = null;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return TryReadHeader(stream, out uuid);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Stream.Read 不保證一次讀滿，比照 ChunkedCipher 既有的同名輔助方法。</summary>
    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }
}
