using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Orders;

/// <summary>
/// HoldId/PaymentMethod/CustomerName/CustomerEmail exist purely to build the OrderSubmitted
/// integration event below — Order.Place/OrderPlaced don't need them (the seat hold and payment
/// method aren't part of Order's own persisted history, only the saga's). CustomerName/Email are
/// plain trusted request fields, same as CustomerId already was pre-Commit-5 — no JWT/claims
/// extraction exists anywhere in Ordering yet, so this doesn't introduce a new pattern, just
/// extends the existing one. Real auth-derived identity is a separate, not-yet-done concern.
/// </summary>
public sealed record PlaceOrderCommand(
    Guid CustomerId,
    Guid CatalogEventId,
    IReadOnlyList<Guid> SeatIds,
    Guid HoldId,
    string PaymentMethod,
    string CustomerName,
    string CustomerEmail);

public sealed class PlaceOrderCommandHandler(
    IOrderStore store,
    IIntegrationEventPublisher publisher,
    ISeatPricingSource pricingSource,
    TimeProvider timeProvider)
    : ICommandHandler<PlaceOrderCommand, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        var seatSectionsTask = pricingSource.GetSeatSectionsAsync(command.CatalogEventId, command.SeatIds, cancellationToken);
        var sectionPricesTask = pricingSource.GetSectionPricesAsync(command.CatalogEventId, cancellationToken);
        await Task.WhenAll(seatSectionsTask, sectionPricesTask);
        var total = OrderPricing.Calculate(command.SeatIds, seatSectionsTask.Result, sectionPricesTask.Result);
        var placed = Order.Place(command.CustomerId, command.CatalogEventId, command.SeatIds, total, timeProvider.GetUtcNow());

        // Both writes go into the same Marten session: the outbox publisher stages OrderSubmitted
        // as a document, so SaveChangesAsync commits the order stream and the message atomically
        // and the outbox dispatcher sends it afterwards. Nothing reaches the bus if the commit fails.
        store.StartStream(placed.OrderId, placed);
        await publisher.PublishAsync(new OrderSubmitted(
            placed.OrderId,
            command.CatalogEventId,
            command.CustomerId,
            command.HoldId,
            command.SeatIds,
            total.Amount,
            command.PaymentMethod,
            command.CustomerName,
            command.CustomerEmail,
            timeProvider.GetUtcNow()), cancellationToken);
        await store.SaveChangesAsync(cancellationToken);

        return OrderResponseMapper.Map(Order.Load([placed]));
    }
}
