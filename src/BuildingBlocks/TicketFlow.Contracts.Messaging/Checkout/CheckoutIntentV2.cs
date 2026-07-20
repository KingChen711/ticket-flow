namespace TicketFlow.Contracts.Messaging.Checkout;

/// <summary>
/// Additive checkout intent for the v2 flow.  The server obtains seats, event, and price from
/// the hold/allocation; callers must not re-submit those client-controlled values.
/// </summary>
public sealed record CheckoutIntentV2(
    Guid HoldId,
    string PaymentMethod,
    string IdempotencyKey,
    string? CustomerName = null,
    string? CustomerEmail = null);
