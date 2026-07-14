using MassTransit;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Ordering.Application.Abstractions;
using TicketFlow.Ordering.Domain.Orders;

namespace TicketFlow.Ordering.Api.Consumers;

/// <summary>See OrderSeatsConfirmedConsumer's doc comment — same shape, next milestone.</summary>
public sealed class OrderPaymentAuthorizedConsumer(IOrderStore store, TimeProvider timeProvider) : IConsumer<PaymentProcessed>
{
    public async Task Consume(ConsumeContext<PaymentProcessed> context)
    {
        store.Append(
            context.Message.OrderId,
            new OrderPaymentAuthorized(context.Message.OrderId, context.Message.PaymentId, timeProvider.GetUtcNow()));
        await store.SaveChangesAsync(context.CancellationToken);
    }
}
