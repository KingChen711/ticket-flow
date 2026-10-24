using TicketFlow.Ordering.Application.Abstractions;

namespace TicketFlow.Ordering.Application.Tests;

/// <summary>In-memory seat → section map and section price tiers for one event.</summary>
public sealed class FakeSeatPricingSource : ISeatPricingSource
{
    public Dictionary<Guid, Guid> SeatSections { get; } = [];
    public List<SectionPrice> SectionPrices { get; } = [];

    /// <summary>All given seats in one section priced at <paramref name="amount"/>.</summary>
    public static FakeSeatPricingSource ForSeats(IEnumerable<Guid> seatIds, decimal amount = 100_000, string currency = "VND")
    {
        var source = new FakeSeatPricingSource();
        var sectionId = Guid.NewGuid();
        source.SectionPrices.Add(new SectionPrice(sectionId, amount, currency));
        foreach (var seatId in seatIds)
            source.SeatSections[seatId] = sectionId;

        return source;
    }

    public Task<IReadOnlyDictionary<Guid, Guid>> GetSeatSectionsAsync(Guid catalogEventId, IReadOnlyList<Guid> seatIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<Guid, Guid>>(
            SeatSections.Where(pair => seatIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));

    public Task<IReadOnlyList<SectionPrice>> GetSectionPricesAsync(Guid catalogEventId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SectionPrice>>(SectionPrices);
}
