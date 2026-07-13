using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Venues;

public sealed record VenueRegistered(Guid VenueId, string Name) : DomainEvent;
