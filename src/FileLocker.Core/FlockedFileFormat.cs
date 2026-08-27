using System.Buffers.Binary;
using System.Text.Json;
using FileLocker.Core.Models;

namespace FileLocker.Core;

/// <summary>
/// 一顆 `.flocked` 檔案的結構：UUID、密文實際佔用的位元組數，以及檔尾嵌入的 metadata。
/// </summary>
/// <param name="CiphertextLength">
/// 密文的位元組長度，已經扣掉檔尾的 metadata 區塊。呼叫端必須照這個長度框住餵給
/// ChunkedCipher.DecryptStream 的串流——它是讀到串流結束為止的，不框範圍會把 metadata
/// 的開頭當成下一個區塊的長度前綴。
/// </param>
/// <param name="EmbeddedMetadata">
/// 檔尾嵌入的驗證材料，null 代表這顆檔案沒有（v1 格式，或還在 Pending 階段尚未補上檔尾），
/// 呼叫端要退回去 Vault 查 {uuid}.meta.json。
/// </param>
public record FlockedFileLayout(
    string Uuid,
    int HeaderLength,
    long CiphertextLength,
    LockedItemMetadata? EmbeddedMetadata);

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

    // "FLKM"：檔尾 metadata 區塊的結束標記（M = metadata），跟開頭的 magic 分開一組，
    // 這樣「這個檔案有沒有嵌入 metadata」可以單獨判斷，不必依賴版本號推測。
    private static readonly byte[] TrailerMagicBytes = [0x46, 0x4C, 0x4B, 0x4D];

    private const byte CurrentVersion = 2;

    // v1（只有 UUID、解密材料要回 Vault 查）的檔案在使用者磁碟上已經存在，仍然要讀得出 UUID
    // 走回既有路徑，不能因為版本號不是最新就整份拒絕。
    private const byte MinSupportedVersion = 1;

    private const int UuidLengthBytes = 16;

    // 保留給未來欄位的空間，目前沒有任何用途，寫入時全部填 0，讀取時直接忽略內容（不解讀、
    // 不驗證）——上面類別註解說明過為什麼寧可先留白，也不要之後為了塞一個小旗標就得跳版本號。
    private const int ReservedBytesLength = 8;

    // Magic + 版本號 + Header 長度欄位本身——這一段的長度是固定的，因為「Header 長度」這個
    // 欄位自己要放在哪裡、多長，必須是讀取端不用先知道總長度就能算出來的，不能套用同一套
    // 「照宣告長度讀」的邏輯（雞生蛋問題）。
    private const int FixedPrefixLengthBytes = 4 /* magic */ + 1 /* version */ + 2 /* header length */;

    private const int CurrentHeaderLengthBytes = FixedPrefixLengthBytes + UuidLengthBytes + ReservedBytesLength;

    // 檔尾 metadata JSON 的長度上限。實際內容只有幾 KB，這個上限純粹是「讀到損毀或被竄改的
    // 長度值時不要嘗試配置荒謬大小的陣列」，用意跟 ChunkedCipher.MaxChunkLengthBytes 相同。
    private const int MaxMetadataLengthBytes = 1024 * 1024;

    // 欄位命名規則跟 VaultManager 寫 .meta.json 時一致（都用型別本身的屬性名稱，沒有套用
    // 命名策略），兩邊序列化出來的同一份 metadata 可以互相讀回來。差別只在這裡不縮排：
    // 嵌在檔案裡的區塊不需要給人讀，省下來的空間直接反映在每顆 .flocked 檔案的大小上。
    private static readonly JsonSerializerOptions MetadataJsonOptions = new() { WriteIndented = false };

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
        if (version < MinSupportedVersion || version > CurrentVersion)
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

    /// <summary>
    /// 把解密所需的驗證材料（鹽值、Argon2 參數、密碼驗證雜湊、Passkey／恢復金鑰的包裝金鑰等）
    /// 序列化成 JSON 接在密文之後，讓 `.flocked` 檔案真的能獨立於 Vault 被解開。
    ///
    /// 放檔尾而不是接在 header 後面，是寫入時機決定的：header 必須在加密開始之前就寫下去
    /// （密文緊接在它後面），但 Passkey／恢復金鑰的包裝金鑰是整份內容加密完成之後才產生的。
    /// 放檔尾的話，commit 階段對既有的暫存密文檔直接 append 再 File.Move 就完成了，不需要
    /// 把整份密文重新讀寫一次（大型項目的差別非常大）。
    ///
    /// <see cref="LockedItemMetadata.OriginalPath"/> 與 <see cref="LockedItemMetadata.StandaloneDestinationDir"/>
    /// 不寫進去：這兩個欄位對解密沒有作用（還原位置看的是 `.flocked` 檔案現在放在哪，見
    /// LockService.DecryptFlockedFileCore），留著只會讓一顆設計上就是要被帶走、被轉交的檔案
    /// 順便洩漏使用者的資料夾結構。
    /// </summary>
    public static void AppendMetadataTrailer(Stream output, LockedItemMetadata metadata)
    {
        var portable = ToPortableMetadata(metadata);
        var json = JsonSerializer.SerializeToUtf8Bytes(portable, MetadataJsonOptions);

        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, json.Length);

        output.Write(json);
        output.Write(lengthBytes);
        output.Write(TrailerMagicBytes);
    }

    /// <summary>
    /// 讀出整個 `.flocked` 檔案的結構：UUID、密文實際的位元組長度，以及檔尾嵌入的 metadata
    /// （沒有就是 null）。
    ///
    /// 密文長度一定要回報出來：ChunkedCipher.DecryptStream 是一路讀到串流結束為止的，檔尾多了
    /// metadata 區塊之後，不框出範圍它會把 metadata 的開頭當成下一個區塊的長度前綴去解析。
    ///
    /// 需要可 seek 的串流（實務上都是檔案）。檔尾讀不到、magic 對不上、宣告長度不合理時一律
    /// 當作「沒有嵌入 metadata」而不是判定整個檔案損毀——退回查 Vault 的既有路徑，至少在原本
    /// 那台裝置上還救得回來。
    /// </summary>
    public static bool TryReadLayout(Stream input, out FlockedFileLayout? layout)
    {
        layout = null;

        var startPosition = input.Position;
        if (!TryReadHeader(input, out var uuid) || uuid is null)
        {
            return false;
        }

        var headerLength = (int)(input.Position - startPosition);
        var totalLength = input.Length - startPosition;
        var afterHeaderLength = totalLength - headerLength;

        var metadata = TryReadMetadataTrailer(input, afterHeaderLength, out var trailerLength)
            ? ReadMetadataTrailer(input, trailerLength)
            : null;

        // metadata 解析失敗（JSON 壞了）時 trailerLength 仍然要扣掉——那段位元組確實不是密文，
        // 把它餵給 ChunkedCipher 只會變成一個更難懂的「內容損毀」錯誤。
        var ciphertextLength = metadata is null && trailerLength == 0
            ? afterHeaderLength
            : afterHeaderLength - trailerLength;

        input.Position = startPosition + headerLength;
        layout = new FlockedFileLayout(uuid, headerLength, ciphertextLength, metadata);
        return true;
    }

    /// <summary>
    /// 檢查檔尾有沒有合法的 metadata 區塊，有的話回報這個區塊（含長度欄位與 magic）總共佔了
    /// 幾個位元組。不改變串流最後停留的位置由呼叫端負責復原。
    /// </summary>
    private static bool TryReadMetadataTrailer(Stream input, long afterHeaderLength, out int trailerLength)
    {
        trailerLength = 0;

        const int SuffixLength = 4 /* metadata 長度 */ + 4 /* magic */;
        if (afterHeaderLength < SuffixLength)
        {
            return false;
        }

        input.Position = input.Length - SuffixLength;
        var suffix = new byte[SuffixLength];
        if (ReadFully(input, suffix, 0, SuffixLength) != SuffixLength)
        {
            return false;
        }

        if (!suffix.AsSpan(4, 4).SequenceEqual(TrailerMagicBytes))
        {
            return false;
        }

        var metadataLength = BinaryPrimitives.ReadInt32BigEndian(suffix.AsSpan(0, 4));

        // 上限比照 ChunkedCipher.MaxChunkLengthBytes 的用意：讀到損毀或被竄改的長度值時，
        // 不去嘗試配置一個荒謬大小的陣列。實際的 metadata JSON 只有幾 KB。
        if (metadataLength <= 0 || metadataLength > MaxMetadataLengthBytes)
        {
            return false;
        }

        if (afterHeaderLength < metadataLength + SuffixLength)
        {
            return false;
        }

        trailerLength = metadataLength + SuffixLength;
        return true;
    }

    private static LockedItemMetadata? ReadMetadataTrailer(Stream input, int trailerLength)
    {
        const int SuffixLength = 8;
        var metadataLength = trailerLength - SuffixLength;

        input.Position = input.Length - trailerLength;
        var json = new byte[metadataLength];
        if (ReadFully(input, json, 0, metadataLength) != metadataLength)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LockedItemMetadata>(json, MetadataJsonOptions);
        }
        catch (JsonException)
        {
            // 內容不是合法 JSON（截斷、竄改）——當作沒有嵌入 metadata，理由同 TryReadLayout。
            return null;
        }
    }

    /// <summary>
    /// 產生要寫進檔尾的 metadata 副本：清掉不該跟著檔案走的位置資訊，其餘原樣保留。
    /// 用複製而不是原地修改，避免動到呼叫端手上那份等一下還要寫進 Vault 的物件。
    /// </summary>
    private static LockedItemMetadata ToPortableMetadata(LockedItemMetadata source) => new()
    {
        Status = source.Status,
        StorageMode = source.StorageMode,
        StandaloneDestinationDir = null,
        Uuid = source.Uuid,
        OriginalName = source.OriginalName,
        OriginalPath = "",
        PasswordVerificationHash = source.PasswordVerificationHash,
        Salt = source.Salt,
        Argon2TimeCost = source.Argon2TimeCost,
        Argon2MemoryCostKb = source.Argon2MemoryCostKb,
        Argon2Parallelism = source.Argon2Parallelism,
        Hint = source.Hint,
        Type = source.Type,
        OriginalSizeBytes = source.OriginalSizeBytes,
        CreatedAtUtc = source.CreatedAtUtc,
        LastAccessedAtUtc = source.LastAccessedAtUtc,
        ContainsNestedLocks = [.. source.ContainsNestedLocks],
        PasskeyEnabled = source.PasskeyEnabled,
        PasskeyCredentialName = source.PasskeyCredentialName,
        PasskeyChallenge = source.PasskeyChallenge,
        PasskeyWrappedContentKey = source.PasskeyWrappedContentKey,
        RecoveryKeyEnabled = source.RecoveryKeyEnabled,
        RecoveryKeyWrappedContentKey = source.RecoveryKeyWrappedContentKey,
        BatchId = source.BatchId,
    };

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
