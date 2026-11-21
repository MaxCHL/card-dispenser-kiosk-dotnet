namespace CardDispenser.Core;

/// <summary>
/// 發卡流程各項可調參數。用 record 是為了 demo 中方便用 <c>with</c> 表達式派生變體設定
/// (例如把等待旅客取卡的逾時縮短以加速展示)。
/// </summary>
public sealed record IssuerOptions
{
    /// <summary>推卡到讀寫位置失敗時的最大重試次數。超過即判定 Jam,不可無限重推以免疊卡。</summary>
    public int MaxDispenseRetries { get; init; } = 2;

    /// <summary>讀卡失敗時的最大重試次數。超過即吞卡換新卡。</summary>
    public int MaxReadRetries { get; init; } = 2;

    /// <summary>寫卡/驗證失敗時的最大重試次數(含驗證不一致視為寫卡失敗的情況)。超過即吞卡換新卡。</summary>
    public int MaxWriteRetries { get; init; } = 2;

    /// <summary>
    /// 單一交易允許「吞卡換新卡」的次數上限。
    /// 這是交易層級的護欄:避免讀寫持續失敗時把整個卡匣都吞光,超過上限就中止交易並告警。
    /// </summary>
    public int MaxCardSwapsPerTransaction { get; init; } = 2;

    /// <summary>每次重試之間的退避延遲(demo 中用短延遲模擬)。</summary>
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>等待旅客於出卡口取卡的逾時時間。</summary>
    public TimeSpan GuestTakeTimeout { get; init; } = TimeSpan.FromSeconds(3);
}
