using CardDispenser.Core.Models;

namespace CardDispenser.Core.Hardware;

/// <summary>閘門 sensor 是否偵測到卡片到位。</summary>
public sealed record GateResult(bool CardDetected, string Message);

/// <summary>讀卡結果:卡別、UID、是否為可沿用的空白卡,以及對應的卡片物件(依卡別擇一提供)。</summary>
public sealed record ReadResult(
    bool Success,
    CardType DetectedType,
    string? Uid,
    bool IsBlankUsable,
    MifareClassicCard? ClassicCard,
    DesfireCard? DesfireCard,
    string Message);

public sealed record WriteResult(bool Success, string Message);

public sealed record VerifyResult(bool Success, string Message);

public sealed record GuestTakeResult(bool Taken, string Message);

/// <summary>吞卡動作結果。Success=false 代表回收艙已滿、實際上沒有真的吞成功。</summary>
public sealed record CaptureResult(bool Success, string Message);
