using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services;

public sealed class CalendarSyncLogRetentionService : BackgroundService
{
    public const string EnabledSettingKey = "calendar.syncLogRetention.enabled";
    public const string RetentionDaysSettingKey = "calendar.syncLogRetention.retentionDays";
    public const string IntervalHoursSettingKey = "calendar.syncLogRetention.intervalHours";
    public const string BatchSizeSettingKey = "calendar.syncLogRetention.batchSize";

    public const int DefaultRetentionDays = 90;
    public const int DefaultIntervalHours = 24;
    public const int DefaultBatchSize = 5000;

    private const int DeleteChunkSize = 500;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CalendarSyncLogRetentionService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public CalendarSyncLogRetentionService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<CalendarSyncLogRetentionService> logger,
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
            var settings = new CalendarSyncLogRetentionSettings(true, DefaultRetentionDays, DefaultIntervalHours, DefaultBatchSize);
            try
            {
                settings = await ResolveSettingsAsync(stoppingToken);
                if (settings.Enabled)
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Calendar sync log retention job failed.");
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordFailureAsync(
                        SystemJobKeys.CalendarSyncLogRetention,
                        DateTime.UtcNow,
                        ex,
                        "Очистка журналов синхронизации календарей завершилась с ошибкой.",
                        stoppingToken);
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

    public async Task<CalendarSyncLogRetentionResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            await RecordSuccessAsync(startedAt, 0, "Очистка журналов синхронизации календарей выключена в настройках.", cancellationToken);
            return new CalendarSyncLogRetentionResult(0, 0, false);
        }

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var result = await DeleteExpiredLogsAsync(settings, cancellationToken);
            await RecordSuccessAsync(
                startedAt,
                result.TotalDeletedCount,
                result.TotalDeletedCount > 0
                    ? $"Удалено устаревших записей синхронизации календарей: Google — {result.GoogleDeletedCount}, Яндекс — {result.YandexDeletedCount}."
                    : "Срок хранения журналов синхронизации календарей проверен, удаление не требовалось.",
                cancellationToken);
            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<CalendarSyncLogRetentionCounts> CountEligibleLogsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return new CalendarSyncLogRetentionCounts(0, 0);
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays);
        var googleCount = await context.GoogleCalendarSyncLogs.AsNoTracking().CountAsync(item => item.CreatedAt < cutoff, cancellationToken);
        var yandexCount = await context.YandexCalendarSyncLogs.AsNoTracking().CountAsync(item => item.CreatedAt < cutoff, cancellationToken);
        return new CalendarSyncLogRetentionCounts(googleCount, yandexCount);
    }

    private async Task<CalendarSyncLogRetentionResult> DeleteExpiredLogsAsync(
        CalendarSyncLogRetentionSettings settings,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var sqliteMaintenanceService = scope.ServiceProvider.GetService<ISqliteMaintenanceService>();
        var cutoff = DateTime.UtcNow.AddDays(-settings.RetentionDays);

        var googleIds = await context.GoogleCalendarSyncLogs
            .AsNoTracking()
            .Where(item => item.CreatedAt < cutoff)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);
        var yandexIds = await context.YandexCalendarSyncLogs
            .AsNoTracking()
            .Where(item => item.CreatedAt < cutoff)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Select(item => item.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        var googleDeleted = await DeleteGoogleLogsAsync(context, googleIds, cancellationToken);
        var yandexDeleted = await DeleteYandexLogsAsync(context, yandexIds, cancellationToken);
        if (googleDeleted + yandexDeleted > 0)
        {
            await (sqliteMaintenanceService?.CheckpointAsync(cancellationToken) ?? Task.CompletedTask);
            _logger.LogInformation(
                "Deleted calendar sync technical logs older than {CutoffUtc}: Google {GoogleDeleted}, Yandex {YandexDeleted} (retention {RetentionDays} days).",
                cutoff,
                googleDeleted,
                yandexDeleted,
                settings.RetentionDays);
        }

        return new CalendarSyncLogRetentionResult(googleDeleted, yandexDeleted, true);
    }

    private static async Task<int> DeleteGoogleLogsAsync(PaymentContext context, IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var chunk in ids.Chunk(DeleteChunkSize))
        {
            deleted += await context.GoogleCalendarSyncLogs.Where(item => chunk.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        return deleted;
    }

    private static async Task<int> DeleteYandexLogsAsync(PaymentContext context, IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var deleted = 0;
        foreach (var chunk in ids.Chunk(DeleteChunkSize))
        {
            deleted += await context.YandexCalendarSyncLogs.Where(item => chunk.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        return deleted;
    }

    private async Task<CalendarSyncLogRetentionSettings> ResolveSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var settings = await context.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == EnabledSettingKey || item.Key == RetentionDaysSettingKey || item.Key == IntervalHoursSettingKey || item.Key == BatchSizeSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        return new CalendarSyncLogRetentionSettings(
            ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("CalendarSync:LogRetention:Enabled", true)),
            NormalizeRetentionDays(ParseInt(settings.GetValueOrDefault(RetentionDaysSettingKey), _configuration.GetValue("CalendarSync:LogRetention:RetentionDays", DefaultRetentionDays))),
            NormalizeIntervalHours(ParseInt(settings.GetValueOrDefault(IntervalHoursSettingKey), _configuration.GetValue("CalendarSync:LogRetention:IntervalHours", DefaultIntervalHours))),
            NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("CalendarSync:LogRetention:BatchSize", DefaultBatchSize))));
    }

    private async Task RecordSuccessAsync(DateTime startedAt, int deletedCount, string summary, CancellationToken cancellationToken)
    {
        if (_jobRunState is not null)
        {
            await _jobRunState.RecordSuccessAsync(SystemJobKeys.CalendarSyncLogRetention, startedAt, deletedCount, null, summary, cancellationToken);
        }
    }

    private static bool ParseBool(string? value, bool fallback) => bool.TryParse(value, out var parsed) ? parsed : fallback;
    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;

    public static int NormalizeRetentionDays(int value) => Math.Clamp(value, 30, 3650);
    public static int NormalizeIntervalHours(int value) => Math.Clamp(value, 1, 168);
    public static int NormalizeBatchSize(int value) => Math.Clamp(value, 100, 10000);

    private readonly record struct CalendarSyncLogRetentionSettings(bool Enabled, int RetentionDays, int IntervalHours, int BatchSize);
}

public sealed record CalendarSyncLogRetentionCounts(int GoogleCount, int YandexCount)
{
    public int TotalCount => GoogleCount + YandexCount;
}

public sealed record CalendarSyncLogRetentionResult(int GoogleDeletedCount, int YandexDeletedCount, bool Enabled)
{
    public int TotalDeletedCount => GoogleDeletedCount + YandexDeletedCount;
}
