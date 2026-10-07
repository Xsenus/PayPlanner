using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Services.LegalIntegrations;

public sealed class ClientCaseCourtSyncSchedulerService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);
    public const string EnabledSettingKey = "legalIntegrations.courtSyncScheduler.enabled";
    public const string IntervalMinutesSettingKey = "legalIntegrations.courtSyncScheduler.intervalMinutes";
    public const string BatchSizeSettingKey = "legalIntegrations.courtSyncScheduler.batchSize";
    public const string CursorCaseIdSettingKey = "legalIntegrations.courtSyncScheduler.cursorCaseId";
    public const int DefaultIntervalMinutes = 360;
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 1440;
    public const int DefaultBatchSize = 25;
    public const int MinBatchSize = 1;
    public const int MaxBatchSize = 100;

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(DefaultIntervalMinutes);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AppFeatureFlagsOptions> _featureFlags;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientCaseCourtSyncSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;

    public ClientCaseCourtSyncSchedulerService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppFeatureFlagsOptions> featureFlags,
        IConfiguration configuration,
        ILogger<ClientCaseCourtSyncSchedulerService> logger,
        ISystemJobRunStateService? jobRunState = null)
    {
        _scopeFactory = scopeFactory;
        _featureFlags = featureFlags;
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
            var nextDelay = DefaultInterval;
            var startedAt = DateTime.UtcNow;
            try
            {
                var settings = await ResolveSettingsAsync(stoppingToken);
                nextDelay = TimeSpan.FromMinutes(settings.IntervalMinutes);
                if (settings.CanRun)
                {
                    var processed = await RunOnceAsync(settings.BatchSize, stoppingToken);
                    _logger.LogInformation("Automatic court source sync finished. Processed cases: {Count}.", processed);
                    if (_jobRunState is not null)
                    {
                        await _jobRunState.RecordSuccessAsync(
                            SystemJobKeys.CourtSync,
                            startedAt,
                            processed,
                            null,
                            processed > 0
                                ? $"Проверено дел по судебным источникам: {processed}."
                                : "Проверка судебной синхронизации выполнена, подходящих дел не найдено.",
                            stoppingToken);
                    }
                }
                else if (_jobRunState is not null)
                {
                    await _jobRunState.RecordSuccessAsync(
                        SystemJobKeys.CourtSync,
                        startedAt,
                        0,
                        null,
                        settings.IsEnabled
                            ? "Судебная синхронизация ожидает включения feature flag."
                            : "Судебная синхронизация выключена в настройках.",
                        stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Automatic court source sync failed. Error type: {ErrorType}.",
                    ex.GetType().Name);
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordFailureAsync(
                        SystemJobKeys.CourtSync,
                        startedAt,
                        ex,
                        "Фоновая судебная синхронизация завершилась с ошибкой.",
                        stoppingToken);
                }
            }

            try
            {
                await Task.Delay(nextDelay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
            }
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        return await RunOnceAsync(settings.BatchSize, cancellationToken);
    }

    private async Task<int> RunOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var admin = await db.Users
            .AsNoTracking()
            .Where(item => item.IsActive && item.IsApproved && item.Role != null && item.Role.Name == "admin")
            .OrderBy(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (admin is null)
        {
            return 0;
        }

        var registry = scope.ServiceProvider.GetService<ICourtSyncProviderRegistry>();
        var metrics = scope.ServiceProvider.GetService<CourtSyncMetrics>();
        var realtimePublisher = scope.ServiceProvider.GetService<IRealtimeNotificationPublisher>();
        var normalizedBatchSize = NormalizeBatchSize(batchSize);
        var candidateLimit = normalizedBatchSize * 5;
        var cursorCaseId = await db.SystemSettings
            .AsNoTracking()
            .Where(item => item.Key == CursorCaseIdSettingKey)
            .Select(item => item.Value)
            .SingleOrDefaultAsync(cancellationToken);
        var cursor = ParseNonNegativeIntSetting(cursorCaseId);
        var candidates = await LoadCandidatesAfterAsync(db, cursor, candidateLimit, cancellationToken);
        if (cursor > 0 && candidates.Count < candidateLimit)
        {
            var wrappedCandidates = await LoadCandidatesThroughAsync(
                db,
                cursor,
                candidateLimit - candidates.Count,
                cancellationToken);
            candidates.AddRange(wrappedCandidates);
        }

        var caseIds = new List<int>(normalizedBatchSize);
        var lastExaminedCaseId = 0;
        foreach (var candidate in candidates)
        {
            lastExaminedCaseId = candidate.Id;
            var sourceUrl = candidate.CourtUrl ?? string.Empty;
            var isSupported = registry?.Resolve(sourceUrl) is not null
                || sourceUrl.Contains(".sudrf.ru", StringComparison.OrdinalIgnoreCase);
            if (!ClientCaseStageClassifier.IsPretrial(candidate.Stage, candidate.CaseType) && isSupported)
            {
                caseIds.Add(candidate.Id);
                if (caseIds.Count == normalizedBatchSize)
                {
                    break;
                }
            }
        }

        if (lastExaminedCaseId == 0)
        {
            return 0;
        }

        var environment = scope.ServiceProvider.GetService<IWebHostEnvironment>();
        var storage = scope.ServiceProvider.GetRequiredService<ICaseDocumentStorage>();
        var sudrf = scope.ServiceProvider.GetRequiredService<ISudrfCourtSyncService>();
        var controller = new CasesV2Controller(
            db,
            environment,
            storage,
            sudrf,
            notificationService: scope.ServiceProvider.GetService<INotificationService>(),
            courtSyncProviderRegistry: registry,
            courtSyncMetrics: metrics,
            configuration: scope.ServiceProvider.GetService<IConfiguration>(),
            managedCaseUploads: scope.ServiceProvider.GetService<IManagedCaseDocumentUploadService>(),
            realtimePublisher: realtimePublisher)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                        new Claim(ClaimTypes.Name, "Система"),
                        new Claim(ClaimTypes.Email, admin.Email),
                        new Claim(ClaimTypes.Role, "admin"),
                    ], "CourtSyncScheduler")),
                },
            },
        };

        var processed = 0;
        foreach (var caseId in caseIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await controller.RunCourtSync(caseId, null, cancellationToken);
            if (result.Result is OkObjectResult)
            {
                processed++;
            }
        }

        await SaveCursorAsync(db, lastExaminedCaseId, cancellationToken);

        return processed;
    }

    /// <summary>
    /// Загружает ограниченную порцию дел после сохраненного курсора. Выборка содержит
    /// только поля, необходимые для определения провайдера, поэтому размер прохода не
    /// зависит от объема карточек и их истории.
    /// </summary>
    private static Task<List<CourtSyncCandidate>> LoadCandidatesAfterAsync(
        PaymentContext db,
        int cursor,
        int take,
        CancellationToken cancellationToken)
        => BuildCandidateQuery(db)
            .Where(item => item.Id > cursor)
            .OrderBy(item => item.Id)
            .Take(take)
            .Select(item => new CourtSyncCandidate(item.Id, item.CourtUrl, item.Stage, item.CaseType))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Дополняет циклический проход делами из начала реестра после достижения конца.
    /// Верхняя граница не дает одному делу попасть в одну и ту же порцию дважды.
    /// </summary>
    private static Task<List<CourtSyncCandidate>> LoadCandidatesThroughAsync(
        PaymentContext db,
        int cursor,
        int take,
        CancellationToken cancellationToken)
        => BuildCandidateQuery(db)
            .Where(item => item.Id <= cursor)
            .OrderBy(item => item.Id)
            .Take(take)
            .Select(item => new CourtSyncCandidate(item.Id, item.CourtUrl, item.Stage, item.CaseType))
            .ToListAsync(cancellationToken);

    private static IQueryable<ClientCase> BuildCandidateQuery(PaymentContext db)
        => db.ClientCases
            .AsNoTracking()
            .Where(item => item.Status == ClientCaseStatus.Open)
            .Where(item => item.CourtSyncMonitoringEnabled)
            .Where(item => item.CourtUrl != string.Empty);

    /// <summary>
    /// Сохраняет позицию циклического обхода. Курсор двигается и при неподдерживаемой
    /// ссылке или ошибке отдельного дела, чтобы одна запись не блокировала остальные.
    /// </summary>
    private static async Task SaveCursorAsync(
        PaymentContext db,
        int caseId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var setting = await db.SystemSettings
            .SingleOrDefaultAsync(item => item.Key == CursorCaseIdSettingKey, cancellationToken);
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                Key = CursorCaseIdSettingKey,
                Value = caseId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            setting.Value = caseId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            setting.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public static int NormalizeIntervalMinutes(int value)
        => Math.Clamp(value, MinIntervalMinutes, MaxIntervalMinutes);

    public static int NormalizeBatchSize(int value)
        => Math.Clamp(value, MinBatchSize, MaxBatchSize);

    private async Task<CourtSyncSchedulerSettings> ResolveSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var settings = await db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EnabledSettingKey ||
                item.Key == IntervalMinutesSettingKey ||
                item.Key == BatchSizeSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        var configEnabled = _configuration.GetValue("LegalIntegrations:CourtSyncScheduler:Enabled", false);
        var configInterval = _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:IntervalMinutes") ?? (int)DefaultInterval.TotalMinutes;
        var configBatchSize = _configuration.GetValue<int?>("LegalIntegrations:CourtSyncScheduler:BatchSize") ?? DefaultBatchSize;

        var isEnabled = ParseBoolSetting(settings.GetValueOrDefault(EnabledSettingKey), configEnabled);
        var intervalMinutes = NormalizeIntervalMinutes(ParseIntSetting(settings.GetValueOrDefault(IntervalMinutesSettingKey), configInterval));
        var batchSize = NormalizeBatchSize(ParseIntSetting(settings.GetValueOrDefault(BatchSizeSettingKey), configBatchSize));

        return new CourtSyncSchedulerSettings(
            isEnabled,
            _featureFlags.CurrentValue.Legal.ExternalCourtSync,
            intervalMinutes,
            batchSize);
    }

    private static bool ParseBoolSetting(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseIntSetting(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseNonNegativeIntSetting(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : 0;

    private sealed record CourtSyncCandidate(int Id, string? CourtUrl, string Stage, string CaseType);

    private readonly record struct CourtSyncSchedulerSettings(
        bool IsEnabled,
        bool FeatureFlagEnabled,
        int IntervalMinutes,
        int BatchSize)
    {
        public bool CanRun => IsEnabled && FeatureFlagEnabled;
    }
}
