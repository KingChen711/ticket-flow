using MassTransit;
using TicketFlow.Contracts.Messaging.Ordering;
using TicketFlow.Contracts.Messaging.Payment;
using TicketFlow.Contracts.Messaging.Reservation;
using TicketFlow.Contracts.Messaging.Ticketing;

namespace TicketFlow.Ordering.Application.Sagas;

/// <summary>
/// Serializes every message for one purchase instance while still allowing unrelated orders to
/// progress in parallel. Marten's optimistic version check remains the cross-process backstop;
/// the outbox withholds publishes until that check has succeeded.
/// </summary>
public sealed class PurchaseSagaDefinition : SagaDefinition<PurchaseState>
{
    private const int PartitionCount = 32;

    protected override void ConfigureSaga(
        IReceiveEndpointConfigurator endpointConfigurator,
        ISagaConfigurator<PurchaseState> sagaConfigurator,
        IRegistrationContext context)
    {
        var partitioner = endpointConfigurator.CreatePartitioner(PartitionCount);

        sagaConfigurator.Message<OrderSubmitted>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<SeatsConfirmed>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<SeatsConfirmationFailed>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<PaymentProcessed>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<PaymentFailed>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<TicketsIssued>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<TicketingFailed>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<PaymentRefunded>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));
        sagaConfigurator.Message<SeatsReleased>(x =>
            x.UsePartitioner(partitioner, message => message.Message.OrderId));

        sagaConfigurator.UseInMemoryOutbox(context);
    }
}
