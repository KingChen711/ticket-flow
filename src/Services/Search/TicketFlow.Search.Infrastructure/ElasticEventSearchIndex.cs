using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using TicketFlow.Search.Application.Abstractions;
using TicketFlow.Search.Application.Events;

namespace TicketFlow.Search.Infrastructure;

/// <summary>
/// Document _id = EventId.ToString() throughout — that's what makes UpsertAsync a true upsert
/// (re-indexing the same event overwrites in place, confirmed via a real reflection/API probe
/// against a live ES instance before writing this: IndexAsync with an explicit Id returns
/// Result.Created the first time, Result.Updated on repeat calls for the same id — not two docs).
/// </summary>
public sealed class ElasticEventSearchIndex(ElasticsearchClient client, string indexName) : IEventSearchIndex
{
    public async Task UpsertAsync(SearchDocument document, CancellationToken cancellationToken = default) =>
        await client.IndexAsync(document, i => i.Index(indexName).Id(document.EventId.ToString()), cancellationToken);

    /// <summary>
    /// Genuine partial update (ES _update API), not a full re-index — CatalogEventRescheduled
    /// doesn't carry Title/Artist/VenueName/City, so UpsertAsync isn't an option here without
    /// losing those fields. Probed for real: client.UpdateAsync&lt;TDocument, TPartialDoc&gt;
    /// with an anonymous partial-doc merges the given fields only, leaves the rest untouched.
    /// </summary>
    public async Task UpdateStartsAtAsync(Guid eventId, DateTimeOffset startsAt,
        CancellationToken cancellationToken = default) =>
        await client.UpdateAsync<SearchDocument, object>(
            indexName, eventId.ToString(), u => u.Doc(new { startsAt }), cancellationToken);

    public async Task DeleteAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        await client.DeleteAsync<SearchDocument>(eventId.ToString(), d => d.Index(indexName), cancellationToken);

    [Obsolete]
    public async Task<SearchEventsResult> SearchAsync(SearchEventsQuery query,
        CancellationToken cancellationToken = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;

        var response = await client.SearchAsync<SearchDocument>(s => s
                .Index(indexName)
                .From((page - 1) * pageSize)
                .Size(pageSize)
                .Query(q => q.Bool(b => b
                    .Must(BuildMustClauses(query))
                    .Filter(BuildFilterClauses(query)))),
            cancellationToken);

        return new SearchEventsResult(response.Documents.ToList(), response.Total);
    }

    // Bool query with no Must clause matches everything by default — so an empty array here
    // (Q not provided) correctly means "no full-text constraint", not "match nothing".
    private static Action<QueryDescriptor<SearchDocument>>[] BuildMustClauses(SearchEventsQuery query) =>
        string.IsNullOrWhiteSpace(query.Q)
            ? []
            : [q => q.Match(m => m.Field(f => f.Title).Query(query.Q))];

    private static Action<QueryDescriptor<SearchDocument>>[] BuildFilterClauses(SearchEventsQuery query)
    {
        var filters = new List<Action<QueryDescriptor<SearchDocument>>>();

        if (!string.IsNullOrWhiteSpace(query.Artist))
            filters.Add(f => f.Term(t => t.Field(ff => ff.Artist).Value(query.Artist)));

        if (!string.IsNullOrWhiteSpace(query.City))
            filters.Add(f => f.Term(t => t.Field(ff => ff.City).Value(query.City)));

        if (query.From.HasValue || query.To.HasValue)
        {
            filters.Add(f => f.Range(r => r.Date(d =>
            {
                d.Field(ff => ff.StartsAt);
                if (query.From.HasValue) d.Gte(query.From.Value.UtcDateTime);
                if (query.To.HasValue) d.Lte(query.To.Value.UtcDateTime);
            })));
        }

        return [.. filters];
    }
}
