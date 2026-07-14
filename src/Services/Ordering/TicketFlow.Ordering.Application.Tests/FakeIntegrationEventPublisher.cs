using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Tests;

/// <summary>In-memory stand-in for MassTransitIntegrationEventPublisher — same role as FakeOrderStore.</summary>
public sealed class FakeIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly List<object> _published = [];

    public IReadOnlyList<object> Published => _published;

    public Task PublishAsync<T>(T integrationEvent, CancellationToken cancellationToken = default) where T : class
    {
        _published.Add(integrationEvent);
        return Task.CompletedTask;
    }
}
