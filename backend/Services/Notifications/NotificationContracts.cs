using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public sealed record NotificationChannelDefinition(
    string Key,
    string Label,
    string Description,
    string Status,
    bool IsEnabled,
    bool IsSendable,
    bool SupportsUserPreferences,
    bool SupportsDeliveryLog,
    bool RequiresProvider,
    bool IsFuture);

public static class NotificationChannels
{
    public const string InApp = "inApp";
    public const string Email = "email";
    public const string Telegram = "telegram";
    public const string WebPush = "webPush";
    public const string Sms = "sms";
    public const string Max = "max";
    public static readonly IReadOnlyList<string> All = [InApp, Email, Telegram, WebPush];
    public static readonly IReadOnlyList<string> External = [Email, Telegram, WebPush];
    public static readonly IReadOnlyList<string> Future = [Sms, Max];
    public static readonly IReadOnlyList<string> Known = [InApp, Email, Telegram, WebPush, Sms, Max];
    public static readonly IReadOnlyList<NotificationChannelDefinition> Definitions =
    [
        new(InApp, "Центр уведомлений", "Внутренние уведомления в CRM, счетчик и центр уведомлений.", "active", true, true, true, false, false, false),
        new(Email, "Email", "Письма через настроенных SMTP-отправителей.", "active", true, true, true, true, true, false),
        new(Telegram, "Telegram", "Сообщения через подключенных Telegram-ботов и привязанные чаты пользователей.", "active", true, true, true, true, true, false),
        new(WebPush, "Web push", "Браузерные push-уведомления для важных событий, когда вкладка CRM закрыта или не на экране.", "active", true, true, true, true, false, false),
        new(Sms, "SMS", "SMS-уведомления. Адаптер подготовлен в модели каналов, провайдер будет выбран отдельно.", "planned", false, false, false, true, true, true),
        new(Max, "MAX", "Уведомления в мессенджер MAX. Канал зарезервирован как будущая интеграция без привязки к конкретному API.", "planned", false, false, false, true, true, true)
    ];
}

public static class NotificationDeliveryStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public static class TelegramBotUpdateModes
{
    public const string Disabled = "disabled";
    public const string Webhook = "webhook";
    public const string Polling = "polling";

    public static readonly IReadOnlyList<string> All = [Disabled, Webhook, Polling];
}

public static class NotificationPurposes
{
    public const string TelegramAuth = "telegramAuth";
    public const string RegistrationUser = "registrationUser";
    public const string RegistrationAdmin = "registrationAdmin";
    public const string PasswordIssue = "passwordIssue";
    public const string TaskReminder = "taskReminder";
    public const string ContractClient = "contractClient";
    public const string ManualTest = "manualTest";

    public static readonly IReadOnlyList<string> All =
    [
        TelegramAuth,
        RegistrationUser,
        RegistrationAdmin,
        PasswordIssue,
        TaskReminder,
        ContractClient,
        ManualTest
    ];
}

public sealed record NotificationPurposeDefinition(
    string Key,
    string Label,
    string Description);

public static class UserNotificationTypes
{
    public const string TaskAssigned = "taskAssigned";
    public const string TaskReminder = "taskReminder";
    public const string TaskChanged = "taskChanged";
    public const string TaskComment = "taskComment";
    public const string TaskCompleted = "taskCompleted";
    public const string TaskScheduleConflict = "taskScheduleConflict";
    public const string TaskApprovalRequested = "taskApprovalRequested";
    public const string TaskApproved = "taskApproved";
    public const string TaskRevisionRequested = "taskRevisionRequested";
    public const string TaskApprovalOverdue = "taskApprovalOverdue";
    public const string CaseInactivityControl = "caseInactivityControl";
    public const string CaseAccessRequested = "caseAccessRequested";
    public const string CaseAccessResolved = "caseAccessResolved";
    public const string CaseCourtEvent = "caseCourtEvent";
    public const string CourtHearing = "courtHearing";
    public const string CourtStatusChanged = "courtStatusChanged";
    public const string CourtDecisionPublished = "courtDecisionPublished";
    public const string CourtSyncFailed = "courtSyncFailed";
    public const string CaseProceduralDeadline = "caseProceduralDeadline";
    public const string CaseDocument = "caseDocument";
    public const string CaseInactivityReminder = "caseInactivityReminder";
    public const string PaymentAdvanceReviewRequired = "paymentAdvanceReviewRequired";
    public const string PaymentAdvanceReviewEscalation = "paymentAdvanceReviewEscalation";
    public const string SystemAdminWarning = "systemAdminWarning";

    public static readonly IReadOnlyList<UserNotificationTypeDefinition> All =
    [
        new(TaskAssigned, "Новая задача", "Назначение новой задачи исполнителю или участникам.", "blue", "normal", NotificationChannels.All, true, false),
        new(TaskReminder, "Напоминание по задаче", "Сроки, просрочки и плановые напоминания по задачам.", "amber", "high", NotificationChannels.All, true, false),
        new(TaskChanged, "Изменение задачи", "Изменения статусов, сроков, исполнителей и других полей задачи.", "blue", "normal", NotificationChannels.All, false, true),
        new(TaskComment, "Комментарий по задаче", "Новые комментарии и сообщения по задаче.", "emerald", "normal", NotificationChannels.All, true, true),
        new(TaskCompleted, "Задача выполнена", "Уведомление постановщику о завершении задачи.", "blue", "normal", NotificationChannels.All, true, false),
        new(TaskScheduleConflict, "Конфликт расписания", "Задача пересекается по времени с другой активной задачей исполнителя.", "rose", "high", NotificationChannels.All, true, false),
        new(TaskApprovalRequested, "Ожидает согласования", "Задача отправлена согласующему на решение.", "violet", "high", NotificationChannels.All, true, false),
        new(TaskApproved, "Задача согласована", "Согласующий подтвердил результат задачи.", "emerald", "normal", NotificationChannels.All, false, false),
        new(TaskRevisionRequested, "Возврат на доработку", "Согласующий вернул задачу исполнителю на доработку.", "orange", "high", NotificationChannels.All, false, false),
        new(TaskApprovalOverdue, "Просрочено согласование", "Срок согласования задачи истек, требуется решение согласующего или контроль исполнителя.", "rose", "high", NotificationChannels.All, true, false),
        new(CaseInactivityControl, "Автоконтроль дела", "По делу долго не было активных задач, создана контрольная задача.", "amber", "high", NotificationChannels.All, true, false),
        new(CaseAccessRequested, "Запрос доступа к делу", "Исполнитель запросил доступ к скрытому делу.", "violet", "high", NotificationChannels.All, false, false),
        new(CaseAccessResolved, "Решение по доступу к делу", "Руководитель разрешил или отклонил доступ к скрытому делу.", "emerald", "normal", NotificationChannels.All, false, false),
        new(CaseCourtEvent, "Судебное событие", "По делу добавлено или обновлено судебное заседание, движение или судебный акт.", "cyan", "high", NotificationChannels.All, false, false),
        new(CourtHearing, "Судебное заседание", "Назначено, перенесено или обновлено судебное заседание по делу.", "cyan", "high", NotificationChannels.All, true, false),
        new(CourtStatusChanged, "Изменение судебного статуса", "Изменился статус судебного производства или движение дела в суде.", "blue", "high", NotificationChannels.All, true, false),
        new(CourtDecisionPublished, "Публикация решения", "По делу опубликовано решение, определение или другой судебный акт.", "emerald", "high", NotificationChannels.All, true, false),
        new(CourtSyncFailed, "Ошибка судебной синхронизации", "Судебный источник не удалось проверить или обновить автоматически.", "rose", "high", NotificationChannels.All, true, false),
        new(CaseProceduralDeadline, "Процессуальный срок", "По делу добавлен, перенесен, закрыт или приближается процессуальный срок.", "amber", "high", NotificationChannels.All, false, false),
        new(CaseDocument, "Документы дела", "По делу загружен, сформирован, обновлен или удален документ.", "emerald", "normal", NotificationChannels.All, false, false),
        new(CaseInactivityReminder, "Напоминание о неактивности", "По делу давно не было значимой активности, требуется проверка ответственного.", "amber", "high", NotificationChannels.All, true, false),
        new(PaymentAdvanceReviewRequired, "Контроль аванса", "Входящий оплаченный платеж не привязан к счету или акту и требует решения по авансу.", "rose", "high", NotificationChannels.All, true, false),
        new(PaymentAdvanceReviewEscalation, "Просроченный аванс", "Нераспределенный аванс слишком долго ожидает решения и требует внимания руководителя.", "rose", "high", NotificationChannels.All, true, false),
        new(SystemAdminWarning, "Системное предупреждение", "Администраторам требуется обратить внимание на фоновый процесс, интеграцию или настройку.", "rose", "high", NotificationChannels.All, true, false)
    ];

    public static bool IsKnown(string? value)
        => All.Any(item => string.Equals(item.Key, value, StringComparison.OrdinalIgnoreCase));

    public static UserNotificationTypeDefinition? Find(string? value)
        => All.FirstOrDefault(item => string.Equals(item.Key, value, StringComparison.OrdinalIgnoreCase));
}

public sealed record UserNotificationTypeDefinition(
    string Key,
    string Label,
    string Description,
    string Tone,
    string Priority,
    IReadOnlyList<string> Channels,
    bool Deduplicate,
    bool Digestible);

public static class UserNotificationPreferenceSettings
{
    public const string SettingKey = "notifications.typePreferences.v1";
    public static readonly UserNotificationPreferenceSnapshot Empty = new([], new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> ParseDisabledTypes(string? rawValue)
        => Parse(rawValue).DisabledTypes;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseDisabledChannels(string? rawValue)
        => Parse(rawValue).DisabledChannels;

    public static UserNotificationPreferenceSnapshot Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Empty;
        }

        try
        {
            var storage = JsonSerializer.Deserialize<UserNotificationPreferenceStorage>(rawValue, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Normalize(storage);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static string Serialize(UserNotificationPreferenceSnapshot preferences)
        => JsonSerializer.Serialize(new UserNotificationPreferenceStorage
        {
            DisabledTypes = NormalizeTypes(preferences.DisabledTypes).ToList(),
            DisabledChannels = NormalizeDisabledChannels(preferences.DisabledChannels)
                .ToDictionary(
                    item => item.Key,
                    item => item.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static bool IsTypeEnabled(string? rawValue, string? type)
        => IsTypeEnabled(Parse(rawValue), type);

    public static bool IsTypeEnabled(UserNotificationPreferenceSnapshot preferences, string? type)
    {
        var normalizedType = NormalizeType(type);
        return string.IsNullOrWhiteSpace(normalizedType)
               || !preferences.DisabledTypes.Contains(normalizedType, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsChannelEnabled(string? rawValue, string? type, string? channel)
        => IsChannelEnabled(Parse(rawValue), type, channel);

    public static bool IsChannelEnabled(UserNotificationPreferenceSnapshot preferences, string? type, string? channel)
    {
        var normalizedType = NormalizeType(type);
        var normalizedChannel = NormalizeChannel(channel);
        if (string.IsNullOrWhiteSpace(normalizedType) || string.IsNullOrWhiteSpace(normalizedChannel))
        {
            return true;
        }

        if (!IsTypeEnabled(preferences, normalizedType))
        {
            return false;
        }

        return !preferences.DisabledChannels.TryGetValue(normalizedType, out var disabledChannels)
               || !disabledChannels.Contains(normalizedChannel, StringComparer.OrdinalIgnoreCase);
    }

    private static UserNotificationPreferenceSnapshot Normalize(UserNotificationPreferenceStorage? storage)
        => new(
            NormalizeTypes(storage?.DisabledTypes),
            NormalizeDisabledChannels(storage?.DisabledChannels));

    private static IReadOnlyList<string> NormalizeTypes(IEnumerable<string>? values)
        => (values ?? [])
            .Select(NormalizeType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeDisabledChannels(Dictionary<string, List<string>>? values)
        => NormalizeDisabledChannelsCore(values?.Select(item => new KeyValuePair<string, IEnumerable<string>>(item.Key, item.Value)));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeDisabledChannels(IReadOnlyDictionary<string, IReadOnlyList<string>>? values)
        => NormalizeDisabledChannelsCore(values?.Select(item => new KeyValuePair<string, IEnumerable<string>>(item.Key, item.Value)));

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> NormalizeDisabledChannelsCore(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? values)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, channels) in values ?? [])
        {
            var normalizedType = NormalizeType(type);
            if (string.IsNullOrWhiteSpace(normalizedType))
            {
                continue;
            }

            var normalizedChannels = NormalizeChannels(channels);
            if (normalizedChannels.Count > 0)
            {
                result[normalizedType] = normalizedChannels;
            }
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeChannels(IEnumerable<string>? values)
        => (values ?? [])
            .Select(NormalizeChannel)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? NormalizeType(string? value)
        => UserNotificationTypes.Find(value)?.Key;

    private static string? NormalizeChannel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return NotificationChannels.All.FirstOrDefault(channel => string.Equals(channel, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class UserNotificationPreferenceStorage
    {
        public List<string> DisabledTypes { get; set; } = [];
        public Dictionary<string, List<string>> DisabledChannels { get; set; } = [];
    }
}

public sealed record UserNotificationPreferenceSnapshot(
    IReadOnlyList<string> DisabledTypes,
    IReadOnlyDictionary<string, IReadOnlyList<string>> DisabledChannels);

public static class RoleNotificationPreferenceSettings
{
    public const string SettingKey = "notifications.roleTypePreferences.v1";

    public static IReadOnlyDictionary<int, UserNotificationPreferenceSnapshot> Parse(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new Dictionary<int, UserNotificationPreferenceSnapshot>();
        }

        try
        {
            var storage = JsonSerializer.Deserialize<RoleNotificationPreferenceStorage>(rawValue, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Normalize(storage?.Roles);
        }
        catch (JsonException)
        {
            return new Dictionary<int, UserNotificationPreferenceSnapshot>();
        }
    }

    public static UserNotificationPreferenceSnapshot? ParseForRole(string? rawValue, int roleId)
        => Parse(rawValue).TryGetValue(roleId, out var preferences) ? preferences : null;

    public static string Serialize(IReadOnlyDictionary<int, UserNotificationPreferenceSnapshot> preferencesByRole)
    {
        var roles = preferencesByRole
            .Where(item => item.Key > 0)
            .OrderBy(item => item.Key)
            .ToDictionary(
                item => item.Key.ToString(CultureInfo.InvariantCulture),
                item => JsonSerializer.Deserialize<RoleNotificationPreferenceItem>(
                    UserNotificationPreferenceSettings.Serialize(item.Value),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new RoleNotificationPreferenceItem(),
                StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(new RoleNotificationPreferenceStorage { Roles = roles }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static IReadOnlyDictionary<int, UserNotificationPreferenceSnapshot> Normalize(Dictionary<string, RoleNotificationPreferenceItem>? values)
    {
        var result = new Dictionary<int, UserNotificationPreferenceSnapshot>();
        foreach (var (roleIdValue, preferences) in values ?? [])
        {
            if (!int.TryParse(roleIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var roleId) || roleId <= 0)
            {
                continue;
            }

            result[roleId] = UserNotificationPreferenceSettings.Parse(
                JsonSerializer.Serialize(preferences, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }

        return result;
    }

    private sealed class RoleNotificationPreferenceStorage
    {
        public Dictionary<string, RoleNotificationPreferenceItem> Roles { get; set; } = [];
    }

    private sealed class RoleNotificationPreferenceItem
    {
        public List<string> DisabledTypes { get; set; } = [];
        public Dictionary<string, List<string>> DisabledChannels { get; set; } = [];
    }
}

public sealed record EmailSendSettings(
    bool Enabled,
    string Host,
    int Port,
    string Username,
    string Password,
    string FromEmail,
    string FromName,
    bool EnableSsl);

public sealed record NotificationMessage(
    string Subject,
    string Body,
    string? HtmlBody = null,
    bool IsSensitive = false,
    string Purpose = NotificationPurposes.ManualTest);

public sealed record NotificationRecipient(
    int? UserId,
    string DisplayName,
    string? Email,
    long? TelegramChatId);

public sealed record TelegramSendResult(
    bool Success,
    string Status,
    string? Error = null);

public sealed record TelegramBotConnectionSettings(
    string ProxyUrl = "",
    string ProxyUsername = "",
    string ProxyPassword = "");

public sealed record TelegramBotProfilePhotoResult(
    byte[] Bytes,
    string ContentType,
    string FileName);

public interface ITelegramNotificationService
{
    Task<TelegramSendResult> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default);
    Task<TelegramSendResult> SendMessageWithTokenAsync(
        long chatId,
        string text,
        string botToken,
        CancellationToken cancellationToken = default,
        TelegramBotConnectionSettings? connection = null);
    Task<TelegramNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
}

public interface ITelegramBotUpdateService
{
    Task<PayPlanner.Api.Models.Responses.TelegramWebhookSyncResultDto> SyncWebhookAsync(
        int senderId,
        HttpRequest request,
        CancellationToken cancellationToken = default);

    Task<PayPlanner.Api.Models.Responses.TelegramWebhookSyncResultDto> DeleteWebhookAsync(
        int senderId,
        CancellationToken cancellationToken = default);

    Task HandleWebhookAsync(
        int senderId,
        JsonDocument updateDocument,
        HttpRequest request,
        CancellationToken cancellationToken = default);

    Task PollEnabledSendersAsync(CancellationToken cancellationToken = default);

    Task<TelegramBotProfileSyncResultDto> SyncBotProfileAsync(
        int senderId,
        CancellationToken cancellationToken = default);

    Task<TelegramBotProfileSyncResultDto> ApplyBotProfileAsync(
        int senderId,
        PayPlanner.Api.Models.Requests.UpdateTelegramBotProfileRequest request,
        CancellationToken cancellationToken = default);

    Task<TelegramBotProfilePhotoResult?> GetBotProfilePhotoAsync(
        int senderId,
        CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default);
    Task<NotificationSettingsDto> UpdateEmailSettingsAsync(
        PayPlanner.Api.Models.Requests.UpdateEmailNotificationSettingsRequest request,
        CancellationToken cancellationToken = default);
    Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(
        PayPlanner.Api.Models.Requests.UpdateTelegramNotificationSettingsRequest request,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default);
    Task<NotificationTemplateDto> UpdateTemplateAsync(
        string key,
        PayPlanner.Api.Models.Requests.UpdateNotificationTemplateRequest request,
        CancellationToken cancellationToken = default);
    Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken cancellationToken = default);
    Task<NotificationMessage> RenderTemplateAsync(
        string key,
        string fallbackSubject,
        string fallbackBody,
        IReadOnlyDictionary<string, string?> values,
        bool isSensitive = false,
        string purpose = NotificationPurposes.ManualTest,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(
        int userId,
        NotificationMessage message,
        IEnumerable<string>? channels = null,
        string? eventKey = null,
        CancellationToken cancellationToken = default,
        int? senderId = null);

    Task<IReadOnlyList<NotificationDelivery>> SendAsync(
        NotificationRecipient recipient,
        NotificationMessage message,
        IEnumerable<string>? channels = null,
        string? eventKey = null,
        CancellationToken cancellationToken = default,
        int? senderId = null);

    Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(
        int deliveryId,
        CancellationToken cancellationToken = default);

    Task<NotificationSettingsDto> UpsertSenderAsync(
        PayPlanner.Api.Models.Requests.UpsertNotificationSenderRequest request,
        int? senderId = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSenderAsync(int senderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationDelivery>> NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationApprovedAsync(User user, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyPasswordIssuedAsync(User user, string password, IEnumerable<string>? channels = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskAssignedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(TaskJournalItem task, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskScheduleConflictAsync(TaskJournalItem task, IReadOnlyList<TaskJournalItem> conflicts, string actorName, int actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalDecisionAsync(TaskJournalItem task, TaskJournalParticipant approver, string decision, string actorName, int actorUserId, string? resolution = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalOverdueAsync(TaskJournalItem task, DateTime nowUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(TaskJournalItem task, int? previousAssigneeUserId, string? previousAssigneeName, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(TaskJournalItem task, TaskJournalComment comment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyCaseInactivityControlTaskCreatedAsync(TaskJournalItem task, int daysWithoutActiveTask, CancellationToken cancellationToken = default);
}
