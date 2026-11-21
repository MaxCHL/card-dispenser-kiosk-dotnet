using CardDispenser.Core.Models;

namespace CardDispenser.Core.Offline;

/// <summary>偵測到的卡片資訊。</summary>
public sealed record EncoderCardInfo(bool CardPresent, CardType DetectedType, string? Uid, bool IsBlank, string Message);

/// <summary>
/// 飯店 encoder(製卡/寫卡機)SDK 的代表性抽象介面(representative abstraction of a
/// hotel card encoder SDK)。
///
/// <para>
/// <b>重要聲明:這不是任何廠商的真實專有 API。</b> 飯店電子鎖廠商的 encoder SDK 通常
/// 未公開文件,本專案無從得知、也未曾參考任何廠商的真實型別或
/// 方法簽章。這個介面是依可查證的產業通用架構(飯店 encoder 透過 USB/序列埠連線讀寫
/// 卡片,典型會支援 detect / read / issue / cancel / event-log 這組操作)自行設計的
/// 代表性建模,純粹用來示範「離線 data-on-card」概念,不應被誤認為任何廠商的真實
/// 介面定義。
/// </para>
/// </summary>
public interface IHotelCardEncoder
{
    /// <summary>與 encoder 建立連線(USB/序列埠 handshake 的代表性模擬)。</summary>
    Task ConnectAsync();

    /// <summary>中斷與 encoder 的連線。</summary>
    Task DisconnectAsync();

    /// <summary>偵測目前放置於 encoder 讀寫頭上的卡片(卡別/UID/是否空白)。</summary>
    Task<EncoderCardInfo> DetectCardAsync();

    /// <summary>讀回卡片上的離線憑證並驗證簽章;結構或簽章驗證失敗時回傳 null。</summary>
    Task<GuestCardCredential?> ReadCredentialAsync();

    /// <summary>將已簽章的離線憑證寫入卡片(房卡業務資料 + 離線安全信封)。</summary>
    Task IssueGuestCardAsync(MifareClassicCard card, GuestCardCredential credential);

    /// <summary>作廢卡片上的憑證(例如卡片印錯、旅客臨時取消入住)。</summary>
    Task CancelCardAsync(MifareClassicCard card);

    /// <summary>讀取 encoder 機台端事件紀錄(連線、發卡、作廢等操作軌跡)。</summary>
    Task<IReadOnlyList<string>> ReadEventLogAsync();
}
