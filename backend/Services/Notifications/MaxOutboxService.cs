using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Services.Notifications;

public sealed record MaxQueueRequest(
    int? RecipientUserId,
    string Scenario,
    string EventKey,
    string Text,
    string? ActionText = null,
    string? ActionUrl = null,
    string? NotificationType = null,
    int? RecipientPortalAccountId = null);

public interface IMaxOutboxService
{
    Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct);
    Task<bool> RetryAsync(Guid publicId, int? actorUserId, string actorName, CancellationToken ct);
}

public static class MaxMessageTemplateCatalog
{
    private const string PayloadPrefix = "pp-max-notification:v1:";
    private static readonly IReadOnlyDictionary<string, string[]> LegacyTypesByText = UserNotificationTypes.All
        .Where(definition => !NotificationPriorities.IsSilent(definition.Priority))
        .GroupBy(definition => RenderNotification(definition.Key), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.Select(definition => definition.Key).ToArray(), StringComparer.Ordinal);

    internal static string CreateNotificationPayload(string notificationType) => PayloadPrefix + notificationType;

    internal static string? ResolveNotificationType(string payload, string? sourceType)
    {
        var sourceDefinition = UserNotificationTypes.Find(sourceType);
        if (sourceType is not null && (sourceDefinition is null || NotificationPriorities.IsSilent(sourceDefinition.Priority)))
            return null;

        UserNotificationTypeDefinition? definition;
        if (payload.StartsWith("pp-max-notification:", StringComparison.Ordinal))
        {
            if (!payload.StartsWith(PayloadPrefix, StringComparison.Ordinal)) return null;
            definition = UserNotificationTypes.Find(payload[PayloadPrefix.Length..]);
        }
        else
        {
            // Старые записи содержат только нейтральный шаблон. Не читаем всю
            // историю CRM ради поиска хеша: используем лишь однозначный шаблон
            // либо найденное по короткому ключу исходное уведомление.
            LegacyTypesByText.TryGetValue(payload, out var legacyTypes);
            if (legacyTypes is { Length: > 1 }) return null;
            definition = legacyTypes is { Length: 1 }
                ? UserNotificationTypes.Find(legacyTypes[0])
                : sourceDefinition;
        }

        return definition is null || NotificationPriorities.IsSilent(definition.Priority)
               || (sourceDefinition is not null && sourceDefinition.Key != definition.Key)
            ? null
            : definition.Key;
    }

    public static string RenderNotification(string notificationType)
    {
        var definition = UserNotificationTypes.Find(notificationType);
        if (definition is null || NotificationPriorities.IsSilent(definition.Priority))
            throw new InvalidOperationException("Для события не настроен безопасный шаблон MAX.");

        return definition.Key == UserNotificationTypes.SystemAdminWarning
            ? "В CRM есть критическое системное предупреждение. Откройте CRM, чтобы безопасно посмотреть подробности."
            : $"В CRM новое уведомление: {definition.Label}. Откройте CRM, чтобы безопасно посмотреть подробности.";
    }
}

internal static class MaxNotificationRecipientPolicy
{
    public static async Task<IReadOnlyDictionary<int, UserNotificationPreferenceSnapshot>> LoadAsync(
        PaymentContext db, IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0)
            return new Dictionary<int, UserNotificationPreferenceSnapshot>();

        var users = await db.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id) && user.IsActive && user.IsApproved)
            .Select(user => new
            {
                user.Id,
                user.RoleId,
                RoleName = user.Role != null ? user.Role.Name : string.Empty,
                Preference = db.UserSettings
                    .Where(setting => setting.UserId == user.Id && setting.Key == UserNotificationPreferenceSettings.SettingKey)
                    .Select(setting => setting.Value)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var roleSetting = users.Any(user => user.Preference is null)
            ? await db.SystemSettings.AsNoTracking()
                .Where(setting => setting.Key == RoleNotificationPreferenceSettings.SettingKey)
                .Select(setting => setting.Value)
                .FirstOrDefaultAsync(ct)
            : null;

        return users.ToDictionary(
            user => user.Id,
            user => user.Preference is not null
                ? UserNotificationPreferenceSettings.Parse(user.Preference)
                : RoleNotificationPreferenceSettings.ParseForRole(roleSetting, user.RoleId)
                    ?? RoleNotificationPreferenceDefaults.Resolve(user.RoleName).Preferences);
    }
}

public sealed class MaxOutboxService(
    PaymentContext db,
    IMaxNotificationService settingsService,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider) : IMaxOutboxService
{
    internal const string ProtectorPurpose = "PayPlanner.Notifications.Max.Outbox.v1";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    public async Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct)
    {
        if (request.RecipientUserId.HasValue == request.RecipientPortalAccountId.HasValue)
            throw new InvalidOperationException("Укажите ровно одного получателя MAX: сотрудника или аккаунт клиентского портала.");
        var runtime = await settingsService.GetRuntimeSettingsAsync(ct);
        if (!runtime.Ready) throw new InvalidOperationException("Канал MAX выключен или настроен не полностью.");
        var scenario = request.Scenario?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsScenarioEnabled(runtime, scenario)) throw new InvalidOperationException("Сценарий MAX выключен администратором.");
        string? notificationType = null;
        if (scenario == MaxMessengerScenarios.Notifications)
        {
            if (!request.RecipientUserId.HasValue)
                throw new InvalidOperationException("Системные уведомления MAX доступны только сотрудникам.");
            notificationType = UserNotificationTypes.Find(request.NotificationType)?.Key;
            if (notificationType is null
                || NotificationPriorities.IsSilent(UserNotificationTypes.Find(notificationType)?.Priority))
                throw new InvalidOperationException("Событие пока не разрешено для MAX.");
            if (!await IsNotificationAllowedForRecipientAsync(request.RecipientUserId.Value, notificationType!, ct))
                throw new InvalidOperationException("Получатель отключил это уведомление MAX.");
        }
        var text = notificationType is null
            ? request.Text?.Trim() ?? string.Empty
            : MaxMessageTemplateCatalog.RenderNotification(notificationType);
        if (text.Length is 0 or > MaxApiClient.MaxTextLength) throw new InvalidOperationException("Текст MAX должен содержать от 1 до 4000 символов.");
        Uri? actionUri = null;
        if (!string.IsNullOrWhiteSpace(request.ActionUrl))
        {
            if (!Uri.TryCreate(request.ActionUrl.Trim(), UriKind.Absolute, out actionUri)
                || actionUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Ссылка MAX должна использовать HTTPS.");
        }
        if (scenario is MaxMessengerScenarios.Notifications or MaxMessengerScenarios.Documents or MaxMessengerScenarios.Confirmations && actionUri is null)
            throw new InvalidOperationException("Для уведомления, документа или подтверждения обязательна защищённая HTTPS-ссылка.");

        var link = await db.MaxRecipientLinks.AsNoTracking()
            .Where(item => item.UserId == request.RecipientUserId
                           && item.ClientPortalAccountId == request.RecipientPortalAccountId
                           && item.Status == MaxRecipientLinkStatuses.Verified
                           && item.MaxUserId != null)
            .OrderByDescending(item => item.VerifiedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Получатель не привязал MAX или отозвал согласие.");

        var normalizedEventKey = NormalizeEventKey(request.EventKey);
        var idempotencyKey = Sha256($"{normalizedEventKey}|{scenario}|{link.Id}|{Sha256(text)}|{actionUri?.AbsoluteUri}");
        var existing = await db.MaxOutboxMessages.FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null) return existing;

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(runtime.QuietHoursTimeZone);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone);
        var nextAttemptAt = now;
        if (runtime.QuietHoursEnabled && IsWithinQuietHours(TimeOnly.FromDateTime(localNow), runtime.QuietHoursStart, runtime.QuietHoursEnd))
        {
            if (scenario != MaxMessengerScenarios.Notifications)
                throw new InvalidOperationException("Сообщение MAX не создано: действуют тихие часы.");
            nextAttemptAt = NextQuietHoursEndUtc(localNow, runtime.QuietHoursStart, runtime.QuietHoursEnd, timeZone);
        }
        var scheduledLocal = TimeZoneInfo.ConvertTimeFromUtc(nextAttemptAt, timeZone);
        var localDayStart = DateTime.SpecifyKind(scheduledLocal.Date, DateTimeKind.Unspecified);
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localDayStart, timeZone);
        var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(localDayStart.AddDays(1), timeZone);
        var sentToday = await db.MaxOutboxMessages.AsNoTracking()
            .CountAsync(item => item.RecipientLinkId == link.Id
                && ((item.NextAttemptAt >= dayStartUtc && item.NextAttemptAt < dayEndUtc
                        && item.Status != MaxOutboxStatuses.Cancelled)
                    || (item.SentAt >= dayStartUtc && item.SentAt < dayEndUtc)
                    || item.Status == MaxOutboxStatuses.Processing), ct);
        if (sentToday >= runtime.DailyLimitPerRecipient)
        {
            if (scenario != MaxMessengerScenarios.Notifications)
                throw new InvalidOperationException("Достигнут дневной лимит MAX для получателя.");
            // Уведомление CRM уже сохранено и не будет создано повторно. Сохраняем
            // доставку MAX до следующих суток, а worker проверит свободный лимит.
            nextAttemptAt = NextQuotaDayUtc(scheduledLocal, runtime, timeZone);
        }

        var message = new MaxOutboxMessage
        {
            EventKey = normalizedEventKey,
            Scenario = scenario,
            RecipientLinkId = link.Id,
            RecipientUserId = request.RecipientUserId,
            ProtectedText = _protector.Protect(notificationType is null
                ? text
                : MaxMessageTemplateCatalog.CreateNotificationPayload(notificationType)),
            ProtectedActionUrl = actionUri is null ? null : _protector.Protect(actionUri.AbsoluteUri),
            ActionText = string.IsNullOrWhiteSpace(request.ActionText) ? null : request.ActionText.Trim()[..Math.Min(request.ActionText.Trim().Length, 128)],
            IdempotencyKey = idempotencyKey,
            Status = MaxOutboxStatuses.Queued,
            NextAttemptAt = nextAttemptAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MaxOutboxMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
            return message;
        }
        catch (DbUpdateException)
        {
            db.Entry(message).State = EntityState.Detached;
            var concurrent = await db.MaxOutboxMessages.FirstOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
            if (concurrent is not null) return concurrent;
            throw;
        }
    }

    public async Task<bool> RetryAsync(Guid publicId, int? actorUserId, string actorName, CancellationToken ct)
    {
        var message = await db.MaxOutboxMessages
            .Include(item => item.RecipientLink)
            .FirstOrDefaultAsync(item => item.PublicId == publicId, ct);
        if (message is null) return false;
        if (message.Status != MaxOutboxStatuses.Failed || message.AttemptCount >= message.MaxAttempts)
            throw new InvalidOperationException("Повтор доступен только для неокончательной ошибки MAX с оставшимися попытками.");
        if (message.RecipientLink.Status != MaxRecipientLinkStatuses.Verified || !message.RecipientLink.MaxUserId.HasValue)
            throw new InvalidOperationException("Получатель MAX больше не привязан.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        message.Status = MaxOutboxStatuses.Queued;
        message.NextAttemptAt = now;
        message.LastErrorCode = null;
        message.UpdatedAt = now;
        db.ObjectAuditEvents.Add(new ObjectAuditEvent
        {
            ObjectType = "max-outbox-message",
            ObjectId = message.Id,
            Action = "Retried",
            Description = "Администратор повторно поставил доставку MAX в очередь.",
            ActorUserId = actorUserId,
            ActorUserName = actorName,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                message.PublicId,
                message.EventKey,
                message.Scenario,
                previousAttempts = message.AttemptCount
            }),
            CreatedAt = now
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    internal static bool IsScenarioEnabled(MaxRuntimeSettings settings, string scenario) => scenario switch
    {
        MaxMessengerScenarios.Notifications => settings.NotificationsEnabled,
        MaxMessengerScenarios.Documents => settings.DocumentsEnabled,
        MaxMessengerScenarios.Correspondence => settings.CorrespondenceEnabled,
        MaxMessengerScenarios.Confirmations => settings.ConfirmationsEnabled,
        _ => false
    };

    private static string NormalizeEventKey(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0) throw new InvalidOperationException("Event key MAX обязателен.");
        return normalized.Length <= 64 ? normalized : $"sha256:{Sha256(normalized)[..57]}";
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static bool IsWithinQuietHours(TimeOnly now, TimeOnly start, TimeOnly end)
        => start == end ? false : start < end ? now >= start && now < end : now >= start || now < end;

    internal static DateTime NextQuietHoursEndUtc(DateTime localNow, TimeOnly start, TimeOnly end, TimeZoneInfo timeZone)
    {
        var endDate = start > end && TimeOnly.FromDateTime(localNow) >= start
            ? localNow.Date.AddDays(1)
            : localNow.Date;
        var localEnd = endDate.Add(end.ToTimeSpan());
        while (timeZone.IsInvalidTime(localEnd))
            localEnd = localEnd.AddMinutes(1);
        return TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
    }

    internal static DateTime NextQuotaDayUtc(DateTime localNow, MaxRuntimeSettings settings, TimeZoneInfo timeZone)
    {
        var nextDay = DateTime.SpecifyKind(localNow.Date.AddDays(1), DateTimeKind.Unspecified);
        while (timeZone.IsInvalidTime(nextDay))
            nextDay = nextDay.AddMinutes(1);
        return settings.QuietHoursEnabled
               && IsWithinQuietHours(TimeOnly.FromDateTime(nextDay), settings.QuietHoursStart, settings.QuietHoursEnd)
            ? NextQuietHoursEndUtc(nextDay, settings.QuietHoursStart, settings.QuietHoursEnd, timeZone)
            : TimeZoneInfo.ConvertTimeToUtc(nextDay, timeZone);
    }

    private async Task<bool> IsNotificationAllowedForRecipientAsync(int userId, string notificationType, CancellationToken ct)
    {
        var preferences = await MaxNotificationRecipientPolicy.LoadAsync(db, [userId], ct);
        return preferences.TryGetValue(userId, out var recipientPreferences)
            && UserNotificationPreferenceSettings.IsChannelEnabled(recipientPreferences, notificationType, NotificationChannels.Max);
    }
}

public interface IMaxOutboxProcessor
{
    /// <summary>
    /// Забирает готовую порцию сообщений MAX, фиксирует их начальное состояние одним
    /// сохранением и затем последовательно отправляет допустимые сообщения провайдеру.
    /// </summary>
    /// <returns>Количество записей, рассмотренных в текущем проходе.</returns>
    Task<int> ProcessBatchAsync(CancellationToken ct);
}

public sealed class MaxOutboxProcessor(
    PaymentContext db,
    IMaxNotificationService settingsService,
    IMaxApiClient apiClient,
    IDataProtectionProvider dataProtectionProvider,
    TimeProvider timeProvider,
    ILogger<MaxOutboxProcessor> logger) : IMaxOutboxProcessor
{
    private const string UnknownDeliveryError = "delivery-outcome-unknown";
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(MaxOutboxService.ProtectorPurpose);

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetRuntimeSettingsAsync(ct);
        if (!settings.Ready) return 0;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var stale = now.AddMinutes(-5);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(settings.QuietHoursTimeZone);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(now, timeZone);
        var quietHoursActive = settings.QuietHoursEnabled
            && MaxOutboxService.IsWithinQuietHours(TimeOnly.FromDateTime(localNow), settings.QuietHoursStart, settings.QuietHoursEnd);
        var quietHoursEndUtc = quietHoursActive
            ? MaxOutboxService.NextQuietHoursEndUtc(localNow, settings.QuietHoursStart, settings.QuietHoursEnd, timeZone)
            : now;
        List<MaxOutboxMessage> messages;
        var lastSendByRecipient = new Dictionary<long, DateTime>();
        var processableMessages = new List<(MaxOutboxMessage Message, long RecipientId, string? NotificationText)>();
        try
        {
            await using var leaseTransaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            messages = await db.MaxOutboxMessages
                .Include(item => item.RecipientLink)
                .Where(item => (item.AttemptCount < item.MaxAttempts
                                && (item.Status == MaxOutboxStatuses.Queued || item.Status == MaxOutboxStatuses.Failed)
                                && item.NextAttemptAt <= now)
                               || (item.Status == MaxOutboxStatuses.Processing && item.UpdatedAt < stale))
                .OrderBy(item => item.NextAttemptAt)
                .ThenBy(item => item.Id)
                .Take(20)
                .ToListAsync(ct);
            processableMessages.Capacity = messages.Count;
            var notificationMessages = messages
                .Where(message => message.Scenario == MaxMessengerScenarios.Notifications && message.RecipientUserId.HasValue)
                .ToArray();
            var notificationRecipientIds = notificationMessages
                .Select(message => message.RecipientUserId!.Value)
                .Distinct()
                .ToArray();
            var recipientPreferences = await MaxNotificationRecipientPolicy.LoadAsync(db, notificationRecipientIds, ct);
            var eventKeys = notificationMessages.Select(message => message.EventKey).Distinct().ToArray();
            var notificationTypes = eventKeys.Length == 0
                ? new Dictionary<(int RecipientUserId, string EventKey), string>()
                : (await db.UserNotifications.AsNoTracking()
                    .Where(notification => notificationRecipientIds.Contains(notification.RecipientUserId)
                        && eventKeys.Contains(notification.EventKey))
                    .Select(notification => new { notification.RecipientUserId, notification.EventKey, notification.Type })
                    .ToListAsync(ct))
                    .GroupBy(notification => (notification.RecipientUserId, notification.EventKey))
                    .ToDictionary(group => group.Key, group => group.First().Type);
            var localDayStart = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
            var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localDayStart, timeZone);
            var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(localDayStart.AddDays(1), timeZone);
            var linkIds = messages.Select(message => message.RecipientLinkId).Distinct().ToArray();
            var leasedIds = messages.Select(message => message.Id).ToArray();
            // Одна агрегатная выборка на порцию. Processing резервирует место до
            // ответа провайдера, включая отправки другого worker через полночь.
            // Последние прерванные попытки резервируются до обработки всей порции.
            var dailyUsage = quietHoursActive || linkIds.Length == 0
                ? new Dictionary<int, int>()
                : await db.MaxOutboxMessages.AsNoTracking()
                    .Where(item => linkIds.Contains(item.RecipientLinkId)
                        && ((item.SentAt >= dayStartUtc && item.SentAt < dayEndUtc)
                            || (item.Status == MaxOutboxStatuses.Processing
                                && (!leasedIds.Contains(item.Id) || item.AttemptCount >= item.MaxAttempts))
                            || (item.Status == MaxOutboxStatuses.Failed && item.LastErrorCode == UnknownDeliveryError
                                && item.UpdatedAt >= dayStartUtc && item.UpdatedAt < dayEndUtc)))
                    .GroupBy(item => item.RecipientLinkId)
                    .Select(group => new { LinkId = group.Key, Count = group.Count() })
                    .ToDictionaryAsync(item => item.LinkId, item => item.Count, ct);
            foreach (var message in messages)
            {
                if (message.Status == MaxOutboxStatuses.Processing && message.AttemptCount >= message.MaxAttempts)
                {
                    // Последняя попытка могла дойти до провайдера до обрыва worker.
                    // Не повторяем её и сохраняем резерв только на сутки восстановления.
                    MarkFailed(message, UnknownDeliveryError, false, now);
                    message.UpdatedAt = now;
                    continue;
                }

                if (message.RecipientLink.Status != MaxRecipientLinkStatuses.Verified
                    || message.RecipientLink.MaxUserId is not long recipientId)
                {
                    message.Status = MaxOutboxStatuses.Cancelled;
                    message.LastErrorCode = "recipient-unlinked";
                    message.UpdatedAt = now;
                    continue;
                }

                string? notificationText = null;
                if (message.Scenario == MaxMessengerScenarios.Notifications)
                {
                    if (message.RecipientUserId is not int userId
                        || !recipientPreferences.TryGetValue(userId, out var preferences))
                    {
                        message.Status = MaxOutboxStatuses.Cancelled;
                        message.LastErrorCode = "recipient-preference-disabled";
                        message.UpdatedAt = now;
                        continue;
                    }

                    try
                    {
                        var payload = _protector.Unprotect(message.ProtectedText);
                        notificationTypes.TryGetValue((userId, message.EventKey), out var sourceType);
                        var notificationType = MaxMessageTemplateCatalog.ResolveNotificationType(payload, sourceType);
                        if (notificationType is null
                            || !UserNotificationPreferenceSettings.IsChannelEnabled(preferences, notificationType, NotificationChannels.Max))
                        {
                            message.Status = MaxOutboxStatuses.Cancelled;
                            message.LastErrorCode = notificationType is null ? "notification-type-unresolved" : "recipient-preference-disabled";
                            message.UpdatedAt = now;
                            continue;
                        }
                        notificationText = MaxMessageTemplateCatalog.RenderNotification(notificationType);
                        var typedPayload = MaxMessageTemplateCatalog.CreateNotificationPayload(notificationType);
                        if (payload != typedPayload)
                            message.ProtectedText = _protector.Protect(typedPayload);
                    }
                    catch (CryptographicException error)
                    {
                        logger.LogError("MAX outbox message {MessageId} has invalid protected payload ({ExceptionType}).", message.Id, error.GetType().Name);
                        MarkFailed(message, "protected-payload-invalid", false, now);
                        message.UpdatedAt = now;
                        continue;
                    }
                }

                if (!MaxOutboxService.IsScenarioEnabled(settings, message.Scenario))
                {
                    message.Status = MaxOutboxStatuses.Cancelled;
                    message.LastErrorCode = "scenario-disabled";
                    message.UpdatedAt = now;
                    continue;
                }

                if (quietHoursActive)
                {
                    message.Status = MaxOutboxStatuses.Queued;
                    message.NextAttemptAt = quietHoursEndUtc;
                    message.UpdatedAt = now;
                    continue;
                }

                var used = dailyUsage.GetValueOrDefault(message.RecipientLinkId);
                if (used >= settings.DailyLimitPerRecipient)
                {
                    message.Status = MaxOutboxStatuses.Queued;
                    message.NextAttemptAt = MaxOutboxService.NextQuotaDayUtc(localNow, settings, timeZone);
                    message.UpdatedAt = now;
                    continue;
                }
                dailyUsage[message.RecipientLinkId] = used + 1;
                message.Status = MaxOutboxStatuses.Processing;
                message.AttemptCount++;
                message.UpdatedAt = now;
                // Идентификатор фиксируется вместе с решением о допустимости отправки.
                // Последующий цикл не перечитывает nullable-поле связи и работает
                // только с уже проверенным значением получателя.
                processableMessages.Add((message, recipientId, notificationText));
            }

            if (messages.Count > 0)
            {
                // Захват порции и перевод в processing коммитятся атомарно. При
                // параллельном worker второй экземпляр не сможет отправить ту же запись.
                await db.SaveChangesAsync(ct);
            }
            await leaseTransaction.CommitAsync(ct);
        }
        catch (Exception exception) when (FinancialConcurrencyGuard.IsSerializationFailure(exception))
        {
            db.ChangeTracker.Clear();
            return 0;
        }

        foreach (var (message, recipientId, notificationText) in processableMessages)
        {
            try
            {
                if (lastSendByRecipient.TryGetValue(recipientId, out var lastSend))
                {
                    var remaining = TimeSpan.FromMilliseconds(500) - (timeProvider.GetUtcNow().UtcDateTime - lastSend);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
                }
                var text = notificationText ?? _protector.Unprotect(message.ProtectedText);
                var actionUrl = string.IsNullOrWhiteSpace(message.ProtectedActionUrl)
                    ? null
                    : new Uri(_protector.Unprotect(message.ProtectedActionUrl));
                var result = await apiClient.SendTextAsync(
                    recipientId, text, settings.BotToken, message.ActionText, actionUrl, ct);
                lastSendByRecipient[recipientId] = timeProvider.GetUtcNow().UtcDateTime;
                if (result.Success)
                {
                    message.Status = MaxOutboxStatuses.Sent;
                    message.ProviderMessageId = result.ExternalMessageId;
                    message.LastErrorCode = null;
                    message.SentAt = timeProvider.GetUtcNow().UtcDateTime;
                }
                else
                {
                    MarkFailed(message, result.ErrorCode ?? "provider-error", result.IsTransient, now);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                MarkFailed(message, "provider-timeout", true, now);
            }
            catch (Exception error) when (error is CryptographicException or UriFormatException)
            {
                logger.LogError("MAX outbox message {MessageId} has invalid protected payload ({ExceptionType}).", message.Id, error.GetType().Name);
                MarkFailed(message, "protected-payload-invalid", false, now);
            }
            message.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        return messages.Count;
    }

    private static void MarkFailed(MaxOutboxMessage message, string code, bool retry, DateTime now)
    {
        message.Status = MaxOutboxStatuses.Failed;
        message.LastErrorCode = code[..Math.Min(code.Length, 128)];
        if (!retry || message.AttemptCount >= message.MaxAttempts)
        {
            message.AttemptCount = message.MaxAttempts;
            message.NextAttemptAt = now;
            return;
        }
        var minutes = message.AttemptCount switch { 1 => 1, 2 => 5, 3 => 30, _ => 180 };
        message.NextAttemptAt = now.AddMinutes(minutes);
    }
}

public sealed class MaxOutboxWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MaxOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMaxOutboxProcessor>().ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogError("MAX outbox worker failed with {ExceptionType}.", error.GetType().Name);
            }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
        }
    }
}
