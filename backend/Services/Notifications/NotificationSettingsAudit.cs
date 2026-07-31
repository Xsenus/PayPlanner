using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public static class NotificationSettingsAudit
{
    public const string ObjectType = "notification-settings";
    public const string UpdatedAction = "Updated";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ObjectAuditEvent? CreateSmsSettingsUpdatedEvent(
        ClaimsPrincipal? user,
        SmsNotificationSettingsDto previous,
        SmsNotificationSettingsDto next,
        bool apiTokenChanged)
    {
        var changes = new List<NotificationSettingsChange>();
        AddIfChanged(changes, "SmsEnabled", FormatBoolean(previous.Enabled), FormatBoolean(next.Enabled));
        AddIfChanged(changes, "SmsProvider", previous.ProviderKey, next.ProviderKey);
        AddIfChanged(changes, "SmsSenderName", previous.SenderName, next.SenderName);
        AddIfChanged(changes, "SmsDailyLimit", FormatNumber(previous.DailyLimit), FormatNumber(next.DailyLimit));
        AddIfChanged(
            changes,
            "SmsRecipientDailyLimit",
            FormatNumber(previous.RecipientDailyLimit),
            FormatNumber(next.RecipientDailyLimit));
        AddIfChanged(
            changes,
            "SmsBurstLimitPerMinute",
            FormatNumber(previous.BurstLimitPerMinute),
            FormatNumber(next.BurstLimitPerMinute));
        AddIfChanged(
            changes,
            "SmsMaxMessageLength",
            FormatNumber(previous.MaxMessageLength),
            FormatNumber(next.MaxMessageLength));
        AddIfChanged(
            changes,
            "SmsLowBalanceThreshold",
            previous.LowBalanceThreshold.ToString(CultureInfo.InvariantCulture),
            next.LowBalanceThreshold.ToString(CultureInfo.InvariantCulture));
        AddIfChanged(changes, "SmsEmergencyStop", FormatBoolean(previous.EmergencyStop), FormatBoolean(next.EmergencyStop));
        AddIfChanged(
            changes,
            "SmsDisabledEvents",
            FormatDisabledEvents(previous.EventTypes),
            FormatDisabledEvents(next.EventTypes));
        AddIfChanged(
            changes,
            "SmsQuietHoursEnabled",
            FormatBoolean(previous.QuietHoursEnabled),
            FormatBoolean(next.QuietHoursEnabled));
        AddIfChanged(changes, "SmsQuietHoursStart", previous.QuietHoursStart, next.QuietHoursStart);
        AddIfChanged(changes, "SmsQuietHoursEnd", previous.QuietHoursEnd, next.QuietHoursEnd);
        AddIfChanged(changes, "SmsQuietHoursTimeZone", previous.QuietHoursTimeZone, next.QuietHoursTimeZone);

        if (apiTokenChanged)
        {
            changes.Add(new NotificationSettingsChange(
                "SmsApiToken",
                previous.ApiTokenSet ? "настроен" : "не задан",
                next.ApiTokenSet
                    ? previous.ApiTokenSet ? "обновлён" : "настроен"
                    : "не задан"));
        }

        if (changes.Count == 0)
        {
            return null;
        }

        return CreateEvent(
            user,
            $"Изменены настройки SMS: {string.Join(", ", changes.Select(item => GetFieldDescription(item.Field)).Distinct())}.",
            new
            {
                category = "sms",
                changedFields = changes.Select(item => item.Field).ToArray(),
                changes,
            });
    }

    public static ObjectAuditEvent? CreateSmsTemplateUpdatedEvent(
        ClaimsPrincipal? user,
        NotificationTemplateDto previous,
        NotificationTemplateDto next,
        bool reset)
    {
        if (!next.Key.StartsWith("sms.", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var changes = new List<NotificationSettingsChange>();
        AddIfChanged(changes, "SmsTemplateEnabled", FormatBoolean(previous.IsEnabled), FormatBoolean(next.IsEnabled));
        AddContentChange(changes, "SmsTemplateSubject", previous.Subject, next.Subject);
        AddContentChange(changes, "SmsTemplateBody", previous.Body, next.Body);
        if (changes.Count == 0)
        {
            return null;
        }

        return CreateEvent(
            user,
            reset
                ? $"Восстановлен стандартный SMS-шаблон «{next.Label}»."
                : $"Изменён SMS-шаблон «{next.Label}».",
            new
            {
                category = "sms-template",
                templateKey = next.Key,
                reset,
                changedFields = changes.Select(item => item.Field).ToArray(),
                changes,
            });
    }

    public static ObjectAuditEvent? CreateMaxSettingsUpdatedEvent(
        ClaimsPrincipal? user,
        MaxNotificationDiagnosticsDto previous,
        MaxNotificationDiagnosticsDto next,
        bool botTokenChanged,
        bool webhookSecretChanged)
    {
        var changes = new List<NotificationSettingsChange>();
        AddIfChanged(changes, "MaxEnabled", FormatBoolean(previous.Enabled), FormatBoolean(next.Enabled));
        AddIfChanged(changes, "MaxEmergencyStop", FormatBoolean(previous.EmergencyStop), FormatBoolean(next.EmergencyStop));
        AddIfChanged(changes, "MaxNotifications", FormatBoolean(previous.NotificationsEnabled), FormatBoolean(next.NotificationsEnabled));
        AddIfChanged(changes, "MaxDocuments", FormatBoolean(previous.DocumentsEnabled), FormatBoolean(next.DocumentsEnabled));
        AddIfChanged(changes, "MaxCorrespondence", FormatBoolean(previous.CorrespondenceEnabled), FormatBoolean(next.CorrespondenceEnabled));
        AddIfChanged(changes, "MaxConfirmations", FormatBoolean(previous.ConfirmationsEnabled), FormatBoolean(next.ConfirmationsEnabled));
        AddIfChanged(changes, "MaxQuietHoursEnabled", FormatBoolean(previous.QuietHoursEnabled), FormatBoolean(next.QuietHoursEnabled));
        AddIfChanged(changes, "MaxQuietHoursStart", previous.QuietHoursStart, next.QuietHoursStart);
        AddIfChanged(changes, "MaxQuietHoursEnd", previous.QuietHoursEnd, next.QuietHoursEnd);
        AddIfChanged(changes, "MaxQuietHoursTimeZone", previous.QuietHoursTimeZone, next.QuietHoursTimeZone);
        AddIfChanged(changes, "MaxDailyLimit", FormatNumber(previous.DailyLimitPerRecipient), FormatNumber(next.DailyLimitPerRecipient));
        AddIfChanged(changes, "MaxRetentionDays", FormatNumber(previous.TechnicalPayloadRetentionDays), FormatNumber(next.TechnicalPayloadRetentionDays));
        if (botTokenChanged)
            changes.Add(new("MaxBotToken", previous.BotTokenSet ? "настроен" : "не задан", next.BotTokenSet ? "обновлён" : "не задан"));
        if (webhookSecretChanged)
            changes.Add(new("MaxWebhookSecret", previous.WebhookSecretSet ? "настроен" : "не задан", next.WebhookSecretSet ? "обновлён" : "не задан"));
        if (changes.Count == 0) return null;

        return CreateEvent(user, "Изменены настройки канала MAX.", new
        {
            category = "max",
            changedFields = changes.Select(item => item.Field).ToArray(),
            changes,
        });
    }

    private static ObjectAuditEvent CreateEvent(ClaimsPrincipal? user, string description, object metadata)
        => new()
        {
            ObjectType = ObjectType,
            ObjectId = 0,
            Action = UpdatedAction,
            Description = description,
            ActorUserId = user?.GetUserId(),
            ActorUserName = user?.GetDisplayName(),
            MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        };

    private static void AddIfChanged(
        ICollection<NotificationSettingsChange> changes,
        string field,
        string? oldValue,
        string? newValue)
    {
        var normalizedOldValue = oldValue?.Trim() ?? string.Empty;
        var normalizedNewValue = newValue?.Trim() ?? string.Empty;
        if (!string.Equals(normalizedOldValue, normalizedNewValue, StringComparison.Ordinal))
        {
            changes.Add(new NotificationSettingsChange(field, normalizedOldValue, normalizedNewValue));
        }
    }

    private static void AddContentChange(
        ICollection<NotificationSettingsChange> changes,
        string field,
        string? oldValue,
        string? newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new NotificationSettingsChange(
            field,
            FormatContentLength(oldValue),
            FormatContentLength(newValue)));
    }

    private static string FormatDisabledEvents(IEnumerable<SmsEventTypeSettingDto> eventTypes)
        => string.Join(
            ", ",
            eventTypes
                .Where(item => !item.Enabled)
                .Select(item => item.Key)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase));

    private static string FormatBoolean(bool value) => value ? "true" : "false";

    private static string FormatNumber(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string FormatContentLength(string? value)
        => $"{value?.Length ?? 0} символов";

    private static string GetFieldDescription(string field)
        => field switch
        {
            "SmsProvider" => "провайдер",
            "SmsSenderName" => "имя отправителя",
            "SmsApiToken" => "API-токен",
            "SmsDailyLimit" or "SmsRecipientDailyLimit" or "SmsBurstLimitPerMinute" => "лимиты",
            "SmsMaxMessageLength" => "длина сообщения",
            "SmsLowBalanceThreshold" => "порог баланса",
            "SmsEmergencyStop" => "аварийная остановка",
            "SmsDisabledEvents" => "разрешённые события",
            "SmsQuietHoursEnabled" or "SmsQuietHoursStart" or "SmsQuietHoursEnd" or "SmsQuietHoursTimeZone" => "тихие часы",
            _ => "состояние канала",
        };

    private sealed record NotificationSettingsChange(string Field, string OldValue, string NewValue);
}
