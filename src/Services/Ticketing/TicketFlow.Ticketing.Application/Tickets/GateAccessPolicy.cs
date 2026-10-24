using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Tickets;

/// <summary>
/// Decides whether a caller may run the gate for an event (Phase 12 Commit 14).
/// </summary>
/// <remarks>
/// Lives in Application rather than inline in the endpoint so the decision can be tested directly —
/// this is the rule that stops one organizer burning another organizer's tickets, and it should not
/// only be reachable through an HTTP fixture.
/// </remarks>
public sealed class GateAccessPolicy(
    IEventOwnerRepository eventOwners,
    IGateAssignmentRepository gateAssignments)
{
    /// <param name="subjectId">The caller's verified subject, or null when it is unusable.</param>
    /// <param name="isAdmin">Admin runs any gate.</param>
    public async Task<bool> CanScanAsync(
        Guid? subjectId,
        bool isAdmin,
        bool isOrganizer,
        bool isGateStaff,
        Guid eventId,
        string gateId,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin)
            return true;

        if (subjectId is null || subjectId == Guid.Empty)
            return false;

        if (isOrganizer)
        {
            // The owning organizer keeps an operational override across their own gates. This is
            // also how they verify a gate before handing the device to assigned staff.
            var owner = await eventOwners.GetAsync(eventId, cancellationToken);
            if (owner is not null && owner.OrganizerId == subjectId.Value)
                return true;
        }

        if (!isGateStaff)
            return false;

        try
        {
            return await gateAssignments.GetAsync(
                eventId,
                GateAssignment.NormalizeGateId(gateId),
                subjectId.Value,
                cancellationToken) is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<bool> CanManageAssignmentsAsync(
        Guid? subjectId,
        bool isAdmin,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin)
            return true;

        if (subjectId is null || subjectId == Guid.Empty)
            return false;

        var owner = await eventOwners.GetAsync(eventId, cancellationToken);
        return owner is not null && owner.OrganizerId == subjectId.Value;
    }
}
