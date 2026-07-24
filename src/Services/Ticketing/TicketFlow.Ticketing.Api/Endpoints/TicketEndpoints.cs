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
public sealed record ValidateTicketRequest(string QrPayload, Guid EventId);

public sealed record ValidateTicketResponse(string Status, Guid? TicketId, DateTimeOffset? CheckedInAt);

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
            CancellationToken ct) =>
        {
            var tickets = await repository.GetByOrderIdAsync(orderId, ct);
            if (tickets.Count == 0)
                return Results.NotFound();

            // 404, not 403: a 403 would confirm the order exists and belongs to someone else,
            // which is itself a leak. Same choice as Ordering's own by-id endpoint (Phase 8).
            if (!CanAccess(tickets[0], user))
                return Results.NotFound();

            return Results.Ok(tickets.Select(Map).ToList());
        }).WithTags("Tickets").RequireAuthorization("RequireAuthenticated");

        app.MapGet("/tickets/{id:guid}/pdf", async (
            Guid id,
            ClaimsPrincipal user,
            ITicketRepository repository,
            ITicketObjectStorage objectStorage,
            CancellationToken ct) =>
        {
            var ticket = await repository.GetByIdAsync(id, ct);
            if (ticket is null || !CanAccess(ticket, user))
                return Results.NotFound();

            var pdfBytes = await objectStorage.DownloadAsync(ticket.PdfObjectKey, ct);
            return Results.File(pdfBytes, "application/pdf", $"ticket-{ticket.Id}.pdf");
        }).WithTags("Tickets").RequireAuthorization("RequireAuthenticated");

        // Gate-scanner endpoint. RequireOrganizer stands in for a dedicated "gate-staff" role until
        // there's a real staffing model to justify one (backlog, docs/phase-8-plan.md mục 4).
        app.MapPost("/tickets/validate", async (
            ValidateTicketRequest request,
            ClaimsPrincipal user,
            ITicketRepository repository,
            GateAccessPolicy gateAccess,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            // Commit 14: holding the organizer role is not the same as running THIS event's gate.
            // Until now any organizer in the realm could check in — and, worse, burn — tickets for
            // an event they had nothing to do with. 403 rather than a scan outcome: this is the
            // scanner being pointed at the wrong tenant, not a verdict about the ticket, and
            // reporting it as "invalid" would send staff looking at the customer instead.
            if (!await IsGateAuthorizedAsync(user, request.EventId, gateAccess, ct))
            {
                return Results.Json(
                    new { error = "This account is not the organizer of the event being scanned." },
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
        }).WithTags("Tickets").RequireAuthorization("RequireOrganizer");
    }

    /// <summary>
    /// A ticket with no recorded owner predates Commit 10; it stays readable by any authenticated
    /// caller rather than becoming permanently unreachable for the customer who bought it.
    /// </summary>
    private static bool CanAccess(Ticket ticket, ClaimsPrincipal user)
    {
        if (user.IsInRole("admin"))
            return true;

        if (ticket.CustomerId is null)
            return true;

        return Guid.TryParse(user.FindFirstValue("sub"), out var callerId) && ticket.CustomerId == callerId;
    }

    /// <summary>
    /// Translates the token into the two facts <see cref="GateAccessPolicy"/> needs. The decision
    /// itself lives in Application so it can be tested without an HTTP fixture.
    /// </summary>
    private static Task<bool> IsGateAuthorizedAsync(
        ClaimsPrincipal user,
        Guid eventId,
        GateAccessPolicy gateAccess,
        CancellationToken cancellationToken) =>
        gateAccess.CanScanAsync(
            Guid.TryParse(user.FindFirstValue("sub"), out var organizerId) ? organizerId : null,
            user.IsInRole("admin"),
            eventId,
            cancellationToken);

    private static TicketResponse Map(Ticket ticket) =>
        new(ticket.Id, ticket.OrderId, ticket.SeatId, ticket.IssuedAt, ticket.CheckedInAt);
}
