namespace TicketFlow.Ticketing.Api.Services;

public sealed class TicketIssuanceReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    TicketIssuanceReconciliationOptions options,
    TimeProvider timeProvider,
    ILogger<TicketIssuanceReconciliationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Interval <= TimeSpan.Zero)
        {
            logger.LogInformation("[ticketing] issuance reconciliation is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var reconciliation = scope.ServiceProvider
                    .GetRequiredService<TicketIssuanceReconciliationService>();
                await reconciliation.ReconcileOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[ticketing] issuance reconciliation pass failed.");
            }

            try
            {
                await Task.Delay(options.Interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
