using CardDispenser.Core.Models;

namespace CardDispenser.Core.Offline;

/// <summary>門鎖對一次刷卡的判斷結果。</summary>
public sealed record LockDecision(bool Granted, string Reason);

/// <summary>
/// 離線智慧門鎖模擬(飯店常見的 autonomous/offline lock)。
/// 鎖本身不連網,權限與效期完全靠卡片上的離線憑證(<see cref="GuestCardCredential"/>)判斷;
/// 鎖的身分(PropertyId/DoorId)、金鑰與時鐘則靠設定卡(母卡,<see cref="ProgrammingCard"/>)佈建。
/// </summary>
public sealed class SimulatedOfflineLock
{
    public ushort PropertyId { get; private set; }
    public string DoorId { get; private set; } = string.Empty;

    /// <summary>鎖端離線 RTC。沒有網路對時,只能靠設定卡佈建當下校準,之後就自行走時。</summary>
    public DateTime ClockUtc { get; private set; }

    public bool IsProvisioned { get; private set; }

    /// <summary>本門的門類型,決定 <see cref="CheckDoorAuthority"/> 用哪一種邏輯判斷通行權限。</summary>
    public DoorKind Kind { get; private set; } = DoorKind.GuestRoom;

    /// <summary>Kind=Facility 時,本門要求的設施旗標。</summary>
    public AccessFlags RequiredFacility { get; private set; } = AccessFlags.None;

    /// <summary>Kind=ElevatorFloor 時,本門對應的樓層(1-based)。</summary>
    public int ElevatorFloor { get; private set; }

    private byte[] _siteKey = Array.Empty<byte>();

    // 一個 SimulatedOfflineLock 實例代表一把鎖(一個門),所以用單一序號即可;
    // 若要模擬同一案場下多把鎖各自比對序號,天然就是各自一個實例、各自一份序號。
    private uint _lastSeenSequence;

    /// <summary>用設定卡佈建鎖:設定案場/門代號/案場金鑰,並校時。</summary>
    public void ProvisionWith(ProgrammingCard programmingCard)
    {
        PropertyId = programmingCard.PropertyId;
        DoorId = programmingCard.DoorId;
        _siteKey = programmingCard.SiteKey;
        ClockUtc = programmingCard.SyncClockUtc;
        Kind = programmingCard.Kind;
        RequiredFacility = programmingCard.RequiredFacility;
        ElevatorFloor = programmingCard.ElevatorFloor;
        IsProvisioned = true;
        _lastSeenSequence = 0;
    }

    /// <summary>
    /// 離線驗證並決定是否開門。依序檢查:
    /// 1. 是否已佈建 2. 憑證讀取/簽章 3. 案場是否相符 4. 是否在效期內
    /// 5. 門禁權限是否相符 6. 序號是否已被較新卡蓋掉。
    /// 任何一關失敗立即回傳明確拒絕理由;全部通過才開門,並更新已記錄序號
    /// (退房蓋卡機制的關鍵:下一張新卡序號更大,舊卡日後刷卡就會在第 6 關被擋下)。
    /// </summary>
    public Task<LockDecision> PresentCardAsync(MifareClassicCard card)
    {
        if (!IsProvisioned)
        {
            return Task.FromResult(new LockDecision(false, "鎖尚未用母卡設定(未佈建),拒絕任何卡片"));
        }

        var (ok, credential, error) = TryReadCredential(card);
        if (!ok || credential is null)
        {
            return Task.FromResult(new LockDecision(false, error));
        }

        if (credential.PropertyId != PropertyId)
        {
            return Task.FromResult(new LockDecision(false,
                $"案場代碼不符(卡片案場={credential.PropertyId}, 本鎖案場={PropertyId}),拒絕"));
        }

        if (ClockUtc < credential.Access.CheckInUtc || ClockUtc > credential.Access.CheckOutUtc)
        {
            return Task.FromResult(new LockDecision(false,
                $"卡片效期不符(有效區間 {credential.Access.CheckInUtc:u} ~ {credential.Access.CheckOutUtc:u},鎖端時間 {ClockUtc:u}),拒絕(已過期或尚未生效)"));
        }

        var (authorized, authorityReason) = CheckDoorAuthority(credential);
        if (!authorized)
        {
            return Task.FromResult(new LockDecision(false, authorityReason));
        }

        if (credential.CardSequence < _lastSeenSequence)
        {
            return Task.FromResult(new LockDecision(false,
                $"卡片序號已被較新卡片蓋掉(卡片序號={credential.CardSequence}, 已記錄序號={_lastSeenSequence}),拒絕——這就是離線退房免連網的蓋卡機制"));
        }

        _lastSeenSequence = credential.CardSequence;
        return Task.FromResult(new LockDecision(true,
            $"開門成功(房號={credential.Access.RoomNo}, 序號={credential.CardSequence})"));
    }

    /// <summary>
    /// 依本門的 <see cref="Kind"/> 判斷這張卡是否有權限通過,對應 <see cref="PresentCardAsync"/> 的第 5 關。
    /// 取代舊版「只比對房號」的簡化邏輯——不同門類型看的旗標/資料不同:
    /// GuestRoom 看 GuestRoom 旗標 + 房號;Facility 看對應設施旗標;ElevatorFloor 看
    /// Elevator 旗標 + 樓層是否落在 <see cref="Models.HotelKeyData.ElevatorFloorMask"/> 內。
    /// </summary>
    private (bool authorized, string reason) CheckDoorAuthority(GuestCardCredential credential)
    {
        switch (Kind)
        {
            case DoorKind.GuestRoom:
                if (!credential.Access.AccessFlags.HasFlag(AccessFlags.GuestRoom))
                {
                    return (false, "門禁權限不符(卡片缺少 GuestRoom 旗標),拒絕");
                }
                if (!string.Equals(credential.Access.RoomNo, DoorId, StringComparison.Ordinal))
                {
                    return (false,
                        $"門禁權限不符(卡片房號={credential.Access.RoomNo}, 本門={DoorId}),拒絕");
                }
                return (true, string.Empty);

            case DoorKind.Facility:
                if (!credential.Access.AccessFlags.HasFlag(RequiredFacility))
                {
                    return (false,
                        $"門禁權限不符(本設施門({DoorId})需要 {RequiredFacility} 旗標,卡片沒有),拒絕");
                }
                return (true, string.Empty);

            case DoorKind.ElevatorFloor:
                if (!credential.Access.AccessFlags.HasFlag(AccessFlags.Elevator))
                {
                    return (false,
                        $"門禁權限不符(電梯樓層門({DoorId})需要 Elevator 旗標,卡片沒有),拒絕");
                }
                var floorBit = 1 << (ElevatorFloor - 1);
                if ((credential.Access.ElevatorFloorMask & floorBit) == 0)
                {
                    return (false,
                        $"門禁權限不符(卡片電梯樓層遮罩 0x{credential.Access.ElevatorFloorMask:X4} 不含樓層 {ElevatorFloor}),拒絕");
                }
                return (true, string.Empty);

            default:
                return (false, $"未知門類型({Kind}),拒絕");
        }
    }

    /// <summary>讀卡 + 結構驗證 + 簽章驗證,對應 <see cref="PresentCardAsync"/> 的第 2 關。</summary>
    private (bool ok, GuestCardCredential? credential, string error) TryReadCredential(MifareClassicCard card)
    {
        if (!card.Authenticate(CardSectors.GuestDataSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey) ||
            !card.Authenticate(CardSectors.EnvelopeSector, MifareKeyType.KeyA, MifareClassicCard.FactoryDefaultKey))
        {
            return (false, null, "讀不到憑證(sector 驗證失敗),拒絕");
        }

        HotelKeyData? access;
        GuestCardCredential? credential;
        try
        {
            var block4 = card.ReadBlock(CardSectors.GuestDataSector, 0);
            var block5 = card.ReadBlock(CardSectors.GuestDataSector, 1);
            var block6 = card.ReadBlock(CardSectors.GuestDataSector, 2);
            if (!HotelKeyData.TryParse(block4, block5, block6, out access, out var accessError) || access is null)
            {
                return (false, null, $"讀不到憑證(房卡資料結構驗證失敗: {accessError}),拒絕");
            }

            var env0 = card.ReadBlock(CardSectors.EnvelopeSector, 0);
            var env1 = card.ReadBlock(CardSectors.EnvelopeSector, 1);
            var env2 = card.ReadBlock(CardSectors.EnvelopeSector, 2);
            if (!GuestCardCredential.TryParse(env0, env1, env2, access, out credential, out var envError) || credential is null)
            {
                return (false, null, $"讀不到憑證(離線信封結構驗證失敗: {envError}),拒絕");
            }
        }
        catch (Exception ex)
        {
            return (false, null, $"讀不到憑證(讀卡例外: {ex.Message}),拒絕");
        }

        if (!CredentialSecurity.Verify(credential.ToSignableBytes(), credential.Signature, _siteKey))
        {
            return (false, null, "簽章驗證失敗(案場金鑰不符或資料遭竄改),拒絕");
        }

        return (true, credential, string.Empty);
    }
}
