namespace TicketFlow.Contracts.Messaging.Ordering;

/// <summary>
/// Integration event published by the ordering service when a new order is placed.
/// The PurchaseSaga listens for this event to start a new saga instance.
/// CorrelationId = OrderId throughout the saga.
/// </summary>
/// <remarks>
/// <see cref="CustomerName"/>/<see cref="CustomerEmail"/> were missing from the original
/// Commit 1 shape — the saga has no other data source to build the <c>IssueTickets</c> command
/// (which needs both) once payment succeeds, since it never talks to Identity/Catalog directly.
/// Found while implementing Commit 4 (PurchaseSaga), same class of gap as the missing
/// <c>EventId</c> on <c>ConfirmSeats</c>/<c>ReleaseSeats</c> caught in Commit 1's review. Real
/// values (from the authenticated user) are wired in Commit 5 when <c>PlaceOrderCommand</c>
/// starts publishing this event for real.
/// </remarks>
public sealed record OrderSubmitted(
    Guid OrderId,
    Guid EventId,
    Guid UserId,
    Guid HoldId,
    IReadOnlyList<Guid> SeatIds,
    decimal Amount,
    string PaymentMethod,
    string CustomerName,
    string CustomerEmail,
    DateTimeOffset SubmittedAt);
