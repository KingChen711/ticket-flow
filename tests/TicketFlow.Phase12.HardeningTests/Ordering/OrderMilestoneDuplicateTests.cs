using System.Collections.Concurrent;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Api.Consumers;
using TicketFlow.Ordering.Api.Metrics;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Orders;
using TicketFlow.Ordering.Domain.Orders;
using TicketFlow.Phase12.HardeningTests.Support;

namespace TicketFlow.Phase12.HardeningTests.Ordering;

public sealed class OrderMilestoneDuplicateTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Concurrent_distinct_OrderCompleted_messages_must_append_one_terminal_transition()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var backend = new MilestoneStoreBackend(PaidHistory(orderId, seatId), loadParticipants: 2);
        await using var provider = BuildHarness(backend);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var completed = Completed(orderId, seatId);

        // Two distinct envelopes, so MessageId inbox dedup cannot help. The rendezvous makes both
        // consumers fold the same version before either commits.
        await Task.WhenAll(
            harness.Bus.Publish(completed, context => context.MessageId = Guid.NewGuid()),
            harness.Bus.Publish(completed, context => context.MessageId = Guid.NewGuid()));

        // Wait for BOTH deliveries to be fully handled, not for a second append to show up: the
        // original wait polled for `>= 2` because that is what the broken writer produced, which
        // means a correct system can only ever make it time out.
        Assert.True(await AsyncTest.WaitUntilAsync(
            () => harness.Consumed.Select<OrderCompleted>().Count() >= 2,
            TimeSpan.FromSeconds(10)));

        Assert.Single(backend.Committed.OfType<OrderConfirmed>());
        Assert.Single(backend.Committed.OfType<OrderTicketsIssued>());
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_redelivered_OrderCompleted_must_not_count_the_sale_twice()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var backend = new MilestoneStoreBackend(PaidHistory(orderId, seatId), loadParticipants: 1);
        await using var provider = BuildHarness(backend);
        using var scope = provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<OrderMilestoneWriter>();

        var first = await writer.RecordAsync(new OrderMilestoneRequest(
            orderId, OrderMilestone.Confirmed, TicketPdfUrls: [$"{orderId}/{seatId}.pdf"]));
        var replay = await writer.RecordAsync(new OrderMilestoneRequest(
            orderId, OrderMilestone.Confirmed, TicketPdfUrls: [$"{orderId}/{seatId}.pdf"]));

        // The counter used to be incremented after every delivery, outside the decision entirely,
        // so a redelivery inflated "vé bán/phút" for an order that sold exactly once.
        Assert.Equal(OrderMilestoneOutcome.Appended, first.Outcome);
        Assert.Equal(1, first.SeatsSold);
        Assert.Equal(OrderMilestoneOutcome.Ignored, replay.Outcome);
        Assert.Equal(0, replay.SeatsSold);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_late_milestone_must_not_walk_a_terminal_order_backwards()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var history = PaidHistory(orderId, seatId)
            .Append(new OrderTicketsIssued(orderId, [$"{orderId}/{seatId}.pdf"], DateTimeOffset.UtcNow))
            .Append(new OrderCancelled(orderId, "Ticketing failed.", DateTimeOffset.UtcNow));
        var backend = new MilestoneStoreBackend(history, loadParticipants: 1);
        await using var provider = BuildHarness(backend);
        using var scope = provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<OrderMilestoneWriter>();

        var late = await writer.RecordAsync(new OrderMilestoneRequest(
            orderId, OrderMilestone.Confirmed, TicketPdfUrls: [$"{orderId}/{seatId}.pdf"]));

        Assert.Equal(OrderMilestoneOutcome.Ignored, late.Outcome);
        Assert.Empty(backend.Committed);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_milestone_for_an_unknown_order_must_never_create_a_stream()
    {
        var backend = new MilestoneStoreBackend([], loadParticipants: 1) { StreamExists = false };
        await using var provider = BuildHarness(backend);
        using var scope = provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<OrderMilestoneWriter>();

        var result = await writer.RecordAsync(
            new OrderMilestoneRequest(Guid.NewGuid(), OrderMilestone.SeatsConfirmed));

        // Marten's Append happily starts a stream that does not exist, so a mis-routed message
        // would otherwise leave a phantom order behind.
        Assert.Equal(OrderMilestoneOutcome.UnknownOrder, result.Outcome);
        Assert.Empty(backend.Committed);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_version_conflict_must_reload_and_re_decide_rather_than_append_twice()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var backend = new MilestoneStoreBackend(PaidHistory(orderId, seatId), loadParticipants: 1)
        {
            // The winner appended the terminal transition while this caller was still deciding.
            ConflictOnceThenHistory = PaidHistory(orderId, seatId)
                .Append(new OrderTicketsIssued(orderId, [$"{orderId}/{seatId}.pdf"], DateTimeOffset.UtcNow))
                .Append(new OrderConfirmed(orderId, DateTimeOffset.UtcNow))
                .ToArray(),
        };
        await using var provider = BuildHarness(backend);
        using var scope = provider.CreateScope();
        var writer = scope.ServiceProvider.GetRequiredService<OrderMilestoneWriter>();

        var result = await writer.RecordAsync(new OrderMilestoneRequest(
            orderId, OrderMilestone.Confirmed, TicketPdfUrls: [$"{orderId}/{seatId}.pdf"]));

        // Re-deciding is the point: retrying the same append would have put a second
        // OrderConfirmed on a stream that already had one.
        Assert.Equal(OrderMilestoneOutcome.Ignored, result.Outcome);
        Assert.Empty(backend.Committed);
    }

    private static object[] PaidHistory(Guid orderId, Guid seatId) =>
    [
        new OrderPlaced(orderId, Guid.NewGuid(), Guid.NewGuid(), [seatId], 500_000m, "VND", DateTimeOffset.UtcNow),
        new OrderSeatsConfirmed(orderId, DateTimeOffset.UtcNow),
        new OrderPaymentAuthorized(orderId, Guid.NewGuid(), DateTimeOffset.UtcNow),
    ];

    private static OrderCompleted Completed(Guid orderId, Guid seatId) => new(
        orderId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        [seatId],
        [$"{orderId}/{seatId}.pdf"],
        DateTimeOffset.UtcNow);

    private static ServiceProvider BuildHarness(MilestoneStoreBackend backend) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(backend)
            // Scoped, exactly like the Marten session it stands in for: one unit of work per
            // consume, so two concurrent deliveries stage their writes independently.
            .AddScoped<IOrderStore, ScopedMilestoneOrderStore>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<TicketSalesMetrics>()
            .AddScoped<OrderMilestoneWriter>()
            .AddMassTransitTestHarness(configuration => configuration.AddConsumer<OrderMilestoneConsumer>())
            .BuildServiceProvider(validateScopes: true);
}

/// <summary>
/// Shared backing store. Split from the per-scope <see cref="ScopedMilestoneOrderStore"/> on
/// purpose: in production each consume gets its own scoped Marten session, and staging writes on a
/// single shared instance would not model that. (An earlier version kept the staged writes in an
/// AsyncLocal on one shared store — they silently vanished, because a value assigned inside an
/// async method does not flow back to its caller.)
/// </summary>
internal sealed class MilestoneStoreBackend(IEnumerable<object> history, int loadParticipants)
{
    private readonly List<object> _events = [.. history];
    private readonly List<object> _committed = [];
    private readonly Lock _gate = new();
    private long _version;
    private bool _conflictServed;

    public AsyncRendezvous LoadRendezvous { get; } = new(loadParticipants);

    public IReadOnlyCollection<object> Committed
    {
        get { lock (_gate) return _committed.ToArray(); }
    }

    public bool StreamExists { get; init; } = true;

    /// <summary>When set, the first commit loses the race and the stream reloads as this history.</summary>
    public object[]? ConflictOnceThenHistory { get; init; }

    public (Order Order, long Version) Read()
    {
        lock (_gate)
            return (Order.Load(_events), _version);
    }

    /// <summary>
    /// Commits only if the stream is still at the version the caller decided on. Appended events
    /// become part of the history, so the next read sees them — the property that makes a
    /// redelivery decide "already recorded" instead of deciding the same append again.
    /// </summary>
    public void Commit(List<object> staged, long readVersion)
    {
        lock (_gate)
        {
            if (ConflictOnceThenHistory is not null && !_conflictServed)
            {
                _conflictServed = true;
                _events.Clear();
                _events.AddRange(ConflictOnceThenHistory);
                _version++;
                throw new MilestoneVersionConflictException();
            }

            if (readVersion != _version)
                throw new MilestoneVersionConflictException();

            _version++;
            _events.AddRange(staged);
            _committed.AddRange(staged);
        }
    }
}

/// <summary>
/// Models the property the production store relies on: an append is checked against the version it
/// was decided at, so a writer that lost the race is told so rather than silently succeeding.
/// </summary>
internal sealed class ScopedMilestoneOrderStore(MilestoneStoreBackend backend) : IOrderStore
{
    private readonly List<object> _staged = [];
    private long _readVersion;

    public Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Order?>(backend.StreamExists ? backend.Read().Order : null);

    public void StartStream(Guid orderId, object initialEvent) => throw new NotSupportedException();

    public void Append(Guid orderId, object @event) => throw new NotSupportedException(
        "Commit 8: milestones go through FetchForWritingAsync so the append is version-checked.");

    public async Task<IOrderStreamWriter?> FetchForWritingAsync(
        Guid orderId,
        CancellationToken cancellationToken = default)
    {
        await backend.LoadRendezvous.SignalAndWaitAsync(cancellationToken);
        if (!backend.StreamExists)
            return null;

        _staged.Clear();
        var (order, version) = backend.Read();
        _readVersion = version;
        return new Writer(order, version, _staged);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_staged.Count == 0)
            return Task.CompletedTask;

        var staged = _staged.ToList();
        _staged.Clear();
        backend.Commit(staged, _readVersion);
        return Task.CompletedTask;
    }

    public bool IsConcurrencyConflict(Exception exception) => exception is MilestoneVersionConflictException;

    private sealed class Writer(Order order, long version, List<object> staged) : IOrderStreamWriter
    {
        public Order Order { get; } = order;
        public long Version { get; } = version;
        public void Append(object @event) => staged.Add(@event);
    }
}

internal sealed class MilestoneVersionConflictException()
    : Exception("The order stream moved while this milestone was being decided.");
