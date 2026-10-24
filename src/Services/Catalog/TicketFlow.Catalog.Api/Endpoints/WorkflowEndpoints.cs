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
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });

            try
            {
                var workflow = await service.StartAsync(request, caller, ct);
                return workflow.Status == "Completed"
                    ? Results.Ok(workflow)
                    : Results.Accepted($"/catalog/workflows/event-onboarding/{workflow.WorkflowId}", workflow);
            }
            catch (WorkflowOwnershipException)
            {
                return Results.NotFound();
            }
        });

        onboarding.MapGet("/{workflowId:guid}", async (
            Guid workflowId,
            ClaimsPrincipal user,
            EventOnboardingWorkflowService service,
            CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });
            try
            {
                var workflow = await service.GetAsync(workflowId, caller, ct);
                return workflow is null ? Results.NotFound() : Results.Ok(workflow);
            }
            catch (WorkflowOwnershipException)
            {
                return Results.NotFound();
            }
        });

        onboarding.MapGet("/by-key/{creationKey}", async (
            string creationKey,
            ClaimsPrincipal user,
            EventOnboardingWorkflowService service,
            CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });
            try
            {
                var workflow = await service.GetByCreationKeyAsync(creationKey, caller, ct);
                return workflow is null ? Results.NotFound() : Results.Ok(workflow);
            }
            catch (WorkflowOwnershipException)
            {
                return Results.NotFound();
            }
        });

        onboarding.MapPost("/{workflowId:guid}/resume", async (
            Guid workflowId,
            ClaimsPrincipal user,
            EventOnboardingWorkflowService service,
            CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });
            try
            {
                return Results.Ok(await service.ResumeAsync(workflowId, caller, ct));
            }
            catch (WorkflowOwnershipException)
            {
                return Results.NotFound();
            }
        });

        onboarding.MapPost("/{workflowId:guid}/compensate", async (
            Guid workflowId,
            ClaimsPrincipal user,
            EventOnboardingWorkflowService service,
            CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });
            try
            {
                return Results.Ok(await service.CompensateAsync(workflowId, caller, ct));
            }
            catch (WorkflowOwnershipException)
            {
                return Results.NotFound();
            }
        });

        app.MapGet("/workflows/event-cancellations/{workflowId:guid}", async (
            Guid workflowId,
            ClaimsPrincipal user,
            EventCancellationWorkflowService service,
            CancellationToken ct) =>
        {
            if (!CatalogCallerFactory.TryCreate(user, out var caller))
                return Results.BadRequest(new { error = "Authenticated subject is not a valid user id." });
            try
            {
                var workflow = await service.GetAsync(workflowId, caller, ct);
                return workflow is null ? Results.NotFound() : Results.Ok(workflow);
            }
            catch (WorkflowOwnershipException)
            {
                return Results.NotFound();
            }
        }).WithTags("Workflows").RequireAuthorization("RequireOrganizer");
    }
}
