namespace TicketFlow.Ticketing.Domain.Tickets;

/// <summary>
/// Grants one user permission to scan tickets at one concrete gate of one event.
/// </summary>
public sealed class GateAssignment
{
    public const int MaxGateIdLength = 80;

    private GateAssignment()
    {
    }

    public Guid EventId { get; private set; }

    public string GateId { get; private set; } = null!;

    public Guid UserId { get; private set; }

    public Guid AssignedBy { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public static GateAssignment Create(
        Guid eventId,
        string gateId,
        Guid userId,
        Guid assignedBy,
        DateTimeOffset assignedAt) =>
        new()
        {
            EventId = RequireId(eventId, nameof(eventId)),
            GateId = NormalizeGateId(gateId),
            UserId = RequireId(userId, nameof(userId)),
            AssignedBy = RequireId(assignedBy, nameof(assignedBy)),
            AssignedAt = assignedAt,
        };

    /// <summary>
    /// Gate identifiers are case-insensitive and whitespace-normalized so "North", " north "
    /// and "NORTH" cannot accidentally create three different authorization scopes.
    /// </summary>
    public static string NormalizeGateId(string gateId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateId);

        var normalized = gateId.Trim().ToUpperInvariant();
        if (normalized.Length > MaxGateIdLength)
            throw new ArgumentException(
                $"Gate id cannot exceed {MaxGateIdLength} characters.",
                nameof(gateId));

        return normalized;
    }

    private static Guid RequireId(Guid value, string parameterName) =>
        value == Guid.Empty
            ? throw new ArgumentException("Identifier cannot be empty.", parameterName)
            : value;
}
