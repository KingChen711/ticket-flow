namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Command sent by the PurchaseSaga to confirm (lock permanently) the seat hold identified by
/// <see cref="HoldId"/>. The reservation service consumer calls
/// <c>ReservationService.ConfirmAsync</c> (Phase 2 Lua atomic, no rewrite needed) and publishes
/// <see cref="SeatsConfirmed"/> or <see cref="SeatsConfirmationFailed"/> in response.
/// </summary>
public sealed record ConfirmSeats(
    Guid OrderId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds);
