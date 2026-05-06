using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Services.Notifications;

public sealed class TelegramBotUpdateService : ITelegramBotUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PaymentContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramBotUpdateService> _logger;
    private readonly ExternalAuthService? _externalAuthService;

    private static readonly Regex CommandRegex = new("^[a-z0-9_]{1,32}$", RegexOptions.Compiled);

    private sealed record IncomingTelegramMessage(
        long ChatId,
        long TelegramUserId,
        string? Username,
        string DisplayName,
        string Text);

    public TelegramBotUpdateService(
        PaymentContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramBotUpdateService> logger)
        : this(context, httpClientFactory, logger, null)
    {
    }

    public TelegramBotUpdateService(
        PaymentContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramBotUpdateService> logger,
        ExternalAuthService? externalAuthService)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _externalAuthService = externalAuthService;
    }

    public async Task<TelegramWebhookSyncResultDto> SyncWebhookAsync(
        int senderId,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var sender = await LoadTelegramSenderAsync(senderId, tracking: true, cancellationToken)
            ?? throw new InvalidOperationException("NotificationSenderNotFound");
        var config = DeserializeConfig(sender.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            throw new InvalidOperationException("TelegramBotTokenMissing");
        }

        if (string.IsNullOrWhiteSpace(config.WebhookSecret))
        {
            config.WebhookSecret = Guid.NewGuid().ToString("N");
        }

        var webhookUrl = BuildWebhookUrl(request, sender.Id);
        using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, config.ToConnectionSettings());
        using var response = await client.PostAsync(
            $"https://api.telegram.org/bot{config.BotToken.Trim()}/setWebhook",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["url"] = webhookUrl,
                ["secret_token"] = config.WebhookSecret,
                ["allowed_updates"] = "[\"message\"]",
            }),
            cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !IsTelegramOk(payload))
        {
            return new TelegramWebhookSyncResultDto
            {
                Success = false,
                Message = ExtractTelegramError(payload) ?? response.ReasonPhrase ?? "Telegram API вернул ошибку.",
                WebhookUrl = webhookUrl,
                BotUsername = config.BotUsername
            };
        }

        config.UpdateMode = TelegramBotUpdateModes.Webhook;
        config.WebhookUrl = webhookUrl;
        config.WebhookSyncedAt = DateTime.UtcNow;
        sender.ConfigJson = JsonSerializer.Serialize(config, JsonOptions);
        sender.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new TelegramWebhookSyncResultDto
        {
            Success = true,
            Message = "Webhook Telegram-бота синхронизирован.",
            WebhookUrl = webhookUrl,
            BotUsername = config.BotUsername
        };
    }

    public async Task<TelegramWebhookSyncResultDto> DeleteWebhookAsync(
        int senderId,
        CancellationToken cancellationToken = default)
    {
        var sender = await LoadTelegramSenderAsync(senderId, tracking: true, cancellationToken)
            ?? throw new InvalidOperationException("NotificationSenderNotFound");
        var config = DeserializeConfig(sender.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            throw new InvalidOperationException("TelegramBotTokenMissing");
        }

        var success = await DeleteWebhookCoreAsync(config, cancellationToken);
        if (!success.Success)
        {
            return success;
        }

        config.UpdateMode = TelegramBotUpdateModes.Disabled;
        config.WebhookUrl = string.Empty;
        config.WebhookSyncedAt = null;
        sender.ConfigJson = JsonSerializer.Serialize(config, JsonOptions);
        sender.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new TelegramWebhookSyncResultDto
        {
            Success = true,
            Message = "Webhook Telegram-бота отключён.",
            BotUsername = config.BotUsername
        };
    }

    public async Task HandleWebhookAsync(
        int senderId,
        JsonDocument updateDocument,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var sender = await LoadTelegramSenderAsync(senderId, tracking: true, cancellationToken)
            ?? throw new InvalidOperationException("NotificationSenderNotFound");
        var config = DeserializeConfig(sender.ConfigJson);
        if (!string.Equals(config.UpdateMode, TelegramBotUpdateModes.Webhook, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Telegram bot webhook is not enabled.");
        }

        var incomingSecret = request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var headerValues)
            ? headerValues.ToString()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(config.WebhookSecret)
            || !string.Equals(config.WebhookSecret, incomingSecret, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Telegram webhook secret is invalid.");
        }

        await ProcessUpdateAsync(sender, config, updateDocument.RootElement, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task PollEnabledSendersAsync(CancellationToken cancellationToken = default)
    {
        var senders = await _context.NotificationSenders
            .Where(item => item.Channel == NotificationChannels.Telegram && item.IsEnabled)
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);

        foreach (var sender in senders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = DeserializeConfig(sender.ConfigJson);
            if (!string.Equals(config.UpdateMode, TelegramBotUpdateModes.Polling, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(config.BotToken))
            {
                continue;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(config.WebhookUrl))
                {
                    await DeleteWebhookCoreAsync(config, cancellationToken);
                    config.WebhookUrl = string.Empty;
                    config.WebhookSyncedAt = null;
                }

                await PollSenderAsync(sender, config, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to poll Telegram notification sender {SenderId}.", sender.Id);
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TelegramBotProfileSyncResultDto> SyncBotProfileAsync(
        int senderId,
        CancellationToken cancellationToken = default)
    {
        var sender = await LoadTelegramSenderAsync(senderId, tracking: true, cancellationToken)
            ?? throw new InvalidOperationException("NotificationSenderNotFound");
        var config = DeserializeConfig(sender.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            throw new InvalidOperationException("TelegramBotTokenMissing");
        }

        using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, config.ToConnectionSettings());
        var me = await CallTelegramAsync(client, config.BotToken, "getMe", null, cancellationToken);
        if (!me.Success)
        {
            return new TelegramBotProfileSyncResultDto
            {
                Success = false,
                Message = me.Error ?? "Telegram bot token не прошёл проверку."
            };
        }

        var botId = ReadInt64(me.Result, "id");
        config.BotId = botId > 0 ? botId : null;
        config.BotFirstName = ReadString(me.Result, "first_name") ?? string.Empty;
        config.BotUsername = ReadString(me.Result, "username") ?? config.BotUsername;
        config.CanJoinGroups = ReadBool(me.Result, "can_join_groups");
        config.CanReadAllGroupMessages = ReadBool(me.Result, "can_read_all_group_messages");
        config.SupportsInlineQueries = ReadBool(me.Result, "supports_inline_queries");

        var name = await CallTelegramAsync(client, config.BotToken, "getMyName", null, cancellationToken);
        if (name.Success)
        {
            config.ProfileName = ReadString(name.Result, "name") ?? string.Empty;
        }

        var description = await CallTelegramAsync(client, config.BotToken, "getMyDescription", null, cancellationToken);
        if (description.Success)
        {
            config.ProfileDescription = ReadString(description.Result, "description") ?? string.Empty;
        }

        var shortDescription = await CallTelegramAsync(client, config.BotToken, "getMyShortDescription", null, cancellationToken);
        if (shortDescription.Success)
        {
            config.ProfileShortDescription = ReadString(shortDescription.Result, "short_description") ?? string.Empty;
        }

        var commands = await CallTelegramAsync(client, config.BotToken, "getMyCommands", null, cancellationToken);
        if (commands.Success && commands.Result.ValueKind == JsonValueKind.Array)
        {
            config.ProfileCommands = ReadCommands(commands.Result);
        }

        if (config.BotId.HasValue)
        {
            var photos = await CallTelegramAsync(
                client,
                config.BotToken,
                "getUserProfilePhotos",
                new Dictionary<string, string>
                {
                    ["user_id"] = config.BotId.Value.ToString(CultureInfo.InvariantCulture),
                    ["limit"] = "1"
                },
                cancellationToken);
            if (photos.Success)
            {
                config.ProfilePhotoFileId = ReadProfilePhotoFileId(photos.Result);
            }
        }

        config.ProfileSyncedAt = DateTime.UtcNow;
        sender.ConfigJson = JsonSerializer.Serialize(config, JsonOptions);
        sender.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new TelegramBotProfileSyncResultDto
        {
            Success = true,
            Message = "Информация Telegram-бота синхронизирована.",
            Profile = BuildProfileDto(config)
        };
    }

    public async Task<TelegramBotProfileSyncResultDto> ApplyBotProfileAsync(
        int senderId,
        UpdateTelegramBotProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var sender = await LoadTelegramSenderAsync(senderId, tracking: true, cancellationToken)
            ?? throw new InvalidOperationException("NotificationSenderNotFound");
        var config = DeserializeConfig(sender.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            throw new InvalidOperationException("TelegramBotTokenMissing");
        }

        var normalized = NormalizeProfileRequest(request);
        var validationError = ValidateProfileRequest(normalized);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return new TelegramBotProfileSyncResultDto
            {
                Success = false,
                Message = validationError
            };
        }

        using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, config.ToConnectionSettings());
        var calls = new[]
        {
            ("setMyName", new Dictionary<string, string> { ["name"] = normalized.Name }),
            ("setMyDescription", new Dictionary<string, string> { ["description"] = normalized.Description }),
            ("setMyShortDescription", new Dictionary<string, string> { ["short_description"] = normalized.ShortDescription }),
            ("setMyCommands", new Dictionary<string, string>
            {
                ["commands"] = JsonSerializer.Serialize(normalized.Commands.Select(item => new
                {
                    command = item.Command,
                    description = item.Description
                }), JsonOptions)
            })
        };

        foreach (var (method, form) in calls)
        {
            var result = await CallTelegramAsync(client, config.BotToken, method, form, cancellationToken);
            if (!result.Success)
            {
                return new TelegramBotProfileSyncResultDto
                {
                    Success = false,
                    Message = $"{method}: {result.Error ?? "Telegram API вернул ошибку."}"
                };
            }
        }

        return await SyncBotProfileAsync(senderId, cancellationToken);
    }

    public async Task<TelegramBotProfilePhotoResult?> GetBotProfilePhotoAsync(
        int senderId,
        CancellationToken cancellationToken = default)
    {
        var sender = await LoadTelegramSenderAsync(senderId, tracking: false, cancellationToken)
            ?? throw new InvalidOperationException("NotificationSenderNotFound");
        var config = DeserializeConfig(sender.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            throw new InvalidOperationException("TelegramBotTokenMissing");
        }

        if (string.IsNullOrWhiteSpace(config.ProfilePhotoFileId))
        {
            return null;
        }

        using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, config.ToConnectionSettings());
        var file = await CallTelegramAsync(
            client,
            config.BotToken,
            "getFile",
            new Dictionary<string, string>
            {
                ["file_id"] = config.ProfilePhotoFileId
            },
            cancellationToken);
        if (!file.Success)
        {
            return null;
        }

        var filePath = ReadString(file.Result, "file_path");
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        using var response = await client.GetAsync(
            $"https://api.telegram.org/file/bot{config.BotToken.Trim()}/{filePath}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType
                          ?? ResolveImageContentType(filePath);
        return new TelegramBotProfilePhotoResult(
            bytes,
            contentType,
            Path.GetFileName(filePath));
    }

    private async Task PollSenderAsync(
        NotificationSender sender,
        TelegramSenderConfig config,
        CancellationToken cancellationToken)
    {
        using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, config.ToConnectionSettings());
        var query = new Dictionary<string, string>
        {
            ["timeout"] = "0",
            ["limit"] = "25",
            ["allowed_updates"] = "[\"message\"]",
        };
        if (config.LastUpdateId > 0)
        {
            query["offset"] = (config.LastUpdateId + 1).ToString(CultureInfo.InvariantCulture);
        }

        var url = $"https://api.telegram.org/bot{config.BotToken.Trim()}/getUpdates?{BuildQuery(query)}";
        using var response = await client.GetAsync(url, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !IsTelegramOk(payload))
        {
            _logger.LogWarning(
                "Telegram polling sender {SenderId} failed: {Error}",
                sender.Id,
                ExtractTelegramError(payload) ?? response.ReasonPhrase);
            return;
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var update in result.EnumerateArray())
        {
            var updateId = ReadInt64(update, "update_id");
            if (updateId > config.LastUpdateId)
            {
                config.LastUpdateId = updateId;
            }

            await ProcessUpdateAsync(sender, config, update, cancellationToken);
        }

        config.LastPollingAt = DateTime.UtcNow;
        sender.ConfigJson = JsonSerializer.Serialize(config, JsonOptions);
        sender.UpdatedAt = DateTime.UtcNow;
    }

    private async Task ProcessUpdateAsync(
        NotificationSender sender,
        TelegramSenderConfig config,
        JsonElement update,
        CancellationToken cancellationToken)
    {
        var message = ReadIncomingMessage(update);
        if (message is null)
        {
            return;
        }

        var command = message.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (command.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            if (_externalAuthService is not null
                && await _externalAuthService.TryHandleTelegramBotAuthUpdateAsync(
                    update,
                    (chatId, text, ct) => SendBotReplyAsync(config, chatId, text, ct),
                    cancellationToken))
            {
                return;
            }

            var user = await ResolveUserAsync(message, cancellationToken);
            if (user is null)
            {
                await SendBotReplyAsync(
                    config,
                    message.ChatId,
                    "Не удалось привязать бот: Telegram не найден в карточке сотрудника. Укажите Telegram username в профиле или войдите через Telegram-авторизацию.",
                    cancellationToken);
                return;
            }

            await UpsertTelegramIdentityAsync(user, message, cancellationToken);
            await UpsertSubscriptionAsync(sender.Id, user, message, cancellationToken);
            await SendBotReplyAsync(
                config,
                message.ChatId,
                $"Готово. Бот \"{sender.Name}\" привязан к сотруднику {DisplayName(user)}.",
                cancellationToken);
            return;
        }

        if (command.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
        {
            var subscription = await _context.NotificationBotSubscriptions
                .AsNoTracking()
                .Include(item => item.User)
                .Where(item =>
                    item.NotificationSenderId == sender.Id
                    && item.TelegramUserId == message.TelegramUserId.ToString(CultureInfo.InvariantCulture)
                    && item.IsActive)
                .OrderByDescending(item => item.LastSeenAt)
                .FirstOrDefaultAsync(cancellationToken);
            var statusText = subscription?.User is null
                ? "Бот пока не привязан к сотруднику. Отправьте /start."
                : $"Бот привязан к сотруднику {DisplayName(subscription.User)}.";
            await SendBotReplyAsync(config, message.ChatId, statusText, cancellationToken);
            return;
        }

        if (command.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await SendBotReplyAsync(
                config,
                message.ChatId,
                "Я служебный бот PayPlanner. Команды: /start - привязать бот, /status - проверить привязку.",
                cancellationToken);
        }
    }

    private async Task<User?> ResolveUserAsync(IncomingTelegramMessage message, CancellationToken cancellationToken)
    {
        var telegramUserId = message.TelegramUserId.ToString(CultureInfo.InvariantCulture);
        var identity = await _context.UserExternalIdentities
            .Include(item => item.User)
            .FirstOrDefaultAsync(item =>
                item.Provider == "telegram"
                && (item.ProviderUserId == telegramUserId || item.TelegramUserId == telegramUserId),
                cancellationToken);
        if (identity?.User is { IsActive: true, IsApproved: true } identityUser)
        {
            return identityUser;
        }

        var username = NormalizeTelegramUsername(message.Username);
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var users = await _context.Users
            .Where(item => item.IsActive && item.IsApproved)
            .ToListAsync(cancellationToken);
        return users
            .Where(item => string.Equals(NormalizeTelegramUsername(item.Telegram), username, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList() is [var single]
            ? single
            : null;
    }

    private async Task UpsertTelegramIdentityAsync(
        User user,
        IncomingTelegramMessage message,
        CancellationToken cancellationToken)
    {
        var telegramUserId = message.TelegramUserId.ToString(CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var identity = await _context.UserExternalIdentities
            .FirstOrDefaultAsync(item => item.UserId == user.Id && item.Provider == "telegram", cancellationToken);

        if (identity is null)
        {
            identity = new UserExternalIdentity
            {
                UserId = user.Id,
                Provider = "telegram",
                ProviderUserId = telegramUserId,
                CreatedAt = now
            };
            _context.UserExternalIdentities.Add(identity);
        }

        identity.ProviderUserId = telegramUserId;
        identity.TelegramUserId = telegramUserId;
        identity.ChatId = message.ChatId;
        identity.ProviderUsername = NormalizeOptional(message.Username);
        identity.DisplayName = NormalizeOptional(message.DisplayName);
        identity.VerifiedAt ??= now;
        identity.LastUsedAt = now;
        identity.UpdatedAt = now;

        if (string.IsNullOrWhiteSpace(user.Telegram) && !string.IsNullOrWhiteSpace(message.Username))
        {
            user.Telegram = message.Username.StartsWith("@", StringComparison.Ordinal)
                ? message.Username
                : $"@{message.Username}";
            user.UpdatedAt = now;
        }
    }

    private async Task UpsertSubscriptionAsync(
        int senderId,
        User user,
        IncomingTelegramMessage message,
        CancellationToken cancellationToken)
    {
        var telegramUserId = message.TelegramUserId.ToString(CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var subscription = await _context.NotificationBotSubscriptions
            .FirstOrDefaultAsync(item =>
                item.NotificationSenderId == senderId
                && item.TelegramUserId == telegramUserId,
                cancellationToken);

        if (subscription is null)
        {
            subscription = new NotificationBotSubscription
            {
                NotificationSenderId = senderId,
                TelegramUserId = telegramUserId,
                CreatedAt = now
            };
            _context.NotificationBotSubscriptions.Add(subscription);
        }

        subscription.UserId = user.Id;
        subscription.ChatId = message.ChatId;
        subscription.Username = NormalizeOptional(message.Username) ?? string.Empty;
        subscription.DisplayName = NormalizeOptional(message.DisplayName) ?? string.Empty;
        subscription.IsActive = true;
        subscription.LastSeenAt = now;
    }

    private async Task SendBotReplyAsync(
        TelegramSenderConfig config,
        long chatId,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.BotToken))
        {
            return;
        }

        using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, config.ToConnectionSettings());
        await client.PostAsync(
            $"https://api.telegram.org/bot{config.BotToken.Trim()}/sendMessage",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture),
                ["text"] = text,
                ["disable_web_page_preview"] = "true",
            }),
            cancellationToken);
    }

    private static IncomingTelegramMessage? ReadIncomingMessage(JsonElement update)
    {
        if (!update.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var text = ReadString(message, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var chatId = ReadInt64(message, "chat", "id");
        var telegramUserId = ReadInt64(message, "from", "id");
        if (chatId == 0 || telegramUserId == 0)
        {
            return null;
        }

        var fullName = string.Join(" ", new[]
        {
            ReadString(message, "from", "first_name"),
            ReadString(message, "from", "last_name")
        }.Where(item => !string.IsNullOrWhiteSpace(item))).Trim();

        return new IncomingTelegramMessage(
            chatId,
            telegramUserId,
            ReadString(message, "from", "username"),
            fullName,
            text.Trim());
    }

    private async Task<NotificationSender?> LoadTelegramSenderAsync(
        int senderId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        var query = tracking
            ? _context.NotificationSenders
            : _context.NotificationSenders.AsNoTracking();
        return await query.FirstOrDefaultAsync(
            item => item.Id == senderId && item.Channel == NotificationChannels.Telegram,
            cancellationToken);
    }

    private async Task<TelegramWebhookSyncResultDto> DeleteWebhookCoreAsync(
        TelegramSenderConfig config,
        CancellationToken cancellationToken)
    {
        using var client = TelegramBotApiHttpClientFactory.Create(_httpClientFactory, config.ToConnectionSettings());
        using var response = await client.PostAsync(
            $"https://api.telegram.org/bot{config.BotToken.Trim()}/deleteWebhook",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["drop_pending_updates"] = "false"
            }),
            cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || !IsTelegramOk(payload))
        {
            return new TelegramWebhookSyncResultDto
            {
                Success = false,
                Message = ExtractTelegramError(payload) ?? response.ReasonPhrase ?? "Telegram API вернул ошибку."
            };
        }

        return new TelegramWebhookSyncResultDto
        {
            Success = true,
            Message = "Webhook Telegram-бота отключён."
        };
    }

    private sealed record TelegramApiCallResult(
        bool Success,
        string? Error,
        JsonElement Result);

    private sealed record TelegramProfileForm(
        string Name,
        string Description,
        string ShortDescription,
        IReadOnlyList<TelegramBotCommandConfig> Commands);

    private static async Task<TelegramApiCallResult> CallTelegramAsync(
        HttpClient client,
        string botToken,
        string method,
        Dictionary<string, string>? form,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(
            $"https://api.telegram.org/bot{botToken.Trim()}/{method}",
            new FormUrlEncodedContent(form ?? new Dictionary<string, string>()),
            cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new TelegramApiCallResult(
                false,
                ExtractTelegramError(payload) ?? response.ReasonPhrase ?? "Telegram API вернул ошибку.",
                default);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var ok = root.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
            if (!ok)
            {
                return new TelegramApiCallResult(
                    false,
                    root.TryGetProperty("description", out var description)
                        ? description.GetString()
                        : "Telegram API вернул ошибку.",
                    default);
            }

            return new TelegramApiCallResult(
                true,
                null,
                root.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
        catch (JsonException)
        {
            return new TelegramApiCallResult(false, "Telegram API вернул некорректный JSON.", default);
        }
    }

    private static TelegramProfileForm NormalizeProfileRequest(UpdateTelegramBotProfileRequest request)
    {
        var commands = (request.Commands ?? [])
            .Select(item => new TelegramBotCommandConfig
            {
                Command = NormalizeCommand(item.Command),
                Description = item.Description.Trim()
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Command) || !string.IsNullOrWhiteSpace(item.Description))
            .ToList();

        return new TelegramProfileForm(
            request.Name?.Trim() ?? string.Empty,
            request.Description?.Trim() ?? string.Empty,
            request.ShortDescription?.Trim() ?? string.Empty,
            commands);
    }

    private static string? ValidateProfileRequest(TelegramProfileForm request)
    {
        if (request.Name.Length > 64)
        {
            return "Имя Telegram-бота должно быть не длиннее 64 символов.";
        }

        if (request.Description.Length > 512)
        {
            return "Описание Telegram-бота должно быть не длиннее 512 символов.";
        }

        if (request.ShortDescription.Length > 120)
        {
            return "Короткое описание Telegram-бота должно быть не длиннее 120 символов.";
        }

        if (request.Commands.Count > 100)
        {
            return "Telegram разрешает не больше 100 команд.";
        }

        foreach (var command in request.Commands)
        {
            if (!CommandRegex.IsMatch(command.Command))
            {
                return "Команда Telegram должна быть 1-32 символа: латинские строчные буквы, цифры и подчёркивание.";
            }

            if (string.IsNullOrWhiteSpace(command.Description) || command.Description.Length > 256)
            {
                return "Описание каждой команды должно быть от 1 до 256 символов.";
            }
        }

        return null;
    }

    private static List<TelegramBotCommandConfig> ReadCommands(JsonElement result)
    {
        var commands = new List<TelegramBotCommandConfig>();
        foreach (var item in result.EnumerateArray())
        {
            var command = ReadString(item, "command") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            commands.Add(new TelegramBotCommandConfig
            {
                Command = NormalizeCommand(command),
                Description = ReadString(item, "description") ?? string.Empty
            });
        }

        return commands;
    }

    private static string ReadProfilePhotoFileId(JsonElement result)
    {
        if (!result.TryGetProperty("photos", out var photos)
            || photos.ValueKind != JsonValueKind.Array
            || photos.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var firstPhoto = photos[0];
        if (firstPhoto.ValueKind != JsonValueKind.Array || firstPhoto.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var largest = firstPhoto[firstPhoto.GetArrayLength() - 1];
        return ReadString(largest, "file_id") ?? string.Empty;
    }

    private static TelegramBotProfileDto BuildProfileDto(TelegramSenderConfig config)
        => new()
        {
            BotId = config.BotId,
            BotUsername = config.BotUsername,
            BotFirstName = config.BotFirstName,
            Name = config.ProfileName,
            Description = config.ProfileDescription,
            ShortDescription = config.ProfileShortDescription,
            Commands = config.ProfileCommands
                .Select(item => new TelegramBotCommandDto
                {
                    Command = item.Command,
                    Description = item.Description
                })
                .ToList(),
            HasProfilePhoto = !string.IsNullOrWhiteSpace(config.ProfilePhotoFileId),
            SyncedAt = config.ProfileSyncedAt,
            CanJoinGroups = config.CanJoinGroups,
            CanReadAllGroupMessages = config.CanReadAllGroupMessages,
            SupportsInlineQueries = config.SupportsInlineQueries
        };

    private static string ResolveImageContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
    }

    private static TelegramSenderConfig DeserializeConfig(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TelegramSenderConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<TelegramSenderConfig>(raw, JsonOptions) ?? new TelegramSenderConfig();
        }
        catch (JsonException)
        {
            return new TelegramSenderConfig();
        }
    }

    private static string BuildWebhookUrl(HttpRequest request, int senderId)
    {
        var scheme = request.Headers.TryGetValue("X-Forwarded-Proto", out var forwardedProto)
            ? forwardedProto.ToString().Split(',')[0].Trim()
            : request.Scheme;
        var host = request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHost)
            ? forwardedHost.ToString().Split(',')[0].Trim()
            : request.Host.Value;
        return $"{scheme}://{host}{request.PathBase}/api/notifications/telegram/{senderId}/webhook";
    }

    private static string BuildQuery(Dictionary<string, string> query)
        => string.Join("&", query.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));

    private static bool IsTelegramOk(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractTelegramError(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("description", out var description)
                ? description.GetString()?.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static long ReadInt64(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return 0;
            }
        }

        return current.ValueKind == JsonValueKind.Number && current.TryGetInt64(out var value) ? value : 0;
    }

    private static bool ReadBool(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeTelegramUsername(string? value)
        => (value ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    private static string NormalizeCommand(string? value)
        => (value ?? string.Empty).Trim().TrimStart('/').ToLowerInvariant();

    private static string DisplayName(User user)
        => string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName.Trim();
}
