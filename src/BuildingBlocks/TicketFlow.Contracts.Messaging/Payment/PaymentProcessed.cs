namespace TicketFlow.Contracts.Messaging.Payment;

/// <summary>
/// Event published by the payment consumer after a successful charge.
/// The PurchaseSaga transitions <c>AwaitingPayment → AwaitingTickets</c> on receipt and sends
/// the <c>IssueTickets</c> command.
/// </summary>
public sealed record PaymentProcessed(
    Guid OrderId,
    Guid PaymentId,
    decimal Amount,
    DateTimeOffset ProcessedAt,
    string Currency = "VND",
    string? GatewayReference = null,
    string? OperationKey = null,
    long AggregateVersion = 0);
