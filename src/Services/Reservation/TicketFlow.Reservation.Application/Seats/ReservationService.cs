using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Seats;

public sealed class ReservationService(
    ISeatRepository seatRepository,
    ISeatHoldStore holdStore,
    IUnitOfWork unitOfWork,
    ISeatMapNotifier notifier,
    TimeProvider timeProvider,
    ReservationOptions options)
{
    private readonly TimeSpan _holdTtl = TimeSpan.FromMinutes(options.HoldTtlMinutes);

    public async Task<HoldSeatsResult> HoldAsync(HoldSeatsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SeatIds.Count == 0)
            throw new ArgumentException("At least one seat id is required.", nameof(request));

        var holdId = Guid.NewGuid();
        var unavailable = await holdStore.TryHoldAsync(request.EventId, request.SeatIds, holdId, _holdTtl, cancellationToken);

        if (unavailable.Count > 0)
            return HoldSeatsResult.Unavailable(unavailable);

        // Best-effort broadcast — a dropped SignalR message just means a live viewer's seat map
        // goes stale until their next refetch, never a source-of-truth issue (Redis already
        // holds the real state). Not awaited-with-retry, not part of any transaction.
        await notifier.SeatsUpdatedAsync(request.EventId, request.SeatIds, "Held", cancellationToken);
        return HoldSeatsResult.Succeeded(holdId, timeProvider.GetUtcNow().Add(_holdTtl));
    }

    public async Task<bool> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default)
    {
        var releasedSeatIds = await holdStore.ReleaseAsync(eventId, holdId, cancellationToken);
        if (releasedSeatIds.Count == 0)
            return false;

        await notifier.SeatsUpdatedAsync(eventId, releasedSeatIds, "Available", cancellationToken);
        return true;
    }

    /// <summary>
    /// Redis confirm first (moves the held seats into the sold set and tells us which seats),
    /// then persist Sold to Postgres. These two are NOT atomic across stores — that gap is the
    /// Saga/Outbox concern of Phase 4/5. Safe here because only the exclusive holder reaches this.
    /// Returns the actual confirmed seat ids (empty means the hold was no longer valid) rather
    /// than a bool — the PurchaseSaga's ConfirmSeatsConsumer (Commit 6) needs the real list for
    /// SeatsConfirmed, not just success/failure; that data already existed here, just discarded.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default)
    {
        var confirmedSeatIds = await holdStore.ConfirmAsync(eventId, holdId, cancellationToken);
        if (confirmedSeatIds.Count == 0)
            return confirmedSeatIds;

        var seats = await seatRepository.GetByIdsAsync(eventId, confirmedSeatIds, cancellationToken);
        foreach (var seat in seats)
            seat.MarkSold();

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await notifier.SeatsUpdatedAsync(eventId, confirmedSeatIds, "Sold", cancellationToken);
        return confirmedSeatIds;
    }

    /// <summary>
    /// Compensation (Commit 9) — reverses a CONFIRMED sale, unlike ReleaseAsync (which only ever
    /// releases an active hold, see Seat.MarkAvailable's doc comment for why they're different).
    /// Idempotent: seats already Available are silently skipped, not an error.
    /// </summary>
    public async Task UnsellAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return;

        await holdStore.RemoveFromSoldAsync(eventId, seatIds, cancellationToken);

        var seats = await seatRepository.GetByIdsAsync(eventId, seatIds, cancellationToken);
        foreach (var seat in seats)
            seat.MarkAvailable();

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Section of each given seat that belongs to the event, for ordering to price an order.
    /// Reads Postgres only: unlike availability it needs neither live Redis status nor every
    /// seat of the event. Seat ids from another event or unknown ids are simply absent.
    /// </summary>
    public async Task<IReadOnlyList<SeatSectionDto>> GetSeatSectionsAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return [];

        var seats = await seatRepository.GetByIdsAsync(eventId, seatIds, cancellationToken);
        return seats.Select(seat => new SeatSectionDto(seat.Id, seat.SectionId)).ToList();
    }

    public async Task<EventAvailabilityResponse> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var seats = await seatRepository.GetByEventAsync(eventId, cancellationToken);
        var liveStatuses = await holdStore.GetLiveStatusesAsync(eventId, seats.Select(seat => seat.Id).ToList(), cancellationToken);

        var dtos = seats
            .Select(seat =>
            {
                var status = liveStatuses.TryGetValue(seat.Id, out var live) ? live : seat.Status;
                return new SeatDto(seat.Id, seat.SectionId, seat.Label, status.ToString());
            })
            .ToList();

        return new EventAvailabilityResponse(eventId, dtos);
    }

    /// <summary>
    /// Used by the gRPC endpoint (Commit 8) where the caller (ordering) supplies seat ids we
    /// haven't validated yet — unlike GetAvailabilityAsync, which only ever asks Redis about
    /// seat ids it already read from Postgres, this must treat any id NOT found in Postgres as
    /// unavailable too, or a made-up seat id would come back as Available by default.
    /// </summary>
    public async Task<CheckAvailabilityResult> CheckAvailabilityAsync(Guid eventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        if (seatIds.Count == 0)
            return new CheckAvailabilityResult(true, []);

        var knownSeats = await seatRepository.GetByIdsAsync(eventId, seatIds, cancellationToken);
        var knownIds = knownSeats.Select(seat => seat.Id).ToHashSet();

        var unavailable = seatIds.Where(id => !knownIds.Contains(id)).ToList();

        var liveStatuses = await holdStore.GetLiveStatusesAsync(eventId, seatIds, cancellationToken);
        foreach (var seat in knownSeats)
        {
            var status = liveStatuses.TryGetValue(seat.Id, out var live) ? live : seat.Status;
            if (status != SeatStatus.Available)
                unavailable.Add(seat.Id);
        }

        return new CheckAvailabilityResult(unavailable.Count == 0, unavailable);
    }
}
