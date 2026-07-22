using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Orders;

/// <summary>
/// The pre-Commit-3 client-authoritative shape, kept only behind the dev-only <c>/orders/legacy</c>
/// endpoint. It still trusts the caller's amount, currency and seat list — that is the whole
/// reason V2 exists and this one is not reachable in production.
/// </summary>
public sealed record PlaceOrderCommand(
    Guid CustomerId,
    Guid CatalogEventId,
    IReadOnlyList<Guid> SeatIds,
    decimal TotalAmount,
    string? Currency,
    Guid HoldId,
    string PaymentMethod,
    string CustomerName,
    string CustomerEmail);

/// <summary>
/// Routes through the same <see cref="CheckoutAcceptanceService"/> as V2. Before Commit 7 this
/// handler was a second, independent writer: it minted its own order id and published straight to
/// the broker, so it had neither the durable receipt nor the outbox. Two writers with different
/// guarantees is the dual-write problem in miniature, so the legacy shape now differs only in
/// where the numbers come from, not in how durably the order is recorded.
/// </summary>
/// <remarks>
/// It has no idempotency key of its own, so one is derived from the hold: the legacy contract's
/// own notion of "the same purchase" is the hold being checked out.
/// </remarks>
public sealed class PlaceOrderCommandHandler(CheckoutAcceptanceService checkout)
    : ICommandHandler<PlaceOrderCommand, OrderResponse>
{
    public async Task<OrderResponse> HandleAsync(PlaceOrderCommand command, CancellationToken cancellationToken = default)
    {
        var currency = command.Currency ?? "VND";
        var total = Money.Create(command.TotalAmount, currency);

        var acceptance = await checkout.AcceptAsync(
            new CheckoutAcceptanceRequest(
                command.CustomerId,
                command.HoldId,
                $"legacy:{command.HoldId:N}",
                CheckoutRequestFingerprint.CreateLegacy(
                    command.CustomerId, command.HoldId, command.CatalogEventId, total.Amount, total.Currency),
                command.CatalogEventId,
                command.SeatIds,
                total,
                command.PaymentMethod,
                command.CustomerName,
                command.CustomerEmail,
                PriceSnapshotVersion: 0,
                AllocationGeneration: 0),
            cancellationToken);

        return acceptance.Order;
    }
}
