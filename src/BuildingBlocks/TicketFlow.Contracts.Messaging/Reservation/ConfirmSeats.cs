namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Command sent by the PurchaseSaga to confirm (lock permanently) the seat hold identified by
/// <see cref="HoldId"/>. The reservation service consumer calls
/// <c>ReservationService.ConfirmAsync(eventId, holdId, ...)</c> (Phase 2 Lua atomic, no rewrite
/// needed) and publishes <see cref="SeatsConfirmed"/> or <see cref="SeatsConfirmationFailed"/>
/// in response. Carries <see cref="EventId"/> — required by the real
/// <c>ConfirmAsync</c> signature (the Redis key scheme is <c>hold:{eventId}:{seatId}</c>, same
/// reason the REST confirm/release endpoints need it in Phase 2 Commit 7).
/// </summary>
public sealed record ConfirmSeats(
    Guid OrderId,
    Guid EventId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds);
