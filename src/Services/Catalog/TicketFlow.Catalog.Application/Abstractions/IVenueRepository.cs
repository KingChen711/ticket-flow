using TicketFlow.Catalog.Domain.Venues;

namespace TicketFlow.Catalog.Application.Abstractions;

public interface IVenueRepository
{
    Task<Venue?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Venue>> ListAsync(CancellationToken cancellationToken = default);
    void Add(Venue venue);
}
