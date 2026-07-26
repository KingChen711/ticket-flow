using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Tickets;

/// <summary>
/// Customer-facing ticket access. Legacy rows with unknown ownership fail closed: a missing owner
/// is a migration/manual-review state, not evidence that every authenticated caller owns the PDF.
/// </summary>
public sealed class TicketAccessPolicy
{
    public bool CanAccess(Ticket ticket, Guid? callerId, bool isAdmin)
    {
        if (isAdmin)
            return true;

        return callerId is { } subject &&
               ticket.CustomerId is { } owner &&
               owner == subject;
    }
}
