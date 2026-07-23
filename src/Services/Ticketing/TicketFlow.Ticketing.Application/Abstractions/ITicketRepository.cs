using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Abstractions;

public interface ITicketRepository
{
    Task<IReadOnlyList<Ticket>> GetByOrderIdAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task<Ticket?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Ticket?> GetByQrPayloadAsync(string qrPayload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the one check-in for this ticket with a single conditional UPDATE (Commit 10).
    /// </summary>
    /// <remarks>
    /// The database decides, not the application. Reading a ticket, seeing <c>CheckedInAt == null</c>
    /// and then saving is a read-then-write race: two scanners at two gates both read null and both
    /// hear "Valid", which is precisely how one ticket admits two people. The conditional UPDATE
    /// names every condition — not yet checked in, right event, not revoked — so exactly one caller
    /// can match it and the losers are told why.
    /// </remarks>
    Task<bool> TryClaimCheckInAsync(
        Guid ticketId,
        Guid expectedEventId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    void Add(Ticket ticket);
}
