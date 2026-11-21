using CardDispenser.Core;
using CardDispenser.Core.Models;
using CardDispenser.Sim;
using Xunit;

namespace CardDispenser.Tests;

/// <summary>Issuer fallback categories, driven by the Sim's deterministic fault injection.</summary>
public class IssuerFallbackTests
{
    // Short delays keep the suite fast; the hardware simulator still adds its own fixed delays.
    private static readonly IssuerOptions Options = new()
    {
        MaxDispenseRetries = 2,
        MaxReadRetries = 2,
        MaxWriteRetries = 2,
        MaxCardSwapsPerTransaction = 2,
        RetryBackoff = TimeSpan.FromMilliseconds(1),
        GuestTakeTimeout = TimeSpan.FromMilliseconds(200),
    };

    private static readonly HotelKeyData KeyData = new()
    {
        RoomNo = "1203",
        CheckInUtc = new DateTime(2026, 8, 29, 15, 0, 0, DateTimeKind.Utc),
        CheckOutUtc = new DateTime(2026, 8, 31, 11, 0, 0, DateTimeKind.Utc),
        AccessFlags = AccessFlags.GuestRoom,
        ElevatorFloorMask = 0,
    };

    [Fact]
    public async Task MechanicalFault_JamRetriesAreBoundedThenOutOfService()
    {
        var hw = new SimulatedDispenserHardware(new FaultInjectionConfig { InitialHopperCount = 5, JamEveryDispense = true });
        var result = await new CheckInCardIssuer(hw, Options).IssueCardAsync(KeyData, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DispenseState.Jam, result.FinalState);
        Assert.Equal(FaultCode.DispenseJam, result.Fault);
        // Each push physically pulls one card out of the hopper: exactly MaxDispenseRetries, never more.
        Assert.Equal(5 - Options.MaxDispenseRetries, (await hw.CheckHopperAsync(TestContext.Current.CancellationToken)).RemainingCount);
    }

    [Fact]
    public async Task BadCard_IsSwallowedAndReplacedWithinTheSameTransaction()
    {
        var hw = new SimulatedDispenserHardware(new FaultInjectionConfig
        {
            InitialHopperCount = 5,
            CaptureBinCapacity = 3,
            ReadFailCardSequences = { 1 },
        });
        var result = await new CheckInCardIssuer(hw, Options).IssueCardAsync(KeyData, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(DispenseState.Completed, result.FinalState);
        Assert.Equal(2, result.CardsConsumed); // one swallowed + one issued
    }

    [Fact]
    public async Task BadCard_ReplacementIsCappedPerTransaction()
    {
        var hw = new SimulatedDispenserHardware(new FaultInjectionConfig
        {
            InitialHopperCount = 6,
            CaptureBinCapacity = 5,
            ReadAlwaysFails = true,
        });
        var result = await new CheckInCardIssuer(hw, Options).IssueCardAsync(KeyData, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DispenseState.Aborted, result.FinalState);
        Assert.Equal(FaultCode.CardSwapLimitExceeded, result.Fault);
        // Cap of 2 swaps means the third bad card trips the limit: 3 cards, not the whole hopper.
        Assert.Equal(Options.MaxCardSwapsPerTransaction + 1, result.CardsConsumed);
        Assert.Equal(6 - 3, (await hw.CheckHopperAsync(TestContext.Current.CancellationToken)).RemainingCount);
    }

    [Fact]
    public async Task ResourceExhausted_EmptyHopperStopsWithOutOfStock()
    {
        var hw = new SimulatedDispenserHardware(new FaultInjectionConfig { InitialHopperCount = 0 });
        var result = await new CheckInCardIssuer(hw, Options).IssueCardAsync(KeyData, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DispenseState.OutOfStock, result.FinalState);
        Assert.Equal(FaultCode.OutOfStock, result.Fault);
        Assert.Equal(0, result.CardsConsumed);
    }

    [Fact]
    public async Task ResourceExhausted_FullCaptureBinStopsInsteadOfSwallowing()
    {
        var hw = new SimulatedDispenserHardware(new FaultInjectionConfig
        {
            InitialHopperCount = 5,
            CaptureBinCapacity = 1,
            CaptureBinStartsFull = true,
            ReadFailCardSequences = { 1 },
        });
        var result = await new CheckInCardIssuer(hw, Options).IssueCardAsync(KeyData, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(DispenseState.CaptureBinFull, result.FinalState);
        Assert.Equal(FaultCode.CaptureBinFull, result.Fault);
    }
}
