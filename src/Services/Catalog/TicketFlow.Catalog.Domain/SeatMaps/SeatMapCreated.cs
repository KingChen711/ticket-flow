using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.SeatMaps;

public sealed record SeatMapCreated(Guid SeatMapId, Guid CatalogEventId) : DomainEvent;
