namespace TicketFlow.Catalog.Application.Venues;

public sealed record RegisterVenueRequest(string Name, string Street, string City, string Country, string? PostalCode);

public sealed record VenueResponse(Guid Id, string Name, string Street, string City, string Country, string? PostalCode);
