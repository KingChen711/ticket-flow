namespace TicketFlow.Contracts.Messaging.Payment;

/// <summary>
/// Command sent by the PurchaseSaga after seats are confirmed. The payment service consumer
/// calls the <c>FakePaymentGatewayClient</c> (Polly-wrapped) to charge the customer, then
/// publishes <see cref="PaymentProcessed"/> or <see cref="PaymentFailed"/>.
/// </summary>
public sealed record ProcessPayment(
    Guid OrderId,
    Guid UserId,
    decimal Amount,
    string PaymentMethod,
    string Currency = "VND",
    string? OperationKey = null,
    long PriceSnapshotVersion = 0);
