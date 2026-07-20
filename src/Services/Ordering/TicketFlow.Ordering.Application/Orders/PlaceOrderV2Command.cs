using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Orders;

public sealed record PlaceOrderV2Command(
    Guid CustomerId,
    Guid HoldId,
    string PaymentMethod,
    string IdempotencyKey,
    string CustomerName,
    string CustomerEmail);

/// <summary>
/// V2 accepts purchase intent only. Event, seats, amount and currency always come from the
/// owner-scoped Reservation quote. Durable idempotency storage is deliberately Commit 7.
/// </summary>
public sealed class PlaceOrderV2CommandHandler(
    IOrderStore store,
    IIntegrationEventPublisher publisher,
    IHoldQuoteProvider quoteProvider,
    TimeProvider timeProvider)
    : ICommandHandler<PlaceOrderV2Command, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(
        PlaceOrderV2Command command,
        CancellationToken cancellationToken = default)
    {
        if (command.CustomerId == Guid.Empty)
            throw new ArgumentException("Customer id is required.", nameof(command));

        if (string.IsNullOrWhiteSpace(command.PaymentMethod))
            throw new ArgumentException("Payment method is required.", nameof(command));

        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 200)
            throw new ArgumentException("Idempotency key is required and cannot exceed 200 characters.", nameof(command));

        var quote = await quoteProvider.GetAsync(command.HoldId, command.CustomerId, cancellationToken)
            ?? throw new InvalidOperationException("Hold quote was not found, expired, cancelled, or belongs to another customer.");

        var now = timeProvider.GetUtcNow();
        if (quote.OwnerUserId != command.CustomerId || quote.ExpiresAt <= now)
            throw new InvalidOperationException("Hold quote is not valid for this customer.");

        if (quote.SeatIds.Count == 0 || quote.Amount <= 0 || quote.PriceSnapshotVersion <= 0)
            throw new InvalidOperationException("Hold quote is incomplete and cannot be used for checkout.");

        var total = Money.Create(quote.Amount, quote.Currency);
        var placed = Order.Place(command.CustomerId, quote.EventId, quote.SeatIds, total, now);

        store.StartStream(placed.OrderId, placed);
        await store.SaveChangesAsync(cancellationToken);

        await publisher.PublishAsync(new OrderSubmitted(
            placed.OrderId,
            quote.EventId,
            command.CustomerId,
            quote.HoldId,
            quote.SeatIds,
            total.Amount,
            command.PaymentMethod,
            command.CustomerName,
            command.CustomerEmail,
            now,
            total.Currency,
            quote.PriceSnapshotVersion,
            command.IdempotencyKey,
            quote.HoldId.ToString()), cancellationToken);

        return OrderResponseMapper.Map(Order.Load([placed]));
    }
}
