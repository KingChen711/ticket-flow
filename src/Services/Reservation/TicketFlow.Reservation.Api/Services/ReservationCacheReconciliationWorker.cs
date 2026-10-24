using TicketFlow.Reservation.Infrastructure.Reconciliation;

namespace TicketFlow.Reservation.Api.Services;

/// <summary>
/// Periodically applies the operator-selected reconciliation mode. Report remains observational;
/// mutating behavior only occurs when RepairCache or QuarantineConflict is explicitly configured.
/// </summary>
public sealed class ReservationCacheReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ReservationCacheReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue(
            "Reservation:CacheReconcileIntervalSeconds",
            60);
        if (intervalSeconds <= 0)
        {
            logger.LogInformation("Periodic reservation cache reconciliation is disabled.");
            return;
        }

        var modeName = configuration["Reservation:CacheReconcileMode"] ?? "RepairCache";
        if (!Enum.TryParse<SeatCacheReconcileMode>(modeName, ignoreCase: true, out var mode))
        {
            logger.LogCritical(
                "Periodic reservation reconciliation is disabled because mode {Mode} is invalid.",
                modeName);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var bootstrapper = scope.ServiceProvider.GetRequiredService<ISeatCacheBootstrapper>();
                var results = await bootstrapper.ReconcileAllAsync(mode, stoppingToken);
                var unresolved = results
                    .Select(result => new
                    {
                        Result = result,
                        Drifts = result.RemainingDrifts
                            .Where(drift =>
                                mode != SeatCacheReconcileMode.QuarantineConflict ||
                                !result.QuarantinedSeatIds.Contains(drift.SeatId))
                            .ToArray(),
                    })
                    .Where(item => item.Drifts.Length > 0)
                    .ToArray();
                if (unresolved.Length > 0)
                {
                    var logLevel = mode == SeatCacheReconcileMode.Report
                        ? LogLevel.Warning
                        : LogLevel.Critical;
                    logger.Log(
                        logLevel,
                        "Periodic reservation reconciliation ({Mode}) found {DriftCount} unresolved drift(s) across {EventCount} event(s).",
                        mode,
                        unresolved.Sum(item => item.Drifts.Length),
                        unresolved.Length);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Periodic reservation cache reconciliation failed.");
            }
        }
    }
}
