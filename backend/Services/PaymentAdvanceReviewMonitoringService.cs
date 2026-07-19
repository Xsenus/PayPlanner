using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class PaymentAdvanceReviewMonitoringService : BackgroundService
{
    public const string EnabledSettingKey = "payments.advanceReview.monitoring.enabled";
    public const string IntervalHoursSettingKey = "payments.advanceReview.monitoring.intervalHours";
    public const string BatchSizeSettingKey = "payments.advanceReview.monitoring.batchSize";
    public const string WarningAgeDaysSettingKey = "payments.advanceReview.monitoring.warningAgeDays";
    public const string CriticalAgeDaysSettingKey = "payments.advanceReview.monitoring.criticalAgeDays";

    public const int DefaultIntervalHours = 24;
    public const int DefaultBatchSize = 100;
    public const int DefaultWarningAgeDays = 1;
    public const int DefaultCriticalAgeDays = 3;

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
                    await RunOnceAsync(settings, stoppingToken);
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

    public Task<PaymentAdvanceReviewMonitoringResult> RunOnceAsync(CancellationToken cancellationToken = default)
        => RunOnceAsync(null, cancellationToken);

    private async Task<PaymentAdvanceReviewMonitoringResult> RunOnceAsync(
        PaymentAdvanceReviewMonitoringSettings? settingsSnapshot,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var settings = settingsSnapshot ?? await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            await RecordSuccessAsync(
                startedAt,
                new PaymentAdvanceReviewMonitoringResult(0, 0, 0, 0, false),
                "Мониторинг нераспределенных авансов выключен в настройках.",
                cancellationToken);
            return new PaymentAdvanceReviewMonitoringResult(0, 0, 0, 0, false);
        }

        await _runLock.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var notificationService = scope.ServiceProvider.GetService<INotificationService>();
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
            var escalatedCount = 0;
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

                if (await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(db, payment, now, notificationService, cancellationToken))
                {
                    notifiedCount++;
                }

                escalatedCount += await PaymentAdvanceReviewService.EnsureReviewEscalationNotificationsAsync(
                    db,
                    payment,
                    now,
                    settings.CriticalAgeDays,
                    notificationService,
                    cancellationToken);
            }

            if (markedCount > 0)
            {
                db.SetAuditReason($"Мониторинг авансов: {markedCount} платежей поставлены на контроль без изменения денежных проводок.");
            }

            if (payments.Count > 0 || notifiedCount > 0 || escalatedCount > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            var result = new PaymentAdvanceReviewMonitoringResult(payments.Count, markedCount, notifiedCount, escalatedCount, true);
            await RecordSuccessAsync(
                startedAt,
                result,
                result.ProcessedCount > 0 || result.NotifiedCount > 0 || result.EscalatedCount > 0
                    ? $"Мониторинг авансов проверил платежей: {result.ProcessedCount}, поставил на контроль: {result.MarkedCount}, создал уведомлений: {result.NotifiedCount}, эскалаций: {result.EscalatedCount}."
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

    public async Task<int> CountCriticalPendingCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        return await CountCriticalPendingCandidatesAsync(db, settings.CriticalAgeDays, cancellationToken);
    }

    public async Task<PaymentAdvanceReviewMonitoringSettingsSnapshot> GetSettingsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        return new PaymentAdvanceReviewMonitoringSettingsSnapshot(settings.Enabled, settings.IntervalHours, settings.BatchSize, settings.WarningAgeDays, settings.CriticalAgeDays);
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
                item.Key == BatchSizeSettingKey ||
                item.Key == WarningAgeDaysSettingKey ||
                item.Key == CriticalAgeDaysSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

        var warningAgeDays = NormalizeWarningAgeDays(ParseInt(
            settings.GetValueOrDefault(WarningAgeDaysSettingKey),
            _configuration.GetValue("Payments:AdvanceReview:Monitoring:WarningAgeDays", DefaultWarningAgeDays)));
        var criticalAgeDays = NormalizeCriticalAgeDays(ParseInt(
            settings.GetValueOrDefault(CriticalAgeDaysSettingKey),
            _configuration.GetValue("Payments:AdvanceReview:Monitoring:CriticalAgeDays", DefaultCriticalAgeDays)),
            warningAgeDays);

        return new PaymentAdvanceReviewMonitoringSettings(
            ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("Payments:AdvanceReview:Monitoring:Enabled", true)),
            NormalizeIntervalHours(ParseInt(settings.GetValueOrDefault(IntervalHoursSettingKey), _configuration.GetValue("Payments:AdvanceReview:Monitoring:IntervalHours", DefaultIntervalHours))),
            NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("Payments:AdvanceReview:Monitoring:BatchSize", DefaultBatchSize))),
            warningAgeDays,
            criticalAgeDays);
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
                result.MarkedCount + result.NotifiedCount + result.EscalatedCount,
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

    public static int NormalizeWarningAgeDays(int value) => Math.Clamp(value, 1, 365);

    public static int NormalizeCriticalAgeDays(int value, int warningAgeDays)
        => Math.Clamp(value, Math.Max(1, warningAgeDays), 365);

    public static async Task<int> CountCriticalPendingCandidatesAsync(
        PaymentContext db,
        int criticalAgeDays,
        CancellationToken cancellationToken = default)
    {
        var normalizedCriticalAgeDays = Math.Max(1, criticalAgeDays);
        var cutoff = DateTime.UtcNow.Date.AddDays(-normalizedCriticalAgeDays);
        var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();

        return await db.Payments
            .AsNoTracking()
            .Where(candidatePredicate)
            .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None
                || payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision)
            .Where(payment =>
                (payment.LastPaymentDate ?? payment.PaidDate ?? (payment.Date == default ? payment.CreatedAt : payment.Date)).Date <= cutoff)
            .CountAsync(cancellationToken);
    }

    private readonly record struct PaymentAdvanceReviewMonitoringSettings(
        bool Enabled,
        int IntervalHours,
        int BatchSize,
        int WarningAgeDays,
        int CriticalAgeDays);
}

public sealed record PaymentAdvanceReviewMonitoringResult(
    int ProcessedCount,
    int MarkedCount,
    int NotifiedCount,
    int EscalatedCount,
    bool Enabled);

public sealed record PaymentAdvanceReviewMonitoringSettingsSnapshot(
    bool Enabled,
    int IntervalHours,
    int BatchSize,
    int WarningAgeDays,
    int CriticalAgeDays);
