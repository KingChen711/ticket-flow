namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Same port shape as TicketFlow.Catalog.Application.Abstractions.IIntegrationEventPublisher —
/// Application knows WHAT to publish (a contract type), Infrastructure knows HOW (MassTransit).
/// Ordering's own PlaceOrderCommandHandler needs this to publish OrderSubmitted without pulling
/// MassTransit's IPublishEndpoint directly into Application (which would also make unit-testing
/// the handler require faking MassTransit's full interface instead of this one method).
/// </summary>
public interface IIntegrationEventPublisher
{
    Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class;
}
