namespace TicketFlow.Ticketing.Domain.Tickets;

/// <summary>
/// Which organizer owns an event, projected from Catalog's <c>CatalogEventPublished</c>
/// (Phase 12 Commit 14). Ticketing needs this to answer one question at the gate: may THIS
/// scanner check in a ticket for THIS event?
/// </summary>
/// <remarks>
/// A local projection rather than a call into Catalog, because the caller is a turnstile: putting
/// a synchronous cross-service dependency on the scan path means Catalog being slow turns into a
/// queue of people at the door. It is also not stamped onto the Ticket itself — a ticket is issued
/// from an order, and the order path never carries the organizer, so deriving it there would mean
/// threading one field through quote, order, saga and issuance to learn a fact Catalog already
/// broadcasts.
///
/// <see cref="Revision"/> is Catalog's AggregateVersion, guarded the same way Search and
/// Notification guard theirs (Commit 11): an out-of-order redelivery must not move ownership
/// backwards.
/// </remarks>
public sealed class EventOwner
{
    private EventOwner()
    {
    }

    public Guid EventId { get; private set; }

    public Guid OrganizerId { get; private set; }

    public long Revision { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static EventOwner Create(Guid eventId, Guid organizerId, long revision, DateTimeOffset updatedAt) =>
        new()
        {
            EventId = eventId == Guid.Empty ? throw new ArgumentException("Event id cannot be empty.", nameof(eventId)) : eventId,
            OrganizerId = organizerId == Guid.Empty ? throw new ArgumentException("Organizer id cannot be empty.", nameof(organizerId)) : organizerId,
            Revision = revision,
            UpdatedAt = updatedAt,
        };

    /// <summary>Applies a newer projection of the same event; an older or equal revision is ignored.</summary>
    public bool TryApply(Guid organizerId, long revision, DateTimeOffset updatedAt)
    {
        if (revision < Revision)
            return false;

        OrganizerId = organizerId;
        Revision = revision;
        UpdatedAt = updatedAt;
        return true;
    }
}
