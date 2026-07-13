using TicketFlow.Catalog.Domain.Venues;

namespace TicketFlow.Catalog.Domain.Tests.Venues;

public class VenueTests
{
    private static Address ValidAddress() => Address.Create("123 Le Loi", "Ho Chi Minh City", "Vietnam");

    [Fact]
    public void Register_WithValidData_RaisesVenueRegistered()
    {
        var venue = Venue.Register("Phu Tho Stadium", ValidAddress());

        Assert.Single(venue.DomainEvents);
        var raised = Assert.IsType<VenueRegistered>(venue.DomainEvents[0]);
        Assert.Equal(venue.Id, raised.VenueId);
        Assert.Equal("Phu Tho Stadium", raised.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WithEmptyName_Throws(string? name)
    {
        Assert.Throws<ArgumentException>(() => Venue.Register(name!, ValidAddress()));
    }

    [Fact]
    public void Register_WithNullAddress_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Venue.Register("Phu Tho Stadium", null!));
    }
}
