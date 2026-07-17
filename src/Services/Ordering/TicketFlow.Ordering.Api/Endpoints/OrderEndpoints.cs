using System.Security.Claims;
using TicketFlow.Ordering.Application.Orders;

namespace TicketFlow.Ordering.Api.Endpoints;

// Request-body-only fields for placing an order — CustomerId/CustomerName/CustomerEmail no
// longer come from the body (Phase 7 Commit 4 closes the gap PlaceOrderCommand's own doc
// comment flagged since Phase 5): the endpoint below reads them from the caller's JWT claims
// instead, since AddTicketFlowAuth's MapInboundClaims=false keeps raw claim names (sub/name/email).
public sealed record PlaceOrderRequest(
    Guid CatalogEventId,
    IReadOnlyList<Guid> SeatIds,
    decimal TotalAmount,
    string? Currency,
    Guid HoldId,
    string PaymentMethod);

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/orders").WithTags("Orders").RequireAuthorization("RequireAuthenticated");

        group.MapPost("/", async (PlaceOrderRequest request, ClaimsPrincipal user, PlaceOrderCommandHandler handler, CancellationToken ct) =>
        {
            var customerId = Guid.Parse(user.FindFirstValue("sub")!);
            var customerName = user.FindFirstValue("name") ?? "";
            var customerEmail = user.FindFirstValue("email") ?? "";

            var command = new PlaceOrderCommand(
                customerId, request.CatalogEventId, request.SeatIds, request.TotalAmount, request.Currency,
                request.HoldId, request.PaymentMethod, customerName, customerEmail);

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
            Results.Ok(await handler.HandleAsync(new ConfirmOrderCommand(id), ct))).RequireAuthorization("RequireAdmin");

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelOrderRequest request, CancelOrderCommandHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new CancelOrderCommand(id, request.Reason), ct))).RequireAuthorization("RequireAdmin");

        // Read side — handlers here go through IOrderViewStore (MyOrderView projection),
        // never IOrderStore/the event stream. That split is the actual point of Commit 6, not
        // just having separate endpoints.
        //
        // Ownership check (Phase 8 Commit 2, closes the Phase 7 backlog item): a customer can
        // only fetch their own order by id. Mismatch returns 404, not 403 — a 403 would confirm
        // the order id exists and belongs to someone else, which is itself a (minor) information
        // leak. Admin bypasses the check.
        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, GetOrderByIdQueryHandler handler, CancellationToken ct) =>
        {
            var order = await handler.HandleAsync(new GetOrderByIdQuery(id), ct);
            if (order is null)
                return Results.NotFound();

            var customerId = Guid.Parse(user.FindFirstValue("sub")!);
            if (order.CustomerId != customerId && !user.IsInRole("admin"))
                return Results.NotFound();

            return Results.Ok(order);
        });

        // "My orders" — always the caller's own, from the sub claim. No customerId query param
        // anymore (Phase 7 backlog: this used to be a "plain trusted request field" the caller
        // could set to anyone's id).
        group.MapGet("/", async (ClaimsPrincipal user, GetMyOrdersQueryHandler handler, CancellationToken ct) =>
        {
            var customerId = Guid.Parse(user.FindFirstValue("sub")!);
            return Results.Ok(await handler.HandleAsync(new GetMyOrdersQuery(customerId), ct));
        });
    }
}
