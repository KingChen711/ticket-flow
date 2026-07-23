using TicketFlow.Domain;

namespace TicketFlow.Ticketing.Domain.Tickets;

public enum TicketIssuanceJobState
{
    /// <summary>Claimed, rendering not finished. A retry of the same order resumes this row.</summary>
    Pending,

    Issued,

    /// <summary>Refused for a reason no retry can change — the only state that may fail the saga.</summary>
    Failed,
}

/// <summary>
/// Durable record that one order's tickets are being (or have been) issued, claimed before any
/// rendering starts (Commit 10).
/// </summary>
/// <remarks>
/// The old consumer's only guard was "does this order already have ticket rows?", which is only
/// true <em>after</em> a successful run. Two deliveries arriving together both saw none and both
/// rendered and uploaded every PDF. Unique on <see cref="OrderId"/>, inserted first, the loser of
/// that race is refused by the database before it can call the renderer at all.
/// </remarks>
public sealed class TicketIssuanceJob : Entity<Guid>
{
    public Guid OrderId { get; private set; }

    public Guid EventId { get; private set; }

    public Guid CustomerId { get; private set; }

    public TicketIssuanceJobState State { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureReason { get; private set; }

    private TicketIssuanceJob() { }

    public static TicketIssuanceJob Claim(Guid orderId, Guid eventId, Guid customerId, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            EventId = eventId,
            CustomerId = customerId,
            State = TicketIssuanceJobState.Pending,
            Attempts = 1,
            CreatedAt = now,
        };

    public void RecordAttempt() => Attempts++;

    public void MarkIssued(DateTimeOffset now)
    {
        State = TicketIssuanceJobState.Issued;
        CompletedAt = now;
        FailureReason = null;
    }

    /// <summary>
    /// Only for a refusal that retrying cannot fix. A transient renderer or storage error must
    /// leave the job Pending and throw instead, so the message is redelivered — marking it Failed
    /// tells the saga to refund and release seats for an order whose PDFs may be perfectly fine.
    /// </summary>
    public void MarkFailed(string reason, DateTimeOffset now)
    {
        State = TicketIssuanceJobState.Failed;
        CompletedAt = now;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
    }
}
