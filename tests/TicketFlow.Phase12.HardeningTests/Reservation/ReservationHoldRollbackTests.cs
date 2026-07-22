using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Application.Seats;
using TicketFlow.Reservation.Domain.SalePolicies;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Phase12.HardeningTests.Reservation;

public sealed class ReservationHoldRollbackTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Redis_hold_is_released_with_the_exact_authority_tuple_when_quote_persistence_fails()
    {
        var now = DateTimeOffset.UtcNow;
        var eventId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var seat = Seat.Create(eventId, sectionId, "ROLLBACK-1");
        var policy = EventSalePolicy.FromSnapshot(
            eventId,
            "Published",
            now.AddHours(-1),
            now.AddHours(1),
            15,
            now,
            [(sectionId, 125_000m, "VND")]);
        var holdStore = new RollbackAwareHoldStore();
        var unitOfWork = new ControlledReservationUnitOfWork { FailNextSave = true };
        var service = new ReservationService(
            new FakeSeatRepository([seat]),
            holdStore,
            new FakeEventSalePolicyRepository(policy),
            new FakeHoldQuoteRepository(),
            new FakeSeatAllocationRepository(),
            new FakeReservationOperationReceiptRepository(),
            new FakeAllocationGenerationSource(),
            unitOfWork,
            new RecordingSeatAvailabilityEventPublisher(),
            TimeProvider.System,
            new ReservationOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.HoldAsync(
            new HoldSeatsRequest(eventId, [seat.Id], ownerUserId)));

        Assert.NotNull(holdStore.HeldRequest);
        Assert.NotNull(holdStore.ReleaseRequest);
        Assert.Equal(holdStore.HeldRequest!.EventId, holdStore.ReleaseRequest!.EventId);
        Assert.Equal(holdStore.HeldRequest.HoldId, holdStore.ReleaseRequest.HoldId);
        Assert.Equal(holdStore.HeldRequest.OwnerUserId, holdStore.ReleaseRequest.OwnerUserId);
        Assert.Equal(holdStore.HeldRequest.Generation, holdStore.ReleaseRequest.Generation);
        Assert.Equal(holdStore.HeldRequest.SeatIds.Order(), holdStore.ReleaseRequest.SeatIds.Order());
        Assert.Null(holdStore.ActiveHold);

        var statuses = await holdStore.GetLiveStatusesAsync(eventId, [seat.Id]);
        Assert.Equal(SeatStatus.Available, statuses[seat.Id]);
    }
}

internal sealed class RollbackAwareHoldStore : ISeatHoldStore
{
    public HoldStoreRequest? HeldRequest { get; private set; }
    public HoldStoreRequest? ActiveHold { get; private set; }
    public ReleaseHoldStoreRequest? ReleaseRequest { get; private set; }

    public Task LoadInventoryAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

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
        HeldRequest = request;
        ActiveHold = request;
        return Task.FromResult(HoldStoreResult.Succeeded());
    }

    public Task<IReadOnlyList<Guid>> ReleaseAsync(
        Guid eventId,
        Guid holdId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<ReleaseHoldStoreResult> ReleaseAsync(
        ReleaseHoldStoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ReleaseRequest = request;
        var exactMatch = ActiveHold is not null
            && ActiveHold.EventId == request.EventId
            && ActiveHold.HoldId == request.HoldId
            && ActiveHold.OwnerUserId == request.OwnerUserId
            && ActiveHold.Generation == request.Generation
            && ActiveHold.SeatIds.ToHashSet().SetEquals(request.SeatIds);
        if (!exactMatch)
        {
            return Task.FromResult(new ReleaseHoldStoreResult(
                ReleaseHoldStoreOutcome.Conflict,
                "release tuple did not match active hold"));
        }

        ActiveHold = null;
        return Task.FromResult(new ReleaseHoldStoreResult(ReleaseHoldStoreOutcome.Applied));
    }

    public Task<IReadOnlyList<Guid>> ConfirmAsync(
        Guid eventId,
        Guid holdId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<IReadOnlyDictionary<Guid, SeatStatus>> GetLiveStatusesAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, SeatStatus>>(seatIds.ToDictionary(
            seatId => seatId,
            seatId => ActiveHold is not null
                      && ActiveHold.EventId == eventId
                      && ActiveHold.SeatIds.Contains(seatId)
                ? SeatStatus.Held
                : SeatStatus.Available));

    public Task RemoveFromSoldAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
