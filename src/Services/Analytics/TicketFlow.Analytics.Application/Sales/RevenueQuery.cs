namespace TicketFlow.Analytics.Application.Sales;

/// <param name="OrganizerId">
/// Restricts the result to events this organizer owns (Phase 12 Commit 14). Null means unscoped
/// and is only ever passed for an admin caller — /revenue used to be unscoped for EVERY organizer,
/// so any of them could read the whole platform's takings, per event, for any date range.
/// </param>
public sealed record RevenueQuery(
    Guid? EventId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Currency = null,
    Guid? OrganizerId = null);

public sealed record EventRevenue(Guid EventId, string Currency, decimal Revenue, long SeatCount);

public sealed record CurrencyRevenue(string Currency, decimal Revenue, long SeatCount);

/// <summary>TotalRevenue is null when the query spans multiple currencies; adding currencies is invalid.</summary>
public sealed record RevenueResult(
    decimal? TotalRevenue,
    long TotalSeats,
    IReadOnlyList<EventRevenue> ByEvent,
    IReadOnlyList<CurrencyRevenue> ByCurrency);
