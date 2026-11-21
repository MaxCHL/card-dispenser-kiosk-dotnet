namespace CardDispenser.Core.Offline;

/// <summary>
/// Sector numbering shared between the guest-card write path (dispenser hardware, encoder)
/// and the offline lock's read path, so both sides agree on where each piece of data lives
/// on a Mifare Classic 1K card.
/// </summary>
public static class CardSectors
{
    /// <summary>Sector 1, block 0~2(絕對 block 4~6):既有的 <see cref="Models.HotelKeyData"/> 房卡資料格式。</summary>
    public const int GuestDataSector = 1;

    /// <summary>Sector 2, block 0~2(絕對 block 8~10):離線安全信封(見 <see cref="GuestCardCredential"/>)。</summary>
    public const int EnvelopeSector = 2;
}
