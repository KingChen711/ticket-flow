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

        group.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmOrderCommandHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new ConfirmOrderCommand(id), ct)));

        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelOrderRequest request, CancelOrderCommandHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(new CancelOrderCommand(id, request.Reason), ct)));
    }
}
