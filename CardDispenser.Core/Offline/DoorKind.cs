namespace CardDispenser.Core.Offline;

/// <summary>
/// 離線門鎖的門類型:決定 <see cref="SimulatedOfflineLock"/> 用哪一種邏輯判斷通行權限,
/// 取代舊版「只比對房號」的簡化做法。
/// </summary>
public enum DoorKind
{
    /// <summary>客房門:需持卡人 <see cref="Models.AccessFlags.GuestRoom"/> 旗標,且卡片房號與本門房號相符。</summary>
    GuestRoom,

    /// <summary>設施門(健身房/泳池/SPA/行政酒廊等):需持卡人具備本門對應的 <see cref="Models.AccessFlags"/> 旗標。</summary>
    Facility,

    /// <summary>電梯樓層門:需持卡人具備 <see cref="Models.AccessFlags.Elevator"/> 旗標,且本門樓層在卡片的 ElevatorFloorMask 內。</summary>
    ElevatorFloor,
}
