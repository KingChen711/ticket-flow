namespace TicketFlow.Contracts.Messaging.Catalog;

/// <summary>
/// Complete, versioned sale snapshot consumed by Reservation. A cancellation is a tombstone
/// carrying the same shape, so it is safe to receive before an older Published snapshot.
/// </summary>
public sealed record CatalogSalePolicyChanged(
    Guid EventId,
    string Status,
    DateTimeOffset SaleStartsAt,
    DateTimeOffset SaleEndsAt,
    IReadOnlyList<PriceTierSnapshotV2> PriceTiers,
    long PriceSnapshotVersion,
    DateTimeOffset OccurredAt,
    Guid? CancellationWorkflowId = null,
    string? CancellationReason = null);

public sealed record PriceTierSnapshotV2(
    Guid SectionId,
    decimal Amount,
    string Currency);
