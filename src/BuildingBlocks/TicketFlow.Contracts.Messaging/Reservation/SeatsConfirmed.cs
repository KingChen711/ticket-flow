namespace TicketFlow.Contracts.Messaging.Reservation;

/// <summary>
/// Event published by the reservation consumer after successfully confirming the seat hold.
/// The PurchaseSaga transitions <c>AwaitingSeats → AwaitingPayment</c> on receipt.
/// </summary>
public sealed record SeatsConfirmed(
    Guid OrderId,
    IReadOnlyList<Guid> SeatIds,
    DateTimeOffset ConfirmedAt,
    string? AllocationReference = null,
    string? OperationKey = null,
    long AggregateVersion = 0);
