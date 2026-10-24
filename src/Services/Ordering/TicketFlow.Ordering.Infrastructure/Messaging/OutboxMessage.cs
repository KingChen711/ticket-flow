namespace TicketFlow.Ordering.Infrastructure.Messaging;

/// <summary>
/// An integration event waiting to be published, stored as a Marten document in the same session
/// (and so the same Postgres transaction) as the order events that produced it.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string MessageType { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}
