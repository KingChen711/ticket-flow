namespace TicketFlow.Catalog.Application.SeatMaps;

public sealed record AddSectionRequest(string Name, int Capacity);

public sealed record SectionResponse(Guid Id, string Name, int Capacity);

public sealed record SeatMapResponse(Guid Id, Guid EventId, IReadOnlyList<SectionResponse> Sections);
