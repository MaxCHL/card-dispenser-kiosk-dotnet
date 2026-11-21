namespace CardDispenser.Sim;

/// <summary>可注入通訊逾時的硬體呼叫點,用來模擬「與機台控制板通訊逾時」的情境。</summary>
public enum HardwareCall
{
    CheckHopper,
    Dispense,
    Read,
    Write,
    Verify,
    Eject,
    WaitGuestTake,
    Recapture,
    CaptureToReject,
}

/// <summary>
/// 模擬硬體的故障注入設定。沒有真實硬體時,demo 需要能穩定重現每一個 fallback 分支,
/// 所以用明確的設定值(而非隨機亂數)決定哪一次動作要失敗,確保每次執行結果都可預期、可重現。
/// </summary>
public sealed class FaultInjectionConfig
{
    /// <summary>卡匣初始庫存張數(同時也是滿匣容量)。</summary>
    public int InitialHopperCount { get; init; } = 5;

    /// <summary>回收艙容量。</summary>
    public int CaptureBinCapacity { get; init; } = 3;

    /// <summary>回收艙一開始就是滿的(用來展示 CaptureBinFull)。</summary>
    public bool CaptureBinStartsFull { get; init; }

    /// <summary>每一次推卡都卡住(用來展示 Jam)。</summary>
    public bool JamEveryDispense { get; init; }

    /// <summary>指定第幾張卡(依交易內累計的卡片序號,從 1 起算)讀卡會失敗。</summary>
    public HashSet<int> ReadFailCardSequences { get; init; } = new();

    /// <summary>每一張卡讀卡都失敗(用來展示換卡次數超過上限而中止交易)。</summary>
    public bool ReadAlwaysFails { get; init; }

    /// <summary>指定第幾張卡寫卡會失敗。</summary>
    public HashSet<int> WriteFailCardSequences { get; init; } = new();

    /// <summary>指定第幾張卡回讀驗證會不一致。</summary>
    public HashSet<int> VerifyMismatchCardSequences { get; init; } = new();

    /// <summary>旅客永遠不取卡(用來展示逾時吞卡回收)。</summary>
    public bool GuestNeverTakesCard { get; init; }

    /// <summary>指定某個硬體呼叫要拋出通訊逾時例外(用來展示 CommunicationError)。</summary>
    public HardwareCall? CommunicationTimeoutOn { get; init; }
}
