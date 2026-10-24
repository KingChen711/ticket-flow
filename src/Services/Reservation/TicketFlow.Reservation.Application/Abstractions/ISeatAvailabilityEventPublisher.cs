namespace TicketFlow.Reservation.Application.Abstractions;

/// <summary>
/// Queues a seat-map change through the service's durable bus outbox. Implementations must not
/// invoke SignalR inline with the allocation transaction. Generation is an allocation ownership
/// epoch for stale-update suppression on the affected seats, not a total order for an event;
/// clients still need snapshot/refetch recovery when a hold expires by TTL.
/// </summary>
public interface ISeatAvailabilityEventPublisher
{
    Task PublishAsync(
        Guid eventId,
        IReadOnlyList<Guid> seatIds,
        string status,
        long generation,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);
}
