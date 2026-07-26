using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;

namespace TicketFlow.Search.Infrastructure;

/// <summary>
/// Dev-only setup-on-startup, same spirit as EF's migrate-on-startup / Marten's
/// AutoCreateSchemaObjects elsewhere in this repo — a real deployment would provision the index
/// as a separate step. Explicit mapping (not ES's dynamic auto-mapping) so Artist/City are
/// `keyword` (exact-match term filters) rather than `text` (which ES would guess by default and
/// which tokenizes, breaking exact filtering — confirmed the difference matters via a real probe
/// before writing this).
/// </summary>
public static class ElasticsearchIndexSetup
{
    public static async Task EnsureCreatedAsync(ElasticsearchClient client, string indexName,
        CancellationToken cancellationToken = default)
    {
        var exists = await client.Indices.ExistsAsync(indexName, cancellationToken);
        if (!exists.IsValidResponse && exists.ApiCallDetails.HttpStatusCode != 404)
        {
            throw new SearchIndexUnavailableException(
                $"Elasticsearch could not check index '{indexName}': {exists.DebugInformation}");
        }

        if (exists.Exists)
            return;

        var created = await client.Indices.CreateAsync(indexName, c => c
                .Mappings(m => m
                    .Properties(new Properties
                    {
                        { "eventId", new KeywordProperty() },
                        { "title", new TextProperty() },
                        { "artist", new KeywordProperty() },
                        { "venueName", new KeywordProperty() },
                        { "city", new KeywordProperty() },
                        { "startsAt", new DateProperty() },
                        { "status", new KeywordProperty() },
                        { "revision", new LongNumberProperty() },
                        { "tombstoned", new BooleanProperty() },
                    })),
            cancellationToken);

        if (created.IsValidResponse)
            return;

        // Multiple startup/migration replicas may race to create the same index. Re-read once and
        // accept the winner; every other invalid response is a real setup failure.
        var afterCreate = await client.Indices.ExistsAsync(indexName, cancellationToken);
        if (!afterCreate.IsValidResponse || !afterCreate.Exists)
        {
            throw new SearchIndexUnavailableException(
                $"Elasticsearch could not create index '{indexName}': {created.DebugInformation}");
        }
    }
}
