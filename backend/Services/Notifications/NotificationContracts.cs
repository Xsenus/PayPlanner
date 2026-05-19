using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public static class NotificationChannels
{
    public const string InApp = "inApp";
    public const string Email = "email";
    public const string Telegram = "telegram";
    public static readonly IReadOnlyList<string> All = [InApp, Email, Telegram];
    public static readonly IReadOnlyList<string> External = [Email, Telegram];
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
    public const string ManualTest = "manualTest";

    public static readonly IReadOnlyList<string> All =
    [
        TelegramAuth,
        RegistrationUser,
        RegistrationAdmin,
        PasswordIssue,
        TaskReminder,
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
        new(TaskRevisionRequested, "Возврат на доработку", "Согласующий вернул задачу исполнителю на доработку.", "orange", "high", NotificationChannels.All, false, false)
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
    Task<NotificationSettingsDto> UpdateEmailSettingsAsync(
        PayPlanner.Api.Models.Requests.UpdateEmailNotificationSettingsRequest request,
        CancellationToken cancellationToken = default);
    Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(
        PayPlanner.Api.Models.Requests.UpdateTelegramNotificationSettingsRequest request,
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
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(TaskJournalItem task, TaskJournalComment comment, CancellationToken cancellationToken = default);
}
