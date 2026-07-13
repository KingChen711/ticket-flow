using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed record EventCreated(Guid CatalogEventId, string Title, Guid VenueId) : DomainEvent;
