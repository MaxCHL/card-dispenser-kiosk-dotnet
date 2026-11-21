using CardDispenser.Core.Models;

namespace CardDispenser.Core.Offline;

/// <summary>
/// <see cref="IHotelCardEncoder"/> 的記憶體模擬實作。
/// 同樣是代表性模擬,非任何廠商真實 encoder 行為——完整聲明見 <see cref="IHotelCardEncoder"/>。
/// 底層直接重用既有 <see cref="MifareClassicCard"/> 的 Authenticate/ReadBlock/WriteBlock:
/// 房卡資料寫 <see cref="CardSectors.GuestDataSector"/>,離線信封寫 <see cref="CardSectors.EnvelopeSector"/>。
/// </summary>
public sealed class SimulatedHotelCardEncoder : IHotelCardEncoder
{
    private readonly SiteKeyRing _siteKeyRing;
    private readonly List<string> _eventLog = new();

    private bool _connected;
    private MifareClassicCard? _currentCard;

    public SimulatedHotelCardEncoder(SiteKeyRing siteKeyRing)
    {
        _siteKeyRing = siteKeyRing;
    }

    /// <summary>
    /// 模擬專用輔助方法(不屬於 <see cref="IHotelCardEncoder"/> 介面):代表「服務人員把卡片
    /// 放上 encoder 讀寫頭」這個物理動作。真實 encoder 這一步是硬體感應,不是 API 呼叫;
    /// 這裡只是讓 demo 能把一張 <see cref="MifareClassicCard"/> 接上這個模擬 encoder。
    /// </summary>
    public void InsertCard(MifareClassicCard? card) => _currentCard = card;

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new InvalidOperationException("Encoder 尚未連線,請先呼叫 ConnectAsync()");
        }
    }

    private void Log(string message) => _eventLog.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");

    public async Task ConnectAsync()
    {
        await Task.Delay(30);
        _connected = true;
        Log("Encoder 連線完成");
    }

    public async Task DisconnectAsync()
    {
        await Task.Delay(10);
        _connected = false;
        Log("Encoder 已中斷連線");
    }

    public async Task<EncoderCardInfo> DetectCardAsync()
    {
        EnsureConnected();
        await Task.Delay(20);

        if (_currentCard is null)
        {
            return new EncoderCardInfo(false, CardType.Unknown, null, false, "讀寫頭未偵測到卡片");
        }

        bool authOk = _currentCard.Authenticate(CardSectors.GuestDataSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey);
        bool isBlank = authOk && _currentCard.IsFactoryBlank(CardSectors.GuestDataSector);
        Log($"DetectCard: UID={_currentCard.Uid}, Blank={isBlank}");
        return new EncoderCardInfo(true, CardType.MifareClassic1K, _currentCard.Uid, isBlank,
            isBlank ? "偵測到空白卡" : "偵測到已寫入資料的卡片");
    }

    public async Task<GuestCardCredential?> ReadCredentialAsync()
    {
        EnsureConnected();
        await Task.Delay(40);

        if (_currentCard is null)
        {
            Log("ReadCredential 失敗:讀寫頭無卡片");
            return null;
        }

        if (!_currentCard.Authenticate(CardSectors.GuestDataSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey) ||
            !_currentCard.Authenticate(CardSectors.EnvelopeSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey))
        {
            Log($"ReadCredential 失敗:UID={_currentCard.Uid} sector 驗證失敗");
            return null;
        }

        HotelKeyData? access;
        GuestCardCredential? credential;
        try
        {
            var block4 = _currentCard.ReadBlock(CardSectors.GuestDataSector, 0);
            var block5 = _currentCard.ReadBlock(CardSectors.GuestDataSector, 1);
            var block6 = _currentCard.ReadBlock(CardSectors.GuestDataSector, 2);
            if (!HotelKeyData.TryParse(block4, block5, block6, out access, out var accessError) || access is null)
            {
                Log($"ReadCredential 失敗:UID={_currentCard.Uid} 房卡資料結構驗證失敗: {accessError}");
                return null;
            }

            var env0 = _currentCard.ReadBlock(CardSectors.EnvelopeSector, 0);
            var env1 = _currentCard.ReadBlock(CardSectors.EnvelopeSector, 1);
            var env2 = _currentCard.ReadBlock(CardSectors.EnvelopeSector, 2);
            if (!GuestCardCredential.TryParse(env0, env1, env2, access, out credential, out var envError) || credential is null)
            {
                Log($"ReadCredential 失敗:UID={_currentCard.Uid} 離線信封結構驗證失敗: {envError}");
                return null;
            }
        }
        catch (Exception ex)
        {
            Log($"ReadCredential 例外:UID={_currentCard.Uid} {ex.Message}");
            return null;
        }

        if (!_siteKeyRing.TryGetKey(credential.PropertyId, out var siteKey) || siteKey is null)
        {
            Log($"ReadCredential 失敗:UID={_currentCard.Uid} 案場 {credential.PropertyId} 無對應 site key");
            return null;
        }

        if (!CredentialSecurity.Verify(credential.ToSignableBytes(), credential.Signature, siteKey))
        {
            Log($"ReadCredential 失敗:UID={_currentCard.Uid} 簽章驗證失敗(案場金鑰不符或資料遭竄改)");
            return null;
        }

        Log($"ReadCredential 成功:UID={_currentCard.Uid}, Room={access.RoomNo}, Seq={credential.CardSequence}");
        return credential;
    }

    public async Task IssueGuestCardAsync(MifareClassicCard card, GuestCardCredential credential)
    {
        EnsureConnected();
        await Task.Delay(100);

        if (!card.Authenticate(CardSectors.GuestDataSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey))
        {
            throw new InvalidOperationException($"Sector {CardSectors.GuestDataSector} 驗證失敗,無法寫入房卡資料");
        }
        var accessBytes = credential.Access.ToBytes();
        card.WriteBlock(CardSectors.GuestDataSector, 0, accessBytes[0..16]);
        card.WriteBlock(CardSectors.GuestDataSector, 1, accessBytes[16..32]);
        card.WriteBlock(CardSectors.GuestDataSector, 2, accessBytes[32..48]);

        if (!card.Authenticate(CardSectors.EnvelopeSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey))
        {
            throw new InvalidOperationException($"Sector {CardSectors.EnvelopeSector} 驗證失敗,無法寫入離線信封");
        }
        var envelopeBytes = credential.ToPayload();
        card.WriteBlock(CardSectors.EnvelopeSector, 0, envelopeBytes[0..16]);
        card.WriteBlock(CardSectors.EnvelopeSector, 1, envelopeBytes[16..32]);
        card.WriteBlock(CardSectors.EnvelopeSector, 2, envelopeBytes[32..48]);

        _currentCard = card;
        Log($"IssueGuestCard 成功:UID={card.Uid}, Room={credential.Access.RoomNo}, PropertyId={credential.PropertyId}, Seq={credential.CardSequence}");
    }

    public async Task CancelCardAsync(MifareClassicCard card)
    {
        EnsureConnected();
        await Task.Delay(30);

        if (card.Authenticate(CardSectors.EnvelopeSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey))
        {
            // 代表性作廢方式:抹除信封的格式辨識碼/版本/序號,使日後任何讀取都判定為結構無效,
            // 等同讓這張卡的離線信封永久失效(真實廠商可能改用專屬的作廢旗標或黑名單機制)。
            card.WriteBlock(CardSectors.EnvelopeSector, 0, new byte[16]);
        }

        Log($"CancelCard: UID={card.Uid} 已作廢");
    }

    public Task<IReadOnlyList<string>> ReadEventLogAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(_eventLog.AsReadOnly());
    }
}
