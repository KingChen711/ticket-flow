using TicketFlow.Ticketing.Application.Abstractions;
using TicketFlow.Ticketing.Domain.Tickets;

namespace TicketFlow.Ticketing.Api.Services;

public sealed class TicketIssuanceReconciliationOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RecoverAfter { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RecoveryCooldown { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Allows a racing renderer to settle before terminal artifact cleanup begins.</summary>
    public TimeSpan TerminalCleanupDelay { get; set; } = TimeSpan.FromMinutes(10);

    public int BatchSize { get; set; } = 50;
}

/// <summary>
/// Re-enqueues stale durable issuance intents and removes object-store artifacts that have no
/// surviving ticket row after a terminal failure/cancellation.
/// </summary>
public sealed class TicketIssuanceReconciliationService(
    ITicketIssuanceJobRepository jobs,
    ITicketRepository tickets,
    ITicketObjectStorage objectStorage,
    ITicketIssuanceRecoveryPublisher publisher,
    IUnitOfWork unitOfWork,
    TicketIssuanceReconciliationOptions options,
    TimeProvider timeProvider,
    ILogger<TicketIssuanceReconciliationService> logger)
{
    public async Task ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var take = Math.Clamp(options.BatchSize, 1, 1_000);
        var recoverable = await jobs.ListRecoverableAsync(
            now - Positive(options.RecoverAfter, TimeSpan.FromMinutes(2)),
            now - Positive(options.RecoveryCooldown, TimeSpan.FromMinutes(2)),
            take,
            cancellationToken);

        foreach (var job in recoverable)
        {
            if (!job.HasRecoveryIntent)
            {
                // Repositories filter this in normal operation. Keep the guard because a legacy
                // or alternate repository must never publish a fabricated customer snapshot.
                logger.LogError(
                    "[ticketing] pending issuance {OrderId} has no complete recovery intent and needs manual review.",
                    job.OrderId);
                continue;
            }

            job.MarkRecoveryScheduled(now);
            await publisher.PublishAsync(
                new TicketIssuanceRecoveryRequest(
                    job.OrderId,
                    job.CustomerId,
                    job.EventId,
                    [.. job.ExpectedSeatIds],
                    job.CustomerName!,
                    job.CustomerEmail!,
                    job.OperationKey,
                    job.PriceSnapshotVersion),
                cancellationToken);
        }

        if (recoverable.Count > 0)
        {
            // Ticketing's scoped MassTransit bus outbox and these recovery markers share this
            // commit. A crash cannot persist "scheduled" without durably enqueuing the command.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[ticketing] scheduled recovery for {Count} stale issuance job(s).",
                recoverable.Count);
        }

        var terminal = await jobs.ListTerminalArtifactsAsync(
            now - Positive(options.TerminalCleanupDelay, TimeSpan.FromMinutes(10)),
            take,
            cancellationToken);
        foreach (var job in terminal)
            await ReconcileTerminalArtifactsAsync(job, now, cancellationToken);

        if (terminal.Count > 0)
        {
            // Deletes are idempotent. If this database commit is ambiguous, the next pass lists
            // the prefix again and safely repeats only the remaining work.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "[ticketing] reconciled artifacts for {Count} terminal issuance job(s).",
                terminal.Count);
        }
    }

    private async Task ReconcileTerminalArtifactsAsync(
        TicketIssuanceJob job,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var durableTickets = await tickets.GetByOrderIdAsync(job.OrderId, cancellationToken);
        var reason = job.FailureReason ?? "Ticket issuance was cancelled before completion.";
        foreach (var ticket in durableTickets)
            ticket.Revoke(reason, now);

        var referencedKeys = durableTickets
            .Select(ticket => ticket.PdfObjectKey)
            .ToHashSet(StringComparer.Ordinal);
        var prefix = $"{job.OrderId}/";
        var storedKeys = await objectStorage.ListKeysAsync(prefix, cancellationToken);
        var deleted = 0;
        foreach (var storedKey in storedKeys
                     .Where(key =>
                         key.StartsWith(prefix, StringComparison.Ordinal) &&
                         !referencedKeys.Contains(key))
                     .Distinct(StringComparer.Ordinal))
        {
            await objectStorage.DeleteAsync(storedKey, cancellationToken);
            deleted++;
        }

        job.MarkArtifactsReconciled(now);
        logger.LogInformation(
            "[ticketing] terminal issuance {OrderId}: revoked {TicketCount} durable ticket(s) and removed {ArtifactCount} orphan artifact(s).",
            job.OrderId,
            durableTickets.Count,
            deleted);
    }

    private static TimeSpan Positive(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;
}
