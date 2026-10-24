using TicketFlow.Catalog.Domain.Events;

namespace TicketFlow.Catalog.Application.Abstractions;

public interface IEventRepository
{
    Task<Event?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Event>> ListAsync(CancellationToken cancellationToken = default);
    void Add(Event @event);
    void Remove(Event @event) => throw new NotSupportedException("This event repository does not support removal.");
}
