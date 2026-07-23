using Microsoft.EntityFrameworkCore;
using TicketFlow.Catalog.Application.Abstractions;
using TicketFlow.Catalog.Domain.Workflows;

namespace TicketFlow.Catalog.Infrastructure.Repositories;

public sealed class EventOnboardingWorkflowRepository(CatalogDbContext dbContext)
    : IEventOnboardingWorkflowRepository
{
    public Task<EventOnboardingWorkflow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.EventOnboardingWorkflows.FirstOrDefaultAsync(workflow => workflow.Id == id, cancellationToken);

    public Task<EventOnboardingWorkflow?> GetByCreationKeyAsync(
        string creationKey,
        CancellationToken cancellationToken = default) =>
        dbContext.EventOnboardingWorkflows.FirstOrDefaultAsync(
            workflow => workflow.CreationKey == creationKey,
            cancellationToken);

    public void Add(EventOnboardingWorkflow workflow) => dbContext.EventOnboardingWorkflows.Add(workflow);
}

public sealed class EventCancellationWorkflowRepository(CatalogDbContext dbContext)
    : IEventCancellationWorkflowRepository
{
    public Task<EventCancellationWorkflow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.EventCancellationWorkflows.FirstOrDefaultAsync(workflow => workflow.Id == id, cancellationToken);

    public Task<EventCancellationWorkflow?> GetByEventIdAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        dbContext.EventCancellationWorkflows.FirstOrDefaultAsync(
            workflow => workflow.EventId == eventId,
            cancellationToken);

    public void Add(EventCancellationWorkflow workflow) => dbContext.EventCancellationWorkflows.Add(workflow);
}
