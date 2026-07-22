using System.Globalization;
using Grpc.Core;
using TicketFlow.Contracts.Reservation;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Api.Services;

public sealed class GrpcHoldQuoteProvider(ReservationService.ReservationServiceClient client) : IHoldQuoteProvider
{
    public async Task<AuthoritativeHoldQuote?> GetAsync(
        Guid holdId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        GetHoldQuoteResponse response;
        try
        {
            response = await client.GetHoldQuoteAsync(
                new GetHoldQuoteRequest
                {
                    HoldId = holdId.ToString(),
                    OwnerUserId = ownerUserId.ToString(),
                },
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            return null;
        }

        return new AuthoritativeHoldQuote(
            Guid.Parse(response.HoldId),
            Guid.Parse(response.EventId),
            Guid.Parse(response.OwnerUserId),
            response.SeatIds.Select(Guid.Parse).ToList(),
            decimal.Parse(response.Amount, NumberStyles.Number, CultureInfo.InvariantCulture),
            response.Currency,
            response.PriceSnapshotVersion,
            DateTimeOffset.Parse(response.ExpiresAt, CultureInfo.InvariantCulture),
            response.Generation);
    }
}
