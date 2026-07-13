namespace TicketFlow.Ordering.Api.Endpoints;

public sealed record CheckSeatsRequest(string EventId, List<string> SeatIds);

public sealed record CheckSeatsResponse(bool AllAvailable, IReadOnlyList<string> UnavailableSeatIds);
