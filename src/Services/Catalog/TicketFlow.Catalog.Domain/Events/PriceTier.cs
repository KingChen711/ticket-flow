using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Events;

public sealed class PriceTier : Entity<Guid>
{
    public string SectionName { get; private set; }
    public Money Price { get; private set; }

    private PriceTier(Guid id, string sectionName, Money price) : base(id)
    {
        SectionName = sectionName;
        Price = price;
    }

    internal static PriceTier Create(string sectionName, Money price) =>
        new(
            Guid.NewGuid(),
            Guard.NotNullOrEmpty(sectionName, nameof(sectionName)),
            price ?? throw new ArgumentNullException(nameof(price)));
}
