using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Checkout;

/// <summary>
/// Everything a checkout has already been authorized to become. Amount, currency, seats and
/// event come from the server-side quote — the caller only contributes intent.
/// </summary>
public sealed record CheckoutAcceptanceRequest(
    Guid CustomerId,
    Guid HoldId,
    string IdempotencyKey,
    string Fingerprint,
    Guid CatalogEventId,
    IReadOnlyList<Guid> SeatIds,
    Money Total,
    string PaymentMethod,
    string CustomerName,
    string CustomerEmail,
    long PriceSnapshotVersion,
    long AllocationGeneration);

public sealed record CheckoutAcceptance(OrderResponse Order, bool WasReplayed);

/// <summary>
/// The one place an order stream comes into existence. Both the V2 checkout and the legacy
/// endpoint route through here, so there is a single durable path rather than two writers with
/// different guarantees.
/// </summary>
/// <remarks>
/// The order stream, the <see cref="CheckoutOperation"/> receipt and the <see cref="OrderDispatch"/>
/// outbox row are committed by one <c>SaveChangesAsync</c>. Nothing is published here — a broker
/// call inside the request is exactly the step that used to be able to fail after the order was
/// already durable, stranding a Pending order that no saga ever picked up.
/// </remarks>
public sealed class CheckoutAcceptanceService(
    IOrderStore orders,
    ICheckoutOperationStore operations,
    TimeProvider timeProvider)
{
    public async Task<CheckoutAcceptance> AcceptAsync(
        CheckoutAcceptanceRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = CheckoutOperation.KeyFor(request.CustomerId, request.IdempotencyKey);

        if (await operations.FindOperationAsync(key, cancellationToken) is { } existing)
            return await ReplayAsync(existing, request, cancellationToken);

        // A different key pointing at the same hold is still the same purchase. Catching it here
        // turns a client that retries with a fresh key into a replay instead of a second order.
        if (await operations.FindOperationByHoldAsync(request.HoldId, cancellationToken) is { } byHold)
            return await ReplayAsync(byHold, request, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var placed = Order.Place(request.CustomerId, request.CatalogEventId, request.SeatIds, request.Total, now);

        orders.StartStream(placed.OrderId, placed);
        operations.StoreOperation(new CheckoutOperation
        {
            Id = key,
            CustomerId = request.CustomerId,
            HoldId = request.HoldId,
            OrderId = placed.OrderId,
            Fingerprint = request.Fingerprint,
            CreatedAt = now,
        });
        operations.StoreDispatch(OrderDispatch.Pending(BuildMessage(request, placed.OrderId, now), now));

        try
        {
            await orders.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (operations.IsUniqueViolation(exception))
        {
            // Two requests raced past both lookups above. Postgres picked a winner; this caller is
            // the loser and must adopt the winner's order rather than retry into a second one.
            CheckoutOperationMetrics.RecordRace();
            operations.DiscardPendingChanges();

            var winner = await operations.FindOperationAsync(key, cancellationToken)
                ?? await operations.FindOperationByHoldAsync(request.HoldId, cancellationToken);

            // No winner means the duplicate was on something else entirely; do not paper over it.
            if (winner is null)
                throw;

            return await ReplayAsync(winner, request, cancellationToken);
        }

        CheckoutOperationMetrics.RecordAccepted();
        return new CheckoutAcceptance(OrderResponseMapper.Map(Order.Load([placed])), WasReplayed: false);
    }

    private async Task<CheckoutAcceptance> ReplayAsync(
        CheckoutOperation operation,
        CheckoutAcceptanceRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(operation.Fingerprint, request.Fingerprint, StringComparison.Ordinal))
        {
            CheckoutOperationMetrics.RecordConflict();
            throw new CheckoutConflictException(
                "This idempotency key was already used for a different checkout request.");
        }

        var order = await orders.LoadAsync(operation.OrderId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Checkout operation '{operation.Id}' points at order '{operation.OrderId}', which has no stream.");

        CheckoutOperationMetrics.RecordReplayed();
        return new CheckoutAcceptance(OrderResponseMapper.Map(order), WasReplayed: true);
    }

    private static OrderSubmitted BuildMessage(CheckoutAcceptanceRequest request, Guid orderId, DateTimeOffset now) =>
        new(
            orderId,
            request.CatalogEventId,
            request.CustomerId,
            request.HoldId,
            request.SeatIds,
            request.Total.Amount,
            request.PaymentMethod,
            request.CustomerName,
            request.CustomerEmail,
            now,
            request.Total.Currency,
            request.PriceSnapshotVersion,
            request.IdempotencyKey,
            request.HoldId.ToString(),
            request.AllocationGeneration);
}
