using TicketFlow.Domain;

namespace TicketFlow.Ticketing.Domain.Tickets;

/// <summary>
/// State-based (EF Core, Phase 5 mục 9 — ticketing is not event-sourced, only ordering is).
/// Deliberately thin, like Seat (Phase 2) or PaymentTransaction (Phase 5 Commit 2): a ticket is
/// an immutable fact once issued ("this seat got this QR/PDF at this time"), not a state
/// machine — Phase 5 only issues tickets, it never revokes/reissues them (order-level
/// cancellation is handled by the saga at the Order aggregate, not by mutating already-issued
/// tickets). Extends Entity&lt;Guid&gt;, not AggregateRoot&lt;Guid&gt;, same reasoning as
/// PaymentTransaction — no domain events needed, the integration event TicketsIssued is built
/// directly from the issue result.
/// </summary>
public sealed class Ticket : Entity<Guid>
{
    public Guid OrderId { get; private set; }
    public Guid SeatId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public Guid? EventId { get; private set; }
    public TicketIssuanceState? IssuanceState { get; private set; }
    public string QrPayload { get; private set; } = null!;
    public string PdfObjectKey { get; private set; } = null!;
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset? CheckedInAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }

    // Required by EF Core.
    private Ticket() { }

    private Ticket(Guid id, Guid orderId, Guid seatId, string qrPayload, string pdfObjectKey, DateTimeOffset issuedAt)
        : base(id)
    {
        OrderId = orderId;
        SeatId = seatId;
        QrPayload = qrPayload;
        PdfObjectKey = pdfObjectKey;
        IssuedAt = issuedAt;
    }

    public static Ticket Issue(Guid orderId, Guid seatId, string qrPayload, string pdfObjectKey, DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            orderId,
            seatId,
            Guard.NotNullOrEmpty(qrPayload, nameof(qrPayload)),
            Guard.NotNullOrEmpty(pdfObjectKey, nameof(pdfObjectKey)),
            now);

    // The one mutation this otherwise-immutable fact allows (see class doc comment) — one-way,
    // no un-check-in. Throwing on a repeat scan (rather than silently no-op'ing) is deliberate:
    // the gate-scanner UI (Phase 8) needs to tell "valid, first scan" apart from "already used"
    // to flag a possible duplicate/resold ticket.
    public void CheckIn(DateTimeOffset now)
    {
        if (CheckedInAt is not null)
            throw new InvalidOperationException($"Ticket {Id} was already checked in at {CheckedInAt:O}.");

        CheckedInAt = now;
    }
}
