using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Ticketing.Application.Abstractions;

namespace TicketFlow.Ticketing.Api.Consumers;

/// <summary>
/// Revokes an order's tickets when the order is cancelled (Commit 10).
/// </summary>
/// <remarks>
/// Until now nothing ever withdrew a ticket. A refunded order's PDF stayed valid and would admit
/// its holder at the gate — the money went back and the seat was given away too. Revoking rather
/// than deleting is deliberate: staff need to see "revoked", not "no such ticket", so a refund is
/// distinguishable from a forgery.
/// </remarks>
public sealed class RevokeTicketsConsumer(
    ITicketRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<RevokeTicketsConsumer> logger) : IConsumer<OrderCancelledIntegrationEvent>
{
    public async Task Consume(ConsumeContext<OrderCancelledIntegrationEvent> context)
    {
        var tickets = await repository.GetByOrderIdAsync(context.Message.OrderId, context.CancellationToken);
        if (tickets.Count == 0)
            return;

        // Revoke is idempotent, so a redelivery costs nothing and needs no separate guard.
        foreach (var ticket in tickets)
            ticket.Revoke(context.Message.Reason, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation(
            "[ticketing] revoked {Count} ticket(s) for cancelled order {OrderId}: {Reason}",
            tickets.Count,
            context.Message.OrderId,
            context.Message.Reason);
    }
}
