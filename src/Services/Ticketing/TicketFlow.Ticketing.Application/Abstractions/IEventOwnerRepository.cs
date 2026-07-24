using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Abstractions;

/// <summary>
/// The event → organizer projection Ticketing keeps so a gate scan can be authorized locally
/// (Phase 12 Commit 14). See <see cref="EventOwner"/> for why this is a projection and not a
/// lookup into Catalog.
/// </summary>
public interface IEventOwnerRepository
{
    Task<EventOwner?> GetAsync(Guid eventId, CancellationToken cancellationToken = default);

    void Add(EventOwner owner);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
