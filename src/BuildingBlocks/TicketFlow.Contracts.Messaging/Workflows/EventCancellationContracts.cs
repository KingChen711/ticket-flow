namespace TicketFlow.Contracts.Messaging.Workflows;

/// <summary>
/// Durable hand-off emitted only after Catalog has committed the event tombstone and the closed
/// sale policy.  WorkflowId is stable across every retry of the cancellation campaign.
/// </summary>
public sealed record EventCancellationRequested(
    Guid WorkflowId,
    Guid EventId,
    string Reason,
    DateTimeOffset RequestedAt);

/// <summary>Per-order command. Ordering's purchase saga owns the state-dependent compensation.</summary>
public sealed record CancelOrderForEvent(
    Guid WorkflowId,
    Guid EventId,
    Guid OrderId,
    string Reason,
    DateTimeOffset RequestedAt);

/// <summary>Durable campaign wake-up. Reconciliation rescans late order dispatches on every pass.</summary>
public sealed record RetryEventCancellation(
    Guid WorkflowId,
    Guid EventId,
    DateTimeOffset ScheduledAt);

/// <summary>Ticketing persisted a cancellation tombstone and revoked every ticket currently present.</summary>
public sealed record TicketsRevokedForEventCancellation(
    Guid WorkflowId,
    Guid EventId,
    Guid OrderId,
    int TicketCount,
    DateTimeOffset RevokedAt);

/// <summary>The delivery provider confirmed the customer's event-cancellation notification.</summary>
public sealed record CustomerNotifiedOfEventCancellation(
    Guid WorkflowId,
    Guid EventId,
    Guid OrderId,
    string ProviderReference,
    DateTimeOffset DeliveredAt);

/// <summary>
/// Published only after every discovered order is cancelled, ticket issuance is fenced/revoked,
/// notification is provider-confirmed, and the late-checkout discovery window is quiet.
/// </summary>
public sealed record EventCancellationCompleted(
    Guid WorkflowId,
    Guid EventId,
    int OrderCount,
    DateTimeOffset CompletedAt);
