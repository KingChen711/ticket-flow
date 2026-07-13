using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed record EventPublished(Guid CatalogEventId) : DomainEvent;
