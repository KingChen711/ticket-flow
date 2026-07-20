using TicketFlow.Reservation.Domain.SalePolicies;

namespace TicketFlow.Reservation.Application.Abstractions;

public interface IEventSalePolicyRepository
{
    Task<EventSalePolicy?> GetAsync(Guid eventId, CancellationToken cancellationToken = default);
    void Add(EventSalePolicy policy);
}
