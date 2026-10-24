using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Application.Orders;

/// <summary>
/// The order total is the sum of each seat's section price tier. It is computed here from
/// catalog and reservation data, never taken from the request body.
/// </summary>
public static class OrderPricing
{
    public static Money Calculate(
        IReadOnlyList<Guid> seatIds,
        IReadOnlyDictionary<Guid, Guid> seatSections,
        IReadOnlyList<SectionPrice> sectionPrices)
    {
        if (seatIds is not { Count: > 0 })
            throw new ArgumentException("An order must include at least one seat.", nameof(seatIds));

        if (seatIds.Distinct().Count() != seatIds.Count)
            throw new ArgumentException("A seat is listed more than once.", nameof(seatIds));

        var priceBySection = sectionPrices.ToDictionary(price => price.SectionId);
        var total = 0m;
        string? currency = null;

        foreach (var seatId in seatIds)
        {
            if (!seatSections.TryGetValue(seatId, out var sectionId))
                throw new InvalidOperationException($"Seat '{seatId}' does not belong to this event.");

            if (!priceBySection.TryGetValue(sectionId, out var price))
                throw new InvalidOperationException($"Section '{sectionId}' has no price tier.");

            if (currency is not null && currency != price.Currency)
                throw new InvalidOperationException("Seats in one order must share a currency.");

            currency = price.Currency;
            total += price.Amount;
        }

        return Money.Create(total, currency!);
    }
}
