using CardDispenser.Core.Models;

namespace CardDispenser.Core.Offline;

/// <summary>
/// 設定卡(母卡 / programming card):用來佈建離線門鎖——寫入案場代碼、門/房代號、
/// 案場金鑰,並校準鎖端離線時鐘。真實飯店鎖系統這張卡多由 飯店 PMS(Property
/// Management System)透過廠商 API 產生,再由工程人員持卡到現場逐一設定每個門鎖。
/// </summary>
public sealed class ProgrammingCard
{
    /// <summary>要佈建的案場代碼。</summary>
    public required ushort PropertyId { get; init; }

    /// <summary>要佈建的門/房代號(demo 中對應 <see cref="Models.HotelKeyData.RoomNo"/>)。</summary>
    public required string DoorId { get; init; }

    /// <summary>要佈建的案場金鑰,鎖端日後憑此離線驗證卡片簽章。</summary>
    public required byte[] SiteKey { get; init; }

    /// <summary>要校準鎖端 RTC 的時間。離線鎖沒有網路對時,只能靠設定卡在佈建當下校時。</summary>
    public required DateTime SyncClockUtc { get; init; }

    /// <summary>要佈建的門類型。預設 GuestRoom,讓既有(佈建當下未指定 Kind 的)呼叫端維持原行為不變。</summary>
    public DoorKind Kind { get; init; } = DoorKind.GuestRoom;

    /// <summary>本門(Kind=Facility 時)要求的設施旗標,例如健身房門要求 <see cref="AccessFlags.Gym"/>。</summary>
    public AccessFlags RequiredFacility { get; init; } = AccessFlags.None;

    /// <summary>本門(Kind=ElevatorFloor 時)對應的樓層,1-based(樓層 N 對應 ElevatorFloorMask 的 bit(N-1))。</summary>
    public int ElevatorFloor { get; init; }
}
