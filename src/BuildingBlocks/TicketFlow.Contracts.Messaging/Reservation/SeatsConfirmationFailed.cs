namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Event published by the reservation consumer when the hold cannot be confirmed — the most
/// common cause is the Redis TTL expiring before the customer completes checkout (Phase 2
/// automatically releases on TTL; no compensation needed here because no charge has been made).
/// The PurchaseSaga transitions directly to <c>Cancelled</c> on receipt.
/// </summary>
public sealed record SeatsConfirmationFailed(
    Guid OrderId,
    Guid HoldId,
    string Reason,
    string? OperationKey = null,
    long Generation = 0);
