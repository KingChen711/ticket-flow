using MassTransit;
using TicketFlow.Contracts.Messaging.Ticketing;

namespace TicketFlow.Ticketing.Api.Services;

public sealed record TicketIssuanceRecoveryRequest(
    Guid OrderId,
    Guid CustomerId,
    Guid EventId,
    IReadOnlyList<Guid> SeatIds,
    string CustomerName,
    string CustomerEmail,
    string? OperationKey,
    long PriceSnapshotVersion);

public interface ITicketIssuanceRecoveryPublisher
{
    Task PublishAsync(
        TicketIssuanceRecoveryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class TicketIssuanceRecoveryPublisher(IPublishEndpoint publisher)
    : ITicketIssuanceRecoveryPublisher
{
    public Task PublishAsync(
        TicketIssuanceRecoveryRequest request,
        CancellationToken cancellationToken = default) =>
        publisher.Publish(
            new IssueTickets(
                request.OrderId,
                request.CustomerId,
                request.EventId,
                request.SeatIds,
                request.CustomerName,
                request.CustomerEmail,
                request.OperationKey,
                request.PriceSnapshotVersion),
            cancellationToken);
}
