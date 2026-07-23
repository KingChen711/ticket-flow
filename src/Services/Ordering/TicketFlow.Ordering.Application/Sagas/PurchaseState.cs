using MassTransit;

namespace TicketFlow.Ordering.Application.Sagas;

/// <summary>
/// PurchaseSaga's transient coordination state — NOT the durable business record (that's the
/// Order event stream in Marten, Phase 4). Deliberately plain public get/set properties, unlike
/// every other type in this codebase (Entity/AggregateRoot always private-set behind factory
/// methods/behavior methods): MassTransit's saga repository (here, MassTransit.Marten) owns
/// construction and mutation directly via reflection/serialization on every transition — there
/// is no domain behavior to protect here, only orchestration data. CorrelationId = OrderId.
/// </summary>
public sealed class PurchaseState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }

    /// <summary>Required by <c>InstanceState</c> — MassTransit stores the current state's name here.</summary>
    public string CurrentState { get; set; } = null!;

    public Guid UserId { get; set; }
    public Guid CatalogEventId { get; set; }
    public Guid HoldId { get; set; }
    public List<Guid> SeatIds { get; set; } = [];
    public decimal Amount { get; set; }
    public string Currency { get; set; } = null!;
    public long PriceSnapshotVersion { get; set; }
    /// <summary>Allocation ownership epoch for this hold/exact seat set; not an event-stream revision.</summary>
    public long AllocationGeneration { get; set; }
    public string? AllocationReference { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string PaymentMethod { get; set; } = null!;
    public string CustomerName { get; set; } = null!;
    public string CustomerEmail { get; set; } = null!;
    public Guid? PaymentId { get; set; }
    public List<string> TicketPdfUrls { get; set; } = [];
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Set on any failure event (Commit 9) — carried through compensation to the final OrderCancelledIntegrationEvent.</summary>
    public string? CancellationReason { get; set; }

    // ---- Recorded facts (Commit 8) ----
    //
    // Before Commit 8 a milestone was only meaningful in the one state that expected it: a
    // TicketsIssued arriving while the saga was still AwaitingSeats matched no handler and was
    // discarded, and no later state could recover it — the purchase hung forever with the tickets
    // already issued. These timestamps make each milestone a fact that is remembered whenever it
    // arrives, so the order can be driven by what is known rather than by what happened to arrive
    // in the expected sequence. Null means "not yet"; a set value is never overwritten, which is
    // what makes redelivery a no-op.

    public DateTimeOffset? SeatsConfirmedAt { get; set; }
    public DateTimeOffset? PaymentAuthorizedAt { get; set; }
    public DateTimeOffset? TicketsIssuedAt { get; set; }

    /// <summary>Set when the command was published, so it is published exactly once.</summary>
    public DateTimeOffset? PaymentRequestedAt { get; set; }

    public DateTimeOffset? TicketsRequestedAt { get; set; }

    /// <summary>Guards the whole completion fan-out: OrderCompleted, the notification and the Kafka sale.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }

    // ---- Event cancellation campaign (Commit 13) ----
    public Guid? EventCancellationWorkflowId { get; set; }
    public DateTimeOffset? EventCancellationRequestedAt { get; set; }
    public DateTimeOffset? PaymentStopRequestedAt { get; set; }
    public DateTimeOffset? PaymentStoppedAt { get; set; }

    // ---- Compensation facts ----
    public DateTimeOffset? RefundRequestedAt { get; set; }
    public DateTimeOffset? PaymentRefundedAt { get; set; }
    public DateTimeOffset? ReleaseRequestedAt { get; set; }
    public DateTimeOffset? SeatsReleasedAt { get; set; }

    // ---- Deadlines and escalation (Commit 9) ----

    /// <summary>Token for the scheduled deadline, so MassTransit can cancel it when the wait ends.</summary>
    public Guid? DeadlineTokenId { get; set; }

    /// <summary>How many times a deadline fired and the saga retried the same participant.</summary>
    public int DeadlineStrikes { get; set; }

    /// <summary>Set when a deadline exhausted its retries and a human has to decide.</summary>
    public DateTimeOffset? ManualReviewAt { get; set; }

    public string? ManualReviewReason { get; set; }

    /// <summary>
    /// A terminal order ignores every further milestone. Without this a late TicketsIssued for a
    /// cancelled order would walk it back into an active state.
    ///
    /// ManualReview is terminal for the state machine but NOT for the order: it means automation
    /// has stopped and an operator owns it. Treating it as non-terminal would let the same silent
    /// participant re-arm deadlines forever, which is the "state vô hạn im lặng" this commit exists
    /// to remove.
    /// </summary>
    public bool IsTerminal =>
        CancelledAt is not null ||
        ManualReviewAt is not null ||
        (CompletedAt is not null && EventCancellationWorkflowId is null);
}
