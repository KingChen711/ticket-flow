namespace TicketFlow.Ordering.Domain.Orders;

/// <summary>
/// Order-stream milestone appended when tickets are generated (saga: AwaitingTickets ->
/// Completed). Named with the "Order" prefix for the same reason as OrderSeatsConfirmed — avoids
/// colliding with TicketFlow.Contracts.Messaging.Ticketing.TicketsIssued.
/// </summary>
public sealed record OrderTicketsIssued(Guid OrderId, IReadOnlyList<string> TicketPdfUrls, DateTimeOffset IssuedAt);
