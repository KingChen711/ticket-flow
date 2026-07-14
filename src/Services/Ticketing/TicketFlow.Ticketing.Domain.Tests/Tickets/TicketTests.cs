using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Domain.Tests.Tickets;

public class TicketTests
{
    [Fact]
    public void Issue_WithValidData_SetsAllFields()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var ticket = Ticket.Issue(orderId, seatId, "qr-payload-123", "tickets/abc.pdf", now);

        Assert.Equal(orderId, ticket.OrderId);
        Assert.Equal(seatId, ticket.SeatId);
        Assert.Equal("qr-payload-123", ticket.QrPayload);
        Assert.Equal("tickets/abc.pdf", ticket.PdfObjectKey);
        Assert.Equal(now, ticket.IssuedAt);
        Assert.NotEqual(Guid.Empty, ticket.Id);
    }

    [Fact]
    public void Issue_WithEmptyQrPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Ticket.Issue(Guid.NewGuid(), Guid.NewGuid(), "", "tickets/abc.pdf", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Issue_WithEmptyPdfObjectKey_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            Ticket.Issue(Guid.NewGuid(), Guid.NewGuid(), "qr-payload-123", "", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Issue_TwiceForSameSeat_ProducesDistinctTicketIds()
    {
        var orderId = Guid.NewGuid();
        var seatId = Guid.NewGuid();

        var first = Ticket.Issue(orderId, seatId, "qr-1", "tickets/1.pdf", DateTimeOffset.UtcNow);
        var second = Ticket.Issue(orderId, seatId, "qr-2", "tickets/2.pdf", DateTimeOffset.UtcNow);

        Assert.NotEqual(first.Id, second.Id);
    }
}
