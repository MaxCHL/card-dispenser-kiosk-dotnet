using CardDispenser.Core;
using CardDispenser.Core.Models;
using CardDispenser.Core.Offline;
using CardDispenser.Sim;

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("========================================================");
Console.WriteLine(" 飯店自助報到機 + 自動發卡機(Card Dispenser)發卡流程模擬 ");
Console.WriteLine(" -- 純軟體模擬,無真實硬體/SDK -- ");
Console.WriteLine("========================================================");

// demo 用的基礎流程參數:縮短逾時與退避時間,讓每個情境能在幾秒內跑完
var baseOptions = new IssuerOptions
{
    MaxDispenseRetries = 2,
    MaxReadRetries = 2,
    MaxWriteRetries = 2,
    MaxCardSwapsPerTransaction = 2,
    RetryBackoff = TimeSpan.FromMilliseconds(120),
    GuestTakeTimeout = TimeSpan.FromSeconds(1),
};

await RunScenario(
    "情境 1:正常發卡成功(happy path)",
    new FaultInjectionConfig { InitialHopperCount = 5, CaptureBinCapacity = 3 },
    baseOptions);

await RunScenario(
    "情境 2:卡匣庫存不足 -> OutOfStock,中止交易",
    new FaultInjectionConfig { InitialHopperCount = 0, CaptureBinCapacity = 3 },
    baseOptions);

await RunScenario(
    "情境 3:讀卡失敗 -> 吞卡 -> 換新卡成功",
    new FaultInjectionConfig { InitialHopperCount = 5, CaptureBinCapacity = 3, ReadFailCardSequences = { 1 } },
    baseOptions);

await RunScenario(
    "情境 4:寫卡後回讀驗證不一致 -> 吞卡 -> 換新卡成功",
    new FaultInjectionConfig { InitialHopperCount = 5, CaptureBinCapacity = 3, VerifyMismatchCardSequences = { 1 } },
    baseOptions);

await RunScenario(
    "情境 5:旅客逾時未取卡 -> 安全吞卡回收",
    new FaultInjectionConfig { InitialHopperCount = 5, CaptureBinCapacity = 3, GuestNeverTakesCard = true },
    baseOptions with { GuestTakeTimeout = TimeSpan.FromMilliseconds(500) });

await RunScenario(
    "情境 6:推卡卡住(Jam)-> 停機告警",
    new FaultInjectionConfig { InitialHopperCount = 5, CaptureBinCapacity = 3, JamEveryDispense = true },
    baseOptions);

await RunScenario(
    "情境 7:換卡次數超過交易上限 -> 中止交易",
    new FaultInjectionConfig { InitialHopperCount = 6, CaptureBinCapacity = 5, ReadAlwaysFails = true },
    baseOptions with { MaxCardSwapsPerTransaction = 2 });

await RunScenario(
    "情境 8(附加):回收艙已滿 -> 無法吞卡,停機告警",
    new FaultInjectionConfig
    {
        InitialHopperCount = 5,
        CaptureBinCapacity = 1,
        CaptureBinStartsFull = true,
        ReadFailCardSequences = { 1 },
    },
    baseOptions);

await RunScenario(
    "情境 9(附加):與發卡機控制板通訊逾時 -> 進入安全狀態並中止",
    new FaultInjectionConfig
    {
        InitialHopperCount = 5,
        CaptureBinCapacity = 3,
        CommunicationTimeoutOn = HardwareCall.Read,
    },
    baseOptions);

// ========================================================================
// Part B:離線 data-on-card 模型(飯店離線鎖擬真)
// 鎖不連網,權限/效期/防偽全部寫在卡片上,鎖端用自己離線的 RTC + 案場金鑰驗證。
// ========================================================================
Console.WriteLine();
Console.WriteLine("========================================================");
Console.WriteLine(" Part B:離線 data-on-card 模型(飯店離線鎖擬真) ");
Console.WriteLine("========================================================");

const ushort DemoPropertyId = 1001;
// Demo 用 site key,正式環境不會這樣明碼放在程式裡——由 HSM/KMS 管理,見 SiteKeyRing 上的說明。
var demoSiteKey = System.Text.Encoding.UTF8.GetBytes("demo-site-key-do-not-use-in-prod");
var siteKeyRing = new SiteKeyRing();
siteKeyRing.SetKey(DemoPropertyId, demoSiteKey);

var lockClock = new DateTime(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);

var encoder = new SimulatedHotelCardEncoder(siteKeyRing);
await encoder.ConnectAsync();

// 單一共用序號計數器,貫穿 B3~B7:序號是「案場層級的單調遞增計數」,不是每個情境各自從 1 開始——
// 這樣 B7 報到機發出的新卡序號才會自然銜接在 B3~B6 之後,而非與門鎖已記錄的序號打架。
var sequenceCounters = new Dictionary<string, uint>();
uint NextSequenceForRoom(string room)
{
    var next = sequenceCounters.GetValueOrDefault(room, 0u) + 1;
    sequenceCounters[room] = next;
    return next;
}

Console.WriteLine();
Console.WriteLine("---- B1:未佈建的鎖 -> 拒絕任何卡 ----");
var freshLock = new SimulatedOfflineLock();
var dummyCard = MifareClassicCard.CreateBlank("04AABBCC");
var b1 = await freshLock.PresentCardAsync(dummyCard);
Console.WriteLine($"判斷結果: {(b1.Granted ? "開門" : "拒絕")} | 理由: {b1.Reason}");

Console.WriteLine();
Console.WriteLine("---- B2:母卡佈建門鎖(PropertyId、房號 1203、site key、校時) ----");
var doorLock = new SimulatedOfflineLock();
doorLock.ProvisionWith(new ProgrammingCard
{
    PropertyId = DemoPropertyId,
    DoorId = "1203",
    SiteKey = demoSiteKey,
    SyncClockUtc = lockClock,
});
Console.WriteLine($"佈建完成: PropertyId={doorLock.PropertyId}, DoorId={doorLock.DoorId}, " +
                   $"ClockUtc={doorLock.ClockUtc:u}, IsProvisioned={doorLock.IsProvisioned}");

Console.WriteLine();
Console.WriteLine("---- B3:發房卡(seq=1) -> 鎖驗證放行 ----");
var accessV1 = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock.AddHours(-1),
    CheckOutUtc = lockClock.AddDays(2),
    AccessFlags = AccessFlags.GuestRoom | AccessFlags.Elevator,
    ElevatorFloorMask = 0b0000_0000_0000_1100,
};
var cardV1 = await IssueSignedGuestCardAsync(encoder, siteKeyRing, DemoPropertyId, NextSequenceForRoom("1203"), accessV1, "04C0FFEE");
var b3 = await doorLock.PresentCardAsync(cardV1);
Console.WriteLine($"判斷結果: {(b3.Granted ? "開門" : "拒絕")} | 理由: {b3.Reason}");

Console.WriteLine();
Console.WriteLine("---- B4:退房重發新卡(seq=2)給下一位旅客 -> 新卡放行;舊卡(seq=1)再刷被拒 ----");
var accessV2 = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock,
    CheckOutUtc = lockClock.AddDays(3),
    AccessFlags = AccessFlags.GuestRoom | AccessFlags.Elevator | AccessFlags.Pool,
    ElevatorFloorMask = 0b0000_0000_0000_1100,
};
var cardV2 = await IssueSignedGuestCardAsync(encoder, siteKeyRing, DemoPropertyId, NextSequenceForRoom("1203"), accessV2, "04DEAD01");
var b4New = await doorLock.PresentCardAsync(cardV2);
Console.WriteLine($"新卡(seq=2)判斷結果: {(b4New.Granted ? "開門" : "拒絕")} | 理由: {b4New.Reason}");
var b4Old = await doorLock.PresentCardAsync(cardV1);
Console.WriteLine($"舊卡(seq=1)再刷判斷結果: {(b4Old.Granted ? "開門" : "拒絕")} | 理由: {b4Old.Reason}");

Console.WriteLine();
Console.WriteLine("---- B5:過期卡(ValidTo 早於鎖 RTC) -> 拒絕 ----");
var expiredAccess = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock.AddDays(-5),
    CheckOutUtc = lockClock.AddDays(-1), // 早於鎖端 ClockUtc,退房效期已過
    AccessFlags = AccessFlags.GuestRoom,
    ElevatorFloorMask = 0,
};
var expiredCard = await IssueSignedGuestCardAsync(encoder, siteKeyRing, DemoPropertyId, NextSequenceForRoom("1203"), expiredAccess, "04EE0002");
var b5 = await doorLock.PresentCardAsync(expiredCard);
Console.WriteLine($"判斷結果: {(b5.Granted ? "開門" : "拒絕")} | 理由: {b5.Reason}");

Console.WriteLine();
Console.WriteLine("---- B6:錯誤案場金鑰 / 竄改簽章 -> 簽章驗證失敗拒絕 ----");
var wrongSiteKeyRing = new SiteKeyRing();
wrongSiteKeyRing.SetKey(DemoPropertyId, System.Text.Encoding.UTF8.GetBytes("wrong-site-key-attacker-guess"));
var forgedAccess = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock.AddHours(-1),
    CheckOutUtc = lockClock.AddDays(1),
    AccessFlags = AccessFlags.GuestRoom,
    ElevatorFloorMask = 0,
};
// 用錯誤的案場金鑰簽章(模擬竄改/金鑰不符),連 encoder 自己回讀都會驗章失敗
var forgedCard = await IssueSignedGuestCardAsync(encoder, wrongSiteKeyRing, DemoPropertyId, NextSequenceForRoom("1203"), forgedAccess, "04BAD999");
var b6 = await doorLock.PresentCardAsync(forgedCard);
Console.WriteLine($"判斷結果: {(b6.Granted ? "開門" : "拒絕")} | 理由: {b6.Reason}");

Console.WriteLine();
Console.WriteLine("---- B7(壓軸整合):報到機跑完整機構流程發卡 -> 交給房門鎖驗證 -> 開門 ----");
// 沿用同一組序號計數器(NextSequenceForRoom):報到機發出的這張卡序號會自然銜接在
// B3~B6 之後,而不是從 1 重來——序號在真實系統裡是案場層級的單調計數,不是每筆交易各自歸零。
var kioskHardware = new SimulatedDispenserHardware(
    new FaultInjectionConfig { InitialHopperCount = 5, CaptureBinCapacity = 3 },
    propertyId: DemoPropertyId,
    siteKeyRing: siteKeyRing,
    nextSequenceProvider: NextSequenceForRoom);
var kioskIssuer = new CheckInCardIssuer(kioskHardware, baseOptions);
kioskIssuer.StateChanged += (state, message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] <{state,-18}> {message}");

var kioskKeyData = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock,
    CheckOutUtc = lockClock.AddDays(2),
    AccessFlags = AccessFlags.GuestRoom | AccessFlags.Elevator | AccessFlags.Gym,
    ElevatorFloorMask = 0b0000_0000_0000_1100,
};
var kioskResult = await kioskIssuer.IssueCardAsync(kioskKeyData);
Console.WriteLine($"報到機發卡結果: {(kioskResult.Success ? "成功" : "失敗")} | 最終狀態={kioskResult.FinalState}");

if (kioskResult.Success && kioskHardware.LastIssuedCard is not null)
{
    var b7 = await doorLock.PresentCardAsync(kioskHardware.LastIssuedCard);
    Console.WriteLine($"房門鎖判斷結果: {(b7.Granted ? "開門成功" : "拒絕")} | 理由: {b7.Reason}");
}
else
{
    Console.WriteLine("報到機發卡未成功,略過房門鎖驗證步驟");
}

Console.WriteLine();
Console.WriteLine("---- B8:門群 / 樓層權限(健身房、電梯樓層各自判斷,不再只比房號) ----");
// 三把獨立的鎖:房門(GuestRoom)、健身房門(Facility)、電梯 12 樓門(ElevatorFloor)。
// 各自佈建成全新實例,不沿用 doorLock,避免與 B3~B7 已記錄的序號狀態互相干擾。
var b8RoomLock = new SimulatedOfflineLock();
b8RoomLock.ProvisionWith(new ProgrammingCard
{
    PropertyId = DemoPropertyId,
    DoorId = "1203",
    SiteKey = demoSiteKey,
    SyncClockUtc = lockClock,
    Kind = DoorKind.GuestRoom,
});
var b8GymLock = new SimulatedOfflineLock();
b8GymLock.ProvisionWith(new ProgrammingCard
{
    PropertyId = DemoPropertyId,
    DoorId = "GYM",
    SiteKey = demoSiteKey,
    SyncClockUtc = lockClock,
    Kind = DoorKind.Facility,
    RequiredFacility = AccessFlags.Gym,
});
var b8ElevatorLock = new SimulatedOfflineLock();
b8ElevatorLock.ProvisionWith(new ProgrammingCard
{
    PropertyId = DemoPropertyId,
    DoorId = "ELEV-12F",
    SiteKey = demoSiteKey,
    SyncClockUtc = lockClock,
    Kind = DoorKind.ElevatorFloor,
    ElevatorFloor = 12,
});
Console.WriteLine($"三把鎖佈建完成: 房門(DoorId={b8RoomLock.DoorId}, Kind={b8RoomLock.Kind}), " +
                   $"健身房門(DoorId={b8GymLock.DoorId}, Kind={b8GymLock.Kind}, RequiredFacility={b8GymLock.RequiredFacility}), " +
                   $"電梯門(DoorId={b8ElevatorLock.DoorId}, Kind={b8ElevatorLock.Kind}, ElevatorFloor={b8ElevatorLock.ElevatorFloor})");

Console.WriteLine();
Console.WriteLine("  [基本房卡] 僅 GuestRoom 旗標,電梯樓層遮罩只含 3、4 樓,無 Gym");
var b8BasicAccess = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock.AddHours(-1),
    CheckOutUtc = lockClock.AddDays(2),
    AccessFlags = AccessFlags.GuestRoom,
    ElevatorFloorMask = 0b0000_0000_0000_1100, // 3、4 樓
};
var b8BasicCard = await IssueSignedGuestCardAsync(encoder, siteKeyRing, DemoPropertyId, NextSequenceForRoom("1203-B8"), b8BasicAccess, "04B8B001");

var b8BasicRoom = await b8RoomLock.PresentCardAsync(b8BasicCard);
Console.WriteLine($"  基本房卡 刷房門: {(b8BasicRoom.Granted ? "開門" : "拒絕")} | 理由: {b8BasicRoom.Reason}");
var b8BasicGym = await b8GymLock.PresentCardAsync(b8BasicCard);
Console.WriteLine($"  基本房卡 刷健身房: {(b8BasicGym.Granted ? "開門" : "拒絕")} | 理由: {b8BasicGym.Reason}");
var b8BasicElevator = await b8ElevatorLock.PresentCardAsync(b8BasicCard);
Console.WriteLine($"  基本房卡 刷電梯12樓: {(b8BasicElevator.Granted ? "開門" : "拒絕")} | 理由: {b8BasicElevator.Reason}");

Console.WriteLine();
Console.WriteLine("  [套房卡] GuestRoom|Elevator|Gym,電梯樓層遮罩含 3、4、12 樓");
var b8SuiteAccess = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock.AddHours(-1),
    CheckOutUtc = lockClock.AddDays(2),
    AccessFlags = AccessFlags.GuestRoom | AccessFlags.Elevator | AccessFlags.Gym,
    ElevatorFloorMask = 0b0000_1000_0000_1100, // 3、4、12 樓
};
var b8SuiteCard = await IssueSignedGuestCardAsync(encoder, siteKeyRing, DemoPropertyId, NextSequenceForRoom("1203-B8"), b8SuiteAccess, "04B8B002");

var b8SuiteGym = await b8GymLock.PresentCardAsync(b8SuiteCard);
Console.WriteLine($"  套房卡 刷健身房: {(b8SuiteGym.Granted ? "開門" : "拒絕")} | 理由: {b8SuiteGym.Reason}");
var b8SuiteElevator = await b8ElevatorLock.PresentCardAsync(b8SuiteCard);
Console.WriteLine($"  套房卡 刷電梯12樓: {(b8SuiteElevator.Granted ? "開門" : "拒絕")} | 理由: {b8SuiteElevator.Reason}");

Console.WriteLine();
Console.WriteLine("---- B9:卡片作廢(對應卡片印錯 / 旅客臨時取消入住的作廢流程) ----");
var b9Lock = new SimulatedOfflineLock();
b9Lock.ProvisionWith(new ProgrammingCard
{
    PropertyId = DemoPropertyId,
    DoorId = "1203",
    SiteKey = demoSiteKey,
    SyncClockUtc = lockClock,
    Kind = DoorKind.GuestRoom,
});
var b9Access = new HotelKeyData
{
    RoomNo = "1203",
    CheckInUtc = lockClock.AddHours(-1),
    CheckOutUtc = lockClock.AddDays(2),
    AccessFlags = AccessFlags.GuestRoom,
    ElevatorFloorMask = 0,
};
var b9Card = await IssueSignedGuestCardAsync(encoder, siteKeyRing, DemoPropertyId, NextSequenceForRoom("1203-B9"), b9Access, "04B9C001");

var b9BeforeCancel = await b9Lock.PresentCardAsync(b9Card);
Console.WriteLine($"作廢前刷卡: {(b9BeforeCancel.Granted ? "開門" : "拒絕")} | 理由: {b9BeforeCancel.Reason}");

encoder.InsertCard(b9Card);
await encoder.CancelCardAsync(b9Card);
Console.WriteLine("已透過 encoder.CancelCardAsync 作廢此卡(模擬卡片印錯 / 旅客臨時取消入住的作廢流程)");

var b9AfterCancel = await b9Lock.PresentCardAsync(b9Card);
Console.WriteLine($"作廢後再刷卡: {(b9AfterCancel.Granted ? "開門" : "拒絕")} | 理由: {b9AfterCancel.Reason}");

// ========================================================================
// Part C:DESFire 3-pass 互相認證(mutual authentication)
// 展示讀卡機與卡片雙向證明持有正確金鑰,對照 Mifare Classic 單向金鑰比對的脆弱模型。
// ========================================================================
Console.WriteLine();
Console.WriteLine("========================================================");
Console.WriteLine(" Part C:DESFire 3-pass 互相認證 ");
Console.WriteLine("========================================================");

// Demo 用明碼 AES128 金鑰,僅供模擬展示;正式環境金鑰須由 HSM/KMS 管理並做金鑰分散,
// 絕不能像這樣把金鑰硬編在程式碼裡。
var desfireDemoKey = System.Text.Encoding.UTF8.GetBytes("demo-desfire-key"); // 剛好 16 bytes(AES128)

Console.WriteLine();
Console.WriteLine("---- C1:正確金鑰 -> 3-pass 互相認證通過,讀卡機與卡片各自衍生出相同 session key ----");
var desfireCard1 = new DesfireCard("04C1AAAA");
desfireCard1.ProvisionMasterKey(DesfireKeyType.Aes128, desfireDemoKey);
desfireCard1.SelectApplication(0x00A1A1);

Console.WriteLine("  [Step1] 讀卡機請求驗證,卡片產生 RndB、回傳 E_k(RndB)");
var (c1Ok, c1SessionKey) = DesfireAuthenticator.Authenticate(desfireCard1, keyNo: 0, desfireDemoKey);
Console.WriteLine("  [Step2] 讀卡機解出 RndB、自產 RndA,送出 E_k(RndA‖rotl(RndB));卡片驗證通過後回 E_k(rotl(RndA))");
Console.WriteLine($"  3-pass 認證結果: {(c1Ok ? "通過" : "失敗")}");

if (!c1Ok || c1SessionKey is null || desfireCard1.SessionKey is null)
{
    throw new InvalidOperationException("C1(正確金鑰)預期應通過 3-pass 認證並取得 session key,但沒有——demo 邏輯有誤");
}

Console.WriteLine($"  讀卡機衍生 session key: {Convert.ToHexString(c1SessionKey)}");
Console.WriteLine($"  卡片自身 session key : {Convert.ToHexString(desfireCard1.SessionKey)}");
if (!c1SessionKey.SequenceEqual(desfireCard1.SessionKey))
{
    throw new InvalidOperationException("斷言失敗:讀卡機與卡片衍生的 session key 不相符,3-pass 認證邏輯有誤");
}
Console.WriteLine("  斷言通過: 讀卡機與卡片各自衍生出的 session key 相符(雙向互相認證的具體展現)");

var c1FileData = System.Text.Encoding.UTF8.GetBytes("guest-room-1203-profile");
var c1WriteOk = desfireCard1.WriteFile(fileId: 1, c1FileData);
var c1ReadBack = desfireCard1.ReadFile(fileId: 1);
Console.WriteLine($"  驗證後寫檔: {(c1WriteOk ? "成功" : "失敗")}; 讀回內容: {(c1ReadBack is null ? "(無)" : System.Text.Encoding.UTF8.GetString(c1ReadBack))}");
if (!c1WriteOk || c1ReadBack is null || !c1ReadBack.SequenceEqual(c1FileData))
{
    throw new InvalidOperationException("斷言失敗:驗證通過後的檔案寫入/讀回內容不一致");
}

Console.WriteLine();
Console.WriteLine("---- C2:錯誤金鑰 -> step2 驗證失敗,無 session key,讀寫被拒 ----");
var desfireCard2 = new DesfireCard("04C2BBBB");
desfireCard2.ProvisionMasterKey(DesfireKeyType.Aes128, desfireDemoKey);
desfireCard2.SelectApplication(0x00A1A1);

var wrongDesfireKey = System.Text.Encoding.UTF8.GetBytes("wrong-desfire-k1"); // 同樣 16 bytes,但內容錯誤
var (c2Ok, c2SessionKey) = DesfireAuthenticator.Authenticate(desfireCard2, keyNo: 0, wrongDesfireKey);
Console.WriteLine($"  3-pass 認證結果: {(c2Ok ? "通過" : "失敗")}(預期失敗:讀卡機用錯誤金鑰算出的挑戰值,卡片端 step2 驗不過)");
Console.WriteLine($"  卡片端 SessionKey: {(desfireCard2.SessionKey is null ? "null(未驗證)" : Convert.ToHexString(desfireCard2.SessionKey))}");

var c2RejectedWrite = desfireCard2.WriteFile(fileId: 1, System.Text.Encoding.UTF8.GetBytes("should-not-be-written"));
var c2RejectedRead = desfireCard2.ReadFile(fileId: 1);
Console.WriteLine($"  未驗證狀態下寫檔: {(c2RejectedWrite ? "成功(不應該!)" : "被拒(預期)")}; 讀檔: {(c2RejectedRead is null ? "被拒(預期)" : "成功(不應該!)")}");

if (c2Ok || c2SessionKey is not null || desfireCard2.SessionKey is not null || c2RejectedWrite || c2RejectedRead is not null)
{
    throw new InvalidOperationException("斷言失敗:錯誤金鑰情境預期應該認證失敗且讀寫被拒,但結果不符預期");
}
Console.WriteLine("  斷言通過: 錯誤金鑰下認證失敗、無 session key、讀寫皆被拒");

Console.WriteLine();
Console.WriteLine("---- Encoder 事件紀錄(ReadEventLogAsync) ----");
foreach (var entry in await encoder.ReadEventLogAsync())
{
    Console.WriteLine($"  {entry}");
}
await encoder.DisconnectAsync();

Console.WriteLine();
Console.WriteLine("所有情境執行完畢。");
return;

static async Task<MifareClassicCard> IssueSignedGuestCardAsync(
    SimulatedHotelCardEncoder encoder, SiteKeyRing signingKeyRing, ushort propertyId, uint sequence,
    HotelKeyData access, string uid)
{
    var card = MifareClassicCard.CreateBlank(uid);
    encoder.InsertCard(card);

    var detected = await encoder.DetectCardAsync();
    Console.WriteLine($"  [Encoder] 偵測卡片: {detected.Message}(UID={detected.Uid}, Blank={detected.IsBlank})");

    var siteKey = signingKeyRing.GetKeyOrThrow(propertyId);
    var signable = GuestCardCredential.ComputeSignableBytes(propertyId, sequence, access);
    var signature = CredentialSecurity.Sign(signable, siteKey);
    var credential = new GuestCardCredential
    {
        PropertyId = propertyId,
        CardSequence = sequence,
        Access = access,
        Signature = signature,
    };

    await encoder.IssueGuestCardAsync(card, credential);
    Console.WriteLine($"  [Encoder] 已發卡: 房號={access.RoomNo}, 序號={sequence}, UID={card.Uid}");

    var readBack = await encoder.ReadCredentialAsync();
    Console.WriteLine($"  [Encoder] Encoder 自身回讀驗章: {(readBack is not null ? "通過" : "失敗(結構或簽章不符)")}");

    return card;
}

static async Task RunScenario(string title, FaultInjectionConfig faultConfig, IssuerOptions options)
{
    Console.WriteLine();
    Console.WriteLine($"---- {title} ----");

    var hardware = new SimulatedDispenserHardware(faultConfig);
    var issuer = new CheckInCardIssuer(hardware, options);
    issuer.StateChanged += (state, message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] <{state,-18}> {message}");

    var keyData = new HotelKeyData
    {
        RoomNo = "1203",
        CheckInUtc = new DateTime(2026, 8, 29, 15, 0, 0, DateTimeKind.Utc),
        CheckOutUtc = new DateTime(2026, 8, 31, 11, 0, 0, DateTimeKind.Utc),
        AccessFlags = AccessFlags.GuestRoom | AccessFlags.Elevator | AccessFlags.Gym,
        ElevatorFloorMask = 0b0000_0000_0000_1100, // 樓層 3、4 可達
    };

    var result = await issuer.IssueCardAsync(keyData);

    Console.WriteLine($"結果: {(result.Success ? "成功" : "失敗")} | 最終狀態={result.FinalState} | 故障碼={result.Fault} | 本交易消耗卡片數={result.CardsConsumed}");
    Console.WriteLine($"訊息: {result.Message}");
}
