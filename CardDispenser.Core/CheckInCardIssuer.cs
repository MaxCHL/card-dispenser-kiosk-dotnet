using CardDispenser.Core.Hardware;
using CardDispenser.Core.Models;

namespace CardDispenser.Core;

/// <summary>
/// 飯店自助報到機的發卡流程 orchestrator。
/// 驅動 StockCheck -> Dispensing -> Positioning -> Reading -> Writing -> Verifying
/// -> Ejecting -> WaitGuestTake -> Completed 這條主線,並處理每一段的 retry / fallback。
///
/// 三個務必落實的不變式:
/// 1. 每個機械/IO 動作各自有 MaxRetries,超過上限就轉入對應 fallback 狀態,不可無限重試。
/// 2. 吞卡前一定先查詢回收艙是否已滿;滿了就不能再吞,要轉入 CaptureBinFull 停機告警。
/// 3. 交易層級有「換卡次數上限」,避免讀寫持續失敗時把整個卡匣吞光。
/// 通訊逾時(HardwareCommunicationException)在任何步驟發生都會被外層攔截,
/// 讓流程收斂到 CommunicationError 這個安全狀態並中止交易,不會卡死在中間態。
/// </summary>
public sealed class CheckInCardIssuer
{
    private readonly ICardDispenserHardware _hw;
    private readonly IssuerOptions _options;

    /// <summary>每次狀態轉移都會觸發,供 UI/Console 印出流程軌跡,方便 demo 觀察。</summary>
    public event Action<DispenseState, string>? StateChanged;

    public DispenseState CurrentState { get; private set; } = DispenseState.Idle;

    public CheckInCardIssuer(ICardDispenserHardware hardware, IssuerOptions? options = null)
    {
        _hw = hardware;
        _options = options ?? new IssuerOptions();
    }

    private void Transition(DispenseState state, string message)
    {
        CurrentState = state;
        StateChanged?.Invoke(state, message);
    }

    private Task Backoff(CancellationToken ct) => Task.Delay(_options.RetryBackoff, ct);

    public async Task<IssueResult> IssueCardAsync(HotelKeyData keyData, CancellationToken ct = default)
    {
        int swapCount = 0;      // 本交易累計換卡次數
        int cardsConsumed = 0;  // 本交易累計消耗(含成功發出或被吞)的卡片數

        try
        {
            while (true)
            {
                // 1) 庫存檢查
                Transition(DispenseState.StockCheck, "檢查卡匣庫存");
                var stock = await _hw.CheckHopperAsync(ct);
                Transition(DispenseState.StockCheck, $"庫存剩餘 {stock.RemainingCount}/{stock.Capacity} 張");
                if (!stock.HasCard)
                {
                    Transition(DispenseState.OutOfStock, "卡匣已無庫存,通知櫃檯補卡並中止交易");
                    return IssueResult.Fail(DispenseState.OutOfStock, FaultCode.OutOfStock, cardsConsumed,
                        "卡匣庫存不足,請通知櫃檯補卡");
                }

                // 2) 推卡到讀寫位置
                if (!await TryDispenseAsync(ct))
                {
                    // 不可盲目持續重推以免疊卡,達上限即判定卡住、停機告警
                    Transition(DispenseState.Jam, "推卡多次仍未到位,判定卡住(Jam),停機告警");
                    return IssueResult.Fail(DispenseState.Jam, FaultCode.DispenseJam, cardsConsumed,
                        "發卡機構卡(Jam),已停機並觸發告警,需人工排除");
                }

                // 3) 定位到 RF/IC 讀寫頭(純機構動作,demo 中視為推卡到位後立即完成)
                Transition(DispenseState.Positioning, "定位至 RF/IC 讀寫頭完成");

                // 4) 讀卡判斷
                if (!await TryReadAsync(ct))
                {
                    if (!await CaptureCurrentCardAsync(isGuestNoTake: false, ct))
                    {
                        return IssueResult.Fail(DispenseState.CaptureBinFull, FaultCode.CaptureBinFull, cardsConsumed,
                            "回收艙已滿,無法安全吞卡,停機告警");
                    }
                    cardsConsumed++;
                    swapCount++;
                    if (swapCount > _options.MaxCardSwapsPerTransaction)
                    {
                        Transition(DispenseState.Aborted, "換卡次數超過交易上限,中止交易並告警");
                        return IssueResult.Fail(DispenseState.Aborted, FaultCode.CardSwapLimitExceeded, cardsConsumed,
                            $"已連續更換 {swapCount} 張卡仍無法完成讀卡,超過換卡上限({_options.MaxCardSwapsPerTransaction}),中止交易並通知櫃檯");
                    }
                    continue; // 換下一張卡,回到庫存檢查
                }

                // 5) 寫卡 + 回讀驗證(驗證不一致視為寫卡失敗,合併在同一組重試上限內)
                if (!await TryWriteAndVerifyAsync(keyData, ct))
                {
                    if (!await CaptureCurrentCardAsync(isGuestNoTake: false, ct))
                    {
                        return IssueResult.Fail(DispenseState.CaptureBinFull, FaultCode.CaptureBinFull, cardsConsumed,
                            "回收艙已滿,無法安全吞卡,停機告警");
                    }
                    cardsConsumed++;
                    swapCount++;
                    if (swapCount > _options.MaxCardSwapsPerTransaction)
                    {
                        Transition(DispenseState.Aborted, "換卡次數超過交易上限,中止交易並告警");
                        return IssueResult.Fail(DispenseState.Aborted, FaultCode.CardSwapLimitExceeded, cardsConsumed,
                            $"已連續更換 {swapCount} 張卡仍無法完成寫卡,超過換卡上限({_options.MaxCardSwapsPerTransaction}),中止交易並通知櫃檯");
                    }
                    continue;
                }

                // 6) 出卡給旅客
                Transition(DispenseState.Ejecting, "推送出卡至出卡口");
                await _hw.EjectToGuestAsync(ct);

                // 7) 等待旅客取卡(逾時)
                Transition(DispenseState.WaitGuestTake, $"等待旅客取卡,逾時秒數 {_options.GuestTakeTimeout.TotalSeconds:0.#}");
                var taken = await _hw.WaitGuestTakeAsync(_options.GuestTakeTimeout, ct);
                if (!taken.Taken)
                {
                    if (!await CaptureCurrentCardAsync(isGuestNoTake: true, ct))
                    {
                        return IssueResult.Fail(DispenseState.CaptureBinFull, FaultCode.CaptureBinFull, cardsConsumed,
                            "旅客逾時未取且回收艙已滿,停機告警,需人工立即處理");
                    }
                    cardsConsumed++;
                    return IssueResult.Fail(DispenseState.Recapture, FaultCode.GuestDidNotTakeCard, cardsConsumed,
                        "旅客逾時未取卡,已基於安全考量吞卡回收並通知櫃檯");
                }

                cardsConsumed++;
                Transition(DispenseState.Completed, $"發卡完成,房號 {keyData.RoomNo}");
                return IssueResult.Ok(DispenseState.Completed, keyData.RoomNo, cardsConsumed, "發卡成功,交易完成");
            }
        }
        catch (HardwareCommunicationException ex)
        {
            // 與機台通訊逾時:讓流程收斂到安全狀態並中止,不可卡死在中間態、也不可無限重試通訊
            Transition(DispenseState.CommunicationError, $"機台通訊逾時,進入安全狀態並中止交易: {ex.Message}");
            return IssueResult.Fail(DispenseState.CommunicationError, FaultCode.CommunicationError, cardsConsumed,
                $"與發卡機通訊逾時,已中止交易並進入安全狀態: {ex.Message}");
        }
    }

    private async Task<bool> TryDispenseAsync(CancellationToken ct)
    {
        Transition(DispenseState.Dispensing, "推卡至讀寫位置");
        for (int attempt = 1; attempt <= _options.MaxDispenseRetries; attempt++)
        {
            var gate = await _hw.DispenseToReadWriteAsync(ct);
            if (gate.CardDetected)
            {
                Transition(DispenseState.Dispensing, $"推卡到位(第 {attempt} 次嘗試): {gate.Message}");
                return true;
            }
            Transition(DispenseState.Dispensing, $"推卡未到位(第 {attempt}/{_options.MaxDispenseRetries} 次): {gate.Message}");
            if (attempt < _options.MaxDispenseRetries)
            {
                await Backoff(ct);
            }
        }
        return false;
    }

    private async Task<bool> TryReadAsync(CancellationToken ct)
    {
        Transition(DispenseState.Reading, "讀取卡片");
        for (int attempt = 1; attempt <= _options.MaxReadRetries; attempt++)
        {
            var result = await _hw.ReadCardAsync(ct);
            if (result.Success && result.IsBlankUsable)
            {
                Transition(DispenseState.Reading, $"讀卡成功(第 {attempt} 次): 卡別={result.DetectedType}, UID={result.Uid}");
                return true;
            }
            Transition(DispenseState.Reading, $"讀卡失敗(第 {attempt}/{_options.MaxReadRetries} 次): {result.Message}");
            if (attempt < _options.MaxReadRetries)
            {
                await Backoff(ct);
            }
        }
        return false;
    }

    private async Task<bool> TryWriteAndVerifyAsync(HotelKeyData keyData, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= _options.MaxWriteRetries; attempt++)
        {
            Transition(DispenseState.Writing, $"寫入房卡資料(第 {attempt} 次): 房號={keyData.RoomNo}");
            var writeResult = await _hw.WriteCardAsync(keyData, ct);
            if (!writeResult.Success)
            {
                Transition(DispenseState.Writing, $"寫卡失敗(第 {attempt}/{_options.MaxWriteRetries} 次): {writeResult.Message}");
                if (attempt < _options.MaxWriteRetries)
                {
                    await Backoff(ct);
                }
                continue;
            }

            Transition(DispenseState.Verifying, $"回讀驗證(第 {attempt} 次)");
            var verifyResult = await _hw.VerifyAsync(keyData, ct);
            if (verifyResult.Success)
            {
                Transition(DispenseState.Verifying, "回讀驗證一致");
                return true;
            }

            // 驗證不一致視為寫卡失敗,回到 Writing 重試(而非直接吞卡)
            Transition(DispenseState.Writing, $"回讀驗證不一致(第 {attempt}/{_options.MaxWriteRetries} 次),視為寫卡失敗: {verifyResult.Message}");
            if (attempt < _options.MaxWriteRetries)
            {
                await Backoff(ct);
            }
        }
        return false;
    }

    private async Task<bool> CaptureCurrentCardAsync(bool isGuestNoTake, CancellationToken ct)
    {
        // 不變式:吞卡前一定先檢查回收艙是否已滿,滿了就不能再吞
        if (await _hw.IsCaptureBinFullAsync(ct))
        {
            Transition(DispenseState.CaptureBinFull, "回收艙已滿,無法再吞卡,停機告警");
            return false;
        }

        if (isGuestNoTake)
        {
            Transition(DispenseState.Recapture, "旅客逾時未取,基於安全考量吞卡回收");
            await _hw.RecaptureAsync(ct);
        }
        else
        {
            Transition(DispenseState.CaptureFaulty, "卡片異常,吞卡至回收艙,準備更換新卡");
            await _hw.CaptureToRejectAsync(ct);
        }
        return true;
    }
}
