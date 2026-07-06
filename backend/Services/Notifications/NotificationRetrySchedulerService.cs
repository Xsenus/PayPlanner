using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed class NotificationRetrySchedulerService : BackgroundService
{
    public const string EnabledSettingKey = "notifications.retry.enabled";
    public const string IntervalMinutesSettingKey = "notifications.retry.intervalMinutes";
    public const string DelayMinutesSettingKey = "notifications.retry.delayMinutes";
    public const string BatchSizeSettingKey = "notifications.retry.batchSize";
    public const string MaxAttemptsSettingKey = "notifications.retry.maxAttempts";

    public const int DefaultIntervalMinutes = 15;
    public const int DefaultDelayMinutes = 15;
    public const int DefaultBatchSize = 25;
    public const int DefaultMaxAttempts = 3;

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationRetrySchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;

    public NotificationRetrySchedulerService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<NotificationRetrySchedulerService> logger,
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
                    _logger.LogError(ex, "Notification retry scheduler failed.");
                    if (_jobRunState is not null)
                    {
                        await _jobRunState.RecordFailureAsync(
                            SystemJobKeys.NotificationRetry,
                            DateTime.UtcNow,
                            ex,
                            "Повторная отправка уведомлений завершилась с ошибкой.",
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

    public async Task<NotificationRetrySchedulerResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            await RecordSuccessAsync(startedAt, 0, 0, "Повторная отправка уведомлений выключена в настройках.", cancellationToken);
            return new NotificationRetrySchedulerResult(0, 0, false, null);
        }

        var candidates = await LoadRetryCandidatesAsync(settings, cancellationToken);
        var retried = 0;
        var skipped = 0;
        string? lastError = null;

        using var scope = _scopeFactory.CreateScope();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var webPushNotificationService = scope.ServiceProvider.GetService<IWebPushNotificationService>();

        foreach (var delivery in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var wasRetried = false;
                if (string.Equals(delivery.Channel, NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase))
                {
                    if (webPushNotificationService is null)
                    {
                        throw new InvalidOperationException("WebPushNotificationServiceMissing");
                    }

                    wasRetried = await webPushNotificationService.RetryDeliveryAsync(delivery.Id, cancellationToken) is not null;
                }
                else
                {
                    var retryDeliveries = await notificationService.RetryDeliveryAsync(delivery.Id, cancellationToken);
                    wasRetried = retryDeliveries.Count > 0;
                }

                if (wasRetried)
                {
                    retried++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (InvalidOperationException ex)
            {
                skipped++;
                lastError = ex.Message;
                _logger.LogInformation(ex, "Notification delivery {DeliveryId} was skipped by retry scheduler.", delivery.Id);
            }
        }

        var summary = retried > 0
            ? $"Повторно отправлены уведомления: {retried}, пропущено: {skipped}."
            : "Проверка ошибок доставки выполнена, уведомлений для повтора не найдено.";
        await RecordSuccessAsync(startedAt, retried, skipped, summary, cancellationToken);
        return new NotificationRetrySchedulerResult(retried, skipped, true, lastError);
    }

    public async Task<int> CountEligibleRetriesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        if (!settings.Enabled)
        {
            return 0;
        }

        return (await LoadRetryCandidatesAsync(settings with { BatchSize = 200 }, cancellationToken)).Count;
    }

    private async Task<IReadOnlyList<NotificationDelivery>> LoadRetryCandidatesAsync(
        NotificationRetrySettings settings,
        CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-settings.DelayMinutes);
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PaymentContext>();
        var failed = await context.NotificationDeliveries
            .AsNoTracking()
            .Where(item =>
                item.Status == NotificationDeliveryStatus.Failed &&
                item.CreatedAt <= cutoff &&
                (item.Channel == NotificationChannels.Email
                    || item.Channel == NotificationChannels.Telegram
                    || item.Channel == NotificationChannels.WebPush))
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(settings.BatchSize * 5)
            .ToListAsync(cancellationToken);

        var result = new List<NotificationDelivery>();
        foreach (var delivery in failed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(delivery.Channel, NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase)
                && !await IsRetryableWebPushDeliveryAsync(context, delivery, cancellationToken))
            {
                continue;
            }

            if (CountRetryAttempts(delivery.EventKey) >= settings.MaxAttempts)
            {
                continue;
            }

            var childPrefix = $"{delivery.EventKey}:retry:";
            var alreadyRetried = await context.NotificationDeliveries
                .AsNoTracking()
                .AnyAsync(item => item.EventKey.StartsWith(childPrefix), cancellationToken);
            if (alreadyRetried)
            {
                continue;
            }

            result.Add(delivery);
            if (result.Count >= settings.BatchSize)
            {
                break;
            }
        }

        return result;
    }

    private static async Task<bool> IsRetryableWebPushDeliveryAsync(
        PaymentContext context,
        NotificationDelivery delivery,
        CancellationToken cancellationToken)
    {
        if (WebPushSubscriptionHealth.IsNonRetryableDeliveryError(delivery.Error))
        {
            return false;
        }

        if (!delivery.RecipientUserId.HasValue || string.IsNullOrWhiteSpace(delivery.RecipientAddress))
        {
            return false;
        }

        var hasActiveSubscription = await context.UserPushSubscriptions
            .AsNoTracking()
            .AnyAsync(item =>
                item.UserId == delivery.RecipientUserId.Value
                && item.Endpoint == delivery.RecipientAddress
                && item.IsActive
                && item.RevokedAt == null,
                cancellationToken);
        if (!hasActiveSubscription)
        {
            return false;
        }

        var baseEventKey = ResolveBaseEventKey(delivery.EventKey);
        return await context.UserNotifications
            .AsNoTracking()
            .AnyAsync(item =>
                item.EventKey == baseEventKey
                && item.RecipientUserId == delivery.RecipientUserId.Value,
                cancellationToken);
    }

    private async Task<NotificationRetrySettings> ResolveSettingsAsync(CancellationToken cancellationToken)
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

        return new NotificationRetrySettings(
            ParseBool(settings.GetValueOrDefault(EnabledSettingKey), _configuration.GetValue("Notifications:Retry:Enabled", true)),
            NormalizeInterval(ParseInt(settings.GetValueOrDefault(IntervalMinutesSettingKey), _configuration.GetValue("Notifications:Retry:IntervalMinutes", DefaultIntervalMinutes))),
            NormalizeDelay(ParseInt(settings.GetValueOrDefault(DelayMinutesSettingKey), _configuration.GetValue("Notifications:Retry:DelayMinutes", DefaultDelayMinutes))),
            NormalizeBatchSize(ParseInt(settings.GetValueOrDefault(BatchSizeSettingKey), _configuration.GetValue("Notifications:Retry:BatchSize", DefaultBatchSize))),
            NormalizeMaxAttempts(ParseInt(settings.GetValueOrDefault(MaxAttemptsSettingKey), _configuration.GetValue("Notifications:Retry:MaxAttempts", DefaultMaxAttempts))));
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
                SystemJobKeys.NotificationRetry,
                startedAt,
                processedCount,
                skippedCount,
                summary,
                cancellationToken);
        }
    }

    private static int CountRetryAttempts(string eventKey)
        => eventKey.Split(":retry:", StringSplitOptions.None).Length - 1;

    private static string ResolveBaseEventKey(string eventKey)
    {
        var retryIndex = eventKey.IndexOf(":retry:", StringComparison.Ordinal);
        return retryIndex < 0 ? eventKey : eventKey[..retryIndex];
    }

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    public static int NormalizeInterval(int value) => Math.Clamp(value, 5, 1440);

    public static int NormalizeDelay(int value) => Math.Clamp(value, 1, 1440);

    public static int NormalizeBatchSize(int value) => Math.Clamp(value, 1, 100);

    public static int NormalizeMaxAttempts(int value) => Math.Clamp(value, 1, 10);

    private readonly record struct NotificationRetrySettings(
        bool Enabled,
        int IntervalMinutes,
        int DelayMinutes,
        int BatchSize,
        int MaxAttempts);
}

public sealed record NotificationRetrySchedulerResult(
    int RetriedCount,
    int SkippedCount,
    bool Enabled,
    string? Error);
