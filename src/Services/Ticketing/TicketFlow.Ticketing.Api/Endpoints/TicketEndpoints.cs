using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Api.Endpoints;

public sealed record TicketResponse(Guid Id, Guid OrderId, Guid SeatId, DateTimeOffset IssuedAt, DateTimeOffset? CheckedInAt);

public sealed record ValidateTicketRequest(string QrPayload);

public sealed record ValidateTicketResponse(string Status, Guid? TicketId, DateTimeOffset? CheckedInAt);

public static class TicketEndpoints
{
    public static void MapTicketEndpoints(this WebApplication app)
    {
        app.MapGet("/orders/{orderId:guid}/tickets", async (Guid orderId, ITicketRepository repository, CancellationToken ct) =>
        {
            var tickets = await repository.GetByOrderIdAsync(orderId, ct);
            return Results.Ok(tickets.Select(Map).ToList());
        }).WithTags("Tickets").RequireAuthorization("RequireAuthenticated");

        app.MapGet("/tickets/{id:guid}/pdf", async (Guid id, ITicketRepository repository, ITicketObjectStorage objectStorage, CancellationToken ct) =>
        {
            var ticket = await repository.GetByIdAsync(id, ct);
            if (ticket is null)
                return Results.NotFound();

            var pdfBytes = await objectStorage.DownloadAsync(ticket.PdfObjectKey, ct);
            return Results.File(pdfBytes, "application/pdf", $"ticket-{ticket.Id}.pdf");
        }).WithTags("Tickets").RequireAuthorization("RequireAuthenticated");

        // Gate-scanner endpoint (Phase 8 Ch12) — server-authoritative validate, no QR signing yet
        // (see ITicketPdfRenderer's doc comment / phase-5-plan.md backlog). RequireOrganizer
        // stands in for a dedicated "gate-staff" role until there's a real staffing model to
        // justify one (backlog, see docs/phase-8-plan.md mục 4).
        app.MapPost("/tickets/validate", async (
            ValidateTicketRequest request,
            ITicketRepository repository,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var ticket = await repository.GetByQrPayloadAsync(request.QrPayload, ct);
            if (ticket is null)
                return Results.Ok(new ValidateTicketResponse("NotFound", null, null));

            if (ticket.CheckedInAt is not null)
                return Results.Ok(new ValidateTicketResponse("AlreadyCheckedIn", ticket.Id, ticket.CheckedInAt));

            ticket.CheckIn(timeProvider.GetUtcNow());
            await unitOfWork.SaveChangesAsync(ct);

            return Results.Ok(new ValidateTicketResponse("Valid", ticket.Id, ticket.CheckedInAt));
        }).WithTags("Tickets").RequireAuthorization("RequireOrganizer");
    }

    private static TicketResponse Map(Ticket ticket) =>
        new(ticket.Id, ticket.OrderId, ticket.SeatId, ticket.IssuedAt, ticket.CheckedInAt);
}
