using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.UserActivity;

public sealed class UserActivityRetentionService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserActivityRetentionService> _logger;

    public UserActivityRetentionService(
        IServiceScopeFactory scopeFactory,
        ILogger<UserActivityRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("UserActivityRetentionService started.");

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserActivityRetentionService tick failed.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }

        _logger.LogInformation("UserActivityRetentionService stopped.");
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var activityService = scope.ServiceProvider.GetRequiredService<IUserActivityService>();
        var sqliteMaintenanceService = scope.ServiceProvider.GetRequiredService<ISqliteMaintenanceService>();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PaymentContext>>();

        var settings = await activityService.GetSettingsAsync(cancellationToken);
        if (!settings.RetentionDays.HasValue)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays.Value);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await context.UserActivityLogs
            .Where(x => x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted <= 0)
        {
            return;
        }

        await sqliteMaintenanceService.CheckpointAsync(cancellationToken);
        _logger.LogInformation(
            "Auto-cleaned {Deleted} user activity log entries older than {CutoffUtc} (retention {RetentionDays} days).",
            deleted,
            cutoff,
            settings.RetentionDays.Value);
    }
}
