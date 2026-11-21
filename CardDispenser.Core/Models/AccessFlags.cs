namespace CardDispenser.Core.Models;

/// <summary>
/// 房卡門禁權限旗標(可複選)。實際門鎖硬體會依此旗標決定哪些門/設施允許通行。
/// 電梯樓層另以 <see cref="HotelKeyData.ElevatorFloorMask"/> 的逐樓層位元表示,不在此旗標內。
/// </summary>
[Flags]
public enum AccessFlags : byte
{
    None = 0,

    /// <summary>客房門禁(基本權限,幾乎所有房卡都會有)。</summary>
    GuestRoom = 1 << 0,

    /// <summary>電梯樓層管制通行(需搭配 ElevatorFloorMask 判斷可達樓層)。</summary>
    Elevator = 1 << 1,

    /// <summary>健身房。</summary>
    Gym = 1 << 2,

    /// <summary>行政酒廊。</summary>
    ExecutiveLounge = 1 << 3,

    /// <summary>泳池。</summary>
    Pool = 1 << 4,

    /// <summary>停車場。</summary>
    ParkingGarage = 1 << 5,

    /// <summary>SPA。</summary>
    Spa = 1 << 6,
}
