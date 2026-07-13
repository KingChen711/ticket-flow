using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed class PriceTier : Entity<Guid>
{
    public Guid SectionId { get; private set; }
    public Money Price { get; private set; }

    private PriceTier(Guid id, Guid sectionId, Money price) : base(id)
    {
        SectionId = sectionId;
        Price = price;
    }

    internal static PriceTier Create(Guid sectionId, Money price) =>
        new(
            Guid.NewGuid(),
            sectionId == Guid.Empty ? throw new ArgumentException("Section id cannot be empty.", nameof(sectionId)) : sectionId,
            price ?? throw new ArgumentNullException(nameof(price)));
}
