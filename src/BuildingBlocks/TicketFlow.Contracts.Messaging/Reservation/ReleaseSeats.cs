namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Command sent by the PurchaseSaga as a compensation step when payment or ticketing fails.
/// The reservation service consumer calls
/// <c>ReservationService.ReleaseAsync(eventId, holdId, ...)</c> (Phase 2) which is already
/// idempotent — releasing an unknown or already-released hold is a no-op. Carries
/// <see cref="EventId"/> — required by the real <c>ReleaseAsync</c> signature, same reason as
/// <see cref="ConfirmSeats"/>.
/// </summary>
public sealed record ReleaseSeats(
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds);
