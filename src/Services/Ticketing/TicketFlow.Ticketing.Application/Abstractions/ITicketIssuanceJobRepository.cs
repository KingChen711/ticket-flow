using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Application.Abstractions;

public interface ITicketIssuanceJobRepository
{
    Task<TicketIssuanceJob?> GetByOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    void Add(TicketIssuanceJob job);

    /// <summary>True when the exception is Postgres refusing a duplicate key.</summary>
    bool IsUniqueViolation(Exception exception);
}
