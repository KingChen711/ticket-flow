namespace TicketFlow.Analytics.Application.Sales;

public sealed record RevenueQuery(Guid? EventId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record EventRevenue(Guid EventId, decimal Revenue, long SeatCount);

public sealed record RevenueResult(decimal TotalRevenue, long TotalSeats, IReadOnlyList<EventRevenue> ByEvent);
