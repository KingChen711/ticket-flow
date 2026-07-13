using TicketFlow.Reservation.Application.Abstractions;
using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Seats;

public sealed class ReservationService(
    ISeatRepository seatRepository,
    ISeatHoldStore holdStore,
    IUnitOfWork unitOfWork,
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

        return unavailable.Count == 0
            ? HoldSeatsResult.Succeeded(holdId, timeProvider.GetUtcNow().Add(_holdTtl))
            : HoldSeatsResult.Unavailable(unavailable);
    }

    public Task<bool> ReleaseAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default) =>
        holdStore.ReleaseAsync(eventId, holdId, cancellationToken);

    /// <summary>
    /// Redis confirm first (moves the held seats into the sold set and tells us which seats),
    /// then persist Sold to Postgres. These two are NOT atomic across stores — that gap is the
    /// Saga/Outbox concern of Phase 4/5. Safe here because only the exclusive holder reaches this.
    /// </summary>
    public async Task<bool> ConfirmAsync(Guid eventId, Guid holdId, CancellationToken cancellationToken = default)
    {
        var confirmedSeatIds = await holdStore.ConfirmAsync(eventId, holdId, cancellationToken);
        if (confirmedSeatIds.Count == 0)
            return false;

        var seats = await seatRepository.GetByIdsAsync(eventId, confirmedSeatIds, cancellationToken);
        foreach (var seat in seats)
            seat.MarkSold();

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
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
