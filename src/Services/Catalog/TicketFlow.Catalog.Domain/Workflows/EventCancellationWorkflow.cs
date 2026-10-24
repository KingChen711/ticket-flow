using TicketFlow.Domain;

namespace TicketFlow.Catalog.Domain.Workflows;

public enum EventCancellationStatus
{
    Requested,
    Completed,
}

/// <summary>Catalog-side durable handle for a cross-service event-cancellation campaign.</summary>
public sealed class EventCancellationWorkflow : Entity<Guid>
{
    public Guid EventId { get; private set; }
    public string Reason { get; private set; } = null!;
    public EventCancellationStatus Status { get; private set; }
    public int? OrderCount { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private EventCancellationWorkflow() { }

    public static EventCancellationWorkflow Start(Guid eventId, string reason, DateTimeOffset now)
    {
        if (eventId == Guid.Empty)
            throw new ArgumentException("Event id cannot be empty.", nameof(eventId));

        return new EventCancellationWorkflow
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Event cancelled by organizer." : reason.Trim(),
            Status = EventCancellationStatus.Requested,
            RequestedAt = now,
        };
    }

    public void Complete(int orderCount, DateTimeOffset completedAt)
    {
        if (Status == EventCancellationStatus.Completed)
            return;

        OrderCount = orderCount;
        CompletedAt = completedAt;
        Status = EventCancellationStatus.Completed;
    }
}
