using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.QueryDsl;
using TicketFlow.Search.Application.Abstractions;
using TicketFlow.Search.Application.Events;

namespace TicketFlow.Search.Infrastructure;

/// <summary>
/// Document _id = EventId.ToString() throughout — a redelivered or re-published lifecycle message
/// always targets the same document, never creates a duplicate.
/// </summary>
/// <remarks>
/// Commit 11 moved the revision/tombstone guard (see IEventSearchIndex's doc comment) INTO the
/// three Painless scripts below, rather than doing a GET, deciding in C#, then a plain write. A
/// GET-then-write has the same read-then-write gap that let two concurrent gate scanners both
/// admit a ticket (Commit 10) — two lifecycle messages for one event, applied concurrently, could
/// both read the same stored revision and both decide to apply. Elasticsearch runs an update
/// script atomically against the single document it targets, so the compare-and-decide happens
/// server-side with nothing else able to interleave.
///
/// Every script below reads <c>ctx._source.revision</c> through a null check, and that is not
/// defensive padding: documents indexed before Commit 11 have no <c>revision</c> field at all, and
/// Painless throws a NullPointerException on <c>&gt;=</c> or <c>Math.max</c> against null. Without
/// the check, ES answers 400, <c>EnsureValid</c> throws, and the consumer retries to the DLQ — so
/// every event indexed by the deployed version would become permanently un-updatable the moment
/// this ships. A missing revision reads as "unknown", which mục 10's expand-only rule defines as
/// 0: the stored document loses to any real write, exactly as an explicit 0 would.
/// </remarks>
public sealed class ElasticEventSearchIndex(ElasticsearchClient client, string indexName) : IEventSearchIndex
{
    /// <summary>
    /// Elasticsearch's <c>_update</c> is a get-modify-index cycle guarded by the document's
    /// sequence number, so two applies landing on one document concurrently make all but one fail
    /// with <c>version_conflict_engine_exception</c> — measured 9 of 10 against a real node. That
    /// is not an outage, and surfacing it as SearchIndexUnavailableException would be a lie.
    /// <c>retry_on_conflict</c> is the documented answer, and it is safe here specifically because
    /// each retry re-runs the script against freshly read state: the compare-and-decide is redone,
    /// never replayed, so a retry can still conclude "noop". This is not a blind write retry.
    ///
    /// 10, not a round guess: measured against a real node with ten simultaneous applies to one
    /// id. Against an existing document 5 was already enough; against a document being created by
    /// the same storm, 5 still left 4 of 10 failing and 10 left none — the upsert-create path
    /// contends harder because every writer starts by losing to the one that creates it. The
    /// budget is a bound, not a guarantee: a wider storm still surfaces as a failed apply, which
    /// the consumer redelivers, and the script re-decides correctly on the way back through.
    /// </summary>
    private const int RetryOnConflict = 10;

    public async Task ApplyPublishedAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        var response = await client.UpdateAsync<SearchDocument, object>(indexName, document.EventId.ToString(), u => u
            .Upsert(document)
            .RetryOnConflict(RetryOnConflict)
            .Script(s => s
                .Source("""
                    def stored = ctx._source.revision;
                    if (ctx._source.tombstoned == true) {
                        ctx.op = 'noop';
                    } else if (stored == null || params.revision >= stored) {
                        ctx._source.title = params.title;
                        ctx._source.artist = params.artist;
                        ctx._source.venueName = params.venueName;
                        ctx._source.city = params.city;
                        ctx._source.startsAt = params.startsAt;
                        ctx._source.status = params.status;
                        ctx._source.revision = params.revision;
                    } else {
                        ctx.op = 'noop';
                    }
                    """)
                .Params(new Dictionary<string, object>
                {
                    ["title"] = document.Title,
                    ["artist"] = document.Artist,
                    ["venueName"] = document.VenueName,
                    ["city"] = document.City,
                    ["startsAt"] = document.StartsAt,
                    ["status"] = document.Status,
                    ["revision"] = document.Revision,
                })),
            cancellationToken);

        EnsureValid(response, $"apply Published to event '{document.EventId}'");
    }

    public async Task ApplyRescheduledAsync(
        Guid eventId,
        DateTimeOffset startsAt,
        long revision,
        CancellationToken cancellationToken = default)
    {
        // Rescheduled carries nothing but StartsAt — if it overtakes Published, the placeholder
        // below is what keeps the fact alive instead of silently dropping it (see the old
        // UpdateStartsAtAsync's failure mode documented on IEventSearchIndex).
        var placeholder = new SearchDocument(eventId, "", "", "", "", startsAt, "Published", revision);

        var response = await client.UpdateAsync<SearchDocument, object>(indexName, eventId.ToString(), u => u
            .Upsert(placeholder)
            .RetryOnConflict(RetryOnConflict)
            .Script(s => s
                .Source("""
                    def stored = ctx._source.revision;
                    if (ctx._source.tombstoned == true) {
                        ctx.op = 'noop';
                    } else if (stored == null || params.revision >= stored) {
                        ctx._source.startsAt = params.startsAt;
                        ctx._source.revision = params.revision;
                    } else {
                        ctx.op = 'noop';
                    }
                    """)
                .Params(new Dictionary<string, object>
                {
                    ["startsAt"] = startsAt,
                    ["revision"] = revision,
                })),
            cancellationToken);

        EnsureValid(response, $"apply Rescheduled to event '{eventId}'");
    }

    public async Task ApplyCancelledAsync(Guid eventId, long revision, CancellationToken cancellationToken = default)
    {
        var placeholder = new SearchDocument(eventId, "", "", "", "", default, "Cancelled", revision, Tombstoned: true);

        // No revision guard — see IEventSearchIndex's doc comment: a Cancel is a domain-enforced
        // one-way door, so it is always safe to apply, and it must always win.
        var response = await client.UpdateAsync<SearchDocument, object>(indexName, eventId.ToString(), u => u
            .Upsert(placeholder)
            .RetryOnConflict(RetryOnConflict)
            .Script(s => s
                // Deliberately an if, not Math.max: Painless resolves Math.max over `def` operands
                // to the double overload, so the revision came back as 4.0 and then failed to
                // deserialize into a long — the document became unreadable through GetByIdAsync
                // the moment it was cancelled. Comparing and assigning keeps the value the
                // integer it arrived as.
                .Source("""
                    def stored = ctx._source.revision;
                    ctx._source.tombstoned = true;
                    ctx._source.status = 'Cancelled';
                    if (stored == null || params.revision > stored) {
                        ctx._source.revision = params.revision;
                    }
                    """)
                .Params(new Dictionary<string, object> { ["revision"] = revision })),
            cancellationToken);

        EnsureValid(response, $"apply Cancelled to event '{eventId}'");
    }

    public async Task<SearchDocument?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync<SearchDocument>(eventId.ToString(), g => g.Index(indexName), cancellationToken);

        // A 404 for GetAsync is Elasticsearch's normal, valid answer to "no document with this
        // id" — IsValidResponse is true and Found is false. Only a transport failure or a genuine
        // server error leaves IsValidResponse false, and that must not be read as "not found".
        if (!response.IsValidResponse && response.ApiCallDetails.HttpStatusCode != 404)
            throw new SearchIndexUnavailableException(
                $"Elasticsearch GET for event '{eventId}' failed: {response.DebugInformation}");

        if (!response.Found)
            return null;

        // A tombstoned document is, to every caller of this port, not found — Commit 11 keeps the
        // document to remember the revision and the fact of cancellation, but a cancelled event
        // was never purchasable and has no reason to still resolve as "found".
        return response.Source is { Tombstoned: true } ? null : response.Source;
    }

    public async Task<SearchEventsResult> SearchAsync(SearchEventsQuery query, CancellationToken cancellationToken = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > 100 ? 20 : query.PageSize;

        var response = await client.SearchAsync<SearchDocument>(s => s
                .Index(indexName)
                .From((page - 1) * pageSize)
                .Size(pageSize)
                .Query(q => q.Bool(b => b
                    .Must(BuildMustClauses(query))
                    .MustNot(f => f.Term(t => t.Field(ff => ff.Tombstoned).Value(true)))
                    .Filter(BuildFilterClauses(query)))),
            cancellationToken);

        EnsureValid(response, "search events");

        return new SearchEventsResult(response.Documents.ToList(), response.Total);
    }

    /// <summary>
    /// The check the old code never made. An ES outage, a 429 under load, or a 5xx all leave
    /// <c>IsValidResponse == false</c> — reading <c>Total</c>/<c>Documents</c> off that response
    /// anyway silently returns an empty result indistinguishable from a genuinely empty index.
    /// </summary>
    private static void EnsureValid(Elastic.Transport.Products.Elasticsearch.ElasticsearchResponse response, string operation)
    {
        if (!response.IsValidResponse)
            throw new SearchIndexUnavailableException($"Elasticsearch rejected the request to {operation}: {response.DebugInformation}");
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
