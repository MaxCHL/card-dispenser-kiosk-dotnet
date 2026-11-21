namespace CardDispenser.Core.Util;

/// <summary>
/// CRC-16/CCITT-FALSE 校驗碼計算工具。
/// 用途:房卡資料寫入卡片前計算校驗碼、回讀後驗證資料完整性(防止寫入過程位元錯誤)。
/// 這是模擬用的簡化實作,正式硬體 SDK 通常會內建對應的 CRC/checksum 函式。
/// </summary>
public static class Crc16
{
    private const ushort InitialValue = 0xFFFF;
    private const ushort Polynomial = 0x1021;

    public static ushort Compute(byte[] data)
    {
        ushort crc = InitialValue;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ Polynomial)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }
}
