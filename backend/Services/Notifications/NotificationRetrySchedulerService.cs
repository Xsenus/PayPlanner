using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using System.Linq.Expressions;

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
    private const int PrefixQueryBatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationRetrySchedulerService> _logger;
    private readonly ISystemJobRunStateService? _jobRunState;
    private readonly SemaphoreSlim _runLock = new(1, 1);

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
                    await RunOnceAsync(settings, stoppingToken);
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

    public Task<NotificationRetrySchedulerResult> RunOnceAsync(CancellationToken cancellationToken = default)
        => RunOnceAsync(null, cancellationToken);

    private async Task<NotificationRetrySchedulerResult> RunOnceAsync(
        NotificationRetrySettings? settingsSnapshot,
        CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var startedAt = DateTime.UtcNow;
            var settings = settingsSnapshot ?? await ResolveSettingsAsync(cancellationToken);
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
        finally
        {
            _runLock.Release();
        }
    }

    public async Task<int> CountEligibleRetriesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await ResolveSettingsAsync(cancellationToken);
        return await CountEligibleRetriesAsync(
            settings.Enabled,
            settings.DelayMinutes,
            settings.MaxAttempts,
            cancellationToken);
    }

    public async Task<int> CountEligibleRetriesAsync(
        bool enabled,
        int delayMinutes,
        int maxAttempts,
        CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            return 0;
        }

        var settings = new NotificationRetrySettings(
            true,
            DefaultIntervalMinutes,
            NormalizeDelay(delayMinutes),
            200,
            NormalizeMaxAttempts(maxAttempts));
        return (await LoadRetryCandidatesAsync(settings, cancellationToken)).Count;
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

        var eligibleByAttempt = failed
            .Where(delivery => CountRetryAttempts(delivery.EventKey) < settings.MaxAttempts)
            .ToList();
        var childPrefixes = eligibleByAttempt
            .Select(delivery => $"{delivery.EventKey}:retry:")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var childEventKeys = await LoadChildEventKeysAsync(context, childPrefixes, cancellationToken);
        var retriedEventKeys = childEventKeys
            .Select(ResolveParentEventKey)
            .ToHashSet(StringComparer.Ordinal);
        var candidatesWithoutRetries = eligibleByAttempt
            .Where(delivery => !retriedEventKeys.Contains(delivery.EventKey))
            .ToList();

        var webPushCandidates = candidatesWithoutRetries
            .Where(delivery => string.Equals(delivery.Channel, NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase))
            .Where(delivery => !WebPushSubscriptionHealth.IsNonRetryableDeliveryError(delivery.Error))
            .Where(delivery => delivery.RecipientUserId.HasValue && !string.IsNullOrWhiteSpace(delivery.RecipientAddress))
            .ToList();
        var webPushUserIds = webPushCandidates
            .Select(delivery => delivery.RecipientUserId!.Value)
            .Distinct()
            .ToArray();
        var webPushEndpoints = webPushCandidates
            .Select(delivery => delivery.RecipientAddress)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var activeSubscriptions = webPushCandidates.Count == 0
            ? []
            : await context.UserPushSubscriptions
                .AsNoTracking()
                .Where(item =>
                    webPushUserIds.Contains(item.UserId)
                    && webPushEndpoints.Contains(item.Endpoint)
                    && item.IsActive
                    && item.RevokedAt == null)
                .Select(item => new { item.UserId, item.Endpoint })
                .ToListAsync(cancellationToken);
        var activeSubscriptionKeys = activeSubscriptions
            .Select(item => (item.UserId, item.Endpoint))
            .ToHashSet();
        var baseEventKeys = webPushCandidates
            .Select(delivery => ResolveBaseEventKey(delivery.EventKey))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceNotifications = webPushCandidates.Count == 0
            ? []
            : await context.UserNotifications
                .AsNoTracking()
                .Where(item =>
                    webPushUserIds.Contains(item.RecipientUserId)
                    && baseEventKeys.Contains(item.EventKey))
                .Select(item => new { item.RecipientUserId, item.EventKey })
                .ToListAsync(cancellationToken);
        var sourceNotificationKeys = sourceNotifications
            .Select(item => (item.RecipientUserId, item.EventKey))
            .ToHashSet();

        var result = new List<NotificationDelivery>();
        foreach (var delivery in candidatesWithoutRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(delivery.Channel, NotificationChannels.WebPush, StringComparison.OrdinalIgnoreCase)
                && !IsRetryableWebPushDelivery(delivery, activeSubscriptionKeys, sourceNotificationKeys))
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

    private static bool IsRetryableWebPushDelivery(
        NotificationDelivery delivery,
        IReadOnlySet<(int UserId, string Endpoint)> activeSubscriptionKeys,
        IReadOnlySet<(int RecipientUserId, string EventKey)> sourceNotificationKeys)
    {
        if (WebPushSubscriptionHealth.IsNonRetryableDeliveryError(delivery.Error))
        {
            return false;
        }

        if (!delivery.RecipientUserId.HasValue || string.IsNullOrWhiteSpace(delivery.RecipientAddress))
        {
            return false;
        }

        if (!activeSubscriptionKeys.Contains((delivery.RecipientUserId.Value, delivery.RecipientAddress)))
        {
            return false;
        }

        var baseEventKey = ResolveBaseEventKey(delivery.EventKey);
        return sourceNotificationKeys.Contains((delivery.RecipientUserId.Value, baseEventKey));
    }

    private static Expression<Func<NotificationDelivery, bool>> BuildEventKeyPrefixPredicate(
        IReadOnlyCollection<string> prefixes)
    {
        var delivery = Expression.Parameter(typeof(NotificationDelivery), "delivery");
        var eventKey = Expression.Property(delivery, nameof(NotificationDelivery.EventKey));
        var startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        Expression body = Expression.Constant(false);
        foreach (var prefix in prefixes)
        {
            body = Expression.OrElse(body, Expression.Call(eventKey, startsWith, Expression.Constant(prefix)));
        }

        return Expression.Lambda<Func<NotificationDelivery, bool>>(body, delivery);
    }

    private static async Task<IReadOnlyList<string>> LoadChildEventKeysAsync(
        PaymentContext context,
        IReadOnlyList<string> prefixes,
        CancellationToken cancellationToken)
    {
        if (prefixes.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        for (var offset = 0; offset < prefixes.Count; offset += PrefixQueryBatchSize)
        {
            var batch = prefixes.Skip(offset).Take(PrefixQueryBatchSize).ToArray();
            result.AddRange(await context.NotificationDeliveries
                .AsNoTracking()
                .Where(BuildEventKeyPrefixPredicate(batch))
                .Select(item => item.EventKey)
                .ToListAsync(cancellationToken));
        }

        return result;
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

    private static string ResolveParentEventKey(string eventKey)
    {
        var retryIndex = eventKey.LastIndexOf(":retry:", StringComparison.Ordinal);
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
