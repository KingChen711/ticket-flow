using System.Net;
using System.Net.Http.Json;
using TicketFlow.Recommendation.Application.Abstractions;
using TicketFlow.Recommendation.Application.Recommendations;

namespace TicketFlow.Recommendation.Infrastructure;

/// <summary>
/// Real HTTP call to the Search service (not a shared library/direct ES connection) — Recommendation
/// doesn't own or duplicate the event index, same service-boundary idiom the Aggregator (Ch5,
/// Commit 5) uses for its own fan-out. Search's minimal-API responses serialize camelCase by
/// default, matching <see cref="SearchDocumentDto"/>'s property names case-insensitively.
/// </summary>
public sealed class HttpSearchClient(HttpClient httpClient) : ISearchClient
{
    public async Task<RecommendedEvent?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetAsync($"/events/{eventId}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadFromJsonAsync<SearchDocumentDto>(cancellationToken);
        return document is null ? null : Map(document);
    }

    public Task<IReadOnlyList<RecommendedEvent>> FindByArtistAsync(string artist, CancellationToken cancellationToken = default) =>
        SearchAsync($"/events?artist={Uri.EscapeDataString(artist)}", cancellationToken);

    public Task<IReadOnlyList<RecommendedEvent>> FindByCityAsync(string city, CancellationToken cancellationToken = default) =>
        SearchAsync($"/events?city={Uri.EscapeDataString(city)}", cancellationToken);

    private async Task<IReadOnlyList<RecommendedEvent>> SearchAsync(string requestUri, CancellationToken cancellationToken)
    {
        var response = await httpClient.GetAsync(requestUri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SearchEventsResultDto>(cancellationToken);
        return result is null ? [] : result.Items.Select(Map).ToList();
    }

    private static RecommendedEvent Map(SearchDocumentDto d) =>
        new(d.EventId, d.Title, d.Artist, d.VenueName, d.City, d.StartsAt);

    private sealed record SearchDocumentDto(
        Guid EventId, string Title, string Artist, string VenueName, string City, DateTimeOffset StartsAt, string Status);

    private sealed record SearchEventsResultDto(IReadOnlyList<SearchDocumentDto> Items, long Total);
}
