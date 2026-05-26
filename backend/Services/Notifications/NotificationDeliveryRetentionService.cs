using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Notifications;

public sealed class NotificationDeliveryRetentionService : BackgroundService
{
    public const string EnabledSettingKey = "notifications.deliveryRetention.enabled";
    public const string RetentionDaysSettingKey = "notifications.deliveryRetention.retentionDays";
    public const string IntervalHoursSettingKey = "notifications.deliveryRetention.intervalHours";
    public const string BatchSizeSettingKey = "notifications.deliveryRetention.batchSize";

    public const int DefaultRetentionDays = 365;
    public const int DefaultIntervalHours = 24;
    public const int DefaultBatchSize = 500;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationDeliveryRetentionService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public NotificationDeliveryRetentionService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<NotificationDeliveryRetentionService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
        _jobRunState = jobRunState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            var settings = await ResolveSettingsAsync(stoppingToken);
            if (settings.Enabled)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Notification delivery retention job failed.");
                    if (_jobRunState is not null)
                    {
                        await _jobRunState.RecordFailureAsync(
                            SystemJobKeys.NotificationDeliveryRetention,
                            DateTime.UtcNow,
                            ex,
                            "Очистка технических логов уведомлений завершилась с ошибкой.",
                            stoppingToken);
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(settings.IntervalHours), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<NotificationDeliveryRetentionResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            await RecordSuccessAsync(
                startedAt,
                0,
                "Очистка технических логов уведомлений выключена в настройках.",
                cancellationToken);
            return new NotificationDeliveryRetentionResult(0, false);
        }

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var deleted = await DeleteExpiredDeliveriesAsync(settings, cancellationToken);
            await RecordSuccessAsync(
                startedAt,
                deleted,
                deleted > 0
                    ? $"Удалено устаревших технических записей доставки уведомлений: {deleted}."
                    : "Проверка срока хранения технических логов уведомлений выполнена, удаление не требовалось.",
                cancellationToken);
            return new NotificationDeliveryRetentionResult(deleted, true);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<int> CountEligibleDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays);
        return await BuildExpiredDeliveryQuery(context, cutoff)
            .CountAsync(cancellationToken);
    }

    private async Task<int> DeleteExpiredDeliveriesAsync(
        NotificationDeliveryRetentionSettings settings,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var sqliteMaintenanceService = scope.ServiceProvider.GetService<ISqliteMaintenanceService>();
        var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays);
        var ids = await BuildExpiredDeliveryQuery(context, cutoff)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0)
        {
            return 0;
        }

        var deleted = await context.NotificationDeliveries
            .Where(item => ids.Contains(item.Id))
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            await (sqliteMaintenanceService?.CheckpointAsync(cancellationToken) ?? Task.CompletedTask);
            _logger.LogInformation(
                "Deleted {Deleted} notification delivery technical logs older than {CutoffUtc} (retention {RetentionDays} days).",
                deleted,
                cutoff,
                settings.RetentionDays);
        }

        return deleted;
    }

    private async Task<NotificationDeliveryRetentionSettings> ResolveSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var settings = await context.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EnabledSettingKey ||
                item.Key == RetentionDaysSettingKey ||
                item.Key == IntervalHoursSettingKey ||
                item.Key == BatchSizeSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        return new NotificationDeliveryRetentionSettings(
            ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("Notifications:DeliveryRetention:Enabled", true)),
            NormalizeRetentionDays(ParseInt(settings.GetValueOrDefault(RetentionDaysSettingKey), _configuration.GetValue("Notifications:DeliveryRetention:RetentionDays", DefaultRetentionDays))),
            NormalizeIntervalHours(ParseInt(settings.GetValueOrDefault(IntervalHoursSettingKey), _configuration.GetValue("Notifications:DeliveryRetention:IntervalHours", DefaultIntervalHours))),
            NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("Notifications:DeliveryRetention:BatchSize", DefaultBatchSize))));
    }

    private async Task RecordSuccessAsync(
        DateTime startedAt,
        int deletedCount,
        string summary,
        CancellationToken cancellationToken)
    {
        if (_jobRunState is not null)
        {
            await _jobRunState.RecordSuccessAsync(
                SystemJobKeys.NotificationDeliveryRetention,
                startedAt,
                deletedCount,
                null,
                summary,
                cancellationToken);
        }
    }

    private static IQueryable<Models.NotificationDelivery> BuildExpiredDeliveryQuery(PaymentContext context, DateTime cutoff)
        => context.NotificationDeliveries
            .AsNoTracking()
            .Where(item =>
                item.CreatedAt < cutoff &&
                item.Status != NotificationDeliveryStatus.Pending);

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    public static int NormalizeRetentionDays(int value) => Math.Clamp(value, 30, 3650);

    public static int NormalizeIntervalHours(int value) => Math.Clamp(value, 1, 168);

    public static int NormalizeBatchSize(int value) => Math.Clamp(value, 1, 500);

    private readonly record struct NotificationDeliveryRetentionSettings(
        bool Enabled,
        int RetentionDays,
        int IntervalHours,
        int BatchSize);
}

public sealed record NotificationDeliveryRetentionResult(int DeletedCount, bool Enabled);
