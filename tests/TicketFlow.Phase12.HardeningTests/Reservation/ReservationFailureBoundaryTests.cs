using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.Holds;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

public sealed class ReservationFailureBoundaryTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Crash_after_Redis_confirm_before_Postgres_commit_must_replay_the_same_confirm_receipt()
    {
        var eventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var seat = Seat.Create(eventId, Guid.NewGuid(), "VIP-1");
        var repository = new FakeSeatRepository([seat]);
        var holds = new FakeSeatHoldStore();
        holds.SeedHold(eventId, holdId, [seat.Id]);
        var unitOfWork = new ControlledReservationUnitOfWork { FailNextSave = true };
        var service = CreateService(repository, holds, unitOfWork, new ControlledSeatMapNotifier());

        // Failpoint: Redis has moved the hold to Sold; Postgres SaveChanges throws.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(eventId, holdId));

        var replay = await service.ConfirmAsync(eventId, holdId);

        // A durable ReservationOperation receipt must return the first operation's exact outcome.
        Assert.Equal([seat.Id], replay);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Failure_after_Postgres_commit_in_SignalR_notifier_must_not_turn_success_into_failure()
    {
        var eventId = Guid.NewGuid();
        var holdId = Guid.NewGuid();
        var seat = Seat.Create(eventId, Guid.NewGuid(), "A-1");
        var repository = new FakeSeatRepository([seat]);
        var holds = new FakeSeatHoldStore();
        holds.SeedHold(eventId, holdId, [seat.Id]);
        var notifier = new ControlledSeatMapNotifier { FailNextNotification = true };
        var service = CreateService(repository, holds, new ControlledReservationUnitOfWork(), notifier);

        // Failpoint: Redis and Postgres committed, then the non-critical notifier threw.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfirmAsync(eventId, holdId));

        var replay = await service.ConfirmAsync(eventId, holdId);

        Assert.Equal([seat.Id], replay);
        Assert.Equal(SeatStatus.Sold, seat.Status);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Stale_release_from_order_A_must_not_unsell_a_seat_now_owned_by_order_B()
    {
        var eventId = Guid.NewGuid();
        var seat = Seat.Create(eventId, Guid.NewGuid(), "B-1");
        seat.MarkSold(); // Durable allocation is conceptually owned by order B.
        var repository = new FakeSeatRepository([seat]);
        var holds = new FakeSeatHoldStore();
        holds.SeedSold([seat.Id]);
        var service = CreateService(repository, holds, new ControlledReservationUnitOfWork(), new ControlledSeatMapNotifier());

        // Current API carries only seat ids, so delayed compensation for order A cannot prove
        // ownership/generation and blindly makes order B's seat Available.
        await service.UnsellAsync(eventId, [seat.Id]);

        Assert.Equal(SeatStatus.Sold, seat.Status);
        Assert.Contains(seat.Id, holds.SoldSeatIds);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Crash_after_inventory_Postgres_commit_before_Redis_load_must_be_repairable_by_retry()
    {
        var eventId = Guid.NewGuid();
        var repository = new FakeSeatRepository();
        var holds = new FakeSeatHoldStore { FailNextInventoryLoad = true };
        var service = new InventoryService(repository, holds, new ControlledReservationUnitOfWork());
        var request = new ProvisionInventoryRequest(
            eventId,
            [new ProvisionSectionRequest(Guid.NewGuid(), "VIP", 2)]);

        // Failpoint: DB rows committed, Redis LoadInventory did not.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProvisionAsync(request));

        var retryError = await Record.ExceptionAsync(() => service.ProvisionAsync(request));

        Assert.Null(retryError);
        Assert.True(holds.InventoryLoaded);
    }

    private static ReservationService CreateService(
        ISeatRepository repository,
        ISeatHoldStore holds,
        IUnitOfWork unitOfWork,
        ISeatMapNotifier notifier) =>
        new(
            repository,
            holds,
            new FakeEventSalePolicyRepository(),
            new FakeHoldQuoteRepository(),
            unitOfWork,
            notifier,
            TimeProvider.System,
            new ReservationOptions());
}

internal sealed class FakeEventSalePolicyRepository(EventSalePolicy? policy = null) : IEventSalePolicyRepository
{
    public EventSalePolicy? Policy { get; private set; } = policy;

    public Task<EventSalePolicy?> GetAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Policy?.Id == eventId ? Policy : null);

    public void Add(EventSalePolicy value) => Policy = value;
}

internal sealed class FakeHoldQuoteRepository : IHoldQuoteRepository
{
    private readonly Dictionary<Guid, HoldQuote> _quotes = [];

    public IReadOnlyCollection<HoldQuote> Quotes => _quotes.Values;

    public Task<HoldQuote?> GetAsync(Guid holdId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_quotes.GetValueOrDefault(holdId));

    public void Add(HoldQuote quote) => _quotes.Add(quote.Id, quote);
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
}

internal sealed class ControlledReservationUnitOfWork : IUnitOfWork
{
    public bool FailNextSave { get; set; }
    public int SaveCount { get; private set; }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        if (FailNextSave)
        {
            FailNextSave = false;
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
