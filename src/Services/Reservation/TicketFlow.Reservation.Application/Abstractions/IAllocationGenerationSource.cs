namespace TicketFlow.Reservation.Application.Abstractions;

public interface IAllocationGenerationSource
{
    Task<long> NextAsync(CancellationToken cancellationToken = default);
}
