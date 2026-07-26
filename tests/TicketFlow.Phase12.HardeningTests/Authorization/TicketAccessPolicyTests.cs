using TicketFlow.Ticketing.Application.Tickets;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Phase12.HardeningTests.Authorization;

public sealed class TicketAccessPolicyTests
{
    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void An_unknown_legacy_owner_is_admin_only_not_open_to_every_authenticated_user()
    {
        var policy = new TicketAccessPolicy();
        var ticket = Ticket.Issue(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "qr",
            "ticket.pdf",
            DateTimeOffset.UtcNow,
            customerId: null,
            eventId: Guid.NewGuid());

        Assert.False(policy.CanAccess(ticket, Guid.NewGuid(), isAdmin: false));
        Assert.True(policy.CanAccess(ticket, Guid.NewGuid(), isAdmin: true));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public void A_known_ticket_is_visible_only_to_its_customer_or_an_admin()
    {
        var policy = new TicketAccessPolicy();
        var owner = Guid.NewGuid();
        var ticket = Ticket.Issue(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "qr",
            "ticket.pdf",
            DateTimeOffset.UtcNow,
            owner,
            Guid.NewGuid());

        Assert.True(policy.CanAccess(ticket, owner, isAdmin: false));
        Assert.False(policy.CanAccess(ticket, Guid.NewGuid(), isAdmin: false));
        Assert.True(policy.CanAccess(ticket, Guid.NewGuid(), isAdmin: true));
    }
}
