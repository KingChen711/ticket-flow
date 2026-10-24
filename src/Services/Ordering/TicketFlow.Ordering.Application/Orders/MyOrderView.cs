namespace TicketFlow.Ordering.Application.Orders;

/// <summary>
/// Read model — a persisted document kept fast for queries (Commit 6), NOT the write-side
/// aggregate (Order). Deliberately a plain data record with no Apply methods: unlike Order,
/// this type doesn't fold events itself — MyOrdersProjection (Infrastructure) does that, since
/// projecting-into-a-document is a Marten-specific technical concern, not something Application
/// needs to know how to do. Kept here (not in Infrastructure) because Application is what
/// defines WHAT shape the read side needs; Infrastructure defines HOW it gets populated.
///
/// Property named `Id`, not `OrderId` — bug caught during verification: Marten's document
/// identity convention resolves either a property literally named `Id`, or `{ClassName}Id`
/// ("MyOrderViewId" here, which nothing produces). `OrderId` matched neither, and
/// SingleStreamProjection's startup validation failed with an unhelpful NullReferenceException
/// instead of a clear "no identity property found" message. Using `[Identity]` to keep the more
/// descriptive name was rejected — it would pull the full Marten runtime assembly into
/// Application just for one attribute, breaking the analyzer-only boundary kept for Order.
/// </summary>
public sealed record MyOrderView(
    Guid Id,
    Guid CustomerId,
    Guid CatalogEventId,
    IReadOnlyList<Guid> SeatIds,
    decimal TotalAmount,
    string Currency,
    string Status,
    DateTimeOffset PlacedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? CancelledAt,
    // Commit 8 (delta review): the ticket PDF object keys were folded into the write stream by
    // OrderTicketsIssued but dropped here, so "my orders" had to call Ticketing just to render a
    // download link for data it already had. Trailing with a default so existing persisted
    // documents keep deserializing until the projection is rebuilt.
    IReadOnlyList<string>? TicketPdfUrls = null,
    // Monotonic stream position exposed to polling clients. Legacy documents deserialize as 0;
    // every newly projected event advances it exactly once.
    long Sequence = 0);
