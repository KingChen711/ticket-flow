using StackExchange.Redis;
using TicketFlow.Reservation.Application.Abstractions;
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

        Assert.Equal([seatId], released);
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

    [Fact]
    public async Task TypedConfirm_IsExactSetAtomic_AndReplaysTheSameConfirmedMarker()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var seats = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await store.LoadInventoryAsync(eventId, seats);

        var held = await store.TryHoldAsync(new HoldStoreRequest(
            eventId, seats, holdId, ownerId, 9, TimeSpan.FromMinutes(5)));
        Assert.Equal(HoldStoreOutcome.Held, held.Outcome);

        var wrongSet = await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, holdId, orderId, ownerId, 9, [seats[0]]));
        Assert.Equal(ConfirmHoldStoreOutcome.Conflict, wrongSet.Outcome);
        var afterConflict = await store.GetLiveStatusesAsync(eventId, seats);
        Assert.All(seats, seatId => Assert.Equal(SeatStatus.Held, afterConflict[seatId]));

        var request = new ConfirmHoldStoreRequest(eventId, holdId, orderId, ownerId, 9, seats);
        var applied = await store.ConfirmAsync(request);
        var replay = await store.ConfirmAsync(request);
        var wrongOwner = await store.ConfirmAsync(request with { OwnerUserId = Guid.NewGuid() });

        Assert.Equal(ConfirmHoldStoreOutcome.Applied, applied.Outcome);
        Assert.Equal(ConfirmHoldStoreOutcome.AlreadyAppliedSame, replay.Outcome);
        Assert.Equal(seats.OrderBy(id => id), replay.SeatIds);
        Assert.Equal(ConfirmHoldStoreOutcome.Conflict, wrongOwner.Outcome);
    }

    [Fact]
    public async Task TypedConfirm_ClaimsAnOrderForExactlyOneHoldBeforeMutatingSeats()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var firstHoldId = Guid.NewGuid();
        var secondHoldId = Guid.NewGuid();
        var firstSeat = Guid.NewGuid();
        var secondSeat = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [firstSeat, secondSeat]);

        Assert.Equal(HoldStoreOutcome.Held, (await store.TryHoldAsync(new HoldStoreRequest(
            eventId, [firstSeat], firstHoldId, ownerId, 21, TimeSpan.FromMinutes(5)))).Outcome);
        Assert.Equal(HoldStoreOutcome.Held, (await store.TryHoldAsync(new HoldStoreRequest(
            eventId, [secondSeat], secondHoldId, ownerId, 22, TimeSpan.FromMinutes(5)))).Outcome);

        var first = await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, firstHoldId, orderId, ownerId, 21, [firstSeat]));
        var second = await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, secondHoldId, orderId, ownerId, 22, [secondSeat]));

        Assert.Equal(ConfirmHoldStoreOutcome.Applied, first.Outcome);
        Assert.Equal(ConfirmHoldStoreOutcome.Conflict, second.Outcome);
        Assert.Equal("order-already-confirmed-by-another-hold", second.Reason);
        var statuses = await store.GetLiveStatusesAsync(eventId, [firstSeat, secondSeat]);
        Assert.Equal(SeatStatus.Sold, statuses[firstSeat]);
        Assert.Equal(SeatStatus.Held, statuses[secondSeat]);
    }

    [Fact]
    public async Task TypedRelease_RequiresExactOwnerGenerationAndSeatSet_AndCannotDeleteANewerHold()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var seats = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await store.LoadInventoryAsync(eventId, seats);
        var held = await store.TryHoldAsync(new HoldStoreRequest(
            eventId, seats, holdId, ownerId, 17, TimeSpan.FromMinutes(5)));
        Assert.Equal(HoldStoreOutcome.Held, held.Outcome);

        var request = new ReleaseHoldStoreRequest(eventId, holdId, ownerId, 17, seats);
        var wrongOwner = await store.ReleaseAsync(request with { OwnerUserId = Guid.NewGuid() });
        var wrongGeneration = await store.ReleaseAsync(request with { Generation = 18 });
        var wrongSet = await store.ReleaseAsync(request with { SeatIds = [seats[0]] });

        Assert.Equal(ReleaseHoldStoreOutcome.Conflict, wrongOwner.Outcome);
        Assert.Equal(ReleaseHoldStoreOutcome.Conflict, wrongGeneration.Outcome);
        Assert.Equal(ReleaseHoldStoreOutcome.Conflict, wrongSet.Outcome);
        var stillHeld = await store.GetLiveStatusesAsync(eventId, seats);
        Assert.All(seats, seatId => Assert.Equal(SeatStatus.Held, stillHeld[seatId]));

        var applied = await store.ReleaseAsync(request);
        var replay = await store.ReleaseAsync(request);
        Assert.Equal(ReleaseHoldStoreOutcome.Applied, applied.Outcome);
        Assert.Equal(ReleaseHoldStoreOutcome.AlreadyReleased, replay.Outcome);

        var newerHold = new HoldStoreRequest(
            eventId, seats, Guid.NewGuid(), Guid.NewGuid(), 18, TimeSpan.FromMinutes(5));
        Assert.Equal(HoldStoreOutcome.Held, (await store.TryHoldAsync(newerHold)).Outcome);
        Assert.Equal(ReleaseHoldStoreOutcome.AlreadyReleased, (await store.ReleaseAsync(request)).Outcome);
        var newerStillHeld = await store.GetLiveStatusesAsync(eventId, seats);
        Assert.All(seats, seatId => Assert.Equal(SeatStatus.Held, newerStillHeld[seatId]));
    }

    /// <summary>
    /// ReleaseAllocationAsync is the cache half of owner-scoped compensation (Commit 5) and, until
    /// the Nhóm B audit, the only store method with no test at all — every test double inherited
    /// the interface's default adapter, which forwards to the blunt pre-Phase-12 sold-set removal,
    /// so nothing ever executed ReleaseAllocationScript. The ABA case below is the exact scenario
    /// that adapter gets wrong: a late compensation for order A, arriving after seat A was resold
    /// to order B, must leave B's paid seat sold.
    /// </summary>
    [Fact]
    public async Task TypedReleaseAllocation_LeavesASeatResoldToAnotherOrderUntouched()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var firstHoldId = Guid.NewGuid();
        var firstOrderId = Guid.NewGuid();
        var firstOwnerId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [seatId]);

        // Order A holds and confirms the seat.
        Assert.Equal(HoldStoreOutcome.Held, (await store.TryHoldAsync(new HoldStoreRequest(
            eventId, [seatId], firstHoldId, firstOwnerId, 5, TimeSpan.FromMinutes(5)))).Outcome);
        Assert.Equal(ConfirmHoldStoreOutcome.Applied, (await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, firstHoldId, firstOrderId, firstOwnerId, 5, [seatId]))).Outcome);

        // A's allocation is reversed, and the seat is then resold to order B.
        var release = new ReleaseAllocationStoreRequest(
            eventId, firstHoldId, firstOrderId, firstOwnerId, 5, [seatId]);
        Assert.Equal(ReleaseAllocationStoreOutcome.Applied, (await store.ReleaseAllocationAsync(release)).Outcome);

        var secondHoldId = Guid.NewGuid();
        var secondOrderId = Guid.NewGuid();
        var secondOwnerId = Guid.NewGuid();
        Assert.Equal(HoldStoreOutcome.Held, (await store.TryHoldAsync(new HoldStoreRequest(
            eventId, [seatId], secondHoldId, secondOwnerId, 6, TimeSpan.FromMinutes(5)))).Outcome);
        Assert.Equal(ConfirmHoldStoreOutcome.Applied, (await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, secondHoldId, secondOrderId, secondOwnerId, 6, [seatId]))).Outcome);

        // The duplicate compensation for A now arrives. The seat carries B's token, so it is
        // reported stale rather than freed — a blunt SREM here would hand away a paid seat.
        var late = await store.ReleaseAllocationAsync(release);

        Assert.Equal(ReleaseAllocationStoreOutcome.OwnershipChanged, late.Outcome);
        Assert.Empty(late.ReleasedSeatIds);
        Assert.Equal([seatId], late.StaleSeatIds);
        var statuses = await store.GetLiveStatusesAsync(eventId, [seatId]);
        Assert.Equal(SeatStatus.Sold, statuses[seatId]);
    }

    [Fact]
    public async Task TypedReleaseAllocation_RequiresTheExactOwnerAndGeneration_AndReplaysAsAlreadyReleased()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var seats = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await store.LoadInventoryAsync(eventId, seats);

        Assert.Equal(HoldStoreOutcome.Held, (await store.TryHoldAsync(new HoldStoreRequest(
            eventId, seats, holdId, ownerId, 11, TimeSpan.FromMinutes(5)))).Outcome);
        Assert.Equal(ConfirmHoldStoreOutcome.Applied, (await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, holdId, orderId, ownerId, 11, seats))).Outcome);

        var request = new ReleaseAllocationStoreRequest(eventId, holdId, orderId, ownerId, 11, seats);
        var wrongOwner = await store.ReleaseAllocationAsync(request with { OwnerUserId = Guid.NewGuid() });
        var wrongGeneration = await store.ReleaseAllocationAsync(request with { Generation = 12 });
        var wrongOrder = await store.ReleaseAllocationAsync(request with { OrderId = Guid.NewGuid() });

        // Every mismatch is stale, never a release — and the seats stay sold throughout.
        Assert.Equal(ReleaseAllocationStoreOutcome.OwnershipChanged, wrongOwner.Outcome);
        Assert.Equal(ReleaseAllocationStoreOutcome.OwnershipChanged, wrongGeneration.Outcome);
        Assert.Equal(ReleaseAllocationStoreOutcome.OwnershipChanged, wrongOrder.Outcome);
        var stillSold = await store.GetLiveStatusesAsync(eventId, seats);
        Assert.All(seats, seatId => Assert.Equal(SeatStatus.Sold, stillSold[seatId]));

        var applied = await store.ReleaseAllocationAsync(request);
        var replay = await store.ReleaseAllocationAsync(request);

        Assert.Equal(ReleaseAllocationStoreOutcome.Applied, applied.Outcome);
        Assert.Equal(seats.OrderBy(id => id), applied.ReleasedSeatIds);
        // Redelivery of the same compensation is a no-op, not a second reversal.
        Assert.Equal(ReleaseAllocationStoreOutcome.AlreadyReleased, replay.Outcome);
        Assert.Empty(replay.ReleasedSeatIds);
        var afterRelease = await store.GetLiveStatusesAsync(eventId, seats);
        Assert.All(seats, seatId => Assert.Equal(SeatStatus.Available, afterRelease[seatId]));
    }

    [Fact]
    public async Task Flush_then_versioned_rebuild_restores_sold_active_held_and_expired_as_available()
    {
        var options = ConfigurationOptions.Parse(fixture.Connection.Configuration);
        options.AllowAdmin = true;
        await using (var admin = await ConnectionMultiplexer.ConnectAsync(options))
        {
            var server = admin.GetServer(admin.GetEndPoints().Single());
            await server.FlushDatabaseAsync();
        }

        var store = CreateStore();
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var activeHoldId = Guid.NewGuid();
        var confirmedHoldId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var activeHeld = Guid.NewGuid();
        var sold = Guid.NewGuid();
        var expiredHeld = Guid.NewGuid();
        var available = Guid.NewGuid();
        var validSeats = new[] { activeHeld, sold, expiredHeld, available };
        var rebuild = await store.RebuildAsync(new RebuildSeatCacheRequest(
            eventId,
            validSeats,
            [
                new CachedSeatAllocation(
                    activeHeld, activeHoldId, ownerId, 4, CachedAllocationState.Held, null, now.AddMinutes(5)),
                new CachedSeatAllocation(
                    sold, confirmedHoldId, ownerId, 8, CachedAllocationState.Confirmed, orderId, null),
                new CachedSeatAllocation(
                    expiredHeld, Guid.NewGuid(), ownerId, 3, CachedAllocationState.Held, null, now.AddMinutes(-1)),
            ],
            42));

        Assert.True(rebuild.Applied);
        Assert.Empty(rebuild.ConflictingSeatIds);
        var statuses = await store.GetLiveStatusesAsync(eventId, validSeats);
        Assert.Equal(SeatStatus.Held, statuses[activeHeld]);
        Assert.Equal(SeatStatus.Sold, statuses[sold]);
        Assert.Equal(SeatStatus.Available, statuses[expiredHeld]);
        Assert.Equal(SeatStatus.Available, statuses[available]);

        var soldAttempt = await store.TryHoldAsync(new HoldStoreRequest(
            eventId, [sold], Guid.NewGuid(), ownerId, 9, TimeSpan.FromMinutes(1)));
        var heldAttempt = await store.TryHoldAsync(new HoldStoreRequest(
            eventId, [activeHeld], Guid.NewGuid(), ownerId, 9, TimeSpan.FromMinutes(1)));
        Assert.Equal(HoldStoreOutcome.Unavailable, soldAttempt.Outcome);
        Assert.Equal(HoldStoreOutcome.Unavailable, heldAttempt.Outcome);

        var replay = await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, confirmedHoldId, orderId, ownerId, 8, [sold]));
        Assert.Equal(ConfirmHoldStoreOutcome.AlreadyAppliedSame, replay.Outcome);
        Assert.Equal([sold], replay.SeatIds);
    }

    [Fact]
    public async Task Rebuild_fails_closed_instead_of_overwriting_a_different_live_owner()
    {
        var store = CreateStore();
        var eventId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var cacheOwner = Guid.NewGuid();
        var cacheHold = Guid.NewGuid();
        await store.LoadInventoryAsync(eventId, [seatId]);
        await store.TryHoldAsync(new HoldStoreRequest(
            eventId, [seatId], cacheHold, cacheOwner, 1, TimeSpan.FromMinutes(5)));

        var authorityOwner = Guid.NewGuid();
        var authorityHold = Guid.NewGuid();
        var rebuild = await store.RebuildAsync(new RebuildSeatCacheRequest(
            eventId,
            [seatId],
            [new CachedSeatAllocation(
                seatId,
                authorityHold,
                authorityOwner,
                2,
                CachedAllocationState.Held,
                null,
                DateTimeOffset.UtcNow.AddMinutes(5))],
            100));

        Assert.False(rebuild.Applied);
        Assert.Equal([seatId], rebuild.ConflictingSeatIds);
        var originalOwnerCanStillConfirm = await store.ConfirmAsync(new ConfirmHoldStoreRequest(
            eventId, cacheHold, Guid.NewGuid(), cacheOwner, 1, [seatId]));
        Assert.Equal(ConfirmHoldStoreOutcome.Applied, originalOwnerCanStillConfirm.Outcome);
    }
}
