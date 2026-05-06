namespace PayPlanner.Api.Models.Requests;

public class UpdateEmailNotificationSettingsRequest
{
    public bool Enabled { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool KeepExistingPassword { get; set; } = true;
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public bool EnableSsl { get; set; } = true;
}

public class UpdateTelegramNotificationSettingsRequest
{
    public bool Enabled { get; set; }
    public string? BotToken { get; set; }
    public bool KeepExistingToken { get; set; } = true;
}

public class SendNotificationTestRequest
{
    public string Channel { get; set; } = "email";
    public int? SenderId { get; set; }
    public int? UserId { get; set; }
    public string? Email { get; set; }
    public long? TelegramChatId { get; set; }
    public string? Subject { get; set; }
    public string? Message { get; set; }
}

public class IssuePasswordRequest
{
    public List<string>? Channels { get; set; }
    public bool IncludePasswordInResponse { get; set; }
}

public class UpsertNotificationSenderRequest
{
    public string Channel { get; set; } = "email";
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public List<string> Purposes { get; set; } = [];
    public int SortOrder { get; set; }
    public EmailNotificationSenderRequest? Email { get; set; }
    public TelegramNotificationSenderRequest? Telegram { get; set; }
}

public class EmailNotificationSenderRequest
{
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public bool KeepExistingPassword { get; set; } = true;
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
    public bool EnableSsl { get; set; } = true;
}

public class TelegramNotificationSenderRequest
{
    public string? BotToken { get; set; }
    public bool KeepExistingToken { get; set; } = true;
    public string? BotUsername { get; set; }
    public string? UpdateMode { get; set; }
    public string? WebhookSecret { get; set; }
    public bool KeepExistingWebhookSecret { get; set; } = true;
    public string? ProxyUrl { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }
    public bool KeepExistingProxyPassword { get; set; } = true;
    public string? ProfileName { get; set; }
    public string? ProfileDescription { get; set; }
    public string? ProfileShortDescription { get; set; }
    public List<TelegramBotCommandRequest>? ProfileCommands { get; set; }
}

public class UpdateTelegramBotProfileRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? ShortDescription { get; set; }
    public List<TelegramBotCommandRequest>? Commands { get; set; }
}

public class TelegramBotCommandRequest
{
    public string Command { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
