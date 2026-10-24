using System.Security.Claims;
using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Application.Tickets;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Api.Endpoints;

public sealed record TicketResponse(Guid Id, Guid OrderId, Guid SeatId, DateTimeOffset IssuedAt, DateTimeOffset? CheckedInAt);

/// <summary>
/// <see cref="EventId"/> is what the gate is scanning for. Required (Commit 10): without it the
/// server cannot tell a ticket for tonight from a ticket for next month, and a scanner pointed at
/// the wrong door would happily admit both.
/// </summary>
public sealed record ValidateTicketRequest(string QrPayload, Guid EventId, string GateId);

public sealed record ValidateTicketResponse(string Status, Guid? TicketId, DateTimeOffset? CheckedInAt);

public sealed record GateAssignmentResponse(
    Guid EventId,
    string GateId,
    Guid UserId,
    Guid AssignedBy,
    DateTimeOffset AssignedAt);

public static class TicketEndpoints
{
    public static void MapTicketEndpoints(this WebApplication app)
    {
        // Owner-scoped (Commit 10). Before this, any authenticated user could list any order's
        // tickets by guessing an order id — the endpoint never asked whose order it was.
        app.MapGet("/orders/{orderId:guid}/tickets", async (
            Guid orderId,
            ClaimsPrincipal user,
            ITicketRepository repository,
            TicketAccessPolicy accessPolicy,
            CancellationToken ct) =>
        {
            var tickets = await repository.GetByOrderIdAsync(orderId, ct);
            if (tickets.Count == 0)
                return Results.NotFound();

            // 404, not 403: a 403 would confirm the order exists and belongs to someone else,
            // which is itself a leak. Same choice as Ordering's own by-id endpoint (Phase 8).
            var callerId = Subject(user);
            if (tickets.Any(ticket => !accessPolicy.CanAccess(ticket, callerId, user.IsInRole("admin"))))
                return Results.NotFound();

            return Results.Ok(tickets.Select(Map).ToList());
        }).WithTags("Tickets").RequireAuthorization("RequireAuthenticated");

        app.MapGet("/tickets/{id:guid}/pdf", async (
            Guid id,
            ClaimsPrincipal user,
            ITicketRepository repository,
            ITicketObjectStorage objectStorage,
            TicketAccessPolicy accessPolicy,
            CancellationToken ct) =>
        {
            var ticket = await repository.GetByIdAsync(id, ct);
            if (ticket is null ||
                !accessPolicy.CanAccess(ticket, Subject(user), user.IsInRole("admin")))
                return Results.NotFound();

            var pdfBytes = await objectStorage.DownloadAsync(ticket.PdfObjectKey, ct);
            return Results.File(pdfBytes, "application/pdf", $"ticket-{ticket.Id}.pdf");
        }).WithTags("Tickets").RequireAuthorization("RequireAuthenticated");

        app.MapGet("/events/{eventId:guid}/gates/{gateId}/assignments", async (
            Guid eventId,
            string gateId,
            ClaimsPrincipal user,
            IGateAssignmentRepository assignments,
            GateAccessPolicy gateAccess,
            CancellationToken ct) =>
        {
            if (!await gateAccess.CanManageAssignmentsAsync(
                    Subject(user), user.IsInRole("admin"), eventId, ct))
                return Results.NotFound();

            if (!TryNormalizeGateId(gateId, out var normalizedGateId))
                return Results.BadRequest(new { error = "A valid gateId is required." });

            var rows = await assignments.ListAsync(eventId, normalizedGateId, ct);
            return Results.Ok(rows.Select(Map).ToList());
        }).WithTags("Gate assignments").RequireAuthorization("RequireOrganizer");

        app.MapPut("/events/{eventId:guid}/gates/{gateId}/assignments/{userId:guid}", async (
            Guid eventId,
            string gateId,
            Guid userId,
            ClaimsPrincipal user,
            IGateAssignmentRepository assignments,
            GateAccessPolicy gateAccess,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var callerId = Subject(user);
            if (!await gateAccess.CanManageAssignmentsAsync(
                    callerId, user.IsInRole("admin"), eventId, ct))
                return Results.NotFound();

            if (callerId is null || userId == Guid.Empty ||
                !TryNormalizeGateId(gateId, out var normalizedGateId))
                return Results.BadRequest(new { error = "Valid caller, userId and gateId values are required." });

            var existing = await assignments.GetAsync(eventId, normalizedGateId, userId, ct);
            if (existing is not null)
                return Results.Ok(Map(existing));

            var assignment = GateAssignment.Create(
                eventId,
                normalizedGateId,
                userId,
                callerId.Value,
                timeProvider.GetUtcNow());
            assignments.Add(assignment);
            await assignments.SaveChangesAsync(ct);
            return Results.Created(
                $"/events/{eventId}/gates/{Uri.EscapeDataString(normalizedGateId)}/assignments/{userId}",
                Map(assignment));
        }).WithTags("Gate assignments").RequireAuthorization("RequireOrganizer");

        app.MapDelete("/events/{eventId:guid}/gates/{gateId}/assignments/{userId:guid}", async (
            Guid eventId,
            string gateId,
            Guid userId,
            ClaimsPrincipal user,
            IGateAssignmentRepository assignments,
            GateAccessPolicy gateAccess,
            CancellationToken ct) =>
        {
            if (!await gateAccess.CanManageAssignmentsAsync(
                    Subject(user), user.IsInRole("admin"), eventId, ct))
                return Results.NotFound();

            if (userId == Guid.Empty || !TryNormalizeGateId(gateId, out var normalizedGateId))
                return Results.BadRequest(new { error = "Valid userId and gateId values are required." });

            var assignment = await assignments.GetAsync(eventId, normalizedGateId, userId, ct);
            if (assignment is not null)
            {
                assignments.Remove(assignment);
                await assignments.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        }).WithTags("Gate assignments").RequireAuthorization("RequireOrganizer");

        // Gate-scanner endpoint. A gate-staff token only reaches the endpoint; the persisted
        // event + gate assignment below is the authorization decision.
        app.MapPost("/tickets/validate", async (
            ValidateTicketRequest request,
            ClaimsPrincipal user,
            ITicketRepository repository,
            GateAccessPolicy gateAccess,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.QrPayload) ||
                request.EventId == Guid.Empty ||
                !TryNormalizeGateId(request.GateId, out var normalizedGateId))
                return Results.BadRequest(new { error = "qrPayload, eventId and gateId are required." });

            // Commit 14: holding the organizer role is not the same as running THIS event's gate.
            // Until now any organizer in the realm could check in — and, worse, burn — tickets for
            // an event they had nothing to do with. 403 rather than a scan outcome: this is the
            // scanner being pointed at the wrong tenant, not a verdict about the ticket, and
            // reporting it as "invalid" would send staff looking at the customer instead.
            if (!await IsGateAuthorizedAsync(
                    user, request.EventId, normalizedGateId, gateAccess, ct))
            {
                return Results.Json(
                    new { error = "This account is not assigned to this event gate." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var ticket = await repository.GetByQrPayloadAsync(request.QrPayload, ct);
            if (ticket is null)
                return Results.Ok(new ValidateTicketResponse(nameof(CheckInOutcome.NotFound), null, null));

            // Evaluated first so a refused scan carries a specific reason — "revoked" and "wrong
            // event" are different conversations at the gate than "already used".
            var evaluation = ticket.EvaluateScan(request.EventId);
            if (evaluation.Outcome != CheckInOutcome.Valid)
                return Results.Ok(new ValidateTicketResponse(
                    evaluation.Outcome.ToString(), evaluation.TicketId, evaluation.CheckedInAt));

            // The conditional UPDATE is the actual decision; everything above is only a fast path
            // for a better message. Losing this race means another scanner claimed the ticket in
            // between, so re-read to report exactly which condition it now fails.
            var now = timeProvider.GetUtcNow();
            if (await repository.TryClaimCheckInAsync(ticket.Id, request.EventId, now, ct))
                return Results.Ok(new ValidateTicketResponse(nameof(CheckInOutcome.Valid), ticket.Id, now));

            var loser = await repository.GetByIdAsync(ticket.Id, ct);
            var reason = loser?.EvaluateScan(request.EventId) ?? new CheckInResult(CheckInOutcome.NotFound);
            return Results.Ok(new ValidateTicketResponse(
                // Never report Valid off a claim that failed, whatever the re-read says.
                reason.Outcome == CheckInOutcome.Valid
                    ? nameof(CheckInOutcome.AlreadyCheckedIn)
                    : reason.Outcome.ToString(),
                reason.TicketId,
                reason.CheckedInAt));
        }).WithTags("Tickets").RequireAuthorization("RequireGateScanner");
    }

    private static Guid? Subject(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var callerId) ? callerId : null;

    /// <summary>
    /// Translates the token into the two facts <see cref="GateAccessPolicy"/> needs. The decision
    /// itself lives in Application so it can be tested without an HTTP fixture.
    /// </summary>
    private static Task<bool> IsGateAuthorizedAsync(
        ClaimsPrincipal user,
        Guid eventId,
        string gateId,
        GateAccessPolicy gateAccess,
        CancellationToken cancellationToken) =>
        gateAccess.CanScanAsync(
            Subject(user),
            user.IsInRole("admin"),
            user.IsInRole("organizer"),
            user.IsInRole("gate-staff"),
            eventId,
            gateId,
            cancellationToken);

    private static TicketResponse Map(Ticket ticket) =>
        new(ticket.Id, ticket.OrderId, ticket.SeatId, ticket.IssuedAt, ticket.CheckedInAt);

    private static GateAssignmentResponse Map(GateAssignment assignment) =>
        new(
            assignment.EventId,
            assignment.GateId,
            assignment.UserId,
            assignment.AssignedBy,
            assignment.AssignedAt);

    private static bool TryNormalizeGateId(string? gateId, out string normalizedGateId)
    {
        try
        {
            normalizedGateId = GateAssignment.NormalizeGateId(gateId!);
            return true;
        }
        catch (ArgumentException)
        {
            normalizedGateId = string.Empty;
            return false;
        }
    }
}
