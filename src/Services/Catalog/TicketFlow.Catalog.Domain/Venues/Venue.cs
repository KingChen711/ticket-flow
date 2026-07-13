using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Venues;

public sealed class Venue : AggregateRoot<Guid>
{
    public string Name { get; private set; } = null!;
    public Address Address { get; private set; } = null!;

    // Required by EF Core: owned navigations (Address) can't be bound through a
    // constructor parameter, only plain mapped properties can — EF materializes
    // this aggregate via this parameterless constructor, then sets every property
    // (including owned types) through reflection.
    private Venue() { }

    private Venue(Guid id, string name, Address address) : base(id)
    {
        Name = name;
        Address = address;
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
}
