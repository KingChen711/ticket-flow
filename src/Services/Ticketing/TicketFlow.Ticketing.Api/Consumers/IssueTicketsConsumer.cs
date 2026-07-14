using MassTransit;
using TicketFlow.Contracts.Messaging.Ticketing;

namespace TicketFlow.Ticketing.Api.Consumers;

/// <summary>
/// Stub for Commit 3 — proves the message reaches this service, doesn't yet render QR/PDF,
/// upload to MinIO, persist a Ticket, or publish TicketsIssued/TicketingFailed. Full pipeline
/// (render + upload + persist + publish + Inbox idempotency) lands in Commit 8, same
/// "skeleton first, wire for real later" split used for Payment (Commit 2 → 7).
/// </summary>
public sealed class IssueTicketsConsumer(ILogger<IssueTicketsConsumer> logger) : IConsumer<IssueTickets>
{
    public Task Consume(ConsumeContext<IssueTickets> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "[ticketing] received IssueTickets for order {OrderId} — {SeatCount} seat(s), customer {CustomerName}",
            message.OrderId,
            message.SeatIds.Count,
            message.CustomerName);

        return Task.CompletedTask;
    }
}
