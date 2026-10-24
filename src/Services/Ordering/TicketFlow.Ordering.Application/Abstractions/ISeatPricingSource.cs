namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Read-only data PlaceOrder needs to price an order on the server: which section each ordered
/// seat belongs to (reservation) and what each section costs (catalog price tiers).
/// </summary>
public interface ISeatPricingSource
{
    /// <summary>Section per seat id; ids that are not seats of the event are absent.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetSeatSectionsAsync(Guid catalogEventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SectionPrice>> GetSectionPricesAsync(Guid catalogEventId, CancellationToken cancellationToken = default);
}

public sealed record SectionPrice(Guid SectionId, decimal Amount, string Currency);
