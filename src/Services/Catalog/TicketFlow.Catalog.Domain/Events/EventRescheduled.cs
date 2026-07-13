using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed record EventRescheduled(Guid CatalogEventId, DateTimeOffset StartsAt, DateTimeOffset EndsAt) : DomainEvent;
