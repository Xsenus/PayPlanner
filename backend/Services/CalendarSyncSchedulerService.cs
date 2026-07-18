using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class CalendarSyncSchedulerService : BackgroundService
{
    public const string EnabledSettingKey = "calendarSync.scheduler.enabled";
    public const string IntervalMinutesSettingKey = "calendarSync.scheduler.intervalMinutes";
    public const string BatchSizeSettingKey = "calendarSync.scheduler.batchSize";
    public const string LookAheadDaysSettingKey = "calendarSync.scheduler.lookAheadDays";

    public const int DefaultIntervalMinutes = 30;
    public const int DefaultBatchSize = 100;
    public const int DefaultLookAheadDays = 90;

    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;
    private readonly ILogger<CalendarSyncSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public CalendarSyncSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
        ILogger<CalendarSyncSchedulerService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _featureFlags = featureFlags;
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
            var settings = await ResolveSchedulerSettingsAsync(stoppingToken);
            if (settings.Enabled)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "External calendar sync scheduler failed.");
                    if (_jobRunState is not null)
                    {
                        await _jobRunState.RecordFailureAsync(
                            SystemJobKeys.ExternalCalendarSync,
                            DateTime.UtcNow,
                            ex,
                            "Проверка внешней календарной синхронизации завершилась с ошибкой.",
                            stoppingToken);
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.IntervalMinutes), stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<CalendarSyncSchedulerResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var schedulerSettings = await ResolveSchedulerSettingsAsync(cancellationToken);
        if (!schedulerSettings.Enabled)
        {
            await RecordSuccessAsync(
                startedAt,
                0,
                0,
                "Фоновая проверка внешних календарей выключена в настройках.",
                cancellationToken);
            return new CalendarSyncSchedulerResult(false, false, 0, 0, "Фоновая проверка внешних календарей выключена.");
        }

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var state = await BuildStateAsync(schedulerSettings, cancellationToken);
            if (!state.FeatureEnabled)
            {
                await RecordSuccessAsync(
                    startedAt,
                    0,
                    state.BlockingRequiredItems,
                    "Внешняя календарная синхронизация не запускалась: контур выключен в админских настройках.",
                    cancellationToken);
                return new CalendarSyncSchedulerResult(true, false, 0, state.BlockingRequiredItems, "Внешние календари выключены в админских настройках.");
            }

            if (!state.Readiness.ReadyToConnect)
            {
                await RecordSuccessAsync(
                    startedAt,
                    0,
                    state.BlockingRequiredItems,
                    $"Внешние календари не готовы к запуску: не закрыто обязательных пунктов {state.BlockingRequiredItems}.",
                    cancellationToken);
                return new CalendarSyncSchedulerResult(true, false, 0, state.BlockingRequiredItems, "Не закрыты обязательные пункты готовности.");
            }

            await RecordSuccessAsync(
                startedAt,
                state.CandidateCount,
                0,
                state.CandidateCount > 0
                    ? $"Подготовительная проверка внешних календарей выполнена: найдено событий-кандидатов {state.CandidateCount}."
                    : "Подготовительная проверка внешних календарей выполнена, событий для синхронизации не найдено.",
                cancellationToken);
            return new CalendarSyncSchedulerResult(true, true, state.CandidateCount, 0, null);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<CalendarSyncSchedulerState> BuildDiagnosticsStateAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSchedulerSettingsAsync(cancellationToken);
        return await BuildStateAsync(settings, cancellationToken);
    }

    private async Task<CalendarSyncSchedulerState> BuildStateAsync(
        CalendarSyncSchedulerSettings schedulerSettings,
        CancellationToken cancellationToken)
    {
        var calendarSettings = await LoadCalendarSyncSettingsAsync(cancellationToken);
        var featureEnabled = calendarSettings.ExternalCalendarSyncEnabled == true;
        var readiness = CalendarSyncProviderRegistry.BuildReadiness(
            calendarSettings.GoogleCalendarSyncEnabled == true,
            calendarSettings.YandexCalendarSyncEnabled == true,
            calendarSettings);
        var candidateStatistics = schedulerSettings.Enabled && featureEnabled
            ? await LoadCandidateStatisticsAsync(schedulerSettings, cancellationToken)
            : new CalendarSyncCandidateStatistics(0, null);

        return new CalendarSyncSchedulerState(
            schedulerSettings.Enabled,
            featureEnabled,
            readiness,
            candidateStatistics.Count,
            candidateStatistics.OldestAt,
            schedulerSettings.IntervalMinutes,
            schedulerSettings.BatchSize,
            schedulerSettings.LookAheadDays);
    }

    private async Task<CalendarSyncCandidateStatistics> LoadCandidateStatisticsAsync(
        CalendarSyncSchedulerSettings settings,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var (fromUtc, toUtc) = BuildWindow(settings.LookAheadDays);

        var taskStatistics = await context.TaskJournalItems
            .AsNoTracking()
            .Where(item =>
                item.DueAt >= fromUtc &&
                item.DueAt <= toUtc &&
                item.Status != "done" &&
                item.Status != "cancelled")
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                OldestAt = group.Min(item => (DateTime?)item.DueAt),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var caseStatistics = await context.ClientCases
            .AsNoTracking()
            .Where(item =>
                (item.DueDate.HasValue && item.DueDate.Value >= fromUtc && item.DueDate.Value <= toUtc) ||
                (item.NextHearingAt.HasValue && item.NextHearingAt.Value >= fromUtc && item.NextHearingAt.Value <= toUtc))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                DeadlineCount = group.Count(item =>
                    item.DueDate.HasValue && item.DueDate.Value >= fromUtc && item.DueDate.Value <= toUtc),
                OldestDeadlineAt = group
                    .Where(item => item.DueDate.HasValue && item.DueDate.Value >= fromUtc && item.DueDate.Value <= toUtc)
                    .Min(item => item.DueDate),
                HearingCount = group.Count(item =>
                    item.NextHearingAt.HasValue && item.NextHearingAt.Value >= fromUtc && item.NextHearingAt.Value <= toUtc),
                OldestHearingAt = group
                    .Where(item => item.NextHearingAt.HasValue && item.NextHearingAt.Value >= fromUtc && item.NextHearingAt.Value <= toUtc)
                    .Min(item => item.NextHearingAt),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var candidateCount = Math.Min(
            (taskStatistics?.Count ?? 0) +
            (caseStatistics?.DeadlineCount ?? 0) +
            (caseStatistics?.HearingCount ?? 0),
            settings.BatchSize);
        var oldestAt = new[]
        {
            taskStatistics?.OldestAt,
            caseStatistics?.OldestDeadlineAt,
            caseStatistics?.OldestHearingAt,
        }
            .Where(item => item.HasValue)
            .Min();

        return new CalendarSyncCandidateStatistics(candidateCount, oldestAt);
    }

    private async Task<CalendarSyncSettingsDto> LoadCalendarSyncSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var setting = await context.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == CalendarSyncSettingsKey, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings(_featureFlags.CurrentValue.Legal.ExternalCalendarSync);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            if (settings is not null)
            {
                settings.UpdatedAt ??= setting.UpdatedAt;
                return CalendarSyncProviderRegistry.NormalizeSettings(settings, _featureFlags.CurrentValue.Legal.ExternalCalendarSync);
            }
        }
        catch (JsonException)
        {
            _logger.LogWarning("Stored calendar sync settings are invalid. Defaults will be used for scheduler diagnostics.");
        }

        var defaults = CalendarSyncProviderRegistry.BuildDefaultSettings(_featureFlags.CurrentValue.Legal.ExternalCalendarSync);
        defaults.UpdatedAt = setting.UpdatedAt;
        return defaults;
    }

    private async Task<CalendarSyncSchedulerSettings> ResolveSchedulerSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var settings = await context.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EnabledSettingKey ||
                item.Key == IntervalMinutesSettingKey ||
                item.Key == BatchSizeSettingKey ||
                item.Key == LookAheadDaysSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        return new CalendarSyncSchedulerSettings(
            ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("CalendarSync:Scheduler:Enabled", false)),
            NormalizeInterval(ParseInt(settings.GetValueOrDefault(IntervalMinutesSettingKey), _configuration.GetValue("CalendarSync:Scheduler:IntervalMinutes", DefaultIntervalMinutes))),
            NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("CalendarSync:Scheduler:BatchSize", DefaultBatchSize))),
            NormalizeLookAheadDays(ParseInt(settings.GetValueOrDefault(LookAheadDaysSettingKey), _configuration.GetValue("CalendarSync:Scheduler:LookAheadDays", DefaultLookAheadDays))));
    }

    private async Task RecordSuccessAsync(
        DateTime startedAt,
        int processedCount,
        int skippedCount,
        string summary,
        CancellationToken cancellationToken)
    {
        if (_jobRunState is not null)
        {
            await _jobRunState.RecordSuccessAsync(
                SystemJobKeys.ExternalCalendarSync,
                startedAt,
                processedCount,
                skippedCount,
                summary,
                cancellationToken);
        }
    }

    private static (DateTime FromUtc, DateTime ToUtc) BuildWindow(int lookAheadDays)
    {
        var now = DateTime.UtcNow;
        return (now.AddDays(-1), now.AddDays(lookAheadDays));
    }

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    public static int NormalizeInterval(int value) => Math.Clamp(value, 5, 1440);

    public static int NormalizeBatchSize(int value) => Math.Clamp(value, 1, 500);

    public static int NormalizeLookAheadDays(int value) => Math.Clamp(value, 1, 365);

    private readonly record struct CalendarSyncSchedulerSettings(
        bool Enabled,
        int IntervalMinutes,
        int BatchSize,
        int LookAheadDays);

    private readonly record struct CalendarSyncCandidateStatistics(
        int Count,
        DateTime? OldestAt);
}

public sealed record CalendarSyncSchedulerResult(
    bool Enabled,
    bool ReadyToConnect,
    int ProcessedCount,
    int SkippedCount,
    string? Message);

public sealed record CalendarSyncSchedulerState(
    bool SchedulerEnabled,
    bool FeatureEnabled,
    CalendarSyncReadinessDto Readiness,
    int CandidateCount,
    DateTime? OldestCandidateAt,
    int IntervalMinutes,
    int BatchSize,
    int LookAheadDays)
{
    public int BlockingRequiredItems => Readiness.Items.Count(item => item.IsRequired && !item.IsReady);
}
