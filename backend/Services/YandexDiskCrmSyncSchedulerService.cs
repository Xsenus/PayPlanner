using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed class YandexDiskCrmSyncSchedulerService : BackgroundService
{
    public const string EnabledSettingKey = "yandexDisk.crmSync.scheduler.enabled";
    public const string IntervalMinutesSettingKey = "yandexDisk.crmSync.scheduler.intervalMinutes";
    public const string DelayMinutesSettingKey = "yandexDisk.crmSync.scheduler.delayMinutes";
    public const string BatchSizeSettingKey = "yandexDisk.crmSync.scheduler.batchSize";
    public const string MaxAttemptsSettingKey = "yandexDisk.crmSync.scheduler.maxAttempts";

    public const int DefaultIntervalMinutes = 15;
    public const int DefaultDelayMinutes = 10;
    public const int DefaultBatchSize = 20;
    public const int DefaultMaxAttempts = 3;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<YandexDiskCrmSyncSchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public YandexDiskCrmSyncSchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<YandexDiskCrmSyncSchedulerService> logger,
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
                    await RunOnceAsync(settings, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Yandex Disk CRM sync scheduler failed.");
                    if (_jobRunState is not null)
                    {
                        await _jobRunState.RecordFailureAsync(
                            SystemJobKeys.YandexDiskCrmSync,
                            DateTime.UtcNow,
                            ex,
                            "Фоновая синхронизация CRM с Я.Диском завершилась с ошибкой.",
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

    public async Task<YandexDiskCrmSyncSchedulerResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var settings = await ResolveSettingsAsync(cancellationToken);
            return await RunOnceCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<YandexDiskCrmSyncSchedulerResult> RunOnceAsync(
        YandexDiskCrmSyncSchedulerSettings settings,
        CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            return await RunOnceCoreAsync(settings, cancellationToken);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<YandexDiskCrmSyncSchedulerResult> RunOnceCoreAsync(
        YandexDiskCrmSyncSchedulerSettings settings,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        if (!settings.Enabled)
        {
            await RecordSuccessAsync(startedAt, 0, 0, "Фоновая синхронизация CRM с Я.Диском выключена в настройках.", cancellationToken);
            return new YandexDiskCrmSyncSchedulerResult(0, 0, false, null);
        }

        var candidates = await LoadRetryCandidatesAsync(settings, cancellationToken);
        var synced = 0;
        var skipped = 0;
        string? lastError = null;

        foreach (var state in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RetryUploadAsync(state, cancellationToken);
                synced++;
            }
            catch (InvalidOperationException ex)
            {
                skipped++;
                lastError = ex.Message;
                await RecordStateFailureAsync(state, ex, cancellationToken);
                _logger.LogInformation(ex, "Yandex Disk CRM sync item {StateId} was skipped.", state.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                skipped++;
                lastError = ex.Message;
                await RecordStateFailureAsync(state, ex, cancellationToken);
                _logger.LogWarning(ex, "Failed to retry Yandex Disk CRM sync item {StateId}.", state.Id);
            }
        }

        var summary = synced > 0
            ? $"Повторно отправлено CRM-документов на Я.Диск: {synced}, пропущено: {skipped}."
            : "Проверка очереди Я.Диска выполнена, документов для повторной отправки не найдено.";
        await RecordSuccessAsync(startedAt, synced, skipped, summary, cancellationToken);
        return new YandexDiskCrmSyncSchedulerResult(synced, skipped, true, lastError);
    }

    public async Task<int> CountEligibleItemsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-settings.DelayMinutes);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        return await db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .CountAsync(item =>
                (item.Status == YandexDiskCrmSyncStatus.Pending || item.Status == YandexDiskCrmSyncStatus.Failed)
                && item.AttemptCount < settings.MaxAttempts
                && item.UpdatedAt <= cutoff,
                cancellationToken);
    }

    private async Task<IReadOnlyList<YandexDiskCrmSyncState>> LoadRetryCandidatesAsync(
        YandexDiskCrmSyncSchedulerSettings settings,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-settings.DelayMinutes);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        return await db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .Where(item =>
                (item.Status == YandexDiskCrmSyncStatus.Pending || item.Status == YandexDiskCrmSyncStatus.Failed)
                && item.AttemptCount < settings.MaxAttempts
                && item.UpdatedAt <= cutoff)
            .OrderBy(item => item.UpdatedAt)
            .ThenBy(item => item.Id)
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task RetryUploadAsync(YandexDiskCrmSyncState state, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        ActionResult<YandexDiskOperationResultDto> result = state.DocumentKind switch
        {
            "caseDocument" => await CreateController<YandexDiskController>(provider)
                .UploadCaseDocument(state.ObjectId, overwrite: null, ct: cancellationToken),
            "taskFile" => await UploadTaskFileAsync(provider, state, cancellationToken),
            "contract" => await CreateController<ContractsController>(provider)
                .UploadDocumentToYandexDisk(state.ObjectId, format: "pdf", templateKey: null, overwrite: null, ct: cancellationToken),
            "invoice" => await CreateController<InvoicesController>(provider)
                .UploadDocumentToYandexDisk(state.ObjectId, format: "pdf", templateKey: null, includeSignatureAndSeal: false, overwrite: null, ct: cancellationToken),
            "act" => await CreateController<ActsController>(provider)
                .UploadDocumentToYandexDisk(state.ObjectId, format: "pdf", includeSignatureAndSeal: false, overwrite: null, ct: cancellationToken),
            _ => throw new InvalidOperationException($"Неизвестный тип CRM-документа для синхронизации с Я.Диском: {state.DocumentKind}."),
        };

        EnsureSuccess(result);
    }

    private async Task<ActionResult<YandexDiskOperationResultDto>> UploadTaskFileAsync(
        IServiceProvider provider,
        YandexDiskCrmSyncState state,
        CancellationToken cancellationToken)
    {
        if (!state.ParentObjectId.HasValue)
        {
            throw new InvalidOperationException("Для файла задачи не найден идентификатор задачи.");
        }

        return await CreateController<YandexDiskController>(provider)
            .UploadTaskFile(state.ParentObjectId.Value, state.ObjectId, overwrite: null, ct: cancellationToken);
    }

    private static TController CreateController<TController>(IServiceProvider provider)
        where TController : ControllerBase
    {
        var controller = ActivatorUtilities.CreateInstance<TController>(provider);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Name, "Фоновая синхронизация Я.Диска"),
                        new Claim(ClaimTypes.Role, "admin"),
                    },
                    "YandexDiskCrmSyncScheduler")),
            },
        };
        return controller;
    }

    private static void EnsureSuccess(ActionResult<YandexDiskOperationResultDto> actionResult)
    {
        if (actionResult.Value?.Success == true)
        {
            return;
        }

        if (actionResult.Result is ObjectResult objectResult)
        {
            throw new InvalidOperationException(ExtractError(objectResult.Value) ?? $"Я.Диск вернул статус {objectResult.StatusCode ?? 500}.");
        }

        if (actionResult.Result is StatusCodeResult statusCodeResult)
        {
            throw new InvalidOperationException($"Я.Диск вернул статус {statusCodeResult.StatusCode}.");
        }

        if (actionResult.Result is NotFoundResult)
        {
            throw new InvalidOperationException("CRM-документ не найден для повторной отправки на Я.Диск.");
        }

        if (actionResult.Result is ForbidResult)
        {
            throw new InvalidOperationException("Недостаточно прав для повторной отправки CRM-документа на Я.Диск.");
        }

        if (actionResult.Result is UnauthorizedResult)
        {
            throw new InvalidOperationException("Не удалось авторизовать фоновую синхронизацию Я.Диска.");
        }

        throw new InvalidOperationException("Повторная отправка CRM-документа на Я.Диск не вернула успешный результат.");
    }

    private static string? ExtractError(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return text;
        }

        if (value is ProblemDetails problem)
        {
            return problem.Detail ?? problem.Title;
        }

        var messageProperty = value.GetType().GetProperty("message");
        return messageProperty?.GetValue(value)?.ToString() ?? value.ToString();
    }

    private async Task RecordStateFailureAsync(
        YandexDiskCrmSyncState state,
        Exception exception,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var currentAttemptCount = await db.YandexDiskCrmSyncStates
            .AsNoTracking()
            .Where(item =>
                item.DocumentKind == state.DocumentKind
                && item.ObjectType == state.ObjectType
                && item.ObjectId == state.ObjectId)
            .Select(item => (int?)item.AttemptCount)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentAttemptCount.HasValue && currentAttemptCount.Value != state.AttemptCount)
        {
            return;
        }

        await YandexDiskCrmSyncStateRecorder.RecordFailureAsync(
            db,
            state.DocumentKind,
            state.ObjectType,
            state.ObjectId,
            state.FileName,
            state.ParentObjectId,
            exception,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<YandexDiskCrmSyncSchedulerSettings> ResolveSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var settings = await context.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EnabledSettingKey ||
                item.Key == IntervalMinutesSettingKey ||
                item.Key == DelayMinutesSettingKey ||
                item.Key == BatchSizeSettingKey ||
                item.Key == MaxAttemptsSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        return new YandexDiskCrmSyncSchedulerSettings(
            ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("YandexDisk:CrmSync:Scheduler:Enabled", true)),
            NormalizeInterval(ParseInt(settings.GetValueOrDefault(IntervalMinutesSettingKey), _configuration.GetValue("YandexDisk:CrmSync:Scheduler:IntervalMinutes", DefaultIntervalMinutes))),
            NormalizeDelay(ParseInt(settings.GetValueOrDefault(DelayMinutesSettingKey), _configuration.GetValue("YandexDisk:CrmSync:Scheduler:DelayMinutes", DefaultDelayMinutes))),
            NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("YandexDisk:CrmSync:Scheduler:BatchSize", DefaultBatchSize))),
            NormalizeMaxAttempts(ParseInt(settings.GetValueOrDefault(MaxAttemptsSettingKey), _configuration.GetValue("YandexDisk:CrmSync:Scheduler:MaxAttempts", DefaultMaxAttempts))));
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
                SystemJobKeys.YandexDiskCrmSync,
                startedAt,
                processedCount,
                skippedCount,
                summary,
                cancellationToken);
        }
    }

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    public static int NormalizeInterval(int value) => Math.Clamp(value, 5, 1440);

    public static int NormalizeDelay(int value) => Math.Clamp(value, 1, 1440);

    public static int NormalizeBatchSize(int value) => Math.Clamp(value, 1, 100);

    public static int NormalizeMaxAttempts(int value) => Math.Clamp(value, 1, 10);

    private readonly record struct YandexDiskCrmSyncSchedulerSettings(
        bool Enabled,
        int IntervalMinutes,
        int DelayMinutes,
        int BatchSize,
        int MaxAttempts);
}

public sealed record YandexDiskCrmSyncSchedulerResult(
    int SyncedCount,
    int SkippedCount,
    bool Enabled,
    string? Error);
