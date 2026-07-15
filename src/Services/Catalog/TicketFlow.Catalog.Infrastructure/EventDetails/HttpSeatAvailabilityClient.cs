using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using TicketFlow.Catalog.Application.EventDetails;

namespace TicketFlow.Catalog.Infrastructure.EventDetails;

/// <summary>
/// Real HTTP call to Reservation's REST availability endpoint — the boundary where a secondary
/// source can actually fail (timeout, connection refused, non-2xx). Catches broadly and degrades
/// to an empty list here, next to the failure, rather than pushing try/catch up into
/// EventDetailAggregator (Ch5 + Ch10: the aggregator shouldn't need to know HOW a source fails,
/// only that a failed source contributes nothing).
/// </summary>
public sealed class HttpSeatAvailabilityClient(HttpClient httpClient, ILogger<HttpSeatAvailabilityClient> logger)
    : ISeatAvailabilityClient
{
    public async Task<IReadOnlyList<SeatAvailabilityResponse>> GetAvailabilityAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<AvailabilityResponseDto>($"/events/{eventId}/availability", cancellationToken);
            return response?.Seats.Select(s => new SeatAvailabilityResponse(s.Id, s.SectionId, s.Label, s.Status)).ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reservation unavailable for event {EventId} — degrading to empty seats.", eventId);
            return [];
        }
    }

    private sealed record SeatDto(Guid Id, Guid SectionId, string Label, string Status);

    private sealed record AvailabilityResponseDto(Guid EventId, IReadOnlyList<SeatDto> Seats);
}
