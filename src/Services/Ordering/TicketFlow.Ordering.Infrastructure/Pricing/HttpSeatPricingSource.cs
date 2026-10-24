using System.Net;
using System.Net.Http.Json;
using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Infrastructure.Pricing;

/// <summary>
/// Reads the ordered seats' sections from reservation and price tiers from catalog's
/// event endpoint, service to service (not through the gateway). Unlike catalog's event-detail
/// aggregator this does not degrade to empty data: an order that cannot be priced is refused.
/// </summary>
public sealed class HttpSeatPricingSource(IHttpClientFactory httpClientFactory) : ISeatPricingSource
{
    public const string CatalogClient = "catalog";
    public const string ReservationClient = "reservation";

    public async Task<IReadOnlyDictionary<Guid, Guid>> GetSeatSectionsAsync(Guid catalogEventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(ReservationClient);
        using var response = await client.PostAsJsonAsync($"/events/{catalogEventId}/seat-sections", new { seatIds }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var seats = await response.Content.ReadFromJsonAsync<List<SeatDto>>(cancellationToken);
        return seats?.ToDictionary(seat => seat.Id, seat => seat.SectionId) ?? [];
    }

    public async Task<IReadOnlyList<SectionPrice>> GetSectionPricesAsync(Guid catalogEventId, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(CatalogClient);
        using var response = await client.GetAsync($"/events/{catalogEventId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Event '{catalogEventId}' was not found.");

        response.EnsureSuccessStatusCode();
        var @event = await response.Content.ReadFromJsonAsync<EventDto>(cancellationToken);
        return @event?.PriceTiers.Select(tier => new SectionPrice(tier.SectionId, tier.Amount, tier.Currency)).ToList() ?? [];
    }

    private sealed record SeatDto(Guid Id, Guid SectionId);

    private sealed record PriceTierDto(Guid SectionId, decimal Amount, string Currency);

    private sealed record EventDto(IReadOnlyList<PriceTierDto> PriceTiers);
}
