namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Command sent by the PurchaseSaga as a compensation step when payment or ticketing fails.
/// The reservation service consumer calls <c>ReservationService.ReleaseAsync</c> (Phase 2)
/// which is already idempotent — releasing an unknown or already-released hold is a no-op.
/// </summary>
public sealed record ReleaseSeats(
    Guid OrderId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds);
