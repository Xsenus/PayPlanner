using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed class PaymentAdvanceReviewMonitoringService : BackgroundService
{
    public const string EnabledSettingKey = "payments.advanceReview.monitoring.enabled";
    public const string IntervalHoursSettingKey = "payments.advanceReview.monitoring.intervalHours";
    public const string BatchSizeSettingKey = "payments.advanceReview.monitoring.batchSize";

    public const int DefaultIntervalHours = 24;
    public const int DefaultBatchSize = 100;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentAdvanceReviewMonitoringService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public PaymentAdvanceReviewMonitoringService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PaymentAdvanceReviewMonitoringService> logger,
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
                    _logger.LogError(ex, "Payment advance review monitoring failed.");
                    if (_jobRunState is not null)
                    {
                        await _jobRunState.RecordFailureAsync(
                            SystemJobKeys.PaymentAdvanceReviewMonitoring,
                            DateTime.UtcNow,
                            ex,
                            "Мониторинг нераспределенных авансов завершился с ошибкой.",
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

    public async Task<PaymentAdvanceReviewMonitoringResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            await RecordSuccessAsync(
                startedAt,
                new PaymentAdvanceReviewMonitoringResult(0, 0, 0, false),
                "Мониторинг нераспределенных авансов выключен в настройках.",
                cancellationToken);
            return new PaymentAdvanceReviewMonitoringResult(0, 0, 0, false);
        }

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();
            var payments = await db.Payments
                .Include(payment => payment.Client)
                .Include(payment => payment.LegalEntity)
                .Include(payment => payment.ClientCase)
                .Where(candidatePredicate)
                .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None
                    || payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision)
                .OrderBy(payment => payment.Date)
                .ThenBy(payment => payment.Id)
                .Take(settings.BatchSize)
                .ToListAsync(cancellationToken);

            var markedCount = 0;
            var notifiedCount = 0;
            var now = DateTime.UtcNow;
            foreach (var payment in payments)
            {
                if (payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None)
                {
                    payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
                    payment.AdvanceReviewReason = null;
                    payment.AdvanceReviewedAt = null;
                    payment.AdvanceReviewedByUserId = null;
                    payment.AdvanceReviewedByUserName = null;
                    markedCount++;
                }

                if (await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(db, payment, now, cancellationToken))
                {
                    notifiedCount++;
                }
            }

            if (markedCount > 0)
            {
                db.SetAuditReason($"Мониторинг авансов: {markedCount} платежей поставлены на контроль без изменения денежных проводок.");
            }

            if (payments.Count > 0 || notifiedCount > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            var result = new PaymentAdvanceReviewMonitoringResult(payments.Count, markedCount, notifiedCount, true);
            await RecordSuccessAsync(
                startedAt,
                result,
                result.ProcessedCount > 0 || result.NotifiedCount > 0
                    ? $"Мониторинг авансов проверил платежей: {result.ProcessedCount}, поставил на контроль: {result.MarkedCount}, создал уведомлений: {result.NotifiedCount}."
                    : "Мониторинг авансов выполнен, новых платежей для разбора не найдено.",
                cancellationToken);
            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<int> CountPendingCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();
        return await db.Payments
            .AsNoTracking()
            .Where(candidatePredicate)
            .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None
                || payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision)
            .CountAsync(cancellationToken);
    }

    public async Task<PaymentAdvanceReviewMonitoringSettingsSnapshot> GetSettingsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        return new PaymentAdvanceReviewMonitoringSettingsSnapshot(settings.Enabled, settings.IntervalHours, settings.BatchSize);
    }

    private async Task<PaymentAdvanceReviewMonitoringSettings> ResolveSettingsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var settings = await db.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EnabledSettingKey ||
                item.Key == IntervalHoursSettingKey ||
                item.Key == BatchSizeSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        return new PaymentAdvanceReviewMonitoringSettings(
            ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("Payments:AdvanceReview:Monitoring:Enabled", true)),
            NormalizeIntervalHours(ParseInt(settings.GetValueOrDefault(IntervalHoursSettingKey), _configuration.GetValue("Payments:AdvanceReview:Monitoring:IntervalHours", DefaultIntervalHours))),
            NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("Payments:AdvanceReview:Monitoring:BatchSize", DefaultBatchSize))));
    }

    private async Task RecordSuccessAsync(
        DateTime startedAt,
        PaymentAdvanceReviewMonitoringResult result,
        string summary,
        CancellationToken cancellationToken)
    {
        if (_jobRunState is not null)
        {
            await _jobRunState.RecordSuccessAsync(
                SystemJobKeys.PaymentAdvanceReviewMonitoring,
                startedAt,
                result.MarkedCount + result.NotifiedCount,
                result.ProcessedCount - result.MarkedCount,
                summary,
                cancellationToken);
        }
    }

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    public static int NormalizeIntervalHours(int value) => Math.Clamp(value, 1, 168);

    public static int NormalizeBatchSize(int value) => Math.Clamp(value, 1, 500);

    private readonly record struct PaymentAdvanceReviewMonitoringSettings(
        bool Enabled,
        int IntervalHours,
        int BatchSize);
}

public sealed record PaymentAdvanceReviewMonitoringResult(
    int ProcessedCount,
    int MarkedCount,
    int NotifiedCount,
    bool Enabled);

public sealed record PaymentAdvanceReviewMonitoringSettingsSnapshot(
    bool Enabled,
    int IntervalHours,
    int BatchSize);
