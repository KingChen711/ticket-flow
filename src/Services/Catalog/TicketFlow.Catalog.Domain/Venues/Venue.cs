using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Venues;

public sealed class Venue : AggregateRoot<Guid>
{
    public string Name { get; private set; }
    public Address Address { get; private set; }
    public SeatMap SeatMap { get; private set; }

    private Venue(Guid id, string name, Address address) : base(id)
    {
        Name = name;
        Address = address;
        SeatMap = new SeatMap();
    }

    public static Venue Register(string name, Address address)
    {
        var venue = new Venue(
            Guid.NewGuid(),
            Guard.NotNullOrEmpty(name, nameof(name)),
            address ?? throw new ArgumentNullException(nameof(address)));

        venue.Raise(new VenueRegistered(venue.Id, venue.Name));

        return venue;
    }

    public void AddSection(string name, int capacity) => SeatMap.AddSection(name, capacity);
}
