using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Services.UserActivity;

public sealed class UserActivityRetentionService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserActivityRetentionService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;

    public UserActivityRetentionService(
        IServiceScopeFactory scopeFactory,
        ILogger<UserActivityRetentionService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jobRunState = jobRunState;
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
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UserActivityRetentionService tick failed.");
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordFailureAsync(
                        SystemJobKeys.UserActivityRetention,
                        DateTime.UtcNow,
                        ex,
                        "Очистка журнала действий завершилась с ошибкой.",
                        stoppingToken);
                }
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

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var deleted = await TickAsync(cancellationToken);
        if (_jobRunState is not null)
        {
            await _jobRunState.RecordSuccessAsync(
                SystemJobKeys.UserActivityRetention,
                startedAt,
                deleted,
                null,
                deleted > 0
                    ? $"Удалено устаревших записей журнала действий: {deleted}."
                    : "Проверка срока хранения журнала действий выполнена, удаление не требовалось.",
                cancellationToken);
        }

        return deleted;
    }

    private async Task<int> TickAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var activityService = scope.ServiceProvider.GetRequiredService<IUserActivityService>();
        var sqliteMaintenanceService = scope.ServiceProvider.GetRequiredService<ISqliteMaintenanceService>();
        var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PaymentContext>>();

        var settings = await activityService.GetSettingsAsync(cancellationToken);
        if (!settings.RetentionDays.HasValue)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays.Value);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await context.UserActivityLogs
            .Where(x => x.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted <= 0)
        {
            return 0;
        }

        await sqliteMaintenanceService.CheckpointAsync(cancellationToken);
        _logger.LogInformation(
            "Auto-cleaned {Deleted} user activity log entries older than {CutoffUtc} (retention {RetentionDays} days).",
            deleted,
            cutoff,
            settings.RetentionDays.Value);
        return deleted;
    }
}
