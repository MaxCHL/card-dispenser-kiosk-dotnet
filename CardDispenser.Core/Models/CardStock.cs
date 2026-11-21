namespace CardDispenser.Core.Models;

/// <summary>
/// 卡匣庫存快照(卡匣裡剩餘的空白卡數量與滿匣容量)。
/// 由硬體層的庫存 sensor 讀出後回報給業務層,業務層不需要知道 sensor 的實作細節。
/// </summary>
public sealed record CardStock(int RemainingCount, int Capacity)
{
    public bool HasCard => RemainingCount > 0;
}
