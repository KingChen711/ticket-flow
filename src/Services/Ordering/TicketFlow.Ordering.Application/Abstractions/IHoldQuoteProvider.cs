namespace TicketFlow.Ordering.Application.Abstractions;

public sealed record AuthoritativeHoldQuote(
    Guid HoldId,
    Guid EventId,
    Guid OwnerUserId,
    IReadOnlyList<Guid> SeatIds,
    decimal Amount,
    string Currency,
    long PriceSnapshotVersion,
    DateTimeOffset ExpiresAt,
    long AllocationGeneration = 0);

public interface IHoldQuoteProvider
{
    Task<AuthoritativeHoldQuote?> GetAsync(
        Guid holdId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);
}
