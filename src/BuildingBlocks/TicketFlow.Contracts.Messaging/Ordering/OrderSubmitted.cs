namespace TicketFlow.Contracts.Messaging.Ordering;

/// <summary>
/// Integration event published by the ordering service when a new order is placed.
/// The PurchaseSaga listens for this event to start a new saga instance.
/// CorrelationId = OrderId throughout the saga.
/// </summary>
public sealed record OrderSubmitted(
    Guid OrderId,
    Guid EventId,
    Guid UserId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds,
    decimal Amount,
    string PaymentMethod,
    DateTimeOffset SubmittedAt);
