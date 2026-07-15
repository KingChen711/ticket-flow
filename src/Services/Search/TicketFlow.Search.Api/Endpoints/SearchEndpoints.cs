using TicketFlow.Search.Application.Abstractions;
using TicketFlow.Search.Application.Events;

namespace TicketFlow.Search.Api.Endpoints;

public static class SearchEndpoints
{
    // Mounted at /events (not /search/events) — internal path, same convention as catalog's own
    // /events; the gateway route (Commit 8) prefixes/strips "/search" the same way it already
    // does for "/catalog".
    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/events", async (
            string? q,
            string? artist,
            string? city,
            DateTimeOffset? from,
            DateTimeOffset? to,
            IEventSearchIndex searchIndex,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20) =>
        {
            // page/pageSize deliberately declared with C# default values (not plain `int`) —
            // real bug caught in Docker verification: a non-nullable `int` parameter with no
            // default is treated as a REQUIRED query param by minimal API model binding, so
            // every call omitting page/pageSize (the common case) returned 400 instead of
            // falling back to page 1 / size 20.
            var query = new SearchEventsQuery(q, artist, city, from, to, page, pageSize);

            return Results.Ok(await searchIndex.SearchAsync(query, ct));
        }).WithTags("Search");

        // Direct doc lookup by EventId — Commit 4 (Recommendation resolves Artist/VenueName/City
        // for a purchased EventId this way instead of duplicating Search's index).
        app.MapGet("/events/{id:guid}", async (Guid id, IEventSearchIndex searchIndex, CancellationToken ct) =>
        {
            var document = await searchIndex.GetByIdAsync(id, ct);
            return document is null ? Results.NotFound() : Results.Ok(document);
        }).WithTags("Search");
    }
}
