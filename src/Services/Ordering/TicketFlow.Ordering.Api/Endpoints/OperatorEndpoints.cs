using System.Security.Claims;
using Marten;
using MassTransit;
using TicketFlow.Ordering.Application.Sagas;

namespace TicketFlow.Ordering.Api.Endpoints;

public sealed record StuckOrderView(
    Guid OrderId,
    string State,
    DateTimeOffset SubmittedAt,
    int DeadlineStrikes,
    DateTimeOffset? ManualReviewAt,
    string? ManualReviewReason,
    string? CancellationReason,
    DateTimeOffset? SeatsConfirmedAt,
    DateTimeOffset? PaymentAuthorizedAt,
    DateTimeOffset? TicketsIssuedAt,
    Guid? PaymentId);

/// <summary>
/// The minimum an operator needs when a purchase stops moving (Commit 9): see which orders are
/// stuck and why, inspect one order's recorded history, and re-poke a specific order.
/// </summary>
/// <remarks>
/// Admin-only, and deliberately narrow. There is no "force complete" and no "force cancel" here:
/// both would let an operator assert an outcome the participants never confirmed, which is exactly
/// the class of bug Phase 12 exists to remove. The one write operation re-sends a command the saga
/// already owes — every such command is idempotent (Commits 5-7), so the worst case is a no-op.
/// </remarks>
public static class OperatorEndpoints
{
    public static void MapOperatorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/ops/orders").WithTags("Operations").RequireAuthorization("RequireAdmin");

        // Everything that automation has stopped driving, plus everything that has taken at least
        // one strike — the second group is the early warning for the first.
        group.MapGet("/stuck", async (IQuerySession session, CancellationToken ct) =>
        {
            var stuck = await session.Query<PurchaseState>()
                .Where(state => state.ManualReviewAt != null || state.DeadlineStrikes > 0)
                .OrderBy(state => state.SubmittedAt)
                .Take(200)
                .ToListAsync(ct);

            return Results.Ok(stuck.Select(Map));
        });

        group.MapGet("/{orderId:guid}", async (Guid orderId, IQuerySession session, CancellationToken ct) =>
        {
            var state = await session.LoadAsync<PurchaseState>(orderId, ct);
            return state is null ? Results.NotFound() : Results.Ok(Map(state));
        });

        // Puts an order back under automation: clears manual review, resets the strike count and
        // re-sends whatever command the saga is owed. Works for a ManualReview order, which is the
        // case an operator actually reaches for — a retry that only re-armed a deadline would be
        // useless there, since a ManualReview order is by definition no longer waiting on one.
        group.MapPost("/{orderId:guid}/resume", async (
            Guid orderId,
            ClaimsPrincipal user,
            IQuerySession session,
            IPublishEndpoint publisher,
            CancellationToken ct) =>
        {
            var state = await session.LoadAsync<PurchaseState>(orderId, ct);
            if (state is null)
                return Results.NotFound();

            if (state.CompletedAt is not null || state.CancelledAt is not null)
                return Results.Conflict(new { error = "This order already reached a final outcome." });

            await publisher.Publish(
                new ResumePurchase(orderId, user.FindFirstValue("sub") ?? "unknown"), ct);

            return Results.Accepted($"/ops/orders/{orderId}");
        });
    }

    private static StuckOrderView Map(PurchaseState state) => new(
        state.CorrelationId,
        state.CurrentState,
        state.SubmittedAt,
        state.DeadlineStrikes,
        state.ManualReviewAt,
        state.ManualReviewReason,
        state.CancellationReason,
        state.SeatsConfirmedAt,
        state.PaymentAuthorizedAt,
        state.TicketsIssuedAt,
        state.PaymentId);
}
