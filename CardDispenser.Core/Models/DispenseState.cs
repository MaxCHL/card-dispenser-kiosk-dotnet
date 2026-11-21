namespace CardDispenser.Core.Models;

/// <summary>
/// 發卡流程狀態機的所有狀態,涵蓋 happy path 與各種 fallback / 錯誤狀態。
/// </summary>
public enum DispenseState
{
    /// <summary>待機。</summary>
    Idle,

    /// <summary>偵測卡匣庫存。</summary>
    StockCheck,

    /// <summary>馬達推卡到讀寫位置。</summary>
    Dispensing,

    /// <summary>定位到 RF/IC 讀寫頭。</summary>
    Positioning,

    /// <summary>讀卡判斷型別/UID/是否為空白可用卡。</summary>
    Reading,

    /// <summary>寫入房卡資料。</summary>
    Writing,

    /// <summary>回讀驗證剛寫入的資料。</summary>
    Verifying,

    /// <summary>推送出卡給旅客。</summary>
    Ejecting,

    /// <summary>等待旅客取卡(有逾時)。</summary>
    WaitGuestTake,

    /// <summary>交易完成。</summary>
    Completed,

    // ---- fallback / 錯誤狀態 ----

    /// <summary>卡匣庫存為空,通知櫃檯補卡並中止交易。</summary>
    OutOfStock,

    /// <summary>推卡多次仍未到位,判定卡住,停機告警。</summary>
    Jam,

    /// <summary>讀卡/寫卡/驗證失敗,吞卡至回收艙(準備換新卡重試)。</summary>
    CaptureFaulty,

    /// <summary>旅客逾時未取卡,基於安全考量吞卡回收。</summary>
    Recapture,

    /// <summary>回收艙已滿,無法再吞卡,停機告警。</summary>
    CaptureBinFull,

    /// <summary>與發卡機控制板通訊逾時,進入安全狀態並中止交易。</summary>
    CommunicationError,

    /// <summary>交易中止(例如換卡次數超過上限)。</summary>
    Aborted,
}
