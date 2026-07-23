using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed class PriceTier : Entity<Guid>
{
    public Guid SectionId { get; private set; }
    public Money Price { get; private set; } = null!;

    // Required by EF Core: Price is an owned navigation, can't be bound through a
    // constructor parameter — see Event's parameterless constructor for the same reason.
    private PriceTier() { }

    private PriceTier(Guid id, Guid sectionId, Money price) : base(id)
    {
        SectionId = sectionId;
        Price = price;
    }

    internal static PriceTier Create(Guid sectionId, Money price) =>
        Create(Guid.NewGuid(), sectionId, price);

    internal static PriceTier Create(Guid id, Guid sectionId, Money price) =>
        new(
            id == Guid.Empty ? throw new ArgumentException("Price-tier id cannot be empty.", nameof(id)) : id,
            sectionId == Guid.Empty ? throw new ArgumentException("Section id cannot be empty.", nameof(sectionId)) : sectionId,
            price ?? throw new ArgumentNullException(nameof(price)));
}
