namespace TicketFlow.Contracts.Messaging.Ordering;

/// <summary>
/// Integration event published when the PurchaseSaga ends on a cancellation / compensation path
/// (seats confirmation failed, payment failed, or ticketing failed after refund + release).
/// Named with the "IntegrationEvent" suffix to distinguish it from the internal Order domain
/// event <c>OrderCancelled</c> (which lives in the ordering aggregate and is not a cross-service
/// contract).
/// </summary>
public sealed record OrderCancelledIntegrationEvent(
    Guid OrderId,
    Guid UserId,
    string Reason,
    DateTimeOffset CancelledAt);
