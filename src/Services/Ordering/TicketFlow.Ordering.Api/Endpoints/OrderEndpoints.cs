using TicketFlow.Ordering.Application.Orders;

namespace TicketFlow.Ordering.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/orders").WithTags("Orders");

        group.MapPost("/", async (PlaceOrderCommand command, PlaceOrderCommandHandler handler, CancellationToken ct) =>
        {
            var order = await handler.HandleAsync(command, ct);
            return Results.Created($"/orders/{order.OrderId}", order);
        });

        // Admin-override only as of Commit 5 (Phase 5) — the customer-facing purchase flow now
        // goes through PurchaseSaga (POST / -> PlaceOrder -> OrderSubmitted starts the saga),
        // which drives confirm/cancel itself via OrderCompletedConsumer and (from Commit 9)
        // compensation. These two endpoints still work exactly as in Phase 4, unchanged, for the
        // narrower case of an operator force-confirming/cancelling a still-Pending order without
        // going through payment/ticketing. See Order.Confirm()'s doc comment.
        group.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmOrderCommandHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new ConfirmOrderCommand(id), ct)));

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelOrderRequest request, CancelOrderCommandHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new CancelOrderCommand(id, request.Reason), ct)));

        // Read side — handlers here go through IOrderViewStore (MyOrderView projection),
        // never IOrderStore/the event stream. That split is the actual point of Commit 6, not
        // just having separate endpoints.
        group.MapGet("/{id:guid}", async (Guid id, GetOrderByIdQueryHandler handler, CancellationToken ct) =>
        {
            var order = await handler.HandleAsync(new GetOrderByIdQuery(id), ct);
            return order is null ? Results.NotFound() : Results.Ok(order);
        });

        group.MapGet("/", async (Guid customerId, GetMyOrdersQueryHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new GetMyOrdersQuery(customerId), ct)));
    }
}
