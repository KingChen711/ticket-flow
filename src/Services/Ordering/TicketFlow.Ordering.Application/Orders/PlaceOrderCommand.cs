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
        var seatSections = await pricingSource.GetSeatSectionsAsync(command.CatalogEventId, cancellationToken);
        var sectionPrices = await pricingSource.GetSectionPricesAsync(command.CatalogEventId, cancellationToken);
        var total = OrderPricing.Calculate(command.SeatIds, seatSections, sectionPrices);
        var placed = Order.Place(command.CustomerId, command.CatalogEventId, command.SeatIds, total, timeProvider.GetUtcNow());

        // No aggregate exists yet to Load — StartStream both creates the stream and stages its
        // first event; SaveChangesAsync is what actually commits it to Postgres.
        store.StartStream(placed.OrderId, placed);
        await store.SaveChangesAsync(cancellationToken);

        // Best-effort, NOT a transactional outbox: MassTransit.Marten (Commit 4) only ships a
        // saga repository, no Marten-backed equivalent of MassTransit.EntityFrameworkCore's
        // AddEntityFrameworkOutbox/UseBusOutbox (confirmed via the same package reflection probe
        // used to find MartenRepository — no Outbox-related type exists in it). A crash between
        // the SaveChangesAsync above and this Publish leaves an orphaned Pending order that never
        // starts a saga. Documented gap, not fixed here — the idiomatic Marten-native fix would
        // be an async daemon subscription (durable, checkpointed, survives crashes) relaying
        // OrderPlaced -> OrderSubmitted, mirroring the exact "Inline vs async projection"
        // trade-off already deferred for MyOrdersProjection in Phase 4. Backlog, not blocking:
        // Phase 5's DoD is about saga compensation (Commit 10), not this specific crash window.
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

        return OrderResponseMapper.Map(Order.Load([placed]));
    }
}
