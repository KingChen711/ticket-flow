using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

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
