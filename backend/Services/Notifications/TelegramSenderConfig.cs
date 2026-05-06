namespace PayPlanner.Api.Services.Notifications;

internal sealed class TelegramSenderConfig
{
    public string BotToken { get; set; } = string.Empty;
    public string BotUsername { get; set; } = string.Empty;
    public string UpdateMode { get; set; } = TelegramBotUpdateModes.Disabled;
    public string WebhookSecret { get; set; } = string.Empty;
    public string WebhookUrl { get; set; } = string.Empty;
    public DateTime? WebhookSyncedAt { get; set; }
    public long LastUpdateId { get; set; }
    public DateTime? LastPollingAt { get; set; }

    public string ProxyUrl { get; set; } = string.Empty;
    public string ProxyUsername { get; set; } = string.Empty;
    public string ProxyPassword { get; set; } = string.Empty;

    public long? BotId { get; set; }
    public string BotFirstName { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string ProfileDescription { get; set; } = string.Empty;
    public string ProfileShortDescription { get; set; } = string.Empty;
    public List<TelegramBotCommandConfig> ProfileCommands { get; set; } = [];
    public string ProfilePhotoFileId { get; set; } = string.Empty;
    public DateTime? ProfileSyncedAt { get; set; }
    public bool CanJoinGroups { get; set; }
    public bool CanReadAllGroupMessages { get; set; }
    public bool SupportsInlineQueries { get; set; }
}

internal sealed class TelegramBotCommandConfig
{
    public string Command { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
