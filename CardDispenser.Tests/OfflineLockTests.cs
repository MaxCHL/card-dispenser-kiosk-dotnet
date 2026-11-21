using CardDispenser.Core.Models;
using CardDispenser.Core.Offline;
using Xunit;

namespace CardDispenser.Tests;

/// <summary>
/// Offline lock behavior. The lock clock is set by the programming card (no wall-clock reads),
/// so every test is deterministic.
/// </summary>
public class OfflineLockTests
{
    private const ushort PropertyId = 1001;
    private static readonly byte[] SiteKey = "test-site-key-not-a-real-key-000"u8.ToArray();
    private static readonly byte[] WrongKey = "some-other-key-not-the-site-key!"u8.ToArray();
    private static readonly DateTime Now = new(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);

    private static SimulatedOfflineLock ProvisionedLock(
        string doorId = "1203", DoorKind kind = DoorKind.GuestRoom,
        AccessFlags facility = AccessFlags.None, int floor = 0, ushort propertyId = PropertyId)
    {
        var door = new SimulatedOfflineLock();
        door.ProvisionWith(new ProgrammingCard
        {
            PropertyId = propertyId,
            DoorId = doorId,
            SiteKey = SiteKey,
            SyncClockUtc = Now,
            Kind = kind,
            RequiredFacility = facility,
            ElevatorFloor = floor,
        });
        return door;
    }

    private static HotelKeyData Access(
        AccessFlags flags = AccessFlags.GuestRoom, string room = "1203",
        int checkInOffsetHours = -1, int checkOutOffsetHours = 48, ushort floorMask = 0) => new()
    {
        RoomNo = room,
        CheckInUtc = Now.AddHours(checkInOffsetHours),
        CheckOutUtc = Now.AddHours(checkOutOffsetHours),
        AccessFlags = flags,
        ElevatorFloorMask = floorMask,
    };

    private static async Task<MifareClassicCard> IssueAsync(
        HotelKeyData access, uint sequence = 1, byte[]? signingKey = null, ushort propertyId = PropertyId,
        string uid = "04C0FFEE")
    {
        var keyRing = new SiteKeyRing();
        keyRing.SetKey(propertyId, signingKey ?? SiteKey);
        var encoder = new SimulatedHotelCardEncoder(keyRing);
        await encoder.ConnectAsync();

        var card = MifareClassicCard.CreateBlank(uid);
        var signature = CredentialSecurity.Sign(
            GuestCardCredential.ComputeSignableBytes(propertyId, sequence, access), signingKey ?? SiteKey);
        await encoder.IssueGuestCardAsync(card, new GuestCardCredential
        {
            PropertyId = propertyId,
            CardSequence = sequence,
            Access = access,
            Signature = signature,
        });
        return card;
    }

    [Fact]
    public async Task ValidCredential_OpensTheDoor()
    {
        var decision = await ProvisionedLock().PresentCardAsync(await IssueAsync(Access()));

        Assert.True(decision.Granted, decision.Reason);
    }

    public enum Rejection { Unprovisioned, BadSignature, WrongSite, Expired, WrongRoom, StaleSequence }

    [Theory]
    [InlineData(Rejection.Unprovisioned, "未佈建")]
    [InlineData(Rejection.BadSignature, "簽章驗證失敗")]
    [InlineData(Rejection.WrongSite, "案場代碼不符")]
    [InlineData(Rejection.Expired, "卡片效期不符")]
    [InlineData(Rejection.WrongRoom, "門禁權限不符")]
    [InlineData(Rejection.StaleSequence, "序號已被較新卡片蓋掉")]
    public async Task EachOfTheSixValidations_RejectsItsFailureCase(Rejection failure, string expectedReason)
    {
        SimulatedOfflineLock door = ProvisionedLock();
        MifareClassicCard card;

        switch (failure)
        {
            case Rejection.Unprovisioned:
                door = new SimulatedOfflineLock();
                card = await IssueAsync(Access());
                break;
            case Rejection.BadSignature:
                card = await IssueAsync(Access(), signingKey: WrongKey);
                break;
            case Rejection.WrongSite:
                card = await IssueAsync(Access(), propertyId: (ushort)2002);
                break;
            case Rejection.Expired:
                card = await IssueAsync(Access(checkInOffsetHours: -120, checkOutOffsetHours: -24));
                break;
            case Rejection.WrongRoom:
                card = await IssueAsync(Access(room: "1204"));
                break;
            default: // StaleSequence: a newer card must have been seen first
                Assert.True((await door.PresentCardAsync(await IssueAsync(Access(), sequence: 5, uid: "04AA0005"))).Granted);
                card = await IssueAsync(Access(), sequence: 4, uid: "04AA0004");
                break;
        }

        var decision = await door.PresentCardAsync(card);

        Assert.False(decision.Granted);
        Assert.Contains(expectedReason, decision.Reason);
    }

    [Fact]
    public async Task NewerSequenceCard_OverridesOlderCardWithoutNetwork()
    {
        var door = ProvisionedLock();
        var oldCard = await IssueAsync(Access(), sequence: 1, uid: "04C0FFEE");
        var newCard = await IssueAsync(Access(), sequence: 2, uid: "04DEAD01");

        Assert.True((await door.PresentCardAsync(oldCard)).Granted);
        Assert.True((await door.PresentCardAsync(newCard)).Granted);

        var oldAgain = await door.PresentCardAsync(oldCard);
        Assert.False(oldAgain.Granted);
        Assert.Contains("序號已被較新卡片蓋掉", oldAgain.Reason);
    }

    [Fact]
    public async Task CancelCard_RevokesAnOtherwiseValidCard()
    {
        var door = ProvisionedLock();
        var card = await IssueAsync(Access());
        Assert.True((await door.PresentCardAsync(card)).Granted);

        var encoder = new SimulatedHotelCardEncoder(new SiteKeyRing());
        await encoder.ConnectAsync();
        await encoder.CancelCardAsync(card);

        var decision = await door.PresentCardAsync(card);
        Assert.False(decision.Granted);
        Assert.Contains("讀不到憑證", decision.Reason);
    }

    [Fact]
    public async Task FacilityDoor_RequiresTheMatchingFacilityFlag()
    {
        var gym = ProvisionedLock("GYM", DoorKind.Facility, facility: AccessFlags.Gym);

        var withGym = await IssueAsync(Access(AccessFlags.GuestRoom | AccessFlags.Gym), uid: "04B8B002");
        var withoutGym = await IssueAsync(Access(AccessFlags.GuestRoom), uid: "04B8B001");

        Assert.True((await gym.PresentCardAsync(withGym)).Granted);
        Assert.False((await ProvisionedLock("GYM", DoorKind.Facility, facility: AccessFlags.Gym)
            .PresentCardAsync(withoutGym)).Granted);
    }

    [Theory]
    [InlineData(AccessFlags.GuestRoom | AccessFlags.Elevator, 0b0000_1000_0000_1100, true)]  // floors 3, 4, 12
    [InlineData(AccessFlags.GuestRoom | AccessFlags.Elevator, 0b0000_0000_0000_1100, false)] // floor 12 not in mask
    [InlineData(AccessFlags.GuestRoom, 0b0000_1000_0000_1100, false)]                        // mask ok, no Elevator flag
    public async Task ElevatorFloorDoor_NeedsElevatorFlagAndFloorInMask(AccessFlags flags, ushort mask, bool expectGranted)
    {
        var elevator = ProvisionedLock("ELEV-12F", DoorKind.ElevatorFloor, floor: 12);
        var card = await IssueAsync(Access(flags, floorMask: mask));

        var decision = await elevator.PresentCardAsync(card);

        Assert.Equal(expectGranted, decision.Granted);
    }
}
