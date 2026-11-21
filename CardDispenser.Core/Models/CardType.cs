namespace CardDispenser.Core.Models;

/// <summary>
/// 卡片型別。飯店智慧門鎖常採 13.56MHz 高頻卡,
/// 本模擬以 Mifare Classic 1K 為主要建模對象,DESFire 僅做輕量抽象(部分支援)。
/// </summary>
public enum CardType
{
    /// <summary>讀卡失敗或無法辨識的未知卡片。</summary>
    Unknown,

    /// <summary>Mifare Classic 1K:16 sector × 4 block × 16 bytes,採 Key A/Key B 驗證。</summary>
    MifareClassic1K,

    /// <summary>DESFire EV1:application/file 架構,支援 AES/DES 雙向認證,本模擬僅輕量支援。</summary>
    DesfireEv1,
}
