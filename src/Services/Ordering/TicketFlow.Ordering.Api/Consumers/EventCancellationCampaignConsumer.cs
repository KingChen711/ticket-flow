using Marten;
using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Workflows;
using TicketFlow.Ordering.Application.Checkout;
using TicketFlow.Ordering.Application.Sagas;
using TicketFlow.Ordering.Application.Workflows;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>
/// Durable fan-out/fan-in coordinator. It rescans both saga state and the checkout dispatch
/// outbox, republishes only unfinished operations, and does not call the workflow complete until
/// order compensation, ticket fencing/revocation and provider-confirmed notification all ack.
/// </summary>
public sealed class EventCancellationCampaignConsumer(
    IDocumentSession session,
    IPublishEndpoint publisher,
    TimeProvider timeProvider) :
    IConsumer<EventCancellationRequested>,
    IConsumer<RetryEventCancellation>,
    IConsumer<OrderCancelledIntegrationEvent>,
    IConsumer<TicketsRevokedForEventCancellation>,
    IConsumer<CustomerNotifiedOfEventCancellation>
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LateOrderQuietPeriod = TimeSpan.FromSeconds(15);

    public async Task Consume(ConsumeContext<EventCancellationRequested> context)
    {
        var message = context.Message;
        var campaign = await session.LoadAsync<EventCancellationCampaign>(message.WorkflowId, context.CancellationToken);
        if (campaign is null)
        {
            var now = timeProvider.GetUtcNow();
            campaign = new EventCancellationCampaign
            {
                Id = message.WorkflowId,
                EventId = message.EventId,
                Reason = message.Reason,
                RequestedAt = message.RequestedAt,
                LastOrderDiscoveredAt = now,
            };
        }
        else if (campaign.EventId != message.EventId)
        {
            throw new InvalidOperationException(
                $"Cancellation workflow '{message.WorkflowId}' is already bound to event '{campaign.EventId}'.");
        }

        await ReconcileOrdersAsync(campaign, context.CancellationToken);
        await PersistThenDriveAsync(campaign, context);
    }

    public async Task Consume(ConsumeContext<RetryEventCancellation> context)
    {
        var message = context.Message;
        var campaign = await GetRequiredAsync(message.WorkflowId, message.EventId, context.CancellationToken);
        await ReconcileOrdersAsync(campaign, context.CancellationToken);
        await PersistThenDriveAsync(campaign, context);
    }

    public async Task Consume(ConsumeContext<OrderCancelledIntegrationEvent> context)
    {
        var message = context.Message;
        if (message.CancellationWorkflowId is not { } workflowId)
            return;

        var campaign = await GetRequiredAsync(workflowId, message.CatalogEventId, context.CancellationToken);
        var operation = GetOperation(campaign, message.OrderId);
        operation.CoreCancelledAt ??= message.CancelledAt;
        operation.TryComplete(timeProvider.GetUtcNow());

        await ReconcileOrdersAsync(campaign, context.CancellationToken);
        await PersistThenDriveAsync(campaign, context);
    }

    public async Task Consume(ConsumeContext<TicketsRevokedForEventCancellation> context)
    {
        var message = context.Message;
        var campaign = await GetRequiredAsync(message.WorkflowId, message.EventId, context.CancellationToken);
        var operation = GetOperation(campaign, message.OrderId);
        operation.TicketsRevokedAt ??= message.RevokedAt;
        operation.TryComplete(timeProvider.GetUtcNow());

        await ReconcileOrdersAsync(campaign, context.CancellationToken);
        await PersistThenDriveAsync(campaign, context);
    }

    public async Task Consume(ConsumeContext<CustomerNotifiedOfEventCancellation> context)
    {
        var message = context.Message;
        var campaign = await GetRequiredAsync(message.WorkflowId, message.EventId, context.CancellationToken);
        var operation = GetOperation(campaign, message.OrderId);
        operation.CustomerNotifiedAt ??= message.DeliveredAt;
        operation.TryComplete(timeProvider.GetUtcNow());

        await ReconcileOrdersAsync(campaign, context.CancellationToken);
        await PersistThenDriveAsync(campaign, context);
    }

    private async Task ReconcileOrdersAsync(
        EventCancellationCampaign campaign,
        CancellationToken cancellationToken)
    {
        var states = await session.Query<PurchaseState>()
            .Where(state => state.CatalogEventId == campaign.EventId)
            .ToListAsync(cancellationToken);
        var dispatches = await session.Query<OrderDispatch>()
            .Where(dispatch => dispatch.Payload.EventId == campaign.EventId)
            .ToListAsync(cancellationToken);

        var existing = campaign.Orders.Select(operation => operation.OrderId).ToHashSet();
        var statesByOrder = states.ToDictionary(state => state.CorrelationId);
        var discovered = states.Select(state => state.CorrelationId)
            .Concat(dispatches.Select(dispatch => dispatch.Id))
            .Distinct();

        foreach (var orderId in discovered)
        {
            if (!existing.Add(orderId))
                continue;

            campaign.Orders.Add(new EventCancellationOrderOperation
            {
                OrderId = orderId,
                StateAtDiscovery = statesByOrder.TryGetValue(orderId, out var state)
                    ? state.CurrentState
                    : "DispatchPending",
            });
            campaign.LastOrderDiscoveredAt = timeProvider.GetUtcNow();
        }
    }

    private async Task PersistThenDriveAsync<T>(
        EventCancellationCampaign campaign,
        ConsumeContext<T> context)
        where T : class
    {
        var now = timeProvider.GetUtcNow();
        foreach (var operation in campaign.Orders.Where(order => order.CompletedAt is null))
            operation.DispatchedAt ??= now;

        if (campaign.IsComplete(now, LateOrderQuietPeriod))
            campaign.CompletedAt ??= now;

        // Persist the campaign intent before any side effect. A fast order acknowledgement can
        // therefore always find its operation, while a crash after this commit merely causes the
        // scheduled/request replay to publish the same idempotent commands again.
        session.Store(campaign);
        await session.SaveChangesAsync(context.CancellationToken);

        if (campaign.CompletedAt is not null)
        {
            await PublishCompletedAsync(campaign, context.CancellationToken);
            return;
        }

        foreach (var operation in campaign.Orders.Where(order => order.CompletedAt is null))
        {
            await publisher.Publish(new CancelOrderForEvent(
                campaign.Id,
                campaign.EventId,
                operation.OrderId,
                campaign.Reason,
                campaign.RequestedAt), context.CancellationToken);
        }

        await context.SchedulePublish(
            RetryInterval,
            new RetryEventCancellation(campaign.Id, campaign.EventId, now.Add(RetryInterval)));
    }

    private async Task<EventCancellationCampaign> GetRequiredAsync(
        Guid workflowId,
        Guid? eventId,
        CancellationToken cancellationToken)
    {
        var campaign = await session.LoadAsync<EventCancellationCampaign>(workflowId, cancellationToken)
            ?? throw new InvalidOperationException($"Cancellation workflow '{workflowId}' was not found.");
        if (eventId is { } expected && expected != Guid.Empty && campaign.EventId != expected)
        {
            throw new InvalidOperationException(
                $"Cancellation workflow '{workflowId}' belongs to event '{campaign.EventId}', not '{expected}'.");
        }

        return campaign;
    }

    private static EventCancellationOrderOperation GetOperation(
        EventCancellationCampaign campaign,
        Guid orderId) =>
        campaign.Orders.SingleOrDefault(order => order.OrderId == orderId)
        ?? throw new InvalidOperationException(
            $"Order '{orderId}' is not part of cancellation workflow '{campaign.Id}'.");

    private Task PublishCompletedAsync(EventCancellationCampaign campaign, CancellationToken cancellationToken) =>
        publisher.Publish(new EventCancellationCompleted(
            campaign.Id,
            campaign.EventId,
            campaign.Orders.Count,
            campaign.CompletedAt ?? timeProvider.GetUtcNow()), cancellationToken);
}
