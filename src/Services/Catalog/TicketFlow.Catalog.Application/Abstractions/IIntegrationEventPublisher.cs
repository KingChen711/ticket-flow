namespace TicketFlow.Catalog.Application.Abstractions;

/// <summary>
/// Port for publishing cross-service integration events (see TicketFlow.Contracts.Messaging) —
/// distinct from domain events (AggregateRoot.Raise), which stay in-process. Application knows
/// WHAT to publish (a contract type, just data); Infrastructure knows HOW (MassTransit + the EF
/// Core Outbox), keeping this layer free of any message-bus package reference.
/// </summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class;
}
