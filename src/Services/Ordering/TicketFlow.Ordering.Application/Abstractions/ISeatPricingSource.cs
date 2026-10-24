namespace TicketFlow.Ordering.Application.Abstractions;

/// <summary>
/// Read-only data PlaceOrder needs to price an order on the server: which section each seat of
/// the event belongs to (reservation) and what each section costs (catalog price tiers).
/// </summary>
public interface ISeatPricingSource
{
    Task<IReadOnlyDictionary<Guid, Guid>> GetSeatSectionsAsync(Guid catalogEventId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SectionPrice>> GetSectionPricesAsync(Guid catalogEventId, CancellationToken cancellationToken = default);
}

public sealed record SectionPrice(Guid SectionId, decimal Amount, string Currency);
