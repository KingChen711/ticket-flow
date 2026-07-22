using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.Allocations;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

public sealed class ReservationFailureBoundaryTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Crash_after_Redis_confirm_before_Postgres_commit_must_replay_the_same_confirm_receipt()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        const long generation = 7;
        var seat = Seat.Create(eventId, Guid.NewGuid(), "VIP-1");
        var repository = new FakeSeatRepository([seat]);
        var holds = new FakeSeatHoldStore();
        holds.SeedTypedHold(eventId, holdId, ownerId, generation, [seat.Id]);
        var quote = ActiveQuote(
            holdId, eventId, ownerId, [seat.Id], generation, 11, 100_000m, "VND", now);
        var quotes = new FakeHoldQuoteRepository([quote]);
        var allocations = new FakeSeatAllocationRepository();
        var receipts = new FakeReservationOperationReceiptRepository();
        var publisher = new RecordingSeatAvailabilityEventPublisher();
        var unitOfWork = new ControlledReservationUnitOfWork { FailOnSaveNumber = 2 };
        var service = CreateService(
            repository,
            holds,
            unitOfWork,
            allocations,
            receipts,
            publisher,
            quotes,
            PublishedPolicy(eventId, seat.SectionId, 11, 100_000m, "VND", now),
            new FixedTimeProvider(now));
        var request = new ConfirmSeatAllocationRequest(
            orderId, eventId, holdId, [seat.Id], ownerId, generation, 11, 100_000m, "VND", "confirm-1");

        // Failpoint: Redis has moved the hold to Sold; Postgres SaveChanges throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(request));

        var replay = await service.ConfirmAsync(request);

        // A durable ReservationOperation receipt must return the first operation's exact outcome.
        Assert.True(replay.Succeeded);
        Assert.Equal(orderId, replay.OrderId);
        Assert.Equal(ownerId, replay.OwnerUserId);
        Assert.Equal(generation, replay.Generation);
        Assert.Equal([seat.Id], replay.SeatIds);
        var confirmed = Assert.Single(allocations.Allocations);
        AssertConfirmedAllocation(
            confirmed, seat.Id, eventId, holdId, orderId, ownerId, generation, 11, 100_000m, "VND");
        Assert.Equal(HoldQuoteState.Consumed, quote.State);
        Assert.Equal(orderId, quote.OrderId);
        Assert.Single(receipts.Receipts);
        Assert.Single(publisher.Published);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Hold_persists_one_active_quote_without_materializing_a_seat_allocation()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seat = Seat.Create(eventId, sectionId, "C-1");
        var quotes = new FakeHoldQuoteRepository();
        var allocations = new FakeSeatAllocationRepository();
        var service = CreateService(
            new FakeSeatRepository([seat]),
            new FakeSeatHoldStore(),
            new ControlledReservationUnitOfWork(),
            allocations,
            quotes: quotes,
            policy: PublishedPolicy(eventId, sectionId, 9, 50_000m, "VND", now),
            timeProvider: new FixedTimeProvider(now));

        var result = await service.HoldAsync(new HoldSeatsRequest(eventId, [seat.Id], ownerId));

        Assert.True(result.Success);
        var quote = Assert.Single(quotes.Quotes);
        Assert.Equal(result.HoldId, quote.Id);
        Assert.Equal(HoldQuoteState.Active, quote.State);
        Assert.Equal(eventId, quote.EventId);
        Assert.Equal(ownerId, quote.OwnerUserId);
        Assert.Equal([seat.Id], quote.SeatIds);
        Assert.Null(quote.OrderId);
        Assert.Null(quote.TerminalAt);
        Assert.Empty(allocations.Allocations);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Confirmation_event_is_outboxed_and_receipt_replay_does_not_repeat_the_mutation()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        const long generation = 3;
        var seat = Seat.Create(eventId, Guid.NewGuid(), "A-1");
        var repository = new FakeSeatRepository([seat]);
        var holds = new FakeSeatHoldStore();
        holds.SeedTypedHold(eventId, holdId, ownerId, generation, [seat.Id]);
        var quote = ActiveQuote(
            holdId, eventId, ownerId, [seat.Id], generation, 4, 75_000m, "VND", now);
        var quotes = new FakeHoldQuoteRepository([quote]);
        var allocations = new FakeSeatAllocationRepository();
        var receipts = new FakeReservationOperationReceiptRepository();
        var publisher = new RecordingSeatAvailabilityEventPublisher();
        var service = CreateService(
            repository,
            holds,
            new ControlledReservationUnitOfWork(),
            allocations,
            receipts,
            publisher,
            quotes,
            PublishedPolicy(eventId, seat.SectionId, 4, 75_000m, "VND", now),
            new FixedTimeProvider(now));
        var request = new ConfirmSeatAllocationRequest(
            orderId, eventId, holdId, [seat.Id], ownerId, generation, 4, 75_000m, "VND", "confirm-2");

        var first = await service.ConfirmAsync(request);
        var replay = await service.ConfirmAsync(request);

        Assert.True(first.Succeeded);
        Assert.True(replay.Succeeded);
        Assert.Equal(first.AllocationReference, replay.AllocationReference);
        Assert.Equal(first.SeatIds, replay.SeatIds);
        Assert.Equal(SeatStatus.Sold, seat.Status);
        var confirmed = Assert.Single(allocations.Allocations);
        AssertConfirmedAllocation(
            confirmed, seat.Id, eventId, holdId, orderId, ownerId, generation, 4, 75_000m, "VND");
        Assert.Equal(HoldQuoteState.Consumed, quote.State);
        Assert.Equal(orderId, quote.OrderId);
        Assert.Single(receipts.Receipts);
        Assert.Single(publisher.Published);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Stale_release_from_order_A_must_not_unsell_a_seat_now_owned_by_order_B()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var orderA = Guid.NewGuid();
        var orderB = Guid.NewGuid();
        var holdA = Guid.NewGuid();
        var holdB = Guid.NewGuid();
        var seat = Seat.Create(eventId, Guid.NewGuid(), "B-1");
        seat.MarkSold();

        // Order A's hold was consumed, then the same seat was resold to order B under a NEWER
        // generation — the exact ABA shape a delayed compensation must not act on.
        var quoteA = ConsumedQuote(holdA, eventId, ownerId, [seat.Id], generation: 4, orderA, now);
        var allocation = SeatAllocation.CreateConfirmed(
            seat.Id, eventId, holdB, orderB, ownerId, 9, 11, 100_000m, "VND", now);
        var allocations = new FakeSeatAllocationRepository([allocation]);
        var holds = new FakeSeatHoldStore();
        holds.SeedSold([seat.Id]);
        var publisher = new RecordingSeatAvailabilityEventPublisher();
        var service = CreateService(
            new FakeSeatRepository([seat]),
            holds,
            new ControlledReservationUnitOfWork(),
            allocations,
            publisher: publisher,
            quotes: new FakeHoldQuoteRepository([quoteA]),
            timeProvider: new FixedTimeProvider(now));

        var result = await service.CompensateAsync(new ReleaseSeatAllocationRequest(
            orderA, eventId, holdA, [seat.Id], ownerId, 4, "release-stale"));

        // The compensation completes (the saga must still reach Cancelled) but touches nothing:
        // order B keeps its paid seat, in PostgreSQL, in the cache, and on the seat map.
        Assert.True(result.Succeeded);
        Assert.Empty(result.ReleasedSeatIds);
        Assert.Equal([seat.Id], result.StaleSeatIds);
        Assert.Equal(SeatStatus.Sold, seat.Status);
        Assert.Contains(seat.Id, holds.SoldSeatIds);
        Assert.Equal(SeatAllocationState.Confirmed, allocation.State);
        Assert.Equal(orderB, allocation.OrderId);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Owner_scoped_compensation_releases_only_its_own_allocation_and_republishes_availability()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        const long generation = 5;
        var seat = Seat.Create(eventId, Guid.NewGuid(), "A-7");
        seat.MarkSold();
        var quote = ConsumedQuote(holdId, eventId, ownerId, [seat.Id], generation, orderId, now);
        var allocation = SeatAllocation.CreateConfirmed(
            seat.Id, eventId, holdId, orderId, ownerId, generation, 11, 100_000m, "VND", now);
        var allocations = new FakeSeatAllocationRepository([allocation]);
        var holds = new FakeSeatHoldStore();
        holds.SeedSold([seat.Id]);
        var publisher = new RecordingSeatAvailabilityEventPublisher();
        var service = CreateService(
            new FakeSeatRepository([seat]),
            holds,
            new ControlledReservationUnitOfWork(),
            allocations,
            publisher: publisher,
            quotes: new FakeHoldQuoteRepository([quote]),
            timeProvider: new FixedTimeProvider(now));
        var request = new ReleaseSeatAllocationRequest(
            orderId, eventId, holdId, [seat.Id], ownerId, generation, "release-owned");

        var first = await service.CompensateAsync(request);
        var replay = await service.CompensateAsync(request);

        Assert.True(first.Succeeded);
        Assert.Equal([seat.Id], first.ReleasedSeatIds);
        Assert.Empty(first.StaleSeatIds);
        Assert.Equal(SeatStatus.Available, seat.Status);
        Assert.DoesNotContain(seat.Id, holds.SoldSeatIds);
        Assert.Equal(SeatAllocationState.Released, allocation.State);

        // Delta review 2026-07-20: the old UnsellAsync never notified, so a compensated seat
        // stayed visually Sold until a manual refetch.
        var published = Assert.Single(publisher.Published);
        Assert.Equal("Available", published.Status);
        Assert.Equal([seat.Id], published.SeatIds);

        // Redelivery replays the receipt instead of re-running the mutation.
        Assert.Equal(first.OperationReference, replay.OperationReference);
        Assert.Equal(first.ReleasedSeatIds, replay.ReleasedSeatIds);
        Assert.Single(publisher.Published);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Compensation_naming_another_orders_seat_set_must_release_nothing()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        const long generation = 2;
        var ownSeat = Seat.Create(eventId, Guid.NewGuid(), "C-1");
        var victimSeat = Seat.Create(eventId, Guid.NewGuid(), "C-2");
        ownSeat.MarkSold();
        victimSeat.MarkSold();
        var quote = ConsumedQuote(holdId, eventId, ownerId, [ownSeat.Id], generation, orderId, now);
        var victimAllocation = SeatAllocation.CreateConfirmed(
            victimSeat.Id, eventId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, 11, 100_000m, "VND", now);
        var service = CreateService(
            new FakeSeatRepository([ownSeat, victimSeat]),
            new FakeSeatHoldStore(),
            new ControlledReservationUnitOfWork(),
            new FakeSeatAllocationRepository([victimAllocation]),
            quotes: new FakeHoldQuoteRepository([quote]),
            timeProvider: new FixedTimeProvider(now));

        // The message claims a seat the durable quote never covered.
        var result = await service.CompensateAsync(new ReleaseSeatAllocationRequest(
            orderId, eventId, holdId, [victimSeat.Id], ownerId, generation, "release-tampered"));

        Assert.False(result.Succeeded);
        Assert.Equal("AuthorityMismatch", result.FailureCode);
        Assert.Empty(result.ReleasedSeatIds);
        Assert.Equal(SeatStatus.Sold, victimSeat.Status);
        Assert.Equal(SeatAllocationState.Confirmed, victimAllocation.State);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Crash_after_inventory_Postgres_commit_before_Redis_load_must_be_repairable_by_retry()
    {
        var eventId = Guid.NewGuid();
        var repository = new FakeSeatRepository();
        var holds = new FakeSeatHoldStore { FailNextInventoryLoad = true };
        var service = new InventoryService(
            repository, holds, new ControlledReservationUnitOfWork(), new ReservationOptions());
        var request = new ProvisionInventoryRequest(
            eventId,
            [new ProvisionSectionRequest(Guid.NewGuid(), "VIP", 2)]);

        // Failpoint: DB rows committed, Redis LoadInventory did not.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProvisionAsync(request));

        var seatCount = await service.ProvisionAsync(request);

        Assert.Equal(2, seatCount);
        Assert.True(holds.InventoryLoaded);
        // The repair must not duplicate the durable inventory it is repairing.
        Assert.Equal(2, (await repository.GetByEventAsync(eventId)).Count);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Repeat_provisioning_with_a_different_shape_must_be_refused()
    {
        var eventId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var service = new InventoryService(
            new FakeSeatRepository(), new FakeSeatHoldStore(), new ControlledReservationUnitOfWork(),
            new ReservationOptions());
        await service.ProvisionAsync(new ProvisionInventoryRequest(
            eventId, [new ProvisionSectionRequest(sectionId, "VIP", 2)]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProvisionAsync(
            new ProvisionInventoryRequest(eventId, [new ProvisionSectionRequest(sectionId, "VIP", 5)])));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_single_user_cannot_lock_out_an_event_by_exceeding_the_per_user_hold_caps()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seats = Enumerable.Range(0, 8).Select(index => Seat.Create(eventId, sectionId, $"G-{index}")).ToArray();
        var quotes = new FakeHoldQuoteRepository();
        var options = new ReservationOptions
        {
            MaxActiveHoldsPerUserPerEvent = 2,
            MaxHeldSeatsPerUserPerEvent = 4,
            MaxHoldsPerUserPerWindow = 10,
        };
        var service = CreateService(
            new FakeSeatRepository(seats),
            new FakeSeatHoldStore(),
            new ControlledReservationUnitOfWork(),
            quotes: quotes,
            policy: PublishedPolicy(eventId, sectionId, 9, 50_000m, "VND", now),
            timeProvider: new FixedTimeProvider(now),
            options: options);

        Assert.True((await service.HoldAsync(new HoldSeatsRequest(eventId, [seats[0].Id], ownerId))).Success);
        Assert.True((await service.HoldAsync(new HoldSeatsRequest(eventId, [seats[1].Id], ownerId))).Success);

        var capped = await service.HoldAsync(new HoldSeatsRequest(eventId, [seats[2].Id], ownerId));

        // A cap rejection must be distinguishable from a genuine sold-out response, which is what
        // the endpoint maps to 409-cap vs 409-unavailable vs 429.
        Assert.False(capped.Success);
        Assert.Equal(HoldRejectionReason.HoldCapExceeded, capped.Reason);
        Assert.Empty(capped.UnavailableSeatIds);
        Assert.Equal(2, quotes.Quotes.Count);

        // Another customer is unaffected by the first user's budget.
        var otherUser = await service.HoldAsync(new HoldSeatsRequest(eventId, [seats[3].Id], Guid.NewGuid()));
        Assert.True(otherUser.Success);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Hold_release_looping_must_hit_the_rate_limit_instead_of_refilling_the_budget()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var seats = Enumerable.Range(0, 5).Select(index => Seat.Create(eventId, sectionId, $"R-{index}")).ToArray();
        var quotes = new FakeHoldQuoteRepository();
        var options = new ReservationOptions
        {
            MaxActiveHoldsPerUserPerEvent = 10,
            MaxHeldSeatsPerUserPerEvent = 50,
            MaxHoldsPerUserPerWindow = 2,
            HoldRateLimitWindowSeconds = 60,
        };
        var service = CreateService(
            new FakeSeatRepository(seats),
            new FakeSeatHoldStore(),
            new ControlledReservationUnitOfWork(),
            quotes: quotes,
            policy: PublishedPolicy(eventId, sectionId, 9, 50_000m, "VND", now),
            timeProvider: new FixedTimeProvider(now),
            options: options);

        var first = await service.HoldAsync(new HoldSeatsRequest(eventId, [seats[0].Id], ownerId));
        var second = await service.HoldAsync(new HoldSeatsRequest(eventId, [seats[1].Id], ownerId));
        Assert.True(first.Success);
        Assert.True(second.Success);

        // Releasing frees the seats but must NOT refill the creation budget, otherwise the limit
        // is bypassed by a hold/release loop.
        Assert.True(await service.ReleaseOwnedHoldAsync(first.HoldId!.Value, ownerId));

        var limited = await service.HoldAsync(new HoldSeatsRequest(eventId, [seats[2].Id], ownerId));

        Assert.False(limited.Success);
        Assert.Equal(HoldRejectionReason.RateLimited, limited.Reason);
        Assert.Empty(limited.UnavailableSeatIds);
    }

    private static ReservationService CreateService(
        ISeatRepository repository,
        ISeatHoldStore holds,
        IUnitOfWork unitOfWork,
        ISeatAllocationRepository? allocations = null,
        IReservationOperationReceiptRepository? receipts = null,
        ISeatAvailabilityEventPublisher? publisher = null,
        FakeHoldQuoteRepository? quotes = null,
        EventSalePolicy? policy = null,
        TimeProvider? timeProvider = null,
        ReservationOptions? options = null) =>
        new(
            repository,
            holds,
            new FakeEventSalePolicyRepository(policy),
            quotes ?? new FakeHoldQuoteRepository(),
            allocations ?? new FakeSeatAllocationRepository(),
            receipts ?? new FakeReservationOperationReceiptRepository(),
            new FakeAllocationGenerationSource(),
            unitOfWork,
            publisher ?? new RecordingSeatAvailabilityEventPublisher(),
            timeProvider ?? TimeProvider.System,
            options ?? new ReservationOptions());

    private static HoldQuote ConsumedQuote(
        Guid holdId,
        Guid eventId,
        Guid ownerId,
        IReadOnlyList<Guid> seatIds,
        long generation,
        Guid orderId,
        DateTimeOffset now)
    {
        var quote = HoldQuote.Create(
            holdId, eventId, ownerId, seatIds, 100_000m, "VND", 11, generation, now, now.AddMinutes(5));
        quote.Consume(orderId, now);
        return quote;
    }

    private static HoldQuote ActiveQuote(
        Guid holdId,
        Guid eventId,
        Guid ownerId,
        IReadOnlyList<Guid> seatIds,
        long generation,
        long priceSnapshotVersion,
        decimal amount,
        string currency,
        DateTimeOffset now) =>
        HoldQuote.Create(
            holdId,
            eventId,
            ownerId,
            seatIds,
            amount,
            currency,
            priceSnapshotVersion,
            generation,
            now,
            now.AddMinutes(5));

    private static EventSalePolicy PublishedPolicy(
        Guid eventId,
        Guid sectionId,
        long priceSnapshotVersion,
        decimal amount,
        string currency,
        DateTimeOffset now) =>
        EventSalePolicy.FromSnapshot(
            eventId,
            "Published",
            now.AddMinutes(-1),
            now.AddHours(1),
            priceSnapshotVersion,
            now,
            [(sectionId, amount, currency)]);

    private static void AssertConfirmedAllocation(
        SeatAllocation allocation,
        Guid seatId,
        Guid eventId,
        Guid holdId,
        Guid orderId,
        Guid ownerId,
        long generation,
        long priceSnapshotVersion,
        decimal amount,
        string currency)
    {
        Assert.Equal(seatId, allocation.SeatId);
        Assert.Equal(eventId, allocation.EventId);
        Assert.Equal(holdId, allocation.HoldId);
        Assert.Equal(orderId, allocation.OrderId);
        Assert.Equal(ownerId, allocation.OwnerUserId);
        Assert.Equal(SeatAllocationState.Confirmed, allocation.State);
        Assert.Equal(generation, allocation.Generation);
        Assert.Null(allocation.ExpiresAt);
        Assert.Equal(priceSnapshotVersion, allocation.PriceSnapshotVersion);
        Assert.Equal(amount, allocation.Amount);
        Assert.Equal(currency, allocation.Currency);
    }
}

internal sealed class FakeEventSalePolicyRepository(EventSalePolicy? policy = null) : IEventSalePolicyRepository
{
    public EventSalePolicy? Policy { get; private set; } = policy;

    public Task<EventSalePolicy?> GetAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Policy?.Id == eventId ? Policy : null);

    public void Add(EventSalePolicy value) => Policy = value;
}

internal sealed class FakeHoldQuoteRepository(IEnumerable<HoldQuote>? initial = null) : IHoldQuoteRepository
{
    private readonly Dictionary<Guid, HoldQuote> _quotes =
        initial?.ToDictionary(quote => quote.Id) ?? [];

    public IReadOnlyCollection<HoldQuote> Quotes => _quotes.Values;

    public Task<HoldQuote?> GetAsync(Guid holdId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_quotes.GetValueOrDefault(holdId));

    public Task<HoldQuote?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_quotes.Values.SingleOrDefault(quote => quote.OrderId == orderId));

    public Task<IReadOnlyList<HoldQuote>> GetActiveOverlappingAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HoldQuote>>(
            _quotes.Values.Where(quote =>
                quote.EventId == eventId &&
                quote.IsActiveAt(now) &&
                quote.SeatIds.Any(seatIds.Contains)).ToList());

    public Task<IReadOnlyList<Guid>> GetActiveIdsByEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(
            _quotes.Values
                .Where(quote => quote.EventId == eventId && quote.State == HoldQuoteState.Active)
                .Select(quote => quote.Id)
                .Order()
                .ToList());

    public Task<IReadOnlyList<HoldQuote>> GetActiveByOwnerAsync(
        Guid eventId,
        Guid ownerUserId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HoldQuote>>(
            _quotes.Values
                .Where(quote =>
                    quote.EventId == eventId &&
                    quote.OwnerUserId == ownerUserId &&
                    quote.IsActiveAt(now))
                .ToList());

    public Task<int> CountCreatedByOwnerSinceAsync(
        Guid eventId,
        Guid ownerUserId,
        DateTimeOffset since,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_quotes.Values.Count(quote =>
            quote.EventId == eventId &&
            quote.OwnerUserId == ownerUserId &&
            quote.CreatedAt >= since));

    public void Add(HoldQuote quote) => _quotes.Add(quote.Id, quote);
}

internal sealed class FakeSeatAllocationRepository(IEnumerable<SeatAllocation>? initial = null) : ISeatAllocationRepository
{
    private readonly List<SeatAllocation> _allocations = initial?.ToList() ?? [];

    public IReadOnlyList<SeatAllocation> Allocations => _allocations;

    public Task<IReadOnlyList<SeatAllocation>> GetBySeatIdsAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SeatAllocation>>(
            _allocations.Where(item => item.EventId == eventId && seatIds.Contains(item.SeatId)).ToList());

    public Task<IReadOnlyList<SeatAllocation>> GetByHoldAsync(
        Guid holdId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SeatAllocation>>(
            _allocations.Where(item => item.HoldId == holdId).ToList());

    public Task<IReadOnlyList<SeatAllocation>> GetByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SeatAllocation>>(
            _allocations.Where(item => item.OrderId == orderId).ToList());

    public void AddRange(IEnumerable<SeatAllocation> allocations) => _allocations.AddRange(allocations);
}

internal sealed class FakeReservationOperationReceiptRepository : IReservationOperationReceiptRepository
{
    private readonly List<ReservationOperationReceipt> _receipts = [];
    public IReadOnlyCollection<ReservationOperationReceipt> Receipts => _receipts;

    public Task<ReservationOperationReceipt?> GetAsync(
        string operationType,
        string businessKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_receipts.SingleOrDefault(
            receipt => receipt.OperationType == operationType && receipt.BusinessKey == businessKey));

    public void Add(ReservationOperationReceipt receipt) => _receipts.Add(receipt);
}

internal sealed class FakeAllocationGenerationSource : IAllocationGenerationSource
{
    private long _generation;
    public Task<long> NextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Interlocked.Increment(ref _generation));
}

internal sealed class RecordingSeatAvailabilityEventPublisher : ISeatAvailabilityEventPublisher
{
    public List<(Guid EventId, IReadOnlyList<Guid> SeatIds, string Status, long Generation)> Published { get; } = [];

    public Task PublishAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        Published.Add((eventId, seatIds, status, generation));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSeatRepository(IEnumerable<Seat>? initial = null) : ISeatRepository
{
    private readonly List<Seat> _seats = initial?.ToList() ?? [];

    public Task<bool> InventoryExistsAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_seats.Any(seat => seat.EventId == eventId));

    public void AddRange(IEnumerable<Seat> seats) => _seats.AddRange(seats);

    public Task<IReadOnlyList<Seat>> GetByEventAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Seat>>(_seats.Where(seat => seat.EventId == eventId).ToList());

    public Task<IReadOnlyList<Seat>> GetByIdsAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Seat>>(
            _seats.Where(seat => seat.EventId == eventId && seatIds.Contains(seat.Id)).ToList());
}

internal sealed class FakeSeatHoldStore : ISeatHoldStore
{
    private Guid _eventId;
    private Guid _holdId;
    private IReadOnlyList<Guid> _heldSeatIds = [];
    private bool _confirmed;
    private readonly HashSet<Guid> _soldSeatIds = [];
    private HoldStoreRequest? _typedHold;
    private ConfirmHoldStoreRequest? _typedConfirmation;

    public bool FailNextInventoryLoad { get; set; }
    public bool InventoryLoaded { get; private set; }
    public IReadOnlySet<Guid> SoldSeatIds => _soldSeatIds;

    public void SeedHold(Guid eventId, Guid holdId, IReadOnlyList<Guid> seatIds)
    {
        _eventId = eventId;
        _holdId = holdId;
        _heldSeatIds = seatIds;
    }

    public void SeedSold(IEnumerable<Guid> seatIds) => _soldSeatIds.UnionWith(seatIds);

    public void SeedTypedHold(
        Guid eventId,
        Guid holdId,
        Guid ownerUserId,
        long generation,
        IReadOnlyList<Guid> seatIds) =>
        _typedHold = new HoldStoreRequest(
            eventId, seatIds, holdId, ownerUserId, generation, TimeSpan.FromMinutes(5));

    public Task LoadInventoryAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        if (FailNextInventoryLoad)
        {
            FailNextInventoryLoad = false;
            throw new InvalidOperationException("Failpoint: Postgres committed before Redis inventory load.");
        }

        InventoryLoaded = true;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> TryHoldAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        Guid holdId,
        TimeSpan ttl,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<HoldStoreResult> TryHoldAsync(
        HoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        _typedHold = request;
        return Task.FromResult(new HoldStoreResult(HoldStoreOutcome.Held, []));
    }

    public Task<IReadOnlyList<Guid>> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default)
    {
        if (_confirmed || eventId != _eventId || holdId != _holdId)
            return Task.FromResult<IReadOnlyList<Guid>>([]);

        _confirmed = true;
        _soldSeatIds.UnionWith(_heldSeatIds);
        return Task.FromResult(_heldSeatIds);
    }

    public Task<ConfirmHoldStoreResult> ConfirmAsync(
        ConfirmHoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_typedConfirmation is not null)
        {
            var same = SameTuple(_typedConfirmation, request);
            return Task.FromResult(new ConfirmHoldStoreResult(
                same ? ConfirmHoldStoreOutcome.AlreadyAppliedSame : ConfirmHoldStoreOutcome.Conflict,
                same ? request.SeatIds.OrderBy(id => id).ToArray() : [],
                same ? null : "confirmation tuple mismatch"));
        }

        if (_typedHold is null)
        {
            return Task.FromResult(new ConfirmHoldStoreResult(
                ConfirmHoldStoreOutcome.MissingOrExpired, [], "hold missing"));
        }

        var heldSeats = _typedHold.SeatIds.ToHashSet();
        var requestedSeats = request.SeatIds.ToHashSet();
        var matches = _typedHold.EventId == request.EventId
            && _typedHold.HoldId == request.HoldId
            && _typedHold.OwnerUserId == request.OwnerUserId
            && _typedHold.Generation == request.Generation
            && heldSeats.SetEquals(requestedSeats);
        if (!matches)
        {
            return Task.FromResult(new ConfirmHoldStoreResult(
                ConfirmHoldStoreOutcome.Conflict, [], "hold authority mismatch"));
        }

        _typedConfirmation = request;
        _soldSeatIds.UnionWith(request.SeatIds);
        return Task.FromResult(new ConfirmHoldStoreResult(
            ConfirmHoldStoreOutcome.Applied, request.SeatIds.OrderBy(id => id).ToArray()));
    }

    public Task RestoreAsync(
        RestoreSeatAllocationRequest request,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RebuildSeatCacheResult> RebuildAsync(
        RebuildSeatCacheRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new RebuildSeatCacheResult(true, [], request.SnapshotVersion));

    public Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, SeatStatus>>(
            seatIds.ToDictionary(id => id, id => _soldSeatIds.Contains(id) ? SeatStatus.Sold : SeatStatus.Available));

    public Task RemoveFromSoldAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default)
    {
        _soldSeatIds.ExceptWith(seatIds);
        return Task.CompletedTask;
    }

    private static bool SameTuple(ConfirmHoldStoreRequest left, ConfirmHoldStoreRequest right) =>
        left.EventId == right.EventId
        && left.HoldId == right.HoldId
        && left.OrderId == right.OrderId
        && left.OwnerUserId == right.OwnerUserId
        && left.Generation == right.Generation
        && left.SeatIds.ToHashSet().SetEquals(right.SeatIds);
}

internal sealed class ControlledReservationUnitOfWork : IUnitOfWork
{
    public bool FailNextSave { get; set; }
    public int? FailOnSaveNumber { get; set; }
    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        if (FailNextSave || FailOnSaveNumber == SaveCount)
        {
            FailNextSave = false;
            FailOnSaveNumber = null;
            throw new InvalidOperationException("Failpoint: Redis committed before Postgres SaveChanges.");
        }

        return Task.CompletedTask;
    }
}

internal sealed class ControlledSeatMapNotifier : ISeatMapNotifier
{
    public bool FailNextNotification { get; set; }

    public Task SeatsUpdatedAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (FailNextNotification)
        {
            FailNextNotification = false;
            throw new InvalidOperationException("Failpoint: durable confirmation committed before SignalR notify.");
        }

        return Task.CompletedTask;
    }
}
