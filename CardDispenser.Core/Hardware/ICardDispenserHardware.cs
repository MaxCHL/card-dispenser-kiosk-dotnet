using CardDispenser.Core.Models;

namespace CardDispenser.Core.Hardware;

/// <summary>
/// 發卡機硬體抽象介面。所有機械/IO 動作皆為 async,以貼近真實硬體透過 RS-232/USB 等
/// 序列通訊與馬達/sensor 溝通時的非同步特性。正式環境會由對應廠商 SDK 實作此介面;
/// 本專案只有 <c>CardDispenser.Sim</c> 中的記憶體模擬實作,沒有任何真實硬體。
/// </summary>
public interface ICardDispenserHardware
{
    /// <summary>偵測卡匣庫存(有無卡片、剩餘數量)。</summary>
    Task<CardStock> CheckHopperAsync(CancellationToken ct = default);

    /// <summary>馬達推卡到讀寫位置,回傳閘門 sensor 是否偵測到卡片到位。</summary>
    Task<GateResult> DispenseToReadWriteAsync(CancellationToken ct = default);

    /// <summary>讀卡:辨識卡別、讀 UID、判斷是否為可沿用的空白卡。</summary>
    Task<ReadResult> ReadCardAsync(CancellationToken ct = default);

    /// <summary>對目前讀寫位置的卡片寫入房卡資料。</summary>
    Task<WriteResult> WriteCardAsync(HotelKeyData data, CancellationToken ct = default);

    /// <summary>回讀剛寫入的資料並比對是否與寫入內容一致。</summary>
    Task<VerifyResult> VerifyAsync(HotelKeyData expected, CancellationToken ct = default);

    /// <summary>將卡片推送到出卡口,交給旅客。</summary>
    Task EjectToGuestAsync(CancellationToken ct = default);

    /// <summary>等待旅客於出卡口取卡,逾時未取則回傳 Taken=false。</summary>
    Task<GuestTakeResult> WaitGuestTakeAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>旅客逾時未取卡時,將卡片拉回並存入回收艙。</summary>
    Task<CaptureResult> RecaptureAsync(CancellationToken ct = default);

    /// <summary>讀寫階段判定卡片異常(讀失敗/非空白/寫失敗/驗證不一致)時,將卡片吞入回收艙。</summary>
    Task<CaptureResult> CaptureToRejectAsync(CancellationToken ct = default);

    /// <summary>查詢讀寫位置的閘門 sensor 目前是否淨空(無卡片停留)。</summary>
    Task<bool> IsGateClearAsync(CancellationToken ct = default);

    /// <summary>查詢回收艙是否已滿。吞卡前必須先呼叫這個查詢,滿了就不可再吞。</summary>
    Task<bool> IsCaptureBinFullAsync(CancellationToken ct = default);
}
