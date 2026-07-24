using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Application.Tickets;

/// <summary>
/// Decides whether a caller may run the gate for an event (Phase 12 Commit 14).
/// </summary>
/// <remarks>
/// Lives in Application rather than inline in the endpoint so the decision can be tested directly —
/// this is the rule that stops one organizer burning another organizer's tickets, and it should not
/// only be reachable through an HTTP fixture.
/// </remarks>
public sealed class GateAccessPolicy(IEventOwnerRepository eventOwners)
{
    /// <param name="organizerId">The caller's verified subject, or null when it is unusable.</param>
    /// <param name="isAdmin">Admin runs any gate.</param>
    public async Task<bool> CanScanAsync(
        Guid? organizerId,
        bool isAdmin,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin)
            return true;

        if (organizerId is null || organizerId == Guid.Empty)
            return false;

        // No projected owner means Ticketing does not know whose event this is — refused, not
        // allowed. Events published before Commit 14 carry no organizer and stay admin-only until
        // Catalog republishes them (POST /admin/events/replay, Commit 11). Defaulting the other way
        // would make every pre-existing event scannable by any organizer in the realm, which is the
        // hole this commit is closing.
        var owner = await eventOwners.GetAsync(eventId, cancellationToken);
        return owner is not null && owner.OrganizerId == organizerId.Value;
    }
}
