namespace TicketFlow.Catalog.Application.Workflows;

/// <summary>
/// A role authorizes the kind of operation; this exception enforces ownership of the particular
/// durable workflow. API callers receive 404 so a guessed workflow id/creation key does not reveal
/// another organizer's in-progress event.
/// </summary>
public sealed class WorkflowOwnershipException(Guid workflowId)
    : Exception($"Workflow '{workflowId}' was not found.");
