namespace CardDispenser.Core.Models;

/// <summary>一次發卡交易的最終結果。</summary>
public sealed class IssueResult
{
    public required bool Success { get; init; }
    public required DispenseState FinalState { get; init; }
    public FaultCode Fault { get; init; } = FaultCode.None;
    public string? RoomNo { get; init; }

    /// <summary>本次交易總共消耗(含被吞卡的失敗卡 + 最終成功或最終被吞的卡)的卡片數量。</summary>
    public required int CardsConsumed { get; init; }

    public required string Message { get; init; }

    public static IssueResult Ok(DispenseState state, string roomNo, int cardsConsumed, string message) => new()
    {
        Success = true,
        FinalState = state,
        Fault = FaultCode.None,
        RoomNo = roomNo,
        CardsConsumed = cardsConsumed,
        Message = message,
    };

    public static IssueResult Fail(DispenseState state, FaultCode fault, int cardsConsumed, string message) => new()
    {
        Success = false,
        FinalState = state,
        Fault = fault,
        CardsConsumed = cardsConsumed,
        Message = message,
    };
}
