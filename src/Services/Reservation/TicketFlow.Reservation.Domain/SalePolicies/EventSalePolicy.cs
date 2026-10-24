using TicketFlow.Domain;

namespace TicketFlow.Reservation.Domain.SalePolicies;

public sealed class EventSalePolicy : Entity<Guid>
{
    private readonly List<EventSalePriceTier> _priceTiers = [];

    public string Status { get; private set; } = null!;
    public DateTimeOffset SaleStartsAt { get; private set; }
    public DateTimeOffset SaleEndsAt { get; private set; }
    public long PriceSnapshotVersion { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public IReadOnlyList<EventSalePriceTier> PriceTiers => _priceTiers.AsReadOnly();

    private EventSalePolicy() { }

    public static EventSalePolicy FromSnapshot(
        Guid eventId,
        string status,
        DateTimeOffset saleStartsAt,
        DateTimeOffset saleEndsAt,
        long version,
        DateTimeOffset occurredAt,
        IEnumerable<(Guid SectionId, decimal Amount, string Currency)> priceTiers)
    {
        var policy = new EventSalePolicy { Id = eventId };
        policy.ApplySnapshot(status, saleStartsAt, saleEndsAt, version, occurredAt, priceTiers);
        return policy;
    }

    public bool ApplySnapshot(
        string status,
        DateTimeOffset saleStartsAt,
        DateTimeOffset saleEndsAt,
        long version,
        DateTimeOffset occurredAt,
        IEnumerable<(Guid SectionId, decimal Amount, string Currency)> priceTiers)
    {
        if (version <= PriceSnapshotVersion)
            return false;

        Status = Guard.NotNullOrEmpty(status, nameof(status));
        SaleStartsAt = saleStartsAt;
        SaleEndsAt = saleEndsAt;
        PriceSnapshotVersion = version;
        OccurredAt = occurredAt;

        _priceTiers.Clear();
        _priceTiers.AddRange(priceTiers.Select(tier =>
            EventSalePriceTier.Create(tier.SectionId, tier.Amount, tier.Currency)));
        return true;
    }

    public bool IsSaleableAt(DateTimeOffset now) =>
        string.Equals(Status, "Published", StringComparison.OrdinalIgnoreCase)
        && now >= SaleStartsAt
        && now < SaleEndsAt;
}

public sealed class EventSalePriceTier : Entity<Guid>
{
    public Guid SectionId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = null!;

    private EventSalePriceTier() { }

    internal static EventSalePriceTier Create(Guid sectionId, decimal amount, string currency) => new()
    {
        Id = Guid.NewGuid(),
        SectionId = sectionId,
        Amount = amount > 0 ? amount : throw new ArgumentOutOfRangeException(nameof(amount)),
        Currency = Guard.NotNullOrEmpty(currency, nameof(currency)).ToUpperInvariant(),
    };
}
