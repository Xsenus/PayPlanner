using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed record ExternalIdentityProfile(
    string Provider,
    string ProviderUserId,
    string? Email,
    bool EmailVerified,
    string? Username,
    string? DisplayName,
    string? AvatarUrl,
    string? Phone = null,
    bool PhoneVerified = false);

public sealed partial class ExternalAuthService
{
    private const int DefaultPollIntervalMs = 2000;
    private const string GoogleEnabledKey = "auth.google.enabled";
    private const string GoogleClientIdKey = "auth.google.clientId";
    private const string GoogleClientSecretKey = "auth.google.clientSecret";
    private const string VkEnabledKey = "auth.vk.enabled";
    private const string VkClientIdKey = "auth.vk.clientId";
    private const string VkClientSecretKey = "auth.vk.clientSecret";
    private const string YandexEnabledKey = "auth.yandex.enabled";
    private const string YandexClientIdKey = "auth.yandex.clientId";
    private const string YandexClientSecretKey = "auth.yandex.clientSecret";
    private const string TelegramEnabledKey = "auth.telegram.enabled";
    private const string TelegramWidgetEnabledKey = "auth.telegram.widgetEnabled";
    private const string TelegramBotTokenKey = "auth.telegram.botToken";
    private const string TelegramBotUsernameKey = "auth.telegram.botUsername";
    private const string TelegramWebhookSecretKey = "auth.telegram.webhookSecret";
    private const string TelegramBotInfoSyncedAtKey = "auth.telegram.botInfoSyncedAt";
    private const string TelegramWebhookSyncedAtKey = "auth.telegram.webhookSyncedAt";

    private readonly PaymentContext _context;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly AuthService _authService;
    private readonly ILogger<ExternalAuthService> _logger;

    public ExternalAuthService(
        PaymentContext context,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        IPasswordHasher<User> passwordHasher,
        AuthService authService,
        ILogger<ExternalAuthService> logger)
    {
        _context = context;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _passwordHasher = passwordHasher;
        _authService = authService;
        _logger = logger;
    }

    public async Task<AuthProviderPublicSettingsDto> GetPublicSettingsAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        var telegramUsername = await GetSettingOrConfigAsync(
            TelegramBotUsernameKey,
            "Auth:Telegram:BotUsername",
            cancellationToken);

        return new AuthProviderPublicSettingsDto
        {
            GoogleEnabled = await IsExternalProviderReadyAsync(
                GoogleEnabledKey,
                "Auth:Google:Enabled",
                GoogleClientIdKey,
                "Auth:Google:ClientId",
                GoogleClientSecretKey,
                "Auth:Google:ClientSecret",
                cancellationToken),
            VkEnabled = await IsExternalProviderReadyAsync(
                VkEnabledKey,
                "Auth:Vk:Enabled",
                VkClientIdKey,
                "Auth:Vk:ClientId",
                VkClientSecretKey,
                "Auth:Vk:ClientSecret",
                cancellationToken),
            YandexEnabled = await IsExternalProviderReadyAsync(
                YandexEnabledKey,
                "Auth:Yandex:Enabled",
                YandexClientIdKey,
                "Auth:Yandex:ClientId",
                YandexClientSecretKey,
                "Auth:Yandex:ClientSecret",
                cancellationToken),
            TelegramEnabled = await IsTelegramReadyAsync(cancellationToken),
            TelegramWidgetEnabled = await IsTelegramWidgetReadyAsync(request, cancellationToken),
            TelegramBotUsername = string.IsNullOrWhiteSpace(telegramUsername) ? null : telegramUsername.Trim()
        };
    }

    public async Task<AuthProviderSettingsDto> GetAdminSettingsAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        var googleReady = await IsExternalProviderReadyAsync(
            GoogleEnabledKey,
            "Auth:Google:Enabled",
            GoogleClientIdKey,
            "Auth:Google:ClientId",
            GoogleClientSecretKey,
            "Auth:Google:ClientSecret",
            cancellationToken);
        var vkReady = await IsExternalProviderReadyAsync(
            VkEnabledKey,
            "Auth:Vk:Enabled",
            VkClientIdKey,
            "Auth:Vk:ClientId",
            VkClientSecretKey,
            "Auth:Vk:ClientSecret",
            cancellationToken);
        var yandexReady = await IsExternalProviderReadyAsync(
            YandexEnabledKey,
            "Auth:Yandex:Enabled",
            YandexClientIdKey,
            "Auth:Yandex:ClientId",
            YandexClientSecretKey,
            "Auth:Yandex:ClientSecret",
            cancellationToken);
        var telegramBotReady = await IsTelegramReadyAsync(cancellationToken);
        var telegramWidgetReady = await IsTelegramWidgetReadyAsync(request, cancellationToken);

        return new AuthProviderSettingsDto
        {
            GoogleEnabled = await GetBooleanSettingOrConfigAsync(GoogleEnabledKey, "Auth:Google:Enabled", false, cancellationToken),
            GoogleClientId = await GetSettingOrConfigAsync(GoogleClientIdKey, "Auth:Google:ClientId", cancellationToken) ?? string.Empty,
            GoogleClientSecret = await GetSettingOrConfigAsync(GoogleClientSecretKey, "Auth:Google:ClientSecret", cancellationToken) ?? string.Empty,
            GoogleReady = googleReady,
            GoogleCallbackUrl = BuildAbsoluteUrl(request, "/api/auth/external/callback/google"),

            VkEnabled = await GetBooleanSettingOrConfigAsync(VkEnabledKey, "Auth:Vk:Enabled", false, cancellationToken),
            VkClientId = await GetSettingOrConfigAsync(VkClientIdKey, "Auth:Vk:ClientId", cancellationToken) ?? string.Empty,
            VkClientSecret = await GetSettingOrConfigAsync(VkClientSecretKey, "Auth:Vk:ClientSecret", cancellationToken) ?? string.Empty,
            VkReady = vkReady,
            VkCallbackUrl = BuildAbsoluteUrl(request, "/api/auth/external/callback/vk"),

            YandexEnabled = await GetBooleanSettingOrConfigAsync(YandexEnabledKey, "Auth:Yandex:Enabled", false, cancellationToken),
            YandexClientId = await GetSettingOrConfigAsync(YandexClientIdKey, "Auth:Yandex:ClientId", cancellationToken) ?? string.Empty,
            YandexClientSecret = await GetSettingOrConfigAsync(YandexClientSecretKey, "Auth:Yandex:ClientSecret", cancellationToken) ?? string.Empty,
            YandexReady = yandexReady,
            YandexCallbackUrl = BuildAbsoluteUrl(request, "/api/auth/external/callback/yandex"),

            TelegramEnabled = await GetBooleanSettingOrConfigAsync(TelegramEnabledKey, "Auth:Telegram:Enabled", false, cancellationToken),
            TelegramWidgetEnabled = await GetBooleanSettingOrConfigAsync(TelegramWidgetEnabledKey, "Auth:Telegram:WidgetEnabled", false, cancellationToken),
            TelegramBotToken = await GetSettingOrConfigAsync(TelegramBotTokenKey, "Auth:Telegram:BotToken", cancellationToken) ?? string.Empty,
            TelegramBotUsername = await GetSettingOrConfigAsync(TelegramBotUsernameKey, "Auth:Telegram:BotUsername", cancellationToken) ?? string.Empty,
            TelegramWebhookSecret = await GetSettingOrConfigAsync(TelegramWebhookSecretKey, "Auth:Telegram:WebhookSecret", cancellationToken) ?? string.Empty,
            TelegramWebhookUrl = BuildAbsoluteUrl(request, "/api/auth/telegram/webhook"),
            TelegramWebhookConfigured = await HasTelegramWebhookSyncAsync(cancellationToken),
            TelegramBotReady = telegramBotReady,
            TelegramWidgetReady = telegramWidgetReady,
            TelegramBotInfoSyncedAt = ParseDateTime(await GetSettingOrConfigAsync(TelegramBotInfoSyncedAtKey, null, cancellationToken)),
            TelegramWebhookSyncedAt = ParseDateTime(await GetSettingOrConfigAsync(TelegramWebhookSyncedAtKey, null, cancellationToken)),
        };
    }

    public async Task<AuthProviderSettingsSaveResultDto> SaveGoogleSettingsAsync(
        ExternalOAuthProviderSettingsUpdateRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await SaveExternalProviderSettingsCoreAsync(
            GoogleEnabledKey,
            GoogleClientIdKey,
            GoogleClientSecretKey,
            request.Enabled,
            request.ClientId,
            request.ClientSecret,
            now,
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
#pragma warning disable CS0162
        return await BuildSettingsSaveResultAsync(httpRequest, "Настройки Google сохранены.", cancellationToken);
    }

    public async Task<AuthProviderSettingsSaveResultDto> SaveVkSettingsAsync(
        ExternalOAuthProviderSettingsUpdateRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await SaveExternalProviderSettingsCoreAsync(
            VkEnabledKey,
            VkClientIdKey,
            VkClientSecretKey,
            request.Enabled,
            request.ClientId,
            request.ClientSecret,
            now,
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return await BuildSettingsSaveResultAsync(httpRequest, "Настройки VK сохранены.", cancellationToken);
    }

    public async Task<AuthProviderSettingsSaveResultDto> SaveYandexSettingsAsync(
        ExternalOAuthProviderSettingsUpdateRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await SaveExternalProviderSettingsCoreAsync(
            YandexEnabledKey,
            YandexClientIdKey,
            YandexClientSecretKey,
            request.Enabled,
            request.ClientId,
            request.ClientSecret,
            now,
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return await BuildSettingsSaveResultAsync(httpRequest, "Настройки Яндекса сохранены.", cancellationToken);
    }

    public async Task<AuthProviderSettingsSaveResultDto> SaveTelegramBotSettingsAsync(
        TelegramLoginBotSettingsUpdateRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await SaveTelegramBotSettingsCoreAsync(
            request.Enabled,
            request.BotToken,
            now,
            cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return await BuildSettingsSaveResultAsync(httpRequest, "Настройки Telegram Bot сохранены.", cancellationToken);
    }

    public async Task<AuthProviderSettingsSaveResultDto> SaveTelegramWidgetSettingsAsync(
        TelegramWidgetSettingsUpdateRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await UpsertSystemSettingAsync(TelegramWidgetEnabledKey, request.Enabled ? "true" : "false", now, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
        return await BuildSettingsSaveResultAsync(httpRequest, "Настройки Telegram Widget сохранены.", cancellationToken);
    }

    public async Task<AuthProviderSettingsSaveResultDto> UpdateSettingsAsync(
        AuthProviderSettingsUpdateRequest request,
        HttpRequest httpRequest,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        await SaveExternalProviderSettingsCoreAsync(
            GoogleEnabledKey,
            GoogleClientIdKey,
            GoogleClientSecretKey,
            request.GoogleEnabled,
            request.GoogleClientId,
            request.GoogleClientSecret,
            now,
            cancellationToken);

        await SaveExternalProviderSettingsCoreAsync(
            VkEnabledKey,
            VkClientIdKey,
            VkClientSecretKey,
            request.VkEnabled,
            request.VkClientId,
            request.VkClientSecret,
            now,
            cancellationToken);

        await SaveExternalProviderSettingsCoreAsync(
            YandexEnabledKey,
            YandexClientIdKey,
            YandexClientSecretKey,
            request.YandexEnabled,
            request.YandexClientId,
            request.YandexClientSecret,
            now,
            cancellationToken);

        await SaveTelegramBotSettingsCoreAsync(
            request.TelegramEnabled,
            request.TelegramBotToken,
            now,
            cancellationToken);

        await UpsertSystemSettingAsync(TelegramWidgetEnabledKey, request.TelegramWidgetEnabled ? "true" : "false", now, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return await BuildSettingsSaveResultAsync(httpRequest, "Настройки внешней авторизации сохранены.", cancellationToken);

        return new AuthProviderSettingsSaveResultDto
        {
            Settings = await GetAdminSettingsAsync(httpRequest, cancellationToken),
            Message = "Настройки внешней авторизации сохранены"
        };
    }

#pragma warning restore CS0162
    private async Task SaveExternalProviderSettingsCoreAsync(
        string enabledSettingKey,
        string clientIdSettingKey,
        string clientSecretSettingKey,
        bool enabled,
        string? clientId,
        string? clientSecret,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await UpsertSystemSettingAsync(enabledSettingKey, enabled ? "true" : "false", now, cancellationToken);
        await UpsertSystemSettingAsync(clientIdSettingKey, NormalizeOptional(clientId) ?? string.Empty, now, cancellationToken);
        await UpsertSystemSettingAsync(clientSecretSettingKey, NormalizeOptional(clientSecret) ?? string.Empty, now, cancellationToken);
    }

    private async Task SaveTelegramBotSettingsCoreAsync(
        bool enabled,
        string? botToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var normalizedToken = NormalizeOptional(botToken) ?? string.Empty;
        var previousEnabled = await GetBooleanSettingOrConfigAsync(TelegramEnabledKey, "Auth:Telegram:Enabled", false, cancellationToken);
        var previousToken = await GetSettingOrConfigAsync(TelegramBotTokenKey, "Auth:Telegram:BotToken", cancellationToken) ?? string.Empty;
        var tokenChanged = !string.Equals(previousToken, normalizedToken, StringComparison.Ordinal);
        var enabledChanged = previousEnabled != enabled;

        await UpsertSystemSettingAsync(TelegramEnabledKey, enabled ? "true" : "false", now, cancellationToken);
        await UpsertSystemSettingAsync(TelegramBotTokenKey, normalizedToken, now, cancellationToken);

        if (string.IsNullOrWhiteSpace(normalizedToken))
        {
            await UpsertSystemSettingAsync(TelegramBotUsernameKey, string.Empty, now, cancellationToken);
            await UpsertSystemSettingAsync(TelegramBotInfoSyncedAtKey, string.Empty, now, cancellationToken);
            await UpsertSystemSettingAsync(TelegramWebhookSyncedAtKey, string.Empty, now, cancellationToken);
            return;
        }

        await RefreshTelegramBotInfoAsync(normalizedToken, now, cancellationToken);
        await EnsureTelegramWebhookSecretAsync(now, cancellationToken);

        if (tokenChanged || enabledChanged)
        {
            await UpsertSystemSettingAsync(TelegramWebhookSyncedAtKey, string.Empty, now, cancellationToken);
        }
    }

    private async Task EnsureTelegramWebhookSecretAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(await GetSettingOrConfigAsync(TelegramWebhookSecretKey, "Auth:Telegram:WebhookSecret", cancellationToken)))
        {
            await UpsertSystemSettingAsync(TelegramWebhookSecretKey, GenerateToken(24), now, cancellationToken);
        }
    }

    private async Task<AuthProviderSettingsSaveResultDto> BuildSettingsSaveResultAsync(
        HttpRequest httpRequest,
        string message,
        CancellationToken cancellationToken)
    {
        return new AuthProviderSettingsSaveResultDto
        {
            Settings = await GetAdminSettingsAsync(httpRequest, cancellationToken),
            Message = message
        };
    }

    public async Task<TelegramWebhookSyncResultDto> SyncTelegramBotAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        var botToken = await GetSettingOrConfigAsync(TelegramBotTokenKey, "Auth:Telegram:BotToken", cancellationToken);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("Укажите Telegram bot token перед синхронизацией.");
        }

        var now = DateTime.UtcNow;
        var username = await RefreshTelegramBotInfoAsync(botToken, now, cancellationToken);
        var secret = await GetSettingOrConfigAsync(TelegramWebhookSecretKey, "Auth:Telegram:WebhookSecret", cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = GenerateToken(24);
            await UpsertSystemSettingAsync(TelegramWebhookSecretKey, secret, now, cancellationToken);
        }

        var webhookUrl = BuildAbsoluteUrl(request, "/api/auth/telegram/webhook");
        var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsync(
            $"https://api.telegram.org/bot{botToken}/setWebhook",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["url"] = webhookUrl,
                ["secret_token"] = secret,
                ["allowed_updates"] = "[\"message\"]"
            }),
            cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetProviderErrorDetail(payload, response.ReasonPhrase)
                ?? "Не удалось настроить Telegram webhook.");
        }

        await UpsertSystemSettingAsync(TelegramWebhookSyncedAtKey, now.ToString("O"), now, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new TelegramWebhookSyncResultDto
        {
            Success = true,
            Message = "Telegram bot и webhook синхронизированы",
            BotUsername = username,
            WebhookUrl = webhookUrl
        };
    }

    public async Task<ExternalAuthStartResponseDto> StartExternalAsync(
        string provider,
        string? returnUrl,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = TechnicalEmailHelper.NormalizeProvider(provider);
        if (normalizedProvider is not ("google" or "vk" or "yandex"))
        {
            throw new InvalidOperationException("Unsupported external auth provider");
        }

        var configuration = await ResolveExternalProviderConfigurationAsync(normalizedProvider, cancellationToken)
            ?? throw new InvalidOperationException($"Внешний провайдер '{normalizedProvider}' не настроен.");

        var entity = new ExternalAuthRequest
        {
            Provider = normalizedProvider,
            State = GenerateToken(12),
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.ExternalAuthRequests.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        var redirectUri = BuildAbsoluteUrl(request, $"/api/auth/external/callback/{normalizedProvider}");

        return new ExternalAuthStartResponseDto
        {
            Provider = normalizedProvider,
            State = entity.State,
            ReturnUrl = entity.ReturnUrl,
            AuthUrl = BuildExternalAuthorizationUrl(configuration, entity.State, redirectUri),
            ExpiresAt = entity.ExpiresAt,
            PollIntervalMs = DefaultPollIntervalMs
        };
    }

    public async Task<ExternalAuthStatusResponseDto> GetExternalStatusAsync(string state, CancellationToken cancellationToken = default)
    {
        var normalizedState = NormalizeRequired(state, "Invalid state");
        var request = await _context.ExternalAuthRequests
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.State == normalizedState, cancellationToken);

        if (request is null)
        {
            throw new KeyNotFoundException("Authorization request not found");
        }

        if (request.ExpiresAt <= DateTime.UtcNow
            && request.Status is not ("completed" or "consumed" or "failed" or "expired"))
        {
            request.Status = "expired";
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (!string.Equals(request.Status, "completed", StringComparison.Ordinal))
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = request.Status,
                Completed = false,
                Provider = request.Provider,
                Detail = request.Error,
                ReturnUrl = request.ReturnUrl
            };
        }

        if (!request.UserId.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "pending",
                Completed = false,
                Provider = request.Provider,
                ReturnUrl = request.ReturnUrl
            };
        }

        if (request.ConsumedAt.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "consumed",
                Completed = false,
                Provider = request.Provider,
                ReturnUrl = request.ReturnUrl
            };
        }

        var user = await _context.Users
            .Include(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == request.UserId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Пользователь внешней авторизации не найден.");

        var result = await BuildCompletionResponseAsync(user, request.Provider, request.ReturnUrl, cancellationToken);
        request.ConsumedAt = DateTime.UtcNow;
        request.Status = "consumed";
        await _context.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<string> HandleExternalCallbackAsync(
        string provider,
        string? state,
        string? code,
        string? deviceId,
        string? error,
        string? errorDescription,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = TechnicalEmailHelper.NormalizeProvider(provider);
        if (normalizedProvider is not ("google" or "vk" or "yandex"))
        {
            return BuildExternalAuthPopupHtml(false, "Неизвестный провайдер авторизации.");
        }

        var configuration = await ResolveExternalProviderConfigurationAsync(normalizedProvider, cancellationToken);
        if (configuration is null)
        {
            return BuildExternalAuthPopupHtml(false, $"Провайдер '{normalizedProvider}' не настроен.");
        }

        var normalizedState = NormalizeOptional(state);
        if (string.IsNullOrWhiteSpace(normalizedState))
        {
            return BuildExternalAuthPopupHtml(false, "Не передан state внешней авторизации.");
        }

        var authRequest = await _context.ExternalAuthRequests
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.Provider == normalizedProvider && x.State == normalizedState, cancellationToken);
        var redirectPath = BuildAuthRedirectPath(normalizedState, normalizedProvider);

        if (authRequest is null)
        {
            return BuildExternalAuthPopupHtml(false, "Запрос внешней авторизации не найден.", redirectPath);
        }

        if (authRequest.ExpiresAt <= DateTime.UtcNow)
        {
            authRequest.Status = "expired";
            authRequest.Error = "Срок действия запроса внешней авторизации истёк.";
            await _context.SaveChangesAsync(cancellationToken);
            return BuildExternalAuthPopupHtml(false, authRequest.Error, redirectPath);
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            authRequest.Status = "failed";
            authRequest.Error = string.IsNullOrWhiteSpace(errorDescription) ? error.Trim() : errorDescription.Trim();
            authRequest.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return BuildExternalAuthPopupHtml(false, authRequest.Error, redirectPath);
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            authRequest.Status = "failed";
            authRequest.Error = "Провайдер не вернул код авторизации.";
            authRequest.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return BuildExternalAuthPopupHtml(false, authRequest.Error, redirectPath);
        }

        try
        {
            var redirectUri = BuildAbsoluteUrl(request, $"/api/auth/external/callback/{normalizedProvider}");
            var tokenResult = await ExchangeExternalAccessTokenAsync(
                configuration,
                code.Trim(),
                redirectUri,
                normalizedState,
                NormalizeOptional(deviceId),
                cancellationToken);

            var profile = normalizedProvider switch
            {
                "google" => await LoadGoogleProfileAsync(tokenResult.AccessToken, cancellationToken),
                "vk" => await LoadVkProfileAsync(configuration, tokenResult, cancellationToken),
                "yandex" => await LoadYandexProfileAsync(tokenResult.AccessToken, cancellationToken),
                _ => throw new InvalidOperationException("Unsupported external auth provider")
            };

            var user = await ResolveOrCreateExternalUserAsync(profile, cancellationToken);
            authRequest.UserId = user.Id;
            authRequest.Status = "completed";
            authRequest.Error = null;
            authRequest.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            return BuildExternalAuthPopupHtml(
                true,
                $"Вход через {configuration.DisplayName} завершён. Вернитесь на сайт.",
                redirectPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "External auth callback failed for {Provider}", normalizedProvider);
            authRequest.Status = "failed";
            authRequest.Error = ex.Message;
            authRequest.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            return BuildExternalAuthPopupHtml(false, ex.Message, redirectPath);
        }
    }

    public async Task<ExternalAuthStatusResponseDto> CompleteTelegramWidgetAsync(
        TelegramWidgetLoginRequest payload,
        CancellationToken cancellationToken = default)
    {
        var botToken = await GetSettingOrConfigAsync(TelegramBotTokenKey, "Auth:Telegram:BotToken", cancellationToken);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("Telegram bot token is not configured.");
        }

        if (!ValidateTelegramWidgetPayload(payload, botToken))
        {
            throw new InvalidOperationException("Invalid telegram auth payload.");
        }

        var fullName = string.Join(" ", new[] { payload.FirstName, payload.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var user = await ResolveOrCreateExternalUserAsync(
            new ExternalIdentityProfile(
                Provider: "telegram",
                ProviderUserId: payload.Id,
                Email: null,
                EmailVerified: false,
                Username: payload.Username,
                DisplayName: fullName,
                AvatarUrl: payload.PhotoUrl),
            cancellationToken);

        return await BuildCompletionResponseAsync(user, "telegram", "/", cancellationToken);
    }

    public async Task<ExternalAuthStartResponseDto> StartTelegramBotAsync(
        string? returnUrl,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await IsTelegramReadyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Telegram bot is not configured for login.");
        }

        var username = await GetSettingOrConfigAsync(TelegramBotUsernameKey, "Auth:Telegram:BotUsername", cancellationToken)
            ?? throw new InvalidOperationException("Telegram bot username is not configured.");

        var entity = new TelegramAuthRequest
        {
            State = GenerateToken(12),
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.TelegramAuthRequests.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return new ExternalAuthStartResponseDto
        {
            Provider = "telegram",
            State = entity.State,
            ReturnUrl = NormalizeReturnUrl(returnUrl),
            AuthUrl = $"https://t.me/{username}?start=login_{entity.State}",
            ExpiresAt = entity.ExpiresAt,
            PollIntervalMs = DefaultPollIntervalMs
        };
    }

    public async Task<ExternalAuthStatusResponseDto> GetTelegramBotStatusAsync(string state, CancellationToken cancellationToken = default)
    {
        var normalizedState = NormalizeRequired(state, "Invalid state");
        var request = await _context.TelegramAuthRequests
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.State == normalizedState, cancellationToken);

        if (request is null)
        {
            throw new KeyNotFoundException("Authorization request not found");
        }

        if (request.ExpiresAt <= DateTime.UtcNow
            && request.Status is not ("completed" or "consumed" or "failed" or "expired"))
        {
            request.Status = "expired";
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (!string.Equals(request.Status, "completed", StringComparison.Ordinal))
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = request.Status,
                Completed = false,
                Provider = "telegram",
                Detail = request.Error
            };
        }

        if (!request.UserId.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "pending",
                Completed = false,
                Provider = "telegram"
            };
        }

        if (request.ConsumedAt.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "consumed",
                Completed = false,
                Provider = "telegram"
            };
        }

        var user = await _context.Users
            .Include(x => x.Role)
            .FirstOrDefaultAsync(x => x.Id == request.UserId.Value, cancellationToken)
            ?? throw new InvalidOperationException("Пользователь Telegram-авторизации не найден.");

        var result = await BuildCompletionResponseAsync(user, "telegram", "/", cancellationToken);
        request.ConsumedAt = DateTime.UtcNow;
        request.Status = "consumed";
        await _context.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task HandleTelegramWebhookAsync(JsonDocument updateDocument, HttpRequest request, CancellationToken cancellationToken = default)
    {
        var secret = await GetSettingOrConfigAsync(TelegramWebhookSecretKey, "Auth:Telegram:WebhookSecret", cancellationToken);
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new UnauthorizedAccessException("Telegram webhook secret is not configured.");
        }

        var incomingSecret = request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var headerValues)
            ? headerValues.ToString()
            : string.Empty;
        if (!string.Equals(secret, incomingSecret, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Telegram webhook secret is invalid.");
        }

        if (!updateDocument.RootElement.TryGetProperty("message", out var messageElement)
            || messageElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var text = ReadString(messageElement, "text");
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var state = ExtractTelegramState(text);
        if (string.IsNullOrWhiteSpace(state))
        {
            return;
        }

        var chatId = ReadInt64(messageElement, "chat", "id");
        var telegramUserId = ReadInt64(messageElement, "from", "id");
        if (chatId == 0 || telegramUserId == 0)
        {
            return;
        }

        var authRequest = await _context.TelegramAuthRequests
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.State == state, cancellationToken);
        if (authRequest is null)
        {
            await SendTelegramMessageAsync(chatId, "Ссылка авторизации не найдена. Запросите новую на сайте.", cancellationToken);
            return;
        }

        if (authRequest.ExpiresAt <= DateTime.UtcNow)
        {
            authRequest.Status = "expired";
            authRequest.Error = "Срок действия ссылки истёк.";
            await _context.SaveChangesAsync(cancellationToken);
            await SendTelegramMessageAsync(chatId, "Ссылка авторизации устарела. Вернитесь на сайт и начните вход заново.", cancellationToken);
            return;
        }

        if (authRequest.Status is "completed" or "consumed")
        {
            await SendTelegramMessageAsync(chatId, "Эта ссылка уже использована. Если нужно, запустите вход заново на сайте.", cancellationToken);
            return;
        }

        var fullName = string.Join(" ", new[]
        {
            ReadString(messageElement, "from", "first_name"),
            ReadString(messageElement, "from", "last_name")
        }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        var user = await ResolveOrCreateExternalUserAsync(
            new ExternalIdentityProfile(
                Provider: "telegram",
                ProviderUserId: telegramUserId.ToString(),
                Email: null,
                EmailVerified: false,
                Username: ReadString(messageElement, "from", "username"),
                DisplayName: fullName,
                AvatarUrl: null),
            cancellationToken);

        authRequest.UserId = user.Id;
        authRequest.ChatId = chatId;
        authRequest.TelegramUserId = telegramUserId.ToString();
        authRequest.Status = "completed";
        authRequest.Error = null;
        authRequest.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var answer = !user.IsActive
            ? "Аккаунт отключён. Обратитесь к администратору."
            : !user.IsApproved
                ? "Аккаунт создан и ожидает подтверждения администратора. Вернитесь на сайт."
                : "Вход подтверждён. Вернитесь на сайт, авторизация завершится автоматически.";
        await SendTelegramMessageAsync(chatId, answer, cancellationToken);
    }

    private async Task<ExternalAuthStatusResponseDto> BuildCompletionResponseAsync(
        User user,
        string provider,
        string? returnUrl,
        CancellationToken cancellationToken)
    {
        if (user.Role is null)
        {
            await _context.Entry(user).Reference(x => x.Role).LoadAsync(cancellationToken);
        }

        if (!user.IsActive)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "inactive",
                Completed = true,
                Provider = provider,
                ReturnUrl = returnUrl,
                IsInactive = true,
                Detail = "Ваш аккаунт отключён. Обратитесь к администратору."
            };
        }

        if (!user.IsApproved)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "pending_approval",
                Completed = true,
                Provider = provider,
                ReturnUrl = returnUrl,
                RequiresApproval = true,
                Detail = "Ваш аккаунт ожидает одобрения администратором."
            };
        }

        var response = await _authService.BuildLoginResponseAsync(user, cancellationToken);
        return new ExternalAuthStatusResponseDto
        {
            Status = "completed",
            Completed = true,
            Provider = provider,
            ReturnUrl = returnUrl,
            Token = response.Token,
            User = response.User
        };
    }

    private async Task<User> ResolveOrCreateExternalUserAsync(ExternalIdentityProfile profile, CancellationToken cancellationToken)
    {
        var provider = TechnicalEmailHelper.NormalizeProvider(profile.Provider);
        var providerUserId = NormalizeRequired(profile.ProviderUserId, "External provider user id is invalid.");
        var confirmedEmail = profile.EmailVerified ? TechnicalEmailHelper.NormalizeRealEmail(profile.Email) : null;
        var now = DateTime.UtcNow;

        var identity = await _context.UserExternalIdentities
            .Include(x => x.User)
            .ThenInclude(x => x!.Role)
            .FirstOrDefaultAsync(x => x.Provider == provider && x.ProviderUserId == providerUserId, cancellationToken);

        var user = identity?.User;
        if (user is null && !string.IsNullOrWhiteSpace(confirmedEmail))
        {
            user = await _context.Users
                .Include(x => x.Role)
                .FirstOrDefaultAsync(x => x.Email == confirmedEmail, cancellationToken);
        }

        var created = false;
        if (user is null)
        {
            var defaultRoleId = await _authService.GetDefaultUserRoleIdAsync(cancellationToken);
            user = new User
            {
                Email = confirmedEmail ?? TechnicalEmailHelper.BuildTechnicalEmail(provider, providerUserId),
                FullName = BuildDisplayName(profile),
                RoleId = defaultRoleId,
                IsActive = true,
                IsApproved = false,
                CreatedAt = now,
                UpdatedAt = now,
                PhotoUrl = NormalizeOptional(profile.AvatarUrl),
                PhoneNumber = NormalizeOptional(profile.Phone),
                Telegram = provider == "telegram" ? FormatTelegramUsername(profile.Username) : null
            };
            user.PasswordHash = _passwordHasher.HashPassword(user, AuthService.CreateGeneratedPassword());
            _context.Users.Add(user);
            created = true;
        }
        else
        {
            var changed = false;

            if (!string.IsNullOrWhiteSpace(confirmedEmail)
                && (TechnicalEmailHelper.IsTechnicalEmail(user.Email)
                    || string.Equals(user.Email, confirmedEmail, StringComparison.OrdinalIgnoreCase)))
            {
                if (!string.Equals(user.Email, confirmedEmail, StringComparison.OrdinalIgnoreCase))
                {
                    user.Email = confirmedEmail;
                    changed = true;
                }
            }

            if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(profile.DisplayName))
            {
                user.FullName = profile.DisplayName.Trim();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(user.PhotoUrl) && !string.IsNullOrWhiteSpace(profile.AvatarUrl))
            {
                user.PhotoUrl = profile.AvatarUrl.Trim();
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(user.PhoneNumber) && !string.IsNullOrWhiteSpace(profile.Phone))
            {
                user.PhoneNumber = profile.Phone.Trim();
                changed = true;
            }

            if (provider == "telegram"
                && string.IsNullOrWhiteSpace(user.Telegram)
                && !string.IsNullOrWhiteSpace(profile.Username))
            {
                user.Telegram = FormatTelegramUsername(profile.Username);
                changed = true;
            }

            if (changed)
            {
                user.UpdatedAt = now;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);

        if (identity is null)
        {
            identity = new UserExternalIdentity
            {
                UserId = user.Id,
                Provider = provider,
                ProviderUserId = providerUserId,
                CreatedAt = now,
                UpdatedAt = now
            };
            _context.UserExternalIdentities.Add(identity);
        }

        identity.UserId = user.Id;
        identity.ProviderEmail = confirmedEmail ?? identity.ProviderEmail;
        identity.ProviderUsername = NormalizeOptional(profile.Username);
        identity.DisplayName = NormalizeOptional(profile.DisplayName);
        identity.AvatarUrl = NormalizeOptional(profile.AvatarUrl);
        identity.TelegramUserId = provider == "telegram" ? providerUserId : identity.TelegramUserId;
        identity.VerifiedAt ??= now;
        identity.LastUsedAt = now;
        identity.UpdatedAt = now;

        await _context.SaveChangesAsync(cancellationToken);

        if (created)
        {
            if (user.Role is null)
            {
                await _context.Entry(user).Reference(x => x.Role).LoadAsync(cancellationToken);
            }

            await _authService.NotifyPendingRegistrationAsync(user, cancellationToken);
        }

        return user;
    }

    private async Task<bool> IsExternalProviderReadyAsync(
        string enabledSettingKey,
        string enabledConfigPath,
        string clientIdSettingKey,
        string clientIdConfigPath,
        string clientSecretSettingKey,
        string clientSecretConfigPath,
        CancellationToken cancellationToken)
    {
        var enabled = await GetBooleanSettingOrConfigAsync(enabledSettingKey, enabledConfigPath, false, cancellationToken);
        if (!enabled)
        {
            return false;
        }

        var clientId = await GetSettingOrConfigAsync(clientIdSettingKey, clientIdConfigPath, cancellationToken);
        var clientSecret = await GetSettingOrConfigAsync(clientSecretSettingKey, clientSecretConfigPath, cancellationToken);
        return !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret);
    }

    private async Task<bool> IsTelegramReadyAsync(CancellationToken cancellationToken)
    {
        var enabled = await GetBooleanSettingOrConfigAsync(TelegramEnabledKey, "Auth:Telegram:Enabled", false, cancellationToken);
        if (!enabled)
        {
            return false;
        }

        var token = await GetSettingOrConfigAsync(TelegramBotTokenKey, "Auth:Telegram:BotToken", cancellationToken);
        var username = await GetSettingOrConfigAsync(TelegramBotUsernameKey, "Auth:Telegram:BotUsername", cancellationToken);
        return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(username);
    }

    private async Task<bool> IsTelegramWidgetReadyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var enabled = await GetBooleanSettingOrConfigAsync(TelegramWidgetEnabledKey, "Auth:Telegram:WidgetEnabled", false, cancellationToken);
        return enabled && IsTelegramWidgetHostSupported(request) && await IsTelegramReadyAsync(cancellationToken);
    }

    private static bool IsTelegramWidgetHostSupported(HttpRequest request)
    {
        var host = request.Headers.TryGetValue("X-Forwarded-Host", out var forwardedHosts)
            ? forwardedHosts.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
        host = string.IsNullOrWhiteSpace(host) ? request.Host.Host : host;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedHost = host.Trim().ToLowerInvariant();
        if (normalizedHost == "localhost"
            || normalizedHost.EndsWith(".localhost", StringComparison.Ordinal)
            || normalizedHost.EndsWith(".local", StringComparison.Ordinal)
            || normalizedHost.EndsWith(".test", StringComparison.Ordinal)
            || normalizedHost.EndsWith(".invalid", StringComparison.Ordinal))
        {
            return false;
        }

        return !IPAddress.TryParse(normalizedHost, out _);
    }

    private async Task<bool> HasTelegramWebhookSyncAsync(CancellationToken cancellationToken)
    {
        var syncedAt = await GetSettingOrConfigAsync(TelegramWebhookSyncedAtKey, null, cancellationToken);
        return ParseDateTime(syncedAt).HasValue;
    }

    private async Task<string> RefreshTelegramBotInfoAsync(string botToken, DateTime now, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync($"https://api.telegram.org/bot{botToken}/getMe", cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetProviderErrorDetail(payload, response.ReasonPhrase)
                ?? "Не удалось получить информацию о Telegram bot.");
        }

        using var document = JsonDocument.Parse(payload);
        var ok = document.RootElement.TryGetProperty("ok", out var okEl)
                 && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False
                 && okEl.GetBoolean();
        if (!ok
            || !document.RootElement.TryGetProperty("result", out var resultEl)
            || resultEl.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Telegram не вернул корректные данные о боте.");
        }

        var username = resultEl.TryGetProperty("username", out var usernameEl) ? usernameEl.GetString()?.Trim() : null;
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("Telegram bot username не найден.");
        }

        await UpsertSystemSettingAsync(TelegramBotUsernameKey, username, now, cancellationToken);
        await UpsertSystemSettingAsync(TelegramBotInfoSyncedAtKey, now.ToString("O"), now, cancellationToken);
        return username;
    }

    private async Task<ExternalProviderConfiguration?> ResolveExternalProviderConfigurationAsync(string provider, CancellationToken cancellationToken)
    {
        return provider switch
        {
            "google" => await ResolveExternalProviderConfigurationAsync(
                provider: "google",
                displayName: "Google",
                enabledSettingKey: GoogleEnabledKey,
                enabledConfigPath: "Auth:Google:Enabled",
                clientIdSettingKey: GoogleClientIdKey,
                clientIdConfigPath: "Auth:Google:ClientId",
                clientSecretSettingKey: GoogleClientSecretKey,
                clientSecretConfigPath: "Auth:Google:ClientSecret",
                authorizationEndpoint: "https://accounts.google.com/o/oauth2/v2/auth",
                tokenEndpoint: "https://oauth2.googleapis.com/token",
                userInfoEndpoint: "https://openidconnect.googleapis.com/v1/userinfo",
                defaultScope: "openid email profile",
                cancellationToken),
            "vk" => await ResolveExternalProviderConfigurationAsync(
                provider: "vk",
                displayName: "VK",
                enabledSettingKey: VkEnabledKey,
                enabledConfigPath: "Auth:Vk:Enabled",
                clientIdSettingKey: VkClientIdKey,
                clientIdConfigPath: "Auth:Vk:ClientId",
                clientSecretSettingKey: VkClientSecretKey,
                clientSecretConfigPath: "Auth:Vk:ClientSecret",
                authorizationEndpoint: "https://id.vk.ru/authorize",
                tokenEndpoint: "https://id.vk.ru/oauth2/auth",
                userInfoEndpoint: "https://id.vk.ru/oauth2/user_info",
                defaultScope: "email",
                cancellationToken),
            "yandex" => await ResolveExternalProviderConfigurationAsync(
                provider: "yandex",
                displayName: "Yandex",
                enabledSettingKey: YandexEnabledKey,
                enabledConfigPath: "Auth:Yandex:Enabled",
                clientIdSettingKey: YandexClientIdKey,
                clientIdConfigPath: "Auth:Yandex:ClientId",
                clientSecretSettingKey: YandexClientSecretKey,
                clientSecretConfigPath: "Auth:Yandex:ClientSecret",
                authorizationEndpoint: "https://oauth.yandex.com/authorize",
                tokenEndpoint: "https://oauth.yandex.com/token",
                userInfoEndpoint: "https://login.yandex.ru/info?format=json",
                defaultScope: "login:info login:email login:avatar",
                cancellationToken),
            _ => null
        };
    }

    private async Task<ExternalProviderConfiguration?> ResolveExternalProviderConfigurationAsync(
        string provider,
        string displayName,
        string enabledSettingKey,
        string enabledConfigPath,
        string clientIdSettingKey,
        string clientIdConfigPath,
        string clientSecretSettingKey,
        string clientSecretConfigPath,
        string authorizationEndpoint,
        string tokenEndpoint,
        string userInfoEndpoint,
        string defaultScope,
        CancellationToken cancellationToken)
    {
        var enabled = await GetBooleanSettingOrConfigAsync(enabledSettingKey, enabledConfigPath, false, cancellationToken);
        if (!enabled)
        {
            return null;
        }

        var clientId = await GetSettingOrConfigAsync(clientIdSettingKey, clientIdConfigPath, cancellationToken);
        var clientSecret = await GetSettingOrConfigAsync(clientSecretSettingKey, clientSecretConfigPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        return new ExternalProviderConfiguration(
            Provider: provider,
            DisplayName: displayName,
            ClientId: clientId.Trim(),
            ClientSecret: clientSecret.Trim(),
            AuthorizationEndpoint: authorizationEndpoint,
            TokenEndpoint: tokenEndpoint,
            UserInfoEndpoint: userInfoEndpoint,
            Scope: defaultScope);
    }
}
