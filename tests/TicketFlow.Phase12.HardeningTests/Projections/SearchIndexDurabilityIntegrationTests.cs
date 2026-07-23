using TicketFlow.Phase12.HardeningTests.Infrastructure;
using TicketFlow.Search.Application.Events;
using TicketFlow.Search.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Projections;

/// <summary>
/// The in-memory tests in CatalogLifecycleOrderingTests prove the revision/tombstone decision;
/// this proves it holds against the real Elasticsearch scripted updates that decision is built on
/// — a Painless script with a typo, or an ES version that resolves ties differently, would pass
/// every in-memory test and still be wrong in production.
/// </summary>
[Collection(HardeningEnvironmentCollection.Name)]
public sealed class SearchIndexDurabilityIntegrationTests(HardeningEnvironmentFixture environment)
{
    private ElasticEventSearchIndex Index => environment.SearchIndex;

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_cancelled_event_must_never_be_resurrected_by_a_late_Published()
    {
        var eventId = Guid.NewGuid();

        await Index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Concert", "Artist", "Venue", "City", DateTimeOffset.UtcNow.AddDays(5), "Published", 1));
        await Index.ApplyCancelledAsync(eventId, revision: 2);

        // A stale Published, revision 1, arrives after the Cancel (revision 2) — the tombstone
        // must win regardless, because Catalog's domain forbids ever un-cancelling an event.
        await Index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Concert", "Artist", "Venue", "City", DateTimeOffset.UtcNow.AddDays(5), "Published", 1));

        Assert.Null(await Index.GetByIdAsync(eventId));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_lifecycle_applies_for_one_event_must_converge_to_the_same_document()
    {
        var eventId = Guid.NewGuid();
        var startsAt = DateTimeOffset.UtcNow.AddDays(10);

        // Ten concurrent appliers racing the same revision-guarded script on the same document —
        // the property under test is that Elasticsearch's atomic per-document script execution
        // means none of these interleave, not that any particular one wins.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Concert", "Artist", "Venue", "City", startsAt, "Published", 1))));

        var stored = await Index.GetByIdAsync(eventId);
        Assert.NotNull(stored);
        Assert.Equal(1, stored.Revision);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_Rescheduled_that_overtakes_Published_must_create_the_document_not_lose_the_fact()
    {
        var eventId = Guid.NewGuid();
        var newStart = DateTimeOffset.UtcNow.AddDays(30);

        // No Published has ever arrived for this id — the old UpdateStartsAtAsync had nothing to
        // patch here and silently dropped the reschedule forever.
        await Index.ApplyRescheduledAsync(eventId, newStart, revision: 1);

        var stored = await Index.GetByIdAsync(eventId);
        Assert.NotNull(stored);
        Assert.Equal(newStart, stored.StartsAt);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_document_indexed_before_Commit_11_must_still_accept_a_revisioned_write()
    {
        var eventId = Guid.NewGuid();

        // The shape a pre-Commit-11 producer left in a live index: no revision, no tombstoned —
        // those fields did not exist. Commit 2's expand-only rule (mục 10) says a missing revision
        // reads as 0, so this document must lose to any real write, not block it. Indexed through
        // the raw client because the port can no longer produce this shape.
        await environment.SearchClient.IndexAsync(
            new Dictionary<string, object>
            {
                ["eventId"] = eventId,
                ["title"] = "Legacy",
                ["artist"] = "Artist",
                ["venueName"] = "Venue",
                ["city"] = "City",
                ["startsAt"] = DateTimeOffset.UtcNow.AddDays(5),
                ["status"] = "Published",
            },
            i => i.Index(environment.SearchIndexName).Id(eventId.ToString()));

        await Index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Upgraded", "Artist", "Venue", "City", DateTimeOffset.UtcNow.AddDays(5), "Published", 3));

        var stored = await Index.GetByIdAsync(eventId);
        Assert.NotNull(stored);
        Assert.Equal("Upgraded", stored.Title);
        Assert.Equal(3, stored.Revision);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_document_indexed_before_Commit_11_must_still_accept_a_cancel()
    {
        var eventId = Guid.NewGuid();

        await environment.SearchClient.IndexAsync(
            new Dictionary<string, object>
            {
                ["eventId"] = eventId,
                ["title"] = "Legacy",
                ["artist"] = "Artist",
                ["venueName"] = "Venue",
                ["city"] = "City",
                ["startsAt"] = DateTimeOffset.UtcNow.AddDays(5),
                ["status"] = "Published",
            },
            i => i.Index(environment.SearchIndexName).Id(eventId.ToString()));

        // Cancel takes Math.max of stored and incoming revision — the same missing-field trap as
        // the guard above, on the one path that must never be blocked.
        await Index.ApplyCancelledAsync(eventId, revision: 4);

        Assert.Null(await Index.GetByIdAsync(eventId));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task SearchAsync_must_never_return_a_tombstoned_document()
    {
        var eventId = Guid.NewGuid();
        await Index.ApplyPublishedAsync(
            new SearchDocument(eventId, "UniqueSearchTitle", "Artist", "Venue", "City", DateTimeOffset.UtcNow.AddDays(5), "Published", 1));
        await Index.ApplyCancelledAsync(eventId, revision: 2);

        // Elasticsearch is near-real-time, not immediately consistent — give the write a moment
        // to become visible before asserting on a query.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var results = await Index.SearchAsync(new SearchEventsQuery("UniqueSearchTitle", null, null, null, null, 1, 20));

        Assert.DoesNotContain(results.Items, document => document.EventId == eventId);
    }
}
