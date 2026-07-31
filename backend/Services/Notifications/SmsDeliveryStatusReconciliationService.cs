using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record SmsDeliveryStatusReconciliationResult(
    int CheckedCount,
    int UpdatedCount,
    int TerminalCount);

public sealed class SmsDeliveryStatusReconciliationService : BackgroundService
{
    private const int BatchSize = 20;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumRecheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumTrackingAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(25);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SmsDeliveryStatusReconciliationService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public SmsDeliveryStatusReconciliationService(
        IServiceScopeFactory scopeFactory,
        ILogger<SmsDeliveryStatusReconciliationService> logger,
        ISystemJobRunStateService? jobRunState = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _jobRunState = jobRunState;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, _timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    "SMS delivery status reconciliation failed with {ExceptionType}.",
                    ex.GetType().Name);
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordFailureAsync(
                        SystemJobKeys.SmsDeliveryStatusReconciliation,
                        startedAt,
                        new InvalidOperationException("SMS delivery status reconciliation failed."),
                        "Сверка статусов SMS завершилась с ошибкой.",
                        stoppingToken);
                }
            }

            try
            {
                await Task.Delay(CheckInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async Task<SmsDeliveryStatusReconciliationResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
            var channelGate = await context.SystemSettings
                .AsNoTracking()
                .Where(item =>
                    item.Key == SystemSmsNotificationService.EnabledSettingKey
                    || item.Key == SystemSmsNotificationService.EmergencyStopSettingKey)
                .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
            if (!ParseEnabled(channelGate.GetValueOrDefault(SystemSmsNotificationService.EnabledSettingKey))
                || ParseEnabled(channelGate.GetValueOrDefault(SystemSmsNotificationService.EmergencyStopSettingKey)))
            {
                const string disabledSummary = "SMS-канал выключен, внешняя сверка статусов не выполнялась.";
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordSuccessAsync(
                        SystemJobKeys.SmsDeliveryStatusReconciliation,
                        startedAt,
                        0,
                        0,
                        disabledSummary,
                        cancellationToken);
                }

                return new SmsDeliveryStatusReconciliationResult(0, 0, 0);
            }

            var providers = scope.ServiceProvider.GetServices<ISmsProvider>()
                .ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var recheckBefore = now - MinimumRecheckInterval;
            var createdAfter = now - MaximumTrackingAge;

            var deliveries = await context.NotificationDeliveries
                .Where(item =>
                    item.Channel == NotificationChannels.Sms
                    && item.Status == NotificationDeliveryStatus.Sent
                    && item.ProviderKey != null
                    && item.ProviderMessageId != null
                    && item.CreatedAt >= createdAfter
                    && (item.ProviderStatusCheckedAt == null || item.ProviderStatusCheckedAt <= recheckBefore)
                    && item.ProviderStatus != SmsProviderMessageStatuses.Delivered
                    && item.ProviderStatus != SmsProviderMessageStatuses.Failed
                    && item.ProviderStatus != SmsProviderMessageStatuses.Expired
                    && item.ProviderStatus != SmsProviderMessageStatuses.Rejected)
                .OrderBy(item => item.ProviderStatusCheckedAt ?? item.CreatedAt)
                .ThenBy(item => item.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            var updatedCount = 0;
            var terminalCount = 0;
            foreach (var delivery in deliveries)
            {
                if (!providers.TryGetValue(delivery.ProviderKey!, out var provider))
                {
                    delivery.ProviderStatusCheckedAt = now;
                    continue;
                }

                SmsProviderMessageStatusResult result;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ProviderTimeout);
                try
                {
                    result = await provider.GetMessageStatusAsync(
                        new SmsProviderMessageStatusRequest(
                            delivery.ProviderMessageId!,
                            delivery.RecipientAddress),
                        timeout.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "SMS status check failed for delivery {DeliveryId} with {ExceptionType}.",
                        delivery.Id,
                        ex.GetType().Name);
                    delivery.ProviderStatusCheckedAt = now;
                    continue;
                }

                delivery.ProviderStatusCheckedAt = result.CheckedAtUtc;
                if (string.Equals(result.Status, SmsProviderMessageStatuses.Unknown, StringComparison.Ordinal)
                    || string.Equals(delivery.ProviderStatus, result.Status, StringComparison.Ordinal))
                {
                    continue;
                }

                delivery.ProviderStatus = result.Status;
                updatedCount++;
                if (string.Equals(result.Status, SmsProviderMessageStatuses.Delivered, StringComparison.Ordinal))
                {
                    terminalCount++;
                    continue;
                }

                if (SmsProviderMessageStatuses.Terminal.Contains(result.Status))
                {
                    delivery.Status = NotificationDeliveryStatus.Failed;
                    delivery.Error = "SMS-провайдер сообщил, что сообщение не доставлено.";
                    terminalCount++;
                }
            }

            if (deliveries.Count > 0)
            {
                await context.SaveChangesAsync(cancellationToken);
            }

            var summary = deliveries.Count == 0
                ? "SMS, ожидающих сверки статуса, нет."
                : $"Проверено SMS: {deliveries.Count}; обновлено статусов: {updatedCount}; конечных: {terminalCount}.";
            if (_jobRunState is not null)
            {
                await _jobRunState.RecordSuccessAsync(
                    SystemJobKeys.SmsDeliveryStatusReconciliation,
                    startedAt,
                    deliveries.Count,
                    deliveries.Count - updatedCount,
                    summary,
                    cancellationToken);
            }

            return new SmsDeliveryStatusReconciliationResult(
                deliveries.Count,
                updatedCount,
                terminalCount);
        }
        finally
        {
            _runLock.Release();
        }
    }

    private static bool ParseEnabled(string? value)
        => bool.TryParse(value, out var enabled) && enabled;
}
