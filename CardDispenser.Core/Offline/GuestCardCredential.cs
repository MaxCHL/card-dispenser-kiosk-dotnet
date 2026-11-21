using CardDispenser.Core.Models;
using CardDispenser.Core.Util;

namespace CardDispenser.Core.Offline;

/// <summary>
/// 離線房卡憑證:在既有 <see cref="HotelKeyData"/>(房卡業務資料)之外,疊加離線
/// data-on-card 安全信封——這是飯店離線鎖(autonomous/offline lock)常見的架構模型:鎖不連網,
/// 權限、效期、案場歸屬與防偽簽章全部寫在卡片上,鎖端用自己離線的時鐘與案場金鑰驗證。
/// </summary>
/// <remarks>
/// 憑證實際寫卡時拆成兩段,分別寫入不同 sector(見 <see cref="CardSectors"/>):
/// - <see cref="Access"/> 沿用既有 <see cref="HotelKeyData.ToBytes"/>,寫入 Sector 1。
/// - <see cref="PropertyId"/> / <see cref="CardSequence"/> / <see cref="Signature"/> 這個信封
///   另外用 <see cref="ToPayload"/> 序列化,寫入 Sector 2。
/// 兩段合起來才是完整憑證,<see cref="TryParse"/> 需要兩段分別讀回的 block 資料重組。
/// </remarks>
public sealed class GuestCardCredential
{
    private const byte EnvelopeMagicHigh = 0xC5;
    private const byte EnvelopeMagicLow = 0x5C;
    private const byte EnvelopeVersion = 0x01;

    /// <summary>信封序列化後占用的總長度(3 個 block × 16 bytes,與 <see cref="HotelKeyData.EncodedLength"/> 對齊)。</summary>
    public const int EnvelopePayloadLength = 48;

    /// <summary>簽章長度(HMAC-SHA256 輸出截斷至前 16 bytes,剛好塞滿一個 block)。</summary>
    public const int SignatureLength = 16;

    /// <summary>案場/案址代碼(site id)。同一案場所有鎖與卡共用同一把 site key。</summary>
    public required ushort PropertyId { get; init; }

    /// <summary>
    /// 單調遞增序號。退房重發新卡時序號 +1,鎖端靠比對序號大小蓋掉舊卡——
    /// 這是離線鎖免連網完成退房的關鍵機制(見 <see cref="SimulatedOfflineLock.PresentCardAsync"/>)。
    /// </summary>
    public required uint CardSequence { get; init; }

    /// <summary>沿用既有房卡業務資料(房號/效期/門禁權限/電梯樓層)。</summary>
    public required HotelKeyData Access { get; init; }

    /// <summary>對 <see cref="ComputeSignableBytes"/> 輸出以案場金鑰計算的 HMAC 簽章,長度固定 <see cref="SignatureLength"/>。</summary>
    public required byte[] Signature { get; init; }

    /// <summary>
    /// 組出要簽章的原始資料(不含簽章本身):PropertyId(2B, big-endian)
    /// + CardSequence(4B, big-endian) + HotelKeyData.ToBytes()(48B)。
    /// 提供靜態版本,方便在還沒建出完整 <see cref="GuestCardCredential"/> 物件
    /// (簽章尚未算出)之前就能呼叫。
    /// </summary>
    public static byte[] ComputeSignableBytes(ushort propertyId, uint cardSequence, HotelKeyData access)
    {
        var accessBytes = access.ToBytes();
        var result = new byte[2 + 4 + HotelKeyData.EncodedLength];
        result[0] = (byte)(propertyId >> 8);
        result[1] = (byte)propertyId;
        result[2] = (byte)(cardSequence >> 24);
        result[3] = (byte)(cardSequence >> 16);
        result[4] = (byte)(cardSequence >> 8);
        result[5] = (byte)cardSequence;
        Array.Copy(accessBytes, 0, result, 6, HotelKeyData.EncodedLength);
        return result;
    }

    /// <summary>實例版本,等同 <see cref="ComputeSignableBytes(ushort, uint, HotelKeyData)"/>(PropertyId, CardSequence, Access)。</summary>
    public byte[] ToSignableBytes() => ComputeSignableBytes(PropertyId, CardSequence, Access);

    /// <summary>
    /// 將信封部分(不含 <see cref="Access"/>,那段沿用 HotelKeyData.ToBytes 另外寫入 Sector 1)
    /// 序列化為 48 bytes,供寫入 Sector 2 的 3 個 block。格式仿照 <see cref="HotelKeyData"/>
    /// 的 magic/version/CRC 慣例:
    /// Block0(16B): [0-1]信封 magic 0xC5 0x5C | [2]版本 | [3-4]PropertyId(big-endian ushort)
    ///              | [5-8]CardSequence(big-endian uint) | [9-15]保留
    /// Block1(16B): [0-15]Signature(16 bytes,剛好塞滿整個 block)
    /// Block2(16B): [0-1]CRC16(對 Block0+Block1 共 32 bytes 計算,big-endian) | [2-15]保留
    /// </summary>
    public byte[] ToPayload()
    {
        if (Signature.Length != SignatureLength)
        {
            throw new InvalidOperationException(
                $"簽章長度不正確(預期 {SignatureLength} bytes,實際 {Signature.Length} bytes),無法序列化信封");
        }

        var block0 = new byte[16];
        block0[0] = EnvelopeMagicHigh;
        block0[1] = EnvelopeMagicLow;
        block0[2] = EnvelopeVersion;
        block0[3] = (byte)(PropertyId >> 8);
        block0[4] = (byte)PropertyId;
        block0[5] = (byte)(CardSequence >> 24);
        block0[6] = (byte)(CardSequence >> 16);
        block0[7] = (byte)(CardSequence >> 8);
        block0[8] = (byte)CardSequence;
        // block0[9..15] 保留

        var block1 = new byte[16];
        Array.Copy(Signature, 0, block1, 0, SignatureLength);

        var combined = new byte[32];
        Array.Copy(block0, 0, combined, 0, 16);
        Array.Copy(block1, 0, combined, 16, 16);
        var crc = Crc16.Compute(combined);

        var block2 = new byte[16];
        block2[0] = (byte)(crc >> 8);
        block2[1] = (byte)(crc & 0xFF);
        // block2[2..15] 保留

        var result = new byte[EnvelopePayloadLength];
        Array.Copy(block0, 0, result, 0, 16);
        Array.Copy(block1, 0, result, 16, 16);
        Array.Copy(block2, 0, result, 32, 16);
        return result;
    }

    /// <summary>
    /// 由回讀的信封 3 個 block 加上另外解析好的 <paramref name="access"/> 重組完整憑證。
    /// 這裡只驗證信封本身的結構完整性(magic/version/CRC);簽章是否可信是另一層檢查,
    /// 須另外呼叫 <see cref="CredentialSecurity.Verify"/>——結構完整不代表簽章有效。
    /// </summary>
    public static bool TryParse(
        byte[] envelopeBlock0, byte[] envelopeBlock1, byte[] envelopeBlock2,
        HotelKeyData access,
        out GuestCardCredential? credential, out string error)
    {
        credential = null;

        if (envelopeBlock0.Length != 16 || envelopeBlock1.Length != 16 || envelopeBlock2.Length != 16)
        {
            error = "信封 block 長度不正確";
            return false;
        }

        if (envelopeBlock0[0] != EnvelopeMagicHigh || envelopeBlock0[1] != EnvelopeMagicLow)
        {
            error = "信封格式辨識碼不符,非本系統離線信封格式";
            return false;
        }

        if (envelopeBlock0[2] != EnvelopeVersion)
        {
            error = $"信封格式版本不支援: 0x{envelopeBlock0[2]:X2}";
            return false;
        }

        var combined = new byte[32];
        Array.Copy(envelopeBlock0, 0, combined, 0, 16);
        Array.Copy(envelopeBlock1, 0, combined, 16, 16);
        var expectedCrc = Crc16.Compute(combined);
        var actualCrc = (ushort)((envelopeBlock2[0] << 8) | envelopeBlock2[1]);
        if (expectedCrc != actualCrc)
        {
            error = $"信封 CRC 校驗失敗(預期 0x{expectedCrc:X4},實際 0x{actualCrc:X4}),資料可能已損毀或被竄改";
            return false;
        }

        var propertyId = (ushort)((envelopeBlock0[3] << 8) | envelopeBlock0[4]);
        var cardSequence =
            ((uint)envelopeBlock0[5] << 24) | ((uint)envelopeBlock0[6] << 16) |
            ((uint)envelopeBlock0[7] << 8) | envelopeBlock0[8];
        var signature = envelopeBlock1[0..SignatureLength];

        credential = new GuestCardCredential
        {
            PropertyId = propertyId,
            CardSequence = cardSequence,
            Access = access,
            Signature = signature,
        };
        error = string.Empty;
        return true;
    }
}
