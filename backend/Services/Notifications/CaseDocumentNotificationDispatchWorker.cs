namespace PayPlanner.Api.Services.Notifications;

public sealed class CaseDocumentNotificationDispatchWorker(IServiceScopeFactory scopes,
    ILogger<CaseDocumentNotificationDispatchWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CaseDocumentNotificationDispatchProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Case document notification delivery will be retried"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
