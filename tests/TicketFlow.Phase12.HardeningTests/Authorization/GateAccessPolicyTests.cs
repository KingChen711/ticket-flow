using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Application.Tickets;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Phase12.HardeningTests.Authorization;

/// <summary>
/// Who may run the gate for an event (Commit 14). Until now <c>POST /tickets/validate</c> only
/// asked for the organizer role, so any organizer in the realm could check in — and therefore burn
/// — tickets for an event that was none of their business.
/// </summary>
public sealed class GateAccessPolicyTests
{
    private static readonly Guid OrganizerA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OrganizerB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task Only_the_owning_organizer_and_an_admin_may_scan()
    {
        var eventId = Guid.NewGuid();
        var policy = new GateAccessPolicy(new FakeEventOwnerRepository(
            EventOwner.Create(eventId, OrganizerA, revision: 1, DateTimeOffset.UtcNow)));

        Assert.True(await policy.CanScanAsync(OrganizerA, isAdmin: false, eventId));
        Assert.False(await policy.CanScanAsync(OrganizerB, isAdmin: false, eventId));
        Assert.True(await policy.CanScanAsync(OrganizerB, isAdmin: true, eventId));
        // No usable subject at all is not a free pass.
        Assert.False(await policy.CanScanAsync(null, isAdmin: false, eventId));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task An_event_with_no_known_owner_is_refused_rather_than_open_to_every_organizer()
    {
        var policy = new GateAccessPolicy(new FakeEventOwnerRepository());

        // Events published before Commit 14 carry no organizer, so nothing projects an owner for
        // them. Defaulting to "allow" would leave exactly the hole this commit closes, on exactly
        // the events that already exist.
        Assert.False(await policy.CanScanAsync(OrganizerA, isAdmin: false, Guid.NewGuid()));
        Assert.True(await policy.CanScanAsync(OrganizerA, isAdmin: true, Guid.NewGuid()));
    }

    [Fact]
    [Trait(HardeningTraits.Name, HardeningTraits.Harness)]
    public async Task A_stale_projection_message_cannot_move_ownership_backwards()
    {
        var eventId = Guid.NewGuid();
        var owner = EventOwner.Create(eventId, OrganizerA, revision: 5, DateTimeOffset.UtcNow);

        // Same revision guard Search and Notification use (Commit 11): an out-of-order redelivery
        // carrying an older AggregateVersion must not hand the event to a previous owner.
        Assert.False(owner.TryApply(OrganizerB, revision: 4, DateTimeOffset.UtcNow));
        Assert.Equal(OrganizerA, owner.OrganizerId);

        Assert.True(owner.TryApply(OrganizerB, revision: 6, DateTimeOffset.UtcNow));
        Assert.Equal(OrganizerB, owner.OrganizerId);

        var policy = new GateAccessPolicy(new FakeEventOwnerRepository(owner));
        Assert.False(await policy.CanScanAsync(OrganizerA, isAdmin: false, eventId));
        Assert.True(await policy.CanScanAsync(OrganizerB, isAdmin: false, eventId));
    }

    private sealed class FakeEventOwnerRepository(params EventOwner[] owners) : IEventOwnerRepository
    {
        private readonly List<EventOwner> _owners = [.. owners];

        public Task<EventOwner?> GetAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_owners.FirstOrDefault(o => o.EventId == eventId));

        public void Add(EventOwner owner) => _owners.Add(owner);

        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
