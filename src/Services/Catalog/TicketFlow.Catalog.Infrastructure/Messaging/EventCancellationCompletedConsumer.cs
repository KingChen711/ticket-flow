using MassTransit;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Contracts.Messaging.Workflows;

namespace TicketFlow.Catalog.Infrastructure.Messaging;

public sealed class EventCancellationCompletedConsumer(
    IEventCancellationWorkflowRepository workflows,
    IUnitOfWork unitOfWork) : IConsumer<EventCancellationCompleted>
{
    public async Task Consume(ConsumeContext<EventCancellationCompleted> context)
    {
        var message = context.Message;
        var workflow = await workflows.GetByIdAsync(message.WorkflowId, context.CancellationToken);
        if (workflow is null || workflow.EventId != message.EventId)
            return;

        workflow.Complete(message.OrderCount, message.CompletedAt);
        await unitOfWork.SaveChangesAsync(context.CancellationToken);
    }
}
