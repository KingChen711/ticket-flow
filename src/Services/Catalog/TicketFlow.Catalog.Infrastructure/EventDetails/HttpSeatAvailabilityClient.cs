using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TicketFlow.Catalog.Application.EventDetails;

namespace TicketFlow.Catalog.Infrastructure.EventDetails;

/// <summary>
/// Real HTTP call to Reservation's REST availability endpoint — the boundary where a secondary
/// source can actually fail (timeout, connection refused, non-2xx). Catches dependency failures
/// and returns an explicit unavailable result here, next to the failure, rather than pushing try/catch up into
/// EventDetailAggregator (Ch5 + Ch10: the aggregator shouldn't need to know HOW a source fails,
/// only that a failed source contributes nothing).
/// </summary>
public sealed class HttpSeatAvailabilityClient(HttpClient httpClient, ILogger<HttpSeatAvailabilityClient> logger)
    : ISeatAvailabilityClient
{
    public async Task<SeatAvailabilityResult> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<AvailabilityResponseDto>($"/events/{eventId}/availability", cancellationToken);
            if (response is null)
                throw new InvalidDataException("Reservation returned an empty availability response.");

            return SeatAvailabilityResult.Available(
                response.Seats.Select(s => new SeatAvailabilityResponse(
                    s.Id, s.SectionId, s.Label, s.Status, s.Generation, s.UpdatedAt)).ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reservation unavailable for event {EventId} — returning explicit degraded availability.", eventId);
            return SeatAvailabilityResult.TemporarilyUnavailable();
        }
    }

    private sealed record SeatDto(
        Guid Id,
        Guid SectionId,
        string Label,
        string Status,
        long Generation = 0,
        DateTimeOffset? UpdatedAt = null);

    private sealed record AvailabilityResponseDto(Guid EventId, IReadOnlyList<SeatDto> Seats);
}
