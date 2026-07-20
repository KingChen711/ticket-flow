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
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
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

        // Canonical Catalog state is Cancelled; delivery order must not allow an older Published
        // message to upsert it again. Current contracts have no revision/tombstone to decide this.
        Assert.Null(await index.GetByIdAsync(eventId));
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

internal sealed class InMemoryEventSearchIndex : IEventSearchIndex
{
    private readonly ConcurrentDictionary<Guid, SearchDocument> _documents = [];
    private int _operationCount;

    public int OperationCount => Volatile.Read(ref _operationCount);

    public Task UpsertAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        _documents[document.EventId] = document;
        Interlocked.Increment(ref _operationCount);
        return Task.CompletedTask;
    }

    public Task UpdateStartsAtAsync(Guid eventId, DateTimeOffset startsAt, CancellationToken cancellationToken = default)
    {
        if (_documents.TryGetValue(eventId, out var current))
            _documents[eventId] = current with { StartsAt = startsAt };

        Interlocked.Increment(ref _operationCount);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        _documents.TryRemove(eventId, out _);
        Interlocked.Increment(ref _operationCount);
        return Task.CompletedTask;
    }

    public Task<SearchDocument?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_documents.GetValueOrDefault(eventId));

    public Task<SearchEventsResult> SearchAsync(
        SearchEventsQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SearchEventsResult(_documents.Values.ToList(), _documents.Count));
}
