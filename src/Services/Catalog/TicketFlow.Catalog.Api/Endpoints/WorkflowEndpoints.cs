using System.Security.Claims;
using TicketFlow.Catalog.Application.Workflows;

namespace TicketFlow.Catalog.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        var onboarding = app.MapGroup("/workflows/event-onboarding")
            .WithTags("Workflows")
            .RequireAuthorization("RequireOrganizer");

        onboarding.MapPost("/", async (
            StartEventOnboardingRequest request,
            ClaimsPrincipal user,
            EventOnboardingWorkflowService service,
            CancellationToken ct) =>
        {
            // The organizer is captured once, here, and stored on the workflow — resume and
            // compensate below deliberately do NOT re-read it from their caller (Commit 14).
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            var workflow = await service.StartAsync(request, caller, ct);
            return workflow.Status == "Completed"
                ? Results.Ok(workflow)
                : Results.Accepted($"/catalog/workflows/event-onboarding/{workflow.WorkflowId}", workflow);
        });

        onboarding.MapGet("/{workflowId:guid}", async (
            Guid workflowId,
            EventOnboardingWorkflowService service,
            CancellationToken ct) =>
        {
            var workflow = await service.GetAsync(workflowId, ct);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        onboarding.MapGet("/by-key/{creationKey}", async (
            string creationKey,
            EventOnboardingWorkflowService service,
            CancellationToken ct) =>
        {
            var workflow = await service.GetByCreationKeyAsync(creationKey, ct);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        onboarding.MapPost("/{workflowId:guid}/resume", async (
            Guid workflowId,
            EventOnboardingWorkflowService service,
            CancellationToken ct) => Results.Ok(await service.ResumeAsync(workflowId, ct)));

        onboarding.MapPost("/{workflowId:guid}/compensate", async (
            Guid workflowId,
            EventOnboardingWorkflowService service,
            CancellationToken ct) => Results.Ok(await service.CompensateAsync(workflowId, ct)));

        app.MapGet("/workflows/event-cancellations/{workflowId:guid}", async (
            Guid workflowId,
            EventCancellationWorkflowService service,
            CancellationToken ct) =>
        {
            var workflow = await service.GetAsync(workflowId, ct);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        }).WithTags("Workflows").RequireAuthorization("RequireOrganizer");
    }
}
