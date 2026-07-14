using TicketFlow.Domain;

namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Deliberately a separate type from TicketFlow.Catalog.Domain.Events.Money, not a shared/moved
/// BuildingBlocks type — Ordering doesn't reference Catalog.Domain (no cross-context project
/// references, same DB-per-service autonomy rule as everywhere else). Structurally identical by
/// coincidence, not because the concepts are the same aggregate's data.
/// </summary>
public sealed class Money : ValueObject
{
    public decimal Amount { get; }
    public string Currency { get; }

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Create(decimal amount, string currency = "VND") =>
        new(
            Guard.NotNegative(amount, nameof(amount)),
            Guard.NotNullOrEmpty(currency, nameof(currency)));

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }
}
