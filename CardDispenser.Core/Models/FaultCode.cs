namespace CardDispenser.Core.Models;

/// <summary>發卡交易的故障分類代碼,對應各個 fallback 狀態,方便上層(櫃檯告警/客服系統)分流處理。</summary>
public enum FaultCode
{
    None,
    OutOfStock,
    DispenseJam,
    ReadFailed,
    WriteFailed,
    CaptureBinFull,
    CommunicationError,
    GuestDidNotTakeCard,
    CardSwapLimitExceeded,
}
