namespace TicketFlow.Ordering.Application.Workflows;

public sealed class EventCancellationCampaign
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Reason { get; set; } = null!;
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset LastOrderDiscoveredAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<EventCancellationOrderOperation> Orders { get; set; } = [];

    public bool IsComplete(DateTimeOffset now, TimeSpan quietPeriod) =>
        CompletedAt is not null ||
        (now - LastOrderDiscoveredAt >= quietPeriod &&
         Orders.All(order => order.CompletedAt is not null));
}

public sealed class EventCancellationOrderOperation
{
    public Guid OrderId { get; set; }
    public string StateAtDiscovery { get; set; } = null!;
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? CoreCancelledAt { get; set; }
    public DateTimeOffset? TicketsRevokedAt { get; set; }
    public DateTimeOffset? CustomerNotifiedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public void TryComplete(DateTimeOffset now)
    {
        if (CoreCancelledAt is not null && TicketsRevokedAt is not null &&
            CustomerNotifiedAt is not null)
            CompletedAt ??= now;
    }
}
