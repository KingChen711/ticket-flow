using Marten;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Ordering.Api.Consumers;
using TicketFlow.Ordering.Api.Services;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Application.Sagas;
using TicketFlow.Ordering.Application.Workflows;
using TicketFlow.Ordering.Infrastructure;
using TicketFlow.Ordering.Infrastructure.Messaging;
using TicketFlow.Phase12.HardeningTests.Infrastructure;

namespace TicketFlow.Phase12.HardeningTests.Workflows;

[Collection(HardeningEnvironmentCollection.Name)]
public sealed class EventCancellationCampaignTests(HardeningEnvironmentFixture environment)
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Campaign_waits_for_core_ticket_and_notification_acknowledgements()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = BuildProvider(time);
        var store = provider.GetRequiredService<IDocumentStore>();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var workflowId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Store(new PurchaseState
            {
                CorrelationId = orderId,
                CatalogEventId = eventId,
                UserId = userId,
                CurrentState = "Completed",
                CustomerName = "Buyer",
                CustomerEmail = "buyer@example.com",
                Currency = "VND",
                PaymentMethod = "CreditCard",
                IdempotencyKey = $"checkout:{orderId:N}",
            });
            await session.SaveChangesAsync();
        }

        await harness.Bus.Publish(new EventCancellationRequested(
            workflowId,
            eventId,
            "Event cancelled.",
            time.GetUtcNow()));
        Assert.True(await harness.Published.Any<CancelOrderForEvent>(x =>
            x.Context.Message.OrderId == orderId));

        await harness.Bus.Publish(new OrderCancelledIntegrationEvent(
            orderId,
            userId,
            "Event cancelled.",
            time.GetUtcNow(),
            eventId,
            workflowId,
            "Buyer",
            "buyer@example.com"));
        await WaitForCampaignAsync(store, workflowId, campaign =>
            campaign.Orders.Single().CoreCancelledAt is not null);
        Assert.Null((await LoadCampaignAsync(store, workflowId)).CompletedAt);

        await harness.Bus.Publish(new TicketsRevokedForEventCancellation(
            workflowId,
            eventId,
            orderId,
            1,
            time.GetUtcNow()));
        await WaitForCampaignAsync(store, workflowId, campaign =>
            campaign.Orders.Single().TicketsRevokedAt is not null);
        Assert.Null((await LoadCampaignAsync(store, workflowId)).CompletedAt);

        time.Advance(EventCancellationCampaignConsumer.LateOrderQuietPeriod + TimeSpan.FromSeconds(1));
        await harness.Bus.Publish(new CustomerNotifiedOfEventCancellation(
            workflowId,
            eventId,
            orderId,
            "provider-42",
            time.GetUtcNow()));

        await WaitForCampaignAsync(store, workflowId, campaign => campaign.CompletedAt is not null);
        Assert.True(await harness.Published.Any<EventCancellationCompleted>(x =>
            x.Context.Message.WorkflowId == workflowId && x.Context.Message.OrderCount == 1));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Retry_rescans_checkout_dispatch_that_became_durable_after_initial_snapshot()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = BuildProvider(time);
        var store = provider.GetRequiredService<IDocumentStore>();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var workflowId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var holdId = Guid.NewGuid();

        await harness.Bus.Publish(new EventCancellationRequested(
            workflowId,
            eventId,
            "Event cancelled.",
            time.GetUtcNow()));
        await WaitForCampaignAsync(store, workflowId, _ => true);

        await using (var session = store.LightweightSession())
        {
            session.Store(OrderDispatch.Pending(new OrderSubmitted(
                orderId,
                eventId,
                userId,
                holdId,
                [Guid.NewGuid()],
                500_000m,
                "CreditCard",
                "Buyer",
                "buyer@example.com",
                time.GetUtcNow(),
                "VND",
                1,
                $"checkout:{orderId:N}"), time.GetUtcNow()));
            await session.SaveChangesAsync();
        }

        await harness.Bus.Publish(new RetryEventCancellation(
            workflowId,
            eventId,
            time.GetUtcNow()));

        Assert.True(await harness.Published.Any<CancelOrderForEvent>(x =>
            x.Context.Message.WorkflowId == workflowId && x.Context.Message.OrderId == orderId));
        var campaign = await LoadCampaignAsync(store, workflowId);
        Assert.Contains(campaign.Orders, operation =>
            operation.OrderId == orderId && operation.StateAtDiscovery == "DispatchPending");
        Assert.Null(campaign.CompletedAt);
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_dispatch_discovered_after_quiet_completion_must_reopen_and_drive_the_campaign()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        await using var provider = BuildProvider(time);
        var store = provider.GetRequiredService<IDocumentStore>();
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        var workflowId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await harness.Bus.Publish(new EventCancellationRequested(
            workflowId,
            eventId,
            "Event cancelled.",
            time.GetUtcNow()));
        await WaitForCampaignAsync(store, workflowId, _ => true);

        time.Advance(EventCancellationCampaignConsumer.LateOrderQuietPeriod + TimeSpan.FromSeconds(1));
        await harness.Bus.Publish(new RetryEventCancellation(workflowId, eventId, time.GetUtcNow()));
        await WaitForCampaignAsync(store, workflowId, campaign => campaign.CompletedAt is not null);

        await using (var session = store.LightweightSession())
        {
            session.Store(OrderDispatch.Pending(new OrderSubmitted(
                orderId,
                eventId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                [Guid.NewGuid()],
                500_000m,
                "CreditCard",
                "Buyer",
                "buyer@example.com",
                time.GetUtcNow(),
                "VND",
                1,
                $"checkout:{orderId:N}"), time.GetUtcNow()));
            await session.SaveChangesAsync();
        }

        var relay = new OrderDispatchRelay(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OrderDispatchOptions { BatchSize = 10 },
            time,
            NullLogger<OrderDispatchRelay>.Instance);
        Assert.Equal(1, await relay.RelayOnceAsync(default));

        Assert.True(await harness.Published.Any<CancelOrderForEvent>(x =>
            x.Context.Message.WorkflowId == workflowId &&
            x.Context.Message.OrderId == orderId));
        var reopened = await LoadCampaignAsync(store, workflowId);
        Assert.Null(reopened.CompletedAt);
        Assert.Contains(reopened.Orders, operation => operation.OrderId == orderId);
    }

    private ServiceProvider BuildProvider(TimeProvider timeProvider)
    {
        var schema = $"phase12_campaign_{Guid.NewGuid():N}";
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton(timeProvider)
            .AddMarten(options =>
            {
                options.Connection(environment.PostgresConnectionString);
                options.DatabaseSchemaName = schema;
                TicketFlow.Ordering.Infrastructure.ServiceCollectionExtensions.Configure(options);
            })
            .Services
            .AddScoped<ICheckoutOperationStore, MartenCheckoutOperationStore>()
            .AddScoped<IIntegrationEventPublisher, MassTransitIntegrationEventPublisher>()
            .AddMassTransitTestHarness(configuration =>
            {
                configuration.AddDelayedMessageScheduler();
                configuration.AddConsumer<EventCancellationCampaignConsumer>();
            })
            .BuildServiceProvider(validateScopes: true);
    }

    private static async Task<EventCancellationCampaign> LoadCampaignAsync(
        IDocumentStore store,
        Guid workflowId)
    {
        await using var query = store.QuerySession();
        return await query.LoadAsync<EventCancellationCampaign>(workflowId)
            ?? throw new InvalidOperationException($"Campaign '{workflowId}' was not found.");
    }

    private static async Task WaitForCampaignAsync(
        IDocumentStore store,
        Guid workflowId,
        Func<EventCancellationCampaign, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var query = store.QuerySession();
            var campaign = await query.LoadAsync<EventCancellationCampaign>(workflowId);
            if (campaign is not null && predicate(campaign))
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Campaign '{workflowId}' did not reach the expected state.");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
