using TicketFlow.Domain;

namespace TicketFlow.Payment.Domain.Payments;

/// <summary>
/// Own copy, not shared with Catalog/Ordering's Money — same DB-per-service/no-cross-context-
/// reference rule kept since Phase 1 (see Ordering.Domain/Orders/Money.cs for the same note).
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
