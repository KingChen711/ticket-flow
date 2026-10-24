using TicketFlow.Domain;

namespace TicketFlow.Ticketing.Domain.Tickets;

public enum TicketIssuanceJobState
{
    /// <summary>Claimed, rendering not finished. A retry of the same order resumes this row.</summary>
    Pending,

    Issued,

    /// <summary>Refused for a reason no retry can change — the only state that may fail the saga.</summary>
    Failed,

    /// <summary>Cancellation tombstone; no late IssueTickets delivery may create a valid ticket.</summary>
    Cancelled,
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

    public Guid[] ExpectedSeatIds { get; private set; } = [];

    public string? CustomerName { get; private set; }

    public string? CustomerEmail { get; private set; }

    public string? OperationKey { get; private set; }

    public long PriceSnapshotVersion { get; private set; }

    public TicketIssuanceJobState State { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastAttemptAt { get; private set; }

    public DateTimeOffset? LastRecoveryAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? ArtifactsReconciledAt { get; private set; }

    public string? FailureReason { get; private set; }

    private TicketIssuanceJob() { }

    public bool HasRecoveryIntent =>
        ExpectedSeatIds.Length > 0 &&
        !string.IsNullOrWhiteSpace(CustomerName) &&
        !string.IsNullOrWhiteSpace(CustomerEmail);

    public static TicketIssuanceJob Claim(
        Guid orderId,
        Guid eventId,
        Guid customerId,
        IReadOnlyCollection<Guid> expectedSeatIds,
        string customerName,
        string customerEmail,
        string? operationKey,
        long priceSnapshotVersion,
        DateTimeOffset now)
    {
        var job = new TicketIssuanceJob
        {
            Id = Guid.NewGuid(),
            OrderId = RequireId(orderId, nameof(orderId)),
            EventId = RequireId(eventId, nameof(eventId)),
            CustomerId = RequireId(customerId, nameof(customerId)),
            State = TicketIssuanceJobState.Pending,
            Attempts = 1,
            CreatedAt = now,
            LastAttemptAt = now,
        };

        if (!job.TryCaptureOrMatchIntent(
                eventId,
                customerId,
                expectedSeatIds,
                customerName,
                customerEmail,
                operationKey,
                priceSnapshotVersion))
            throw new ArgumentException("The issuance intent is invalid.");

        return job;
    }

    public static TicketIssuanceJob CancelBeforeIssue(
        Guid orderId,
        Guid eventId,
        Guid customerId,
        string reason,
        DateTimeOffset now)
    {
        var job = new TicketIssuanceJob
        {
            Id = Guid.NewGuid(),
            OrderId = RequireId(orderId, nameof(orderId)),
            // Some legacy cancellation messages have no CatalogEventId. The tombstone still has
            // to fence the order, and cannot fabricate an event id.
            EventId = eventId,
            CustomerId = RequireId(customerId, nameof(customerId)),
            State = TicketIssuanceJobState.Pending,
            Attempts = 1,
            CreatedAt = now,
            LastAttemptAt = now,
        };
        job.MarkCancelled(reason, now);
        return job;
    }

    /// <summary>
    /// Captures a command snapshot for legacy rows that predate recovery metadata, or verifies
    /// that a redelivery is the same command. A reused OrderId never gets to rewrite the intent.
    /// </summary>
    public bool TryCaptureOrMatchIntent(
        Guid eventId,
        Guid customerId,
        IReadOnlyCollection<Guid> expectedSeatIds,
        string customerName,
        string customerEmail,
        string? operationKey,
        long priceSnapshotVersion)
    {
        if (eventId == Guid.Empty ||
            customerId == Guid.Empty ||
            expectedSeatIds.Count == 0 ||
            expectedSeatIds.Any(seatId => seatId == Guid.Empty) ||
            expectedSeatIds.Distinct().Count() != expectedSeatIds.Count ||
            string.IsNullOrWhiteSpace(customerName) ||
            string.IsNullOrWhiteSpace(customerEmail))
            return false;

        if (ExpectedSeatIds.Length == 0)
        {
            if (EventId != eventId || CustomerId != customerId)
                return false;

            ExpectedSeatIds = [.. expectedSeatIds];
            CustomerName = customerName;
            CustomerEmail = customerEmail;
            OperationKey = operationKey;
            PriceSnapshotVersion = priceSnapshotVersion;
            return true;
        }

        return EventId == eventId &&
               CustomerId == customerId &&
               ExpectedSeatIds.Length == expectedSeatIds.Count &&
               ExpectedSeatIds.ToHashSet().SetEquals(expectedSeatIds) &&
               string.Equals(CustomerName, customerName, StringComparison.Ordinal) &&
               string.Equals(CustomerEmail, customerEmail, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(OperationKey, operationKey, StringComparison.Ordinal) &&
               PriceSnapshotVersion == priceSnapshotVersion;
    }

    public void RecordAttempt(DateTimeOffset now)
    {
        Attempts++;
        LastAttemptAt = now;
    }

    public void MarkRecoveryScheduled(DateTimeOffset now) => LastRecoveryAt = now;

    public void MarkArtifactsReconciled(DateTimeOffset now) => ArtifactsReconciledAt = now;

    public void MarkIssued(DateTimeOffset now)
    {
        if (State == TicketIssuanceJobState.Cancelled)
            throw new InvalidOperationException($"Ticket issuance for order '{OrderId}' was cancelled.");

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

    public void MarkCancelled(string reason, DateTimeOffset now)
    {
        if (State == TicketIssuanceJobState.Cancelled)
            return;

        State = TicketIssuanceJobState.Cancelled;
        CompletedAt = now;
        FailureReason = reason.Length > 500 ? reason[..500] : reason;
    }

    private static Guid RequireId(Guid value, string parameterName) =>
        value == Guid.Empty
            ? throw new ArgumentException("Identifier cannot be empty.", parameterName)
            : value;
}
