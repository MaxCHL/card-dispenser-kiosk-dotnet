using CardDispenser.Core.Hardware;
using CardDispenser.Core.Models;
using CardDispenser.Core.Offline;

namespace CardDispenser.Sim;

/// <summary>
/// 記憶體中的模擬發卡機硬體。沒有任何真實裝置——卡匣、回收艙、退卡艙都只是記憶體狀態,
/// 卡片本體用 <see cref="MifareClassicCard"/> 物件表示。支援 <see cref="FaultInjectionConfig"/>
/// 故障注入,讓 demo 能穩定重現每一個 fallback 分支。
///
/// 房卡資料固定寫入 sector 1 的 block 4~6(對應 sector 內 blockInSector 0~2,block 3 是 trailer)。
///
/// 可選的離線 data-on-card 模型(見建構子的 <c>propertyId</c>/<c>siteKeyRing</c>/
/// <c>nextSequenceProvider</c> 三個參數):若有提供,<see cref="WriteCardAsync"/> 除了原本
/// 寫入 <see cref="HotelKeyData"/> 之外,還會額外組出 <see cref="GuestCardCredential"/>
/// 離線安全信封(案場代碼 + 遞增序號 + 簽章)寫入 <see cref="CardSectors.EnvelopeSector"/>。
/// 未提供這三個參數(預設值)時,行為與原本完全相同,不寫入任何信封——這是刻意的加法式
/// 設計,原有 9 個情境不受影響。
/// </summary>
public sealed class SimulatedDispenserHardware : ICardDispenserHardware
{
    private const int DataSector = 1;

    private readonly FaultInjectionConfig _config;
    private readonly int _hopperCapacity;
    private readonly ushort? _propertyId;
    private readonly SiteKeyRing? _siteKeyRing;
    private readonly Func<string, uint>? _nextSequenceProvider;

    private int _hopperCount;
    private int _captureBinCount;
    private int _cardSequence;
    private MifareClassicCard? _currentCard;

    /// <summary>
    /// 上一次成功完成「寫入 + 回讀驗證」的卡片物件。用於離線模型的整合情境
    /// (Part B7):報到機流程走完後,拿這張卡直接交給房門鎖驗證,串成
    /// 「自助報到 → 房門開啟」。<see cref="WaitGuestTakeAsync"/> 之後會把
    /// <c>_currentCard</c> 清空(卡片已離開機器交給旅客),但這個屬性仍保留參考。
    /// </summary>
    public MifareClassicCard? LastIssuedCard { get; private set; }

    public SimulatedDispenserHardware(
        FaultInjectionConfig config,
        ushort? propertyId = null,
        SiteKeyRing? siteKeyRing = null,
        Func<string, uint>? nextSequenceProvider = null)
    {
        _config = config;
        _hopperCapacity = config.InitialHopperCount;
        _hopperCount = config.InitialHopperCount;
        _captureBinCount = config.CaptureBinStartsFull ? config.CaptureBinCapacity : 0;
        _propertyId = propertyId;
        _siteKeyRing = siteKeyRing;
        _nextSequenceProvider = nextSequenceProvider;
    }

    private void ThrowIfInjected(HardwareCall call)
    {
        if (_config.CommunicationTimeoutOn == call)
        {
            throw new HardwareCommunicationException($"模擬與發卡機控制板通訊逾時(呼叫點: {call})");
        }
    }

    public async Task<CardStock> CheckHopperAsync(CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.CheckHopper);
        await Task.Delay(30, ct);
        return new CardStock(_hopperCount, _hopperCapacity);
    }

    public async Task<GateResult> DispenseToReadWriteAsync(CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.Dispense);
        await Task.Delay(60, ct);

        if (_hopperCount <= 0)
        {
            return new GateResult(false, "卡匣已空,無法推卡");
        }

        // 每一次推卡動作,實體上都會有一張卡離開卡匣進入傳送機構——
        // 即使卡住(Jam),那張卡physically 也已經離開卡匣、卡在機構裡了。
        // 這正是「不可盲目重推以免疊卡」的來源:每重推一次就會再消耗一張卡卡進機構。
        _hopperCount--;

        if (_config.JamEveryDispense)
        {
            return new GateResult(false, "閘門 sensor 未偵測到卡到位(卡片卡在傳送路徑中)");
        }

        _cardSequence++;
        _currentCard = MifareClassicCard.CreateBlank(GenerateUid(_cardSequence));
        return new GateResult(true, $"閘門 sensor 偵測到卡到位(本交易第 {_cardSequence} 張卡)");
    }

    public async Task<ReadResult> ReadCardAsync(CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.Read);
        await Task.Delay(80, ct);

        if (_currentCard is null)
        {
            return new ReadResult(false, CardType.Unknown, null, false, null, null, "讀寫頭未偵測到卡片");
        }

        if (_config.ReadAlwaysFails || _config.ReadFailCardSequences.Contains(_cardSequence))
        {
            return new ReadResult(false, CardType.Unknown, _currentCard.Uid, false, null, null,
                "讀取逾時或訊號雜訊過大,無法辨識卡片");
        }

        // 用出廠預設 Key A 驗證資料 sector,驗證成功且資料區仍為出廠預設值 => 視為空白可用卡
        bool authOk = _currentCard.Authenticate(DataSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey);
        bool isBlank = authOk && _currentCard.IsFactoryBlank(DataSector);

        if (!authOk)
        {
            return new ReadResult(true, CardType.Unknown, _currentCard.Uid, false, null, null,
                "無法用預設金鑰驗證,可能為非本系統卡片或金鑰已變更");
        }

        if (!isBlank)
        {
            return new ReadResult(true, CardType.MifareClassic1K, _currentCard.Uid, false, _currentCard, null,
                "驗證通過但資料區非出廠空白狀態,可能為已使用過的房卡");
        }

        return new ReadResult(true, CardType.MifareClassic1K, _currentCard.Uid, true, _currentCard, null,
            "已辨識為 Mifare Classic 1K 出廠空白卡");
    }

    public async Task<WriteResult> WriteCardAsync(HotelKeyData data, CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.Write);
        await Task.Delay(100, ct);

        if (_currentCard is null)
        {
            return new WriteResult(false, "無卡片於讀寫位置");
        }

        if (_config.WriteFailCardSequences.Contains(_cardSequence))
        {
            return new WriteResult(false, "寫入逾時或 IC 通訊異常,寫入未完成");
        }

        try
        {
            if (!_currentCard.IsAuthenticated(DataSector) &&
                !_currentCard.Authenticate(DataSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey))
            {
                return new WriteResult(false, $"Sector {DataSector} 驗證失敗,無法寫入");
            }

            var bytes = data.ToBytes();
            _currentCard.WriteBlock(DataSector, 0, bytes[0..16]);
            _currentCard.WriteBlock(DataSector, 1, bytes[16..32]);
            _currentCard.WriteBlock(DataSector, 2, bytes[32..48]);

            // 離線 data-on-card 模型(選用):額外組出安全信封(案場代碼 + 遞增序號 + 簽章)
            // 寫入信封 sector。三個參數缺一都視為未啟用離線模型,維持原本只寫 HotelKeyData 的行為。
            if (_propertyId.HasValue && _siteKeyRing is not null && _nextSequenceProvider is not null)
            {
                var sequence = _nextSequenceProvider(data.RoomNo);
                var siteKey = _siteKeyRing.GetKeyOrThrow(_propertyId.Value);
                var signable = GuestCardCredential.ComputeSignableBytes(_propertyId.Value, sequence, data);
                var signature = CredentialSecurity.Sign(signable, siteKey);
                var credential = new GuestCardCredential
                {
                    PropertyId = _propertyId.Value,
                    CardSequence = sequence,
                    Access = data,
                    Signature = signature,
                };

                if (!_currentCard.IsAuthenticated(CardSectors.EnvelopeSector) &&
                    !_currentCard.Authenticate(CardSectors.EnvelopeSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey))
                {
                    return new WriteResult(false, $"Sector {CardSectors.EnvelopeSector} 驗證失敗,無法寫入離線信封");
                }

                var envelopeBytes = credential.ToPayload();
                _currentCard.WriteBlock(CardSectors.EnvelopeSector, 0, envelopeBytes[0..16]);
                _currentCard.WriteBlock(CardSectors.EnvelopeSector, 1, envelopeBytes[16..32]);
                _currentCard.WriteBlock(CardSectors.EnvelopeSector, 2, envelopeBytes[32..48]);
            }
        }
        catch (Exception ex)
        {
            return new WriteResult(false, $"寫入例外: {ex.Message}");
        }

        return new WriteResult(true, "房卡資料寫入完成");
    }

    public async Task<VerifyResult> VerifyAsync(HotelKeyData expected, CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.Verify);
        await Task.Delay(60, ct);

        if (_currentCard is null)
        {
            return new VerifyResult(false, "無卡片於讀寫位置");
        }

        if (_config.VerifyMismatchCardSequences.Contains(_cardSequence))
        {
            return new VerifyResult(false, "回讀資料與寫入資料不一致(模擬雜訊造成的位元錯誤)");
        }

        try
        {
            var block4 = _currentCard.ReadBlock(DataSector, 0);
            var block5 = _currentCard.ReadBlock(DataSector, 1);
            var block6 = _currentCard.ReadBlock(DataSector, 2);

            // 先驗證結構完整性(格式辨識碼/版本/CRC),再比對業務內容
            if (!HotelKeyData.TryParse(block4, block5, block6, out _, out var parseError))
            {
                return new VerifyResult(false, $"回讀資料結構驗證失敗: {parseError}");
            }

            var actualBytes = new byte[HotelKeyData.EncodedLength];
            Array.Copy(block4, 0, actualBytes, 0, 16);
            Array.Copy(block5, 0, actualBytes, 16, 16);
            Array.Copy(block6, 0, actualBytes, 32, 16);
            var expectedBytes = expected.ToBytes();

            if (!expectedBytes.AsSpan().SequenceEqual(actualBytes))
            {
                return new VerifyResult(false, "回讀資料與寫入資料不一致");
            }
        }
        catch (Exception ex)
        {
            return new VerifyResult(false, $"回讀例外: {ex.Message}");
        }

        // 這是「確認寫入且驗證都通過」的那張卡,記下來讓 Part B7 這類整合情境
        // 能在整趟報到流程跑完後,取回同一張卡物件直接交給房門鎖驗證。
        LastIssuedCard = _currentCard;
        return new VerifyResult(true, "回讀驗證通過(結構與內容皆一致)");
    }

    public async Task EjectToGuestAsync(CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.Eject);
        await Task.Delay(40, ct);
        // 卡片狀態上視為「停留在出卡口」,交由 WaitGuestTakeAsync/RecaptureAsync 後續處理
    }

    public async Task<GuestTakeResult> WaitGuestTakeAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.WaitGuestTake);

        if (_config.GuestNeverTakesCard)
        {
            await Task.Delay(timeout, ct);
            return new GuestTakeResult(false, "逾時,旅客未取卡");
        }

        // 模擬旅客很快就取走卡片
        await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
        _currentCard = null;
        return new GuestTakeResult(true, "旅客已取卡");
    }

    public async Task<CaptureResult> RecaptureAsync(CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.Recapture);
        await Task.Delay(50, ct);

        // 呼叫端(CheckInCardIssuer)已事先確認回收艙未滿才會呼叫這裡,此處不重複判斷
        _captureBinCount++;
        _currentCard = null;
        return new CaptureResult(true, "已將未取走的卡片拉回並存入回收艙");
    }

    public async Task<CaptureResult> CaptureToRejectAsync(CancellationToken ct = default)
    {
        ThrowIfInjected(HardwareCall.CaptureToReject);
        await Task.Delay(50, ct);

        _captureBinCount++;
        _currentCard = null;
        return new CaptureResult(true, "異常卡片已吞入回收艙");
    }

    public Task<bool> IsGateClearAsync(CancellationToken ct = default) => Task.FromResult(_currentCard is null);

    public Task<bool> IsCaptureBinFullAsync(CancellationToken ct = default) =>
        Task.FromResult(_captureBinCount >= _config.CaptureBinCapacity);

    private static string GenerateUid(int cardSequence) => $"04{cardSequence % 256:X2}BEEF";
}
