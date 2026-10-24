using TicketFlow.Catalog.Domain.Workflows;

namespace TicketFlow.Catalog.Application.Abstractions;

public interface IEventOnboardingWorkflowRepository
{
    Task<EventOnboardingWorkflow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<EventOnboardingWorkflow?> GetByCreationKeyAsync(string creationKey, CancellationToken cancellationToken = default);
    void Add(EventOnboardingWorkflow workflow);
}

public interface IEventCancellationWorkflowRepository
{
    Task<EventCancellationWorkflow?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<EventCancellationWorkflow?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default);
    void Add(EventCancellationWorkflow workflow);
}

public interface IReservationInventoryClient
{
    Task ProvisionAsync(
        Guid eventId,
        IReadOnlyList<ReservationInventorySection> sections,
        CancellationToken cancellationToken = default);

    Task CleanupAsync(Guid eventId, CancellationToken cancellationToken = default);
}

public sealed record ReservationInventorySection(Guid SectionId, string SectionName, int Capacity);

/// <summary>Test seam for proving resume behavior at every persisted boundary.</summary>
public interface IWorkflowFailureInjector
{
    Task BeforeStepAsync(Guid workflowId, EventOnboardingStep step, CancellationToken cancellationToken = default);
}

public sealed class NoWorkflowFailureInjector : IWorkflowFailureInjector
{
    public Task BeforeStepAsync(Guid workflowId, EventOnboardingStep step, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
