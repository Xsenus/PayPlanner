using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public static class NotificationChannels
{
    public const string Email = "email";
    public const string Telegram = "telegram";
    public static readonly IReadOnlyList<string> All = [Email, Telegram];
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
    public const string RegistrationUser = "registrationUser";
    public const string RegistrationAdmin = "registrationAdmin";
    public const string PasswordIssue = "passwordIssue";
    public const string TaskReminder = "taskReminder";
    public const string ManualTest = "manualTest";

    public static readonly IReadOnlyList<string> All =
    [
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
    Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default);
}
