using TicketFlow.Catalog.Domain.SeatMaps;

namespace TicketFlow.Catalog.Domain.Tests.SeatMaps;

public class SeatMapTests
{
    [Fact]
    public void CreateForEvent_RaisesSeatMapCreated()
    {
        var eventId = Guid.NewGuid();

        var seatMap = SeatMap.CreateForEvent(eventId);

        var raised = Assert.Single(seatMap.DomainEvents.OfType<SeatMapCreated>());
        Assert.Equal(seatMap.Id, raised.SeatMapId);
        Assert.Equal(eventId, raised.CatalogEventId);
        Assert.Equal(eventId, seatMap.EventId);
        Assert.Empty(seatMap.Sections);
    }

    [Fact]
    public void AddSection_WithValidData_AddsToSections()
    {
        var seatMap = SeatMap.CreateForEvent(Guid.NewGuid());

        seatMap.AddSection("VIP Floor", 500);

        var section = Assert.Single(seatMap.Sections);
        Assert.Equal("VIP Floor", section.Name);
        Assert.Equal(500, section.Capacity);
    }

    [Fact]
    public void AddSection_WithDuplicateName_Throws()
    {
        var seatMap = SeatMap.CreateForEvent(Guid.NewGuid());
        seatMap.AddSection("Section A", 100);

        Assert.Throws<InvalidOperationException>(() => seatMap.AddSection("section a", 200));
    }

    [Fact]
    public void AddSection_WithZeroCapacity_Throws()
    {
        var seatMap = SeatMap.CreateForEvent(Guid.NewGuid());

        Assert.Throws<ArgumentOutOfRangeException>(() => seatMap.AddSection("Section A", 0));
    }
}
