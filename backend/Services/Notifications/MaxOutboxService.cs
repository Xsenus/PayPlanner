using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Notifications;

public sealed record MaxQueueRequest(
    int RecipientUserId,
    string Scenario,
    string EventKey,
    string Text,
    string? ActionText = null,
    string? ActionUrl = null,
    string? NotificationType = null);

public interface IMaxOutboxService
{
    Task<MaxOutboxMessage> QueueAsync(MaxQueueRequest request, CancellationToken ct);
}

public static class MaxMessageTemplateCatalog
{
    public static string RenderNotification(string notificationType) => notificationType switch
    {
        UserNotificationTypes.SystemAdminWarning => "В CRM есть критическое системное предупреждение. Откройте CRM, чтобы безопасно посмотреть подробности.",
        _ => throw new InvalidOperationException("Для события не настроен безопасный шаблон MAX."),
    };
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
        var runtime = await settingsService.GetRuntimeSettingsAsync(ct);
        if (!runtime.Ready) throw new InvalidOperationException("Канал MAX выключен или настроен не полностью.");
        var scenario = request.Scenario?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IsScenarioEnabled(runtime, scenario)) throw new InvalidOperationException("Сценарий MAX выключен администратором.");
        string? notificationType = null;
        if (scenario == MaxMessengerScenarios.Notifications)
        {
            notificationType = UserNotificationTypes.Find(request.NotificationType)?.Key;
            if (!string.Equals(notificationType, UserNotificationTypes.SystemAdminWarning, StringComparison.Ordinal))
                throw new InvalidOperationException("Событие пока не разрешено для MAX.");
            if (!await IsNotificationAllowedForRecipientAsync(request.RecipientUserId, notificationType!, ct))
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
        if (runtime.QuietHoursEnabled && IsWithinQuietHours(TimeOnly.FromDateTime(localNow), runtime.QuietHoursStart, runtime.QuietHoursEnd))
            throw new InvalidOperationException("Сообщение MAX не создано: действуют тихие часы.");
        var localDayStart = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(localDayStart, timeZone);
        var dayEndUtc = TimeZoneInfo.ConvertTimeToUtc(localDayStart.AddDays(1), timeZone);
        var sentToday = await db.MaxOutboxMessages.AsNoTracking()
            .CountAsync(item => item.RecipientLinkId == link.Id
                && item.CreatedAt >= dayStartUtc && item.CreatedAt < dayEndUtc
                && item.Status != MaxOutboxStatuses.Cancelled, ct);
        if (sentToday >= runtime.DailyLimitPerRecipient)
            throw new InvalidOperationException("Достигнут дневной лимит MAX для получателя.");

        var message = new MaxOutboxMessage
        {
            EventKey = normalizedEventKey,
            Scenario = scenario,
            RecipientLinkId = link.Id,
            RecipientUserId = request.RecipientUserId,
            ProtectedText = _protector.Protect(text),
            ProtectedActionUrl = actionUri is null ? null : _protector.Protect(actionUri.AbsoluteUri),
            ActionText = string.IsNullOrWhiteSpace(request.ActionText) ? null : request.ActionText.Trim()[..Math.Min(request.ActionText.Trim().Length, 128)],
            IdempotencyKey = idempotencyKey,
            Status = MaxOutboxStatuses.Queued,
            NextAttemptAt = now,
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

    private static bool IsScenarioEnabled(MaxRuntimeSettings settings, string scenario) => scenario switch
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

    private static bool IsWithinQuietHours(TimeOnly now, TimeOnly start, TimeOnly end)
        => start == end ? false : start < end ? now >= start && now < end : now >= start || now < end;

    private async Task<bool> IsNotificationAllowedForRecipientAsync(int userId, string notificationType, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .Where(item => item.Id == userId && item.IsActive && item.IsApproved)
            .Select(item => new
            {
                item.RoleId,
                RoleName = item.Role != null ? item.Role.Name : string.Empty,
                Preference = db.UserSettings
                    .Where(setting => setting.UserId == item.Id)
                    .Where(setting => setting.Key == UserNotificationPreferenceSettings.SettingKey)
                    .Select(setting => setting.Value)
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (user is null) return false;
        UserNotificationPreferenceSnapshot preferences;
        if (user.Preference is not null)
        {
            preferences = UserNotificationPreferenceSettings.Parse(user.Preference);
        }
        else
        {
            var roleSetting = await db.SystemSettings.AsNoTracking()
                .Where(item => item.Key == RoleNotificationPreferenceSettings.SettingKey)
                .Select(item => item.Value)
                .FirstOrDefaultAsync(ct);
            preferences = RoleNotificationPreferenceSettings.ParseForRole(roleSetting, user.RoleId)
                ?? RoleNotificationPreferenceDefaults.Resolve(user.RoleName).Preferences;
        }
        return UserNotificationPreferenceSettings.IsChannelEnabled(preferences, notificationType, NotificationChannels.Max);
    }
}

public interface IMaxOutboxProcessor
{
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
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(MaxOutboxService.ProtectorPurpose);

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var settings = await settingsService.GetRuntimeSettingsAsync(ct);
        if (!settings.Ready) return 0;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var stale = now.AddMinutes(-5);
        var messages = await db.MaxOutboxMessages
            .Include(item => item.RecipientLink)
            .Where(item => item.AttemptCount < item.MaxAttempts
                           && ((item.Status == MaxOutboxStatuses.Queued && item.NextAttemptAt <= now)
                               || (item.Status == MaxOutboxStatuses.Failed && item.NextAttemptAt <= now)
                               || (item.Status == MaxOutboxStatuses.Processing && item.UpdatedAt < stale)))
            .OrderBy(item => item.NextAttemptAt)
            .ThenBy(item => item.Id)
            .Take(20)
            .ToListAsync(ct);
        var lastSendByRecipient = new Dictionary<long, DateTime>();
        foreach (var message in messages)
        {
            if (message.RecipientLink.Status != MaxRecipientLinkStatuses.Verified || !message.RecipientLink.MaxUserId.HasValue)
            {
                message.Status = MaxOutboxStatuses.Cancelled;
                message.LastErrorCode = "recipient-unlinked";
                message.UpdatedAt = now;
                continue;
            }
            message.Status = MaxOutboxStatuses.Processing;
            message.AttemptCount++;
            message.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            try
            {
                var recipientId = message.RecipientLink.MaxUserId.Value;
                if (lastSendByRecipient.TryGetValue(recipientId, out var lastSend))
                {
                    var remaining = TimeSpan.FromMilliseconds(500) - (timeProvider.GetUtcNow().UtcDateTime - lastSend);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
                }
                var text = _protector.Unprotect(message.ProtectedText);
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
