using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public static class SmsChannelMonitoringIssueCodes
{
    public const string ConfigurationIncomplete = "configuration-incomplete";
    public const string AdapterMissing = "adapter-missing";
    public const string ProviderUnhealthy = "provider-unhealthy";
    public const string BalanceUnavailable = "balance-unavailable";
    public const string LowBalance = "low-balance";
}

public sealed record SmsChannelMonitoringDecision(
    bool ShouldNotify,
    string? IssueCode,
    string Summary);

public sealed record SmsChannelMonitoringResult(
    bool Enabled,
    bool Checked,
    bool NotificationRequested,
    string? IssueCode,
    string Summary);

public static class SmsChannelMonitoringPolicy
{
    public static SmsChannelMonitoringDecision Evaluate(
        SmsNotificationSettingsDto settings,
        bool adapterAvailable,
        SmsProviderHealthResult? health,
        SmsProviderBalanceResult? balance)
    {
        if (!settings.Enabled)
        {
            return new SmsChannelMonitoringDecision(false, null, "SMS-канал отключён.");
        }

        if (!settings.ProviderConfigured || string.IsNullOrWhiteSpace(settings.SenderName))
        {
            return new SmsChannelMonitoringDecision(
                true,
                SmsChannelMonitoringIssueCodes.ConfigurationIncomplete,
                "Настройки SMS-провайдера заполнены не полностью.");
        }

        if (!adapterAvailable)
        {
            return new SmsChannelMonitoringDecision(
                true,
                SmsChannelMonitoringIssueCodes.AdapterMissing,
                "Для выбранного SMS-провайдера не подключён рабочий адаптер.");
        }

        if (health is not { IsHealthy: true })
        {
            return new SmsChannelMonitoringDecision(
                true,
                SmsChannelMonitoringIssueCodes.ProviderUnhealthy,
                "SMS-провайдер не прошёл ежедневную проверку доступности.");
        }

        if (balance is not { IsAvailable: true, Amount: not null })
        {
            return new SmsChannelMonitoringDecision(
                true,
                SmsChannelMonitoringIssueCodes.BalanceUnavailable,
                "SMS-провайдер не вернул актуальный баланс.");
        }

        if (settings.LowBalanceThreshold > 0 && balance.Amount.Value < settings.LowBalanceThreshold)
        {
            var currency = NormalizeCurrency(balance.Currency);
            return new SmsChannelMonitoringDecision(
                true,
                SmsChannelMonitoringIssueCodes.LowBalance,
                string.Create(
                    CultureInfo.GetCultureInfo("ru-RU"),
                    $"Баланс SMS: {balance.Amount.Value:N2} {currency}; порог предупреждения: {settings.LowBalanceThreshold:N2} {currency}."));
        }

        return new SmsChannelMonitoringDecision(false, null, "SMS-канал доступен, баланс выше порога.");
    }

    private static string NormalizeCurrency(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 12
            ? "ед."
            : normalized;
    }
}

public interface ISmsChannelWarningPublisher
{
    Task<int> PublishAsync(
        string eventKey,
        string issueCode,
        string title,
        string body,
        CancellationToken cancellationToken = default);
}

public sealed class SmsChannelWarningPublisher(INotificationService notificationService) : ISmsChannelWarningPublisher
{
    public Task<int> PublishAsync(
        string eventKey,
        string issueCode,
        string title,
        string body,
        CancellationToken cancellationToken = default)
        => notificationService.NotifySmsChannelWarningAsync(
            eventKey,
            issueCode,
            title,
            body,
            cancellationToken);
}

public sealed class SmsChannelMonitoringService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(9);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan ProviderDiagnosticsTimeout = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SmsChannelMonitoringService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public SmsChannelMonitoringService(
        IServiceScopeFactory scopeFactory,
        ILogger<SmsChannelMonitoringService> logger,
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
                    "SMS channel monitoring failed with {ExceptionType}.",
                    ex.GetType().Name);
                if (_jobRunState is not null)
                {
                    await _jobRunState.RecordFailureAsync(
                        SystemJobKeys.SmsChannelMonitoring,
                        startedAt,
                        new InvalidOperationException("SMS channel monitoring failed."),
                        "Ежедневная проверка SMS-канала завершилась с ошибкой.",
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

    public async Task<SmsChannelMonitoringResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        var startedAt = _timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var smsService = scope.ServiceProvider.GetRequiredService<ISmsNotificationService>();
            var warningPublisher = scope.ServiceProvider.GetRequiredService<ISmsChannelWarningPublisher>();
            var settings = await smsService.GetSettingsAsync(cancellationToken);
            if (!settings.Enabled)
            {
                var disabledResult = new SmsChannelMonitoringResult(
                    false,
                    false,
                    false,
                    null,
                    "SMS-канал отключён, проверка провайдера не требуется.");
                await RecordSuccessAsync(startedAt, disabledResult, cancellationToken);
                return disabledResult;
            }

            var provider = scope.ServiceProvider
                .GetServices<ISmsProvider>()
                .FirstOrDefault(item => string.Equals(
                    item.Key,
                    settings.ProviderKey,
                    StringComparison.OrdinalIgnoreCase));
            SmsProviderHealthResult? health = null;
            SmsProviderBalanceResult? balance = null;
            if (provider is not null)
            {
                using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                providerTimeout.CancelAfter(ProviderDiagnosticsTimeout);
                try
                {
                    health = await provider.CheckHealthAsync(providerTimeout.Token);
                    if (health.IsHealthy)
                    {
                        balance = await provider.GetBalanceAsync(providerTimeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "SMS provider diagnostics timed out for {ProviderKey}.",
                        settings.ProviderKey);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "SMS provider diagnostics failed for {ProviderKey} with {ExceptionType}.",
                        settings.ProviderKey,
                        ex.GetType().Name);
                }
            }

            var decision = SmsChannelMonitoringPolicy.Evaluate(
                settings,
                provider is not null,
                health,
                balance);
            var notificationRequested = false;
            if (decision.ShouldNotify && decision.IssueCode is not null)
            {
                var nowUtc = _timeProvider.GetUtcNow();
                var timeZone = CalendarTimeZoneHelper.ResolveTimeZone(settings.QuietHoursTimeZone);
                var localDate = CalendarTimeZoneHelper.ToUserLocal(nowUtc.UtcDateTime, timeZone).Date;
                var providerLabel = string.IsNullOrWhiteSpace(settings.ProviderKey)
                    ? "не выбран"
                    : settings.ProviderKey.Trim();
                var eventKey = $"{localDate:yyyyMMdd}";
                var notifiedAdmins = await warningPublisher.PublishAsync(
                    eventKey,
                    decision.IssueCode,
                    "SMS-канал требует внимания",
                    $"Провайдер: {providerLabel}.\n{decision.Summary}\nПроверьте раздел «Настройки → Уведомления → SMS».",
                    cancellationToken);
                notificationRequested = notifiedAdmins > 0;
            }

            var result = new SmsChannelMonitoringResult(
                true,
                provider is not null,
                notificationRequested,
                decision.IssueCode,
                decision.Summary);
            await RecordSuccessAsync(startedAt, result, cancellationToken);
            return result;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private Task RecordSuccessAsync(
        DateTime startedAt,
        SmsChannelMonitoringResult result,
        CancellationToken cancellationToken)
        => _jobRunState is null
            ? Task.CompletedTask
            : _jobRunState.RecordSuccessAsync(
                SystemJobKeys.SmsChannelMonitoring,
                startedAt,
                result.Checked ? 1 : 0,
                result.Enabled ? 0 : 1,
                result.Summary,
                cancellationToken);
}
