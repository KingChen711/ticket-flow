using TicketFlow.Reservation.Domain.Seats;

namespace TicketFlow.Reservation.Application.Tests.Domain;

public sealed class SeatTests
{
    [Fact]
    public void Inventory_explodes_section_capacity_into_labeled_available_seats()
    {
        var eventId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();

        var seats = SeatInventory.ForSection(eventId, sectionId, "VIP", 3);

        Assert.Equal(["VIP-1", "VIP-2", "VIP-3"], seats.Select(seat => seat.Label));
        Assert.All(seats, seat =>
        {
            Assert.Equal(eventId, seat.EventId);
            Assert.Equal(sectionId, seat.SectionId);
            Assert.Equal(SeatStatus.Available, seat.Status);
        });
        Assert.Equal(3, seats.Select(seat => seat.Id).Distinct().Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Inventory_rejects_non_positive_capacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SeatInventory.ForSection(Guid.NewGuid(), Guid.NewGuid(), "A", capacity));
    }

    [Fact]
    public void Selling_the_same_seat_twice_is_rejected()
    {
        var seat = Seat.Create(Guid.NewGuid(), Guid.NewGuid(), "A-1");
        seat.MarkSold();

        Assert.Throws<InvalidOperationException>(seat.MarkSold);
    }

    [Fact]
    public void MarkAvailable_reverses_a_sale_and_is_a_no_op_when_already_available()
    {
        var seat = Seat.Create(Guid.NewGuid(), Guid.NewGuid(), "A-1");
        seat.MarkSold();

        seat.MarkAvailable();
        seat.MarkAvailable();

        Assert.Equal(SeatStatus.Available, seat.Status);
    }

    [Fact]
    public void Seat_requires_event_and_section()
    {
        Assert.Throws<ArgumentException>(() => Seat.Create(Guid.Empty, Guid.NewGuid(), "A-1"));
        Assert.Throws<ArgumentException>(() => Seat.Create(Guid.NewGuid(), Guid.Empty, "A-1"));
    }
}
