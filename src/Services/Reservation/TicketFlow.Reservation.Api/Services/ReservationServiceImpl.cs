using Grpc.Core;
using TicketFlow.Contracts.Reservation;
using ApplicationReservationService = TicketFlow.Reservation.Application.Seats.ReservationService;

namespace TicketFlow.Reservation.Api.Services;

/// <summary>
/// Real availability as of Commit 6/7 (see docs/phase-2-plan.md) — replaces the Phase 1
/// hardcoded "always available" skeleton. Aliasing the Application service to
/// ApplicationReservationService because the generated gRPC base type is ALSO named
/// ReservationService (from the .proto's own "service ReservationService") — both live in
/// scope here, so the bare name would be ambiguous.
/// </summary>
public sealed class ReservationServiceImpl(ApplicationReservationService reservationService)
    : ReservationService.ReservationServiceBase
{
    public override async Task<CheckSeatAvailabilityResponse> CheckSeatAvailability(
        CheckSeatAvailabilityRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.EventId, out var eventId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.EventId}' is not a valid event id."));

        var seatIds = new List<Guid>(request.SeatIds.Count);
        foreach (var rawSeatId in request.SeatIds)
        {
            if (!Guid.TryParse(rawSeatId, out var seatId))
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{rawSeatId}' is not a valid seat id."));

            seatIds.Add(seatId);
        }

        var result = await reservationService.CheckAvailabilityAsync(eventId, seatIds, context.CancellationToken);

        var response = new CheckSeatAvailabilityResponse { AllAvailable = result.AllAvailable };
        response.UnavailableSeatIds.AddRange(result.UnavailableSeatIds.Select(id => id.ToString()));

        return response;
    }

    public override async Task<GetHoldQuoteResponse> GetHoldQuote(
        GetHoldQuoteRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.HoldId, out var holdId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.HoldId}' is not a valid hold id."));

        if (!Guid.TryParse(request.OwnerUserId, out var ownerUserId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{request.OwnerUserId}' is not a valid user id."));

        var quote = await reservationService.GetQuoteAsync(holdId, ownerUserId, context.CancellationToken);
        if (quote is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Hold quote was not found, is expired, cancelled, or belongs to another user."));

        var response = new GetHoldQuoteResponse
        {
            HoldId = quote.HoldId.ToString(),
            EventId = quote.EventId.ToString(),
            OwnerUserId = quote.OwnerUserId.ToString(),
            Amount = quote.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Currency = quote.Currency,
            PriceSnapshotVersion = quote.PriceSnapshotVersion,
            Generation = quote.Generation,
            ExpiresAt = quote.ExpiresAt.ToString("O"),
        };
        response.SeatIds.AddRange(quote.SeatIds.Select(id => id.ToString()));
        return response;
    }
}
