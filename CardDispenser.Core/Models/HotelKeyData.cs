using System.Text;
using CardDispenser.Core.Util;

namespace CardDispenser.Core.Models;

/// <summary>
/// 房卡業務資料:房號、入住/退房時間、門禁權限旗標、電梯可達樓層。
/// 這是要寫入卡片資料 sector 的內容,對應 Mifare Classic 1K sector 1 的 block 4~6(共 48 bytes)。
/// </summary>
/// <remarks>
/// Byte 佈局(模擬用簡化格式,非任何真實廠商協定):
/// Block4(16B): [0-1]格式辨識碼 0xA5 0x5A | [2]格式版本 | [3-6]房號(ASCII,右補 0,上限 4 碼)
///              | [7-10]入住時間(Unix秒,big-endian uint32) | [11-14]退房時間(同上) | [15]保留
/// Block5(16B): [0]AccessFlags | [1-2]ElevatorFloorMask(little-endian ushort) | [3-15]保留
/// Block6(16B): [0-1]CRC16(對 Block4+Block5 共 32 bytes 計算,big-endian) | [2-15]保留
///
/// 房號欄位僅預留 4 bytes,是這個 16-byte block 格式下刻意的簡化取捨(demo 用),
/// 正式系統若房號位數較長,應改配置多一個 block 或改用變長編碼。
/// </remarks>
public sealed class HotelKeyData
{
    private const byte FormatMagicHigh = 0xA5;
    private const byte FormatMagicLow = 0x5A;
    private const byte FormatVersion = 0x01;
    private const int RoomNoFieldLength = 4;

    /// <summary>寫入卡片後占用的總長度(3 個 block × 16 bytes)。</summary>
    public const int EncodedLength = 48;

    public required string RoomNo { get; init; }
    public DateTime CheckInUtc { get; init; }
    public DateTime CheckOutUtc { get; init; }
    public AccessFlags AccessFlags { get; init; }

    /// <summary>逐樓層可達旗標,bit(N-1) 對應樓層 N,最多支援 16 個樓層。</summary>
    public ushort ElevatorFloorMask { get; init; }

    /// <summary>
    /// 將房卡資料序列化為 48 bytes(對應 block4/5/6 依序串接),供寫卡時使用。
    /// </summary>
    public byte[] ToBytes()
    {
        if (string.IsNullOrEmpty(RoomNo) || RoomNo.Length > RoomNoFieldLength)
        {
            throw new InvalidOperationException(
                $"房號 \"{RoomNo}\" 超出本卡片格式上限({RoomNoFieldLength} 碼),無法編碼");
        }

        var block4 = new byte[16];
        var block5 = new byte[16];
        var block6 = new byte[16];

        block4[0] = FormatMagicHigh;
        block4[1] = FormatMagicLow;
        block4[2] = FormatVersion;

        var roomBytes = Encoding.ASCII.GetBytes(RoomNo.PadRight(RoomNoFieldLength, '\0'));
        Array.Copy(roomBytes, 0, block4, 3, RoomNoFieldLength);

        WriteUInt32BigEndian(block4, 7, ToUnixSeconds(CheckInUtc));
        WriteUInt32BigEndian(block4, 11, ToUnixSeconds(CheckOutUtc));
        block4[15] = 0x00; // 保留

        block5[0] = (byte)AccessFlags;
        block5[1] = (byte)(ElevatorFloorMask & 0xFF);
        block5[2] = (byte)(ElevatorFloorMask >> 8);
        // block5[3..15] 保留,預設 0

        var combined = new byte[32];
        Array.Copy(block4, 0, combined, 0, 16);
        Array.Copy(block5, 0, combined, 16, 16);
        var crc = Crc16.Compute(combined);
        block6[0] = (byte)(crc >> 8);
        block6[1] = (byte)(crc & 0xFF);
        // block6[2..15] 保留

        var result = new byte[EncodedLength];
        Array.Copy(block4, 0, result, 0, 16);
        Array.Copy(block5, 0, result, 16, 16);
        Array.Copy(block6, 0, result, 32, 16);
        return result;
    }

    /// <summary>
    /// 由回讀的 block4/5/6 反序列化並驗證格式辨識碼、版本、CRC。
    /// 用於「Verifying:回讀驗證」步驟——不只比對內容,先確認資料結構完整(未被雜訊破壞)。
    /// </summary>
    public static bool TryParse(byte[] block4, byte[] block5, byte[] block6, out HotelKeyData? data, out string error)
    {
        data = null;

        if (block4.Length != 16 || block5.Length != 16 || block6.Length != 16)
        {
            error = "Block 長度不正確";
            return false;
        }

        if (block4[0] != FormatMagicHigh || block4[1] != FormatMagicLow)
        {
            error = "格式辨識碼不符,非本系統房卡格式";
            return false;
        }

        if (block4[2] != FormatVersion)
        {
            error = $"格式版本不支援: 0x{block4[2]:X2}";
            return false;
        }

        var combined = new byte[32];
        Array.Copy(block4, 0, combined, 0, 16);
        Array.Copy(block5, 0, combined, 16, 16);
        var expectedCrc = Crc16.Compute(combined);
        var actualCrc = (ushort)((block6[0] << 8) | block6[1]);
        if (expectedCrc != actualCrc)
        {
            error = $"CRC 校驗失敗(預期 0x{expectedCrc:X4},實際 0x{actualCrc:X4}),資料可能已損毀";
            return false;
        }

        var roomNo = Encoding.ASCII.GetString(block4, 3, RoomNoFieldLength).TrimEnd('\0');
        var checkIn = FromUnixSeconds(ReadUInt32BigEndian(block4, 7));
        var checkOut = FromUnixSeconds(ReadUInt32BigEndian(block4, 11));
        var accessFlags = (AccessFlags)block5[0];
        var elevatorMask = (ushort)(block5[1] | (block5[2] << 8));

        data = new HotelKeyData
        {
            RoomNo = roomNo,
            CheckInUtc = checkIn,
            CheckOutUtc = checkOut,
            AccessFlags = accessFlags,
            ElevatorFloorMask = elevatorMask,
        };
        error = string.Empty;
        return true;
    }

    private static uint ToUnixSeconds(DateTime utc) =>
        (uint)new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static DateTime FromUnixSeconds(uint seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24)
        | ((uint)buffer[offset + 1] << 16)
        | ((uint)buffer[offset + 2] << 8)
        | buffer[offset + 3];
}
