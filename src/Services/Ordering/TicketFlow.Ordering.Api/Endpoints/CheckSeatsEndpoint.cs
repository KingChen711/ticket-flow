using TicketFlow.Contracts.Reservation;

namespace TicketFlow.Ordering.Api.Endpoints;

/// <summary>
/// Proves the sync gRPC round-trip (Ch3): ordering (client) calls reservation (server)
/// over HTTP/2 and bridges the result back out as plain REST for the gateway.
/// </summary>
public static class CheckSeatsEndpoint
{
    public static void MapCheckSeatsEndpoint(this WebApplication app)
    {
        app.MapPost("/check-seats", async (
            CheckSeatsRequest request,
            ReservationService.ReservationServiceClient reservationClient,
            CancellationToken ct) =>
        {
            var grpcRequest = new CheckSeatAvailabilityRequest { EventId = request.EventId };
            grpcRequest.SeatIds.AddRange(request.SeatIds);

            var grpcResponse = await reservationClient.CheckSeatAvailabilityAsync(grpcRequest, cancellationToken: ct);

            return Results.Ok(new CheckSeatsResponse(grpcResponse.AllAvailable, grpcResponse.UnavailableSeatIds));
        }).WithTags("Ordering");
    }
}
