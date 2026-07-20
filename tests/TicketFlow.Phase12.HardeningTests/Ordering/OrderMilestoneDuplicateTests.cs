using System.Collections.Concurrent;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ordering.Api.Consumers;
using TicketFlow.Ordering.Api.Metrics;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;
using TicketFlow.Phase12.HardeningTests.Support;

namespace TicketFlow.Phase12.HardeningTests.Ordering;

public sealed class OrderMilestoneDuplicateTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.ExpectedFailure)]
    public async Task Concurrent_distinct_OrderCompleted_messages_must_append_one_terminal_transition()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var history = new object[]
        {
            new OrderPlaced(orderId, Guid.NewGuid(), Guid.NewGuid(), [seatId], 500_000m, "VND", DateTimeOffset.UtcNow),
            new OrderSeatsConfirmed(orderId, DateTimeOffset.UtcNow),
            new OrderPaymentAuthorized(orderId, Guid.NewGuid(), DateTimeOffset.UtcNow),
        };
        var store = new ConcurrentMilestoneOrderStore(history, loadParticipants: 2);

        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOrderStore>(store)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<TicketSalesMetrics>()
            .AddMassTransitTestHarness(configuration => configuration.AddConsumer<OrderCompletedConsumer>())
            .BuildServiceProvider(validateScopes: true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        var completed = new OrderCompleted(
            orderId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [seatId],
            [$"{orderId}/{seatId}.pdf"],
            DateTimeOffset.UtcNow);

        await Task.WhenAll(
            harness.Bus.Publish(completed, context => context.MessageId = Guid.NewGuid()),
            harness.Bus.Publish(completed, context => context.MessageId = Guid.NewGuid()));

        Assert.True(await AsyncTest.WaitUntilAsync(
            () => store.Appended.OfType<OrderConfirmed>().Count() >= 2,
            TimeSpan.FromSeconds(10)));

        Assert.Single(store.Appended.OfType<OrderConfirmed>());
        Assert.Single(store.Appended.OfType<OrderTicketsIssued>());
    }
}

internal sealed class ConcurrentMilestoneOrderStore(IEnumerable<object> history, int loadParticipants) : IOrderStore
{
    private readonly object[] _history = history.ToArray();
    private readonly AsyncRendezvous _loadRendezvous = new(loadParticipants);
    private readonly ConcurrentBag<object> _appended = [];

    public IReadOnlyCollection<object> Appended => _appended;

    public async Task<Order?> LoadAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await _loadRendezvous.SignalAndWaitAsync(cancellationToken);
        return Order.Load(_history);
    }

    public void StartStream(Guid orderId, object initialEvent) => throw new NotSupportedException();

    public void Append(Guid orderId, object @event) => _appended.Add(@event);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
