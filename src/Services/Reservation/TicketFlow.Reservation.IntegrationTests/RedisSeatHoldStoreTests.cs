using TicketFlow.Reservation.Domain.Seats;
using TicketFlow.Reservation.Infrastructure.Redis;

namespace TicketFlow.Reservation.IntegrationTests;

/// <summary>
/// The correctness proof behind Phase 2's DoD (docs/phase-2-plan.md) — real Redis, real
/// concurrency, no mocks. NBomber (Commit 11) measures throughput on top of this; it does not
/// re-prove atomicity, that's this file's job.
/// </summary>
public sealed class RedisSeatHoldStoreTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    private RedisSeatHoldStore CreateStore() => new(fixture.Connection);

    [Fact]
    public async Task TryHoldAsync_With1000ConcurrentRequestsForTheSameSeat_ExactlyOneWins()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [seatId]);

        var tasks = Enumerable.Range(0, 1000)
            .Select(_ => store.TryHoldAsync(eventId, [seatId], Guid.NewGuid(), TimeSpan.FromMinutes(10)));
        var results = await Task.WhenAll(tasks);

        var winners = results.Count(unavailable => unavailable.Count == 0);
        var losers = results.Count(unavailable => unavailable.Count == 1 && unavailable[0] == seatId);

        Assert.Equal(1, winners);
        Assert.Equal(999, losers);
    }

    [Fact]
    public async Task TryHoldAsync_IsAllOrNothing_WhenOneOfSeveralSeatsIsAlreadyTaken()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var seatA = Guid.NewGuid();
        var seatB = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [seatA, seatB]);

        await store.TryHoldAsync(eventId, [seatA], Guid.NewGuid(), TimeSpan.FromMinutes(10));

        // seatB is free on its own, but bundled with the already-taken seatA the whole
        // request must fail — and critically, seatB must NOT end up held as a side effect.
        var result = await store.TryHoldAsync(eventId, [seatA, seatB], Guid.NewGuid(), TimeSpan.FromMinutes(10));

        Assert.Equal([seatA], result);

        var statuses = await store.GetLiveStatusesAsync(eventId, [seatB]);
        Assert.Equal(SeatStatus.Available, statuses[seatB]);
    }

    [Fact]
    public async Task ReleaseAsync_ReturnsAHeldSeatToAvailable()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [seatId]);

        var holdId = Guid.NewGuid();
        await store.TryHoldAsync(eventId, [seatId], holdId, TimeSpan.FromMinutes(10));

        var released = await store.ReleaseAsync(eventId, holdId);

        Assert.True(released);
        var statuses = await store.GetLiveStatusesAsync(eventId, [seatId]);
        Assert.Equal(SeatStatus.Available, statuses[seatId]);
    }

    [Fact]
    public async Task ConfirmAsync_MovesHeldSeatToSold_AndBlocksFurtherHolds()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [seatId]);

        var holdId = Guid.NewGuid();
        await store.TryHoldAsync(eventId, [seatId], holdId, TimeSpan.FromMinutes(10));

        var confirmed = await store.ConfirmAsync(eventId, holdId);

        Assert.Equal([seatId], confirmed);

        var reHoldAttempt = await store.TryHoldAsync(eventId, [seatId], Guid.NewGuid(), TimeSpan.FromMinutes(10));
        Assert.Equal([seatId], reHoldAttempt);
    }

    [Fact]
    public async Task Hold_ExpiresAutomaticallyAfterTtl_WithNoBackgroundJob()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [seatId]);

        await store.TryHoldAsync(eventId, [seatId], Guid.NewGuid(), TimeSpan.FromSeconds(2));

        var duringHold = await store.GetLiveStatusesAsync(eventId, [seatId]);
        Assert.Equal(SeatStatus.Held, duringHold[seatId]);

        await Task.Delay(TimeSpan.FromSeconds(4));

        var afterExpiry = await store.GetLiveStatusesAsync(eventId, [seatId]);
        Assert.Equal(SeatStatus.Available, afterExpiry[seatId]);
    }
}
