using System.Collections.Concurrent;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Catalog;
using TicketFlow.Phase12.HardeningTests.Support;
using TicketFlow.Search.Api.Consumers;
using TicketFlow.Search.Application.Abstractions;
using TicketFlow.Search.Application.Events;

namespace TicketFlow.Phase12.HardeningTests.Projections;

public sealed class CatalogLifecycleOrderingTests
{
    public static IEnumerable<object[]> LifecyclePermutations()
    {
        var events = new[] { "Published", "Rescheduled", "Cancelled" };
        foreach (var first in events)
        foreach (var second in events.Where(item => item != first))
        foreach (var third in events.Where(item => item != first && item != second))
            yield return [new[] { first, second, third }];
    }

    [Theory]
    [MemberData(nameof(LifecyclePermutations))]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Published_rescheduled_cancelled_permutations_must_never_resurrect_a_cancelled_event(
        string[] deliveryOrder)
    {
        var index = new InMemoryEventSearchIndex();
        await using var provider = new ServiceCollection()
            .AddSingleton<IEventSearchIndex>(index)
            .AddMassTransitTestHarness(configuration =>
            {
                configuration.AddConsumer<CatalogEventPublishedConsumer>();
                configuration.AddConsumer<CatalogEventRescheduledConsumer>();
                configuration.AddConsumer<CatalogEventCancelledConsumer>();
            })
            .BuildServiceProvider(validateScopes: true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var expectedOperations = 0;

        foreach (var lifecycleEvent in deliveryOrder)
        {
            await PublishAsync(harness.Bus, lifecycleEvent, eventId, now);
            expectedOperations++;
            Assert.True(await AsyncTest.WaitUntilAsync(
                () => index.OperationCount >= expectedOperations,
                TimeSpan.FromSeconds(5)));
        }

        // Canonical Catalog state is Cancelled regardless of delivery order — Commit 11's tombstone
        // (Cancel is a one-way domain transition, so it always wins) is what makes this converge.
        Assert.Null(await index.GetByIdAsync(eventId));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_out_of_order_older_Published_must_not_overwrite_a_newer_Rescheduled()
    {
        var index = new InMemoryEventSearchIndex();
        var eventId = Guid.NewGuid();
        var originalStart = DateTimeOffset.UtcNow.AddDays(10);
        var reschedule = DateTimeOffset.UtcNow.AddDays(20);

        // Published at revision 1, then Rescheduled at revision 2 (the real Catalog ordering —
        // AggregateVersion strictly increases per mutation), but the network delivers them the
        // other way around.
        await index.ApplyRescheduledAsync(eventId, reschedule, revision: 2);
        await index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Concert", "Artist", "Venue", "City", originalStart, "Published", Revision: 1));

        var stored = await index.GetByIdAsync(eventId);
        Assert.NotNull(stored);
        // The stale Published must not roll the schedule back to the original time.
        Assert.Equal(reschedule, stored.StartsAt);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_old_v1_message_at_revision_zero_must_never_overwrite_a_v2_document()
    {
        var index = new InMemoryEventSearchIndex();
        var eventId = Guid.NewGuid();

        await index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Real Title", "Real Artist", "Real Venue", "Real City",
                DateTimeOffset.UtcNow.AddDays(5), "Published", Revision: 3));

        // A v1 producer that never learned about AggregateVersion always sends the contract's
        // default (0) — this is the exact scenario mục 10's expand-only compatibility window
        // describes, and it must lose against any real v2 write.
        await index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Stale v1 Title", "", "", "", DateTimeOffset.UtcNow, "Published", Revision: 0));

        var stored = await index.GetByIdAsync(eventId);
        Assert.Equal("Real Title", stored?.Title);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_cancelled_event_must_never_appear_in_search_results()
    {
        var index = new InMemoryEventSearchIndex();
        var eventId = Guid.NewGuid();

        await index.ApplyPublishedAsync(
            new SearchDocument(eventId, "Concert", "Artist", "Venue", "City", DateTimeOffset.UtcNow.AddDays(5), "Published", 1));
        await index.ApplyCancelledAsync(eventId, revision: 2);

        var results = await index.SearchAsync(new SearchEventsQuery(null, null, null, null, null, 1, 20));
        Assert.DoesNotContain(results.Items, document => document.EventId == eventId);
    }

    private static Task PublishAsync(
        IPublishEndpoint publishEndpoint,
        string lifecycleEvent,
        Guid eventId,
        DateTimeOffset now) =>
        lifecycleEvent switch
        {
            "Published" => publishEndpoint.Publish(new CatalogEventPublished(
                eventId, "Concert", Guid.NewGuid(), now.AddDays(10), now,
                "Artist", "Venue", "Ho Chi Minh City")),
            "Rescheduled" => publishEndpoint.Publish(new CatalogEventRescheduled(
                eventId, now.AddDays(11), now.AddDays(11).AddHours(2))),
            "Cancelled" => publishEndpoint.Publish(new CatalogEventCancelled(eventId)),
            _ => throw new ArgumentOutOfRangeException(nameof(lifecycleEvent), lifecycleEvent, "Unknown lifecycle event."),
        };
}

/// <summary>
/// Models the same compare-and-swap semantics as ElasticEventSearchIndex/MongoEventReminderReadModel:
/// tombstoned is sticky and wins unconditionally; Published/Rescheduled apply only when the
/// incoming revision is at least the stored one, and create the document if it is missing.
/// </summary>
internal sealed class InMemoryEventSearchIndex : IEventSearchIndex
{
    private readonly ConcurrentDictionary<Guid, SearchDocument> _documents = [];
    private readonly Lock _gate = new();
    private int _operationCount;

    public int OperationCount => Volatile.Read(ref _operationCount);

    public Task ApplyPublishedAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_documents.TryGetValue(document.EventId, out var current) ||
                (!current.Tombstoned && document.Revision >= current.Revision))
                _documents[document.EventId] = document;

            Interlocked.Increment(ref _operationCount);
        }

        return Task.CompletedTask;
    }

    public Task ApplyRescheduledAsync(
        Guid eventId,
        DateTimeOffset startsAt,
        long revision,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_documents.TryGetValue(eventId, out var current))
            {
                if (!current.Tombstoned && revision >= current.Revision)
                    _documents[eventId] = current with { StartsAt = startsAt, Revision = revision };
            }
            else
            {
                _documents[eventId] = new SearchDocument(eventId, "", "", "", "", startsAt, "Published", revision);
            }

            Interlocked.Increment(ref _operationCount);
        }

        return Task.CompletedTask;
    }

    public Task ApplyCancelledAsync(Guid eventId, long revision, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var current = _documents.GetValueOrDefault(eventId);
            var mergedRevision = Math.Max(current?.Revision ?? 0, revision);
            _documents[eventId] = (current ?? new SearchDocument(eventId, "", "", "", "", default, "Cancelled"))
                with
            {
                Status = "Cancelled", Revision = mergedRevision, Tombstoned = true,
            };

            Interlocked.Increment(ref _operationCount);
        }

        return Task.CompletedTask;
    }

    public Task<SearchDocument?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var document = _documents.GetValueOrDefault(eventId);
        return Task.FromResult(document is { Tombstoned: true } ? null : document);
    }

    public Task<SearchEventsResult> SearchAsync(
        SearchEventsQuery query,
        CancellationToken cancellationToken = default)
    {
        var items = _documents.Values.Where(document => !document.Tombstoned).ToList();
        return Task.FromResult(new SearchEventsResult(items, items.Count));
    }
}
