namespace PayPlanner.Api.Models.Responses;

public class NotificationSettingsDto
{
    public EmailNotificationSettingsDto Email { get; set; } = new();
    public TelegramNotificationSettingsDto Telegram { get; set; } = new();
    public IReadOnlyList<NotificationSenderDto> Senders { get; set; } = Array.Empty<NotificationSenderDto>();
    public IReadOnlyList<NotificationPurposeDto> Purposes { get; set; } = Array.Empty<NotificationPurposeDto>();
    public IReadOnlyList<NotificationTemplateDto> Templates { get; set; } = Array.Empty<NotificationTemplateDto>();
}

public class EmailNotificationSettingsDto
{
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public bool ContractTerminationClientEmailsEnabled { get; set; }
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; }
    public string SmtpUsername { get; set; } = string.Empty;
    public bool SmtpPasswordSet { get; set; }
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public bool EnableSsl { get; set; }
}

public class TelegramNotificationSettingsDto
{
    public bool Enabled { get; set; }
    public bool Ready { get; set; }
    public bool BotTokenSet { get; set; }
    public string BotUsername { get; set; } = string.Empty;
    public int LinkedUsersCount { get; set; }
}

public class NotificationDeliveryDto
{
    public int Id { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public int? SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public int? RecipientUserId { get; set; }
    public string RecipientAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public class UserNotificationDto
{
    public int Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string TypeLabel { get; set; } = string.Empty;
    public string EventKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BodyPreview { get; set; } = string.Empty;
    public string Tone { get; set; } = "blue";
    public string ObjectType { get; set; } = string.Empty;
    public int? ObjectId { get; set; }
    public int? TaskId { get; set; }
    public bool IsRead { get; set; }
    public DateTime? ReadAt { get; set; }
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserNotificationTypeDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tone { get; set; } = "blue";
    public string Priority { get; set; } = "normal";
    public IReadOnlyList<string> Channels { get; set; } = Array.Empty<string>();
    public bool Deduplicate { get; set; }
    public bool Digestible { get; set; }
}

public class UserNotificationBulkActionResultDto
{
    public int UpdatedCount { get; set; }
}

public class NotificationRolePreferencesDto
{
    public IReadOnlyList<NotificationRolePreferenceDto> Roles { get; set; } = Array.Empty<NotificationRolePreferenceDto>();
}

public class NotificationRolePreferenceDto
{
    public int RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsSystemRole { get; set; }
    public UserNotificationPreferenceDto Preferences { get; set; } = new();
}

public class UserNotificationPreferenceDto
{
    public IReadOnlyList<string> DisabledTypes { get; set; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DisabledChannels { get; set; } = new Dictionary<string, IReadOnlyList<string>>();
}

public class SendNotificationTestResponseDto
{
    public bool Success { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
}

public class IssuePasswordResponseDto
{
    public int UserId { get; set; }
    public bool PasswordChanged { get; set; }
    public string? Password { get; set; }
    public IReadOnlyList<NotificationDeliveryDto> Deliveries { get; set; } = Array.Empty<NotificationDeliveryDto>();
}

public class NotificationSenderDto
{
    public int Id { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool Ready { get; set; }
    public IReadOnlyList<string> Purposes { get; set; } = Array.Empty<string>();
    public int SortOrder { get; set; }
    public EmailNotificationSenderDto? Email { get; set; }
    public TelegramNotificationSenderDto? Telegram { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class EmailNotificationSenderDto
{
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = string.Empty;
    public bool SmtpPasswordSet { get; set; }
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
}

public class TelegramNotificationSenderDto
{
    public bool BotTokenSet { get; set; }
    public string BotUsername { get; set; } = string.Empty;
    public string UpdateMode { get; set; } = "disabled";
    public bool WebhookSecretSet { get; set; }
    public string WebhookUrl { get; set; } = string.Empty;
    public DateTime? WebhookSyncedAt { get; set; }
    public DateTime? LastPollingAt { get; set; }
    public int SubscriptionsCount { get; set; }
    public string ProxyUrl { get; set; } = string.Empty;
    public string ProxyUsername { get; set; } = string.Empty;
    public bool ProxyPasswordSet { get; set; }
    public long? BotId { get; set; }
    public string BotFirstName { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string ProfileDescription { get; set; } = string.Empty;
    public string ProfileShortDescription { get; set; } = string.Empty;
    public IReadOnlyList<TelegramBotCommandDto> ProfileCommands { get; set; } = Array.Empty<TelegramBotCommandDto>();
    public bool HasProfilePhoto { get; set; }
    public DateTime? ProfileSyncedAt { get; set; }
    public bool CanJoinGroups { get; set; }
    public bool CanReadAllGroupMessages { get; set; }
    public bool SupportsInlineQueries { get; set; }
}

public class TelegramBotCommandDto
{
    public string Command { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class TelegramBotProfileDto
{
    public long? BotId { get; set; }
    public string BotUsername { get; set; } = string.Empty;
    public string BotFirstName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public IReadOnlyList<TelegramBotCommandDto> Commands { get; set; } = Array.Empty<TelegramBotCommandDto>();
    public bool HasProfilePhoto { get; set; }
    public DateTime? SyncedAt { get; set; }
    public bool CanJoinGroups { get; set; }
    public bool CanReadAllGroupMessages { get; set; }
    public bool SupportsInlineQueries { get; set; }
}

public class TelegramBotProfileSyncResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public TelegramBotProfileDto? Profile { get; set; }
}

public class NotificationPurposeDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class NotificationTemplateDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string DefaultSubject { get; set; } = string.Empty;
    public string DefaultBody { get; set; } = string.Empty;
    public IReadOnlyList<NotificationTemplateVariableDto> Variables { get; set; } = Array.Empty<NotificationTemplateVariableDto>();
}

public class NotificationTemplateVariableDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
