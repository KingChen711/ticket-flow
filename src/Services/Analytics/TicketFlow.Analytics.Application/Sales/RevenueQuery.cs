namespace TicketFlow.Analytics.Application.Sales;

public sealed record RevenueQuery(Guid? EventId, DateTimeOffset? From, DateTimeOffset? To, string? Currency = null);

public sealed record EventRevenue(Guid EventId, string Currency, decimal Revenue, long SeatCount);

public sealed record CurrencyRevenue(string Currency, decimal Revenue, long SeatCount);

/// <summary>TotalRevenue is null when the query spans multiple currencies; adding currencies is invalid.</summary>
public sealed record RevenueResult(
    decimal? TotalRevenue,
    long TotalSeats,
    IReadOnlyList<EventRevenue> ByEvent,
    IReadOnlyList<CurrencyRevenue> ByCurrency);
