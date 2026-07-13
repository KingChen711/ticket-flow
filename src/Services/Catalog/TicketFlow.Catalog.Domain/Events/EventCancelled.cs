using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed record EventCancelled(Guid CatalogEventId) : DomainEvent;
