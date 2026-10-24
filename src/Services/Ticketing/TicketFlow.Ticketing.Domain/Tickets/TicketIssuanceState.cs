namespace TicketFlow.Ticketing.Domain.Tickets;

public enum TicketIssuanceState
{
    Issued,
    Revoked,
}

/// <summary>
/// What a gate scan actually decided (Commit 10). A single "invalid" would tell the person at the
/// gate nothing useful — a ticket for tomorrow's show, a refunded ticket, and a ticket someone has
/// already used are three different conversations.
/// </summary>
public enum CheckInOutcome
{
    /// <summary>First valid scan for this ticket at this event.</summary>
    Valid,

    /// <summary>No ticket carries that QR payload.</summary>
    NotFound,

    /// <summary>The ticket exists but was already scanned — a possible duplicate or resale.</summary>
    AlreadyCheckedIn,

    /// <summary>A real ticket, but not for the event this gate is scanning.</summary>
    WrongEvent,

    /// <summary>Refunded or otherwise revoked; it must not admit anyone.</summary>
    Revoked,
}

public sealed record CheckInResult(
    CheckInOutcome Outcome,
    Guid? TicketId = null,
    DateTimeOffset? CheckedInAt = null);
