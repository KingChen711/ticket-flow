using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.SeatMaps;

public sealed class Section : Entity<Guid>
{
    public string Name { get; private set; }
    public int Capacity { get; private set; }

    private Section(Guid id, string name, int capacity) : base(id)
    {
        Name = name;
        Capacity = capacity;
    }

    internal static Section Create(string name, int capacity) =>
        new(
            Guid.NewGuid(),
            Guard.NotNullOrEmpty(name, nameof(name)),
            capacity <= 0
                ? throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.")
                : capacity);
}
