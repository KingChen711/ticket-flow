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

    /// <summary>Seat ids from the order request, kept to compare against what reservation confirmed.</summary>
    public List<Guid> RequestedSeatIds { get; set; } = [];
    public decimal Amount { get; set; }
    public string PaymentMethod { get; set; } = null!;
    public string CustomerName { get; set; } = null!;
    public string CustomerEmail { get; set; } = null!;
    public Guid? PaymentId { get; set; }
    public List<string> TicketPdfUrls { get; set; } = [];
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Set on any failure event (Commit 9) — carried through compensation to the final OrderCancelledIntegrationEvent.</summary>
    public string? CancellationReason { get; set; }

    /// <summary>
    /// Replaces the working seat list with what reservation confirmed. Instances persisted before
    /// RequestedSeatIds existed still hold the requested seats in SeatIds, so those are kept first.
    /// </summary>
    public void RecordConfirmedSeats(IEnumerable<Guid> confirmedSeatIds)
    {
        if (RequestedSeatIds.Count == 0)
            RequestedSeatIds = [.. SeatIds];

        SeatIds = [.. confirmedSeatIds];
    }

    /// <summary>A method, not a property, so the saga repository doesn't serialize it.</summary>
    public bool ConfirmedAllRequestedSeats() => RequestedSeatIds.ToHashSet().SetEquals(SeatIds);
}
