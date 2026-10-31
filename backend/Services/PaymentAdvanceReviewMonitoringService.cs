using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Services;

public sealed class PaymentAdvanceReviewMonitoringService : BackgroundService
{
    public const string CursorSettingKey = "payments.advanceReview.monitoring.cursor";
    private static readonly JsonSerializerOptions CursorJsonOptions = new(JsonSerializerDefaults.Web);

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
            var candidatePredicate = PaymentAdvanceReviewService.UnallocatedAdvanceCandidatePredicate();
            var markedCount = 0;
            int[] ids;
            var now = DateTime.UtcNow;
            await using (var lease = await BalanceApplicationLease.BeginSystemAsync(db, cancellationToken))
            {
                if (db.Database.IsNpgsql()) await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
                if (db.Database.IsNpgsql())
                {
                    // Serialize cursor initialization as well as existing rows.
                    // NOWAIT admission keeps independent jobs from waiting on each other.
                    if (!await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(831033, hashtext({CursorSettingKey})) AS \"Value\"").SingleAsync(cancellationToken))
                        throw new DbUpdateConcurrencyException("Фоновый контроль авансов уже выполняется. Повторите запуск позднее.");
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"SystemSettings\" WHERE \"Key\" = {CursorSettingKey} FOR UPDATE NOWAIT", cancellationToken);
                }
                var cursorSetting = await db.SystemSettings.SingleOrDefaultAsync(item => item.Key == CursorSettingKey, cancellationToken);
                var cursor = ParseCursor(cursorSetting?.Value);
                if (cursorSetting is not null && cursor is null)
                    _logger.LogWarning("Advance review cursor is invalid; restarting its bounded cycle.");
                var query = db.Payments.Where(candidatePredicate)
                    .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.None
                        || payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision);
                var page = await LoadCursorPageAsync(query, cursor, settings.BatchSize, cancellationToken);
                ids = page.Ids;
                await lease.LockPaymentsAsync(ids, cancellationToken);
                var lockedPayments = await query.Where(payment => ids.Contains(payment.Id)).ToListAsync(cancellationToken);
                if (lockedPayments.Count != ids.Length) throw new BalanceApplicationConflictException();
                foreach (var payment in lockedPayments)
                {
                    if (payment.AdvanceReviewStatus != PaymentAdvanceReviewStatus.None) continue;
                    payment.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
                    payment.AdvanceReviewReason = null;
                    payment.AdvanceReviewedAt = null;
                    payment.AdvanceReviewedByUserId = null;
                    payment.AdvanceReviewedByUserName = null;
                    markedCount++;
                }
                if (markedCount > 0)
                {
                    db.SetAuditReason($"Мониторинг авансов: {markedCount} платежей поставлены на контроль без изменения денежных проводок.");
                }
                var cursorChanged = UpdateCursor(db, cursorSetting, page.Position, now);
                if (markedCount > 0 || cursorChanged) await db.SaveChangesAsync(cancellationToken);
                await lease.CommitAsync(cancellationToken);
            }

            // Dispatch only after the owned transaction has been committed and
            // released. A delivery failure cannot undo or retry the batch write.
            var notifiedCount = 0;
            var escalatedCount = 0;
            var notificationService = scope.ServiceProvider.GetService<INotificationService>();
            if (ids.Length > 0 && notificationService is not null)
            {
                var committedPayments = await db.Payments.AsNoTracking().Where(payment => ids.Contains(payment.Id))
                    .Where(candidatePredicate)
                    .Where(payment => payment.AdvanceReviewStatus == PaymentAdvanceReviewStatus.RequiresDecision)
                    .Include(payment => payment.Client).Include(payment => payment.LegalEntity).Include(payment => payment.ClientCase)
                    .OrderBy(payment => payment.Date).ThenBy(payment => payment.Id).ToListAsync(cancellationToken);
                var deliveryErrors = new NotificationFailures();
                foreach (var payment in committedPayments)
                {
                    notifiedCount += await DispatchIsolatedAsync(payment.Id, async (notificationDb, dispatcher) =>
                        await PaymentAdvanceReviewService.EnsureReviewRequiredNotificationAsync(notificationDb, payment, now, dispatcher, cancellationToken) ? 1 : 0,
                        deliveryErrors, cancellationToken);
                    escalatedCount += await DispatchIsolatedAsync(payment.Id, (notificationDb, dispatcher) =>
                        PaymentAdvanceReviewService.EnsureReviewEscalationNotificationsAsync(notificationDb, payment, now, settings.CriticalAgeDays, dispatcher, cancellationToken,
                            async (envelope, token) => await DispatchIsolatedAsync(payment.Id,
                                async (_, recipientDispatcher) => await recipientDispatcher.NotifyAsync(envelope, token) ? 1 : 0, deliveryErrors, token) > 0),
                        deliveryErrors, cancellationToken);
                }
                if (deliveryErrors.Count == 1 && deliveryErrors.Errors[0] is not OperationCanceledException) ExceptionDispatchInfo.Capture(deliveryErrors.Errors[0]).Throw();
                if (deliveryErrors.Count > 0) throw new AggregateException($"Не удалось создать уведомлений контроля авансов: {deliveryErrors.Count}.", deliveryErrors.Errors);
            }

            var result = new PaymentAdvanceReviewMonitoringResult(ids.Length, markedCount, notifiedCount, escalatedCount, true);
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

    private sealed class NotificationFailures
    {
        public const int SampleLimit = 32;
        public int Count { get; private set; }
        public List<Exception> Errors { get; } = [];
        public void Record(Exception error)
        {
            Count++;
            if (Errors.Count < SampleLimit) Errors.Add(error);
        }
    }

    private async Task<int> DispatchIsolatedAsync(int paymentId,
        Func<PaymentContext, INotificationService, Task<int>> dispatch, NotificationFailures errors, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // A failed notification may leave its DbContext unusable or dirty.
            // Other recipients must never save that context's pending changes.
            using var scope = _scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetService<INotificationService>();
            return dispatcher is null ? 0 : await dispatch(scope.ServiceProvider.GetRequiredService<PaymentContext>(), dispatcher);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            errors.Record(error);
            if (errors.Count <= NotificationFailures.SampleLimit)
                _logger.LogError(error, "Advance review notification failed for payment {PaymentId}.", paymentId);
            else if (errors.Count == NotificationFailures.SampleLimit + 1)
                _logger.LogWarning("Additional advance review notification failures are counted without individual error logs.");
            return 0;
        }
    }

    private readonly record struct MonitoringCursor(int Version, int AfterId, int ThroughId);

    private static MonitoringCursor? ParseCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<MonitoringCursor>(value, CursorJsonOptions);
            return cursor.Version == 1 && cursor.AfterId >= 0 && cursor.ThroughId >= cursor.AfterId ? cursor : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<(int[] Ids, MonitoringCursor Position)> LoadCursorPageAsync(
        IQueryable<Payment> query, MonitoringCursor? previous, int limit, CancellationToken ct)
    {
        // Immutable IDs and a finite high-water mark prevent backdated or
        // continuous arrivals from extending a cycle indefinitely. No OFFSET.
        async Task<MonitoringCursor> StartCycleAsync()
            => new(1, 0, await query.Select(payment => (int?)payment.Id).MaxAsync(ct) ?? 0);
        Task<int[]> LoadAsync(MonitoringCursor position)
            => position.ThroughId == 0 ? Task.FromResult(Array.Empty<int>())
                : query.AsNoTracking().Where(payment => payment.Id > position.AfterId && payment.Id <= position.ThroughId)
                    .OrderBy(payment => payment.Id).Select(payment => payment.Id).Take(limit).ToArrayAsync(ct);
        var cursor = previous ?? new(1, 0, 0);
        if (cursor.AfterId >= cursor.ThroughId) cursor = await StartCycleAsync();
        var ids = await LoadAsync(cursor);
        if (ids.Length == 0 && cursor.ThroughId > 0)
        {
            cursor = await StartCycleAsync();
            ids = await LoadAsync(cursor);
        }
        return (ids, ids.Length == 0 ? new(1, 0, 0) : cursor with { AfterId = ids[^1] });
    }

    private static bool UpdateCursor(PaymentContext db, SystemSetting? setting, MonitoringCursor cursor, DateTime now)
    {
        if (setting is null && cursor.ThroughId == 0) return false;
        var value = JsonSerializer.Serialize(cursor, CursorJsonOptions);
        if (setting is null)
            db.SystemSettings.Add(new() { Key = CursorSettingKey, Value = value, CreatedAt = now, UpdatedAt = now });
        else
        {
            if (setting.Value == value) return false;
            setting.Value = value;
            setting.UpdatedAt = now;
        }
        return true;
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
