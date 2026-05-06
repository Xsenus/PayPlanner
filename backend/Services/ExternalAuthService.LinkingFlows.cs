using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed partial class ExternalAuthService
{
    public async Task<ExternalAuthStartResponseDto> StartExternalFlowAsync(
        string provider,
        string? returnUrl,
        string? intent,
        int? currentUserId,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = TechnicalEmailHelper.NormalizeProvider(provider);
        if (normalizedProvider is not ("google" or "vk" or "yandex"))
        {
            throw new InvalidOperationException("Unsupported external auth provider");
        }

        var normalizedIntent = NormalizeExternalAuthIntent(intent);
        if (normalizedIntent == "link" && !currentUserId.HasValue)
        {
            throw new InvalidOperationException("Требуется авторизованный пользователь для привязки способа входа.");
        }

        var configuration = await ResolveExternalProviderConfigurationAsync(normalizedProvider, cancellationToken)
            ?? throw new InvalidOperationException($"Внешний провайдер '{normalizedProvider}' не настроен.");

        var authRequest = new ExternalAuthRequest
        {
            Provider = normalizedProvider,
            State = GenerateToken(12),
            ReturnUrl = ResolveFrontendReturnUrl(request, returnUrl),
            Intent = normalizedIntent,
            UserId = normalizedIntent == "link" ? currentUserId : null,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.ExternalAuthRequests.Add(authRequest);
        await _context.SaveChangesAsync(cancellationToken);

        var redirectUri = BuildAbsoluteUrl(request, $"/api/auth/external/callback/{normalizedProvider}");

        return new ExternalAuthStartResponseDto
        {
            Provider = normalizedProvider,
            State = authRequest.State,
            ReturnUrl = authRequest.ReturnUrl,
            Intent = normalizedIntent,
            AuthUrl = BuildExternalAuthorizationUrl(configuration, authRequest.State, redirectUri),
            ExpiresAt = authRequest.ExpiresAt,
            PollIntervalMs = DefaultPollIntervalMs
        };
    }

    public async Task<ExternalAuthStatusResponseDto> GetExternalStatusForClientAsync(
        string state,
        CancellationToken cancellationToken = default)
    {
        var normalizedState = NormalizeRequired(state, "Invalid state");
        var authRequest = await _context.ExternalAuthRequests
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.State == normalizedState, cancellationToken);

        if (authRequest is null)
        {
            throw new KeyNotFoundException("Authorization request not found");
        }

        if (authRequest.ExpiresAt <= DateTime.UtcNow
            && authRequest.Status is not ("completed" or "consumed" or "failed" or "expired"))
        {
            authRequest.Status = "expired";
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (!string.Equals(authRequest.Status, "completed", StringComparison.Ordinal))
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = authRequest.Status,
                Completed = false,
                Provider = authRequest.Provider,
                Detail = authRequest.Error,
                ReturnUrl = authRequest.ReturnUrl
            };
        }

        if (!authRequest.UserId.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "pending",
                Completed = false,
                Provider = authRequest.Provider,
                ReturnUrl = authRequest.ReturnUrl
            };
        }

        if (authRequest.ConsumedAt.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "consumed",
                Completed = false,
                Provider = authRequest.Provider,
                ReturnUrl = authRequest.ReturnUrl
            };
        }

        if (string.Equals(authRequest.Intent, "link", StringComparison.Ordinal))
        {
            authRequest.ConsumedAt = DateTime.UtcNow;
            authRequest.Status = "consumed";
            await _context.SaveChangesAsync(cancellationToken);

            return new ExternalAuthStatusResponseDto
            {
                Status = "completed",
                Completed = true,
                Linked = true,
                Provider = authRequest.Provider,
                ReturnUrl = authRequest.ReturnUrl
            };
        }

        return await GetExternalStatusAsync(state, cancellationToken);
    }

    public async Task<string> HandleExternalCallbackForClientAsync(
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

        var redirectPath = BuildAuthRedirectPath(ResolveFrontendReturnUrl(request, "/"), normalizedState, normalizedProvider);
        if (authRequest is null)
        {
            return BuildExternalAuthPopupHtml(false, "Запрос внешней авторизации не найден.", redirectPath);
        }

        redirectPath = BuildAuthRedirectPath(authRequest.ReturnUrl, normalizedState, normalizedProvider);

        if (authRequest.ExpiresAt <= DateTime.UtcNow)
        {
            authRequest.Status = "expired";
            authRequest.Error = "Срок действия запроса внешней авторизации истек.";
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

            if (string.Equals(authRequest.Intent, "link", StringComparison.Ordinal))
            {
                if (!authRequest.UserId.HasValue)
                {
                    throw new InvalidOperationException("Пользователь для привязки не найден.");
                }

                await AttachExternalIdentityAsync(authRequest.UserId.Value, profile, cancellationToken: cancellationToken);
            }
            else
            {
                var user = await ResolveOrCreateExternalUserAsync(profile, cancellationToken);
                authRequest.UserId = user.Id;
            }

            authRequest.Status = "completed";
            authRequest.Error = null;
            authRequest.CompletedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            var successMessage = string.Equals(authRequest.Intent, "link", StringComparison.Ordinal)
                ? $"Способ входа {configuration.DisplayName} успешно привязан. Вернитесь на сайт."
                : $"Вход через {configuration.DisplayName} завершен. Вернитесь на сайт.";

            return BuildExternalAuthPopupHtml(true, successMessage, redirectPath);
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

    public async Task<ExternalAuthStatusResponseDto> CompleteTelegramWidgetFlowAsync(
        TelegramWidgetLoginRequest payload,
        int? currentUserId,
        CancellationToken cancellationToken = default)
    {
        var telegramBot = await ResolveTelegramAuthBotAsync(cancellationToken);
        var botToken = telegramBot?.BotToken
                       ?? await GetSettingOrConfigAsync(TelegramBotTokenKey, "Auth:Telegram:BotToken", cancellationToken);
        if (string.IsNullOrWhiteSpace(botToken))
        {
            throw new InvalidOperationException("Telegram bot token is not configured.");
        }

        if (!ValidateTelegramWidgetPayload(payload, botToken))
        {
            throw new InvalidOperationException("Invalid telegram auth payload.");
        }

        var intent = NormalizeExternalAuthIntent(payload.Intent);
        if (intent == "link" && !currentUserId.HasValue)
        {
            throw new InvalidOperationException("Требуется авторизованный пользователь для привязки Telegram.");
        }

        var fullName = string.Join(" ", new[] { payload.FirstName, payload.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var profile = new ExternalIdentityProfile(
            Provider: "telegram",
            ProviderUserId: payload.Id,
            Email: null,
            EmailVerified: false,
            Username: payload.Username,
            DisplayName: fullName,
            AvatarUrl: payload.PhotoUrl);

        if (intent == "link")
        {
            await AttachExternalIdentityAsync(currentUserId!.Value, profile, cancellationToken: cancellationToken);
            return new ExternalAuthStatusResponseDto
            {
                Status = "completed",
                Completed = true,
                Linked = true,
                Provider = "telegram",
                ReturnUrl = "/?ppTab=profile"
            };
        }

        return await CompleteTelegramWidgetAsync(payload, cancellationToken);
    }

    public async Task<ExternalAuthStartResponseDto> StartTelegramBotFlowAsync(
        string? returnUrl,
        string? intent,
        int? currentUserId,
        HttpRequest request,
        CancellationToken cancellationToken = default)
    {
        var telegramBot = await ResolveTelegramAuthBotAsync(cancellationToken);
        if (!IsTelegramReady(telegramBot))
        {
            throw new InvalidOperationException("Telegram bot is not configured for login.");
        }

        var normalizedIntent = NormalizeExternalAuthIntent(intent);
        if (normalizedIntent == "link" && !currentUserId.HasValue)
        {
            throw new InvalidOperationException("Требуется авторизованный пользователь для привязки Telegram.");
        }

        var username = telegramBot!.BotUsername;

        var authRequest = new TelegramAuthRequest
        {
            State = GenerateToken(12),
            UserId = normalizedIntent == "link" ? currentUserId : null,
            Intent = normalizedIntent,
            Status = "pending",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        _context.TelegramAuthRequests.Add(authRequest);
        await _context.SaveChangesAsync(cancellationToken);

        return new ExternalAuthStartResponseDto
        {
            Provider = "telegram",
            State = authRequest.State,
            ReturnUrl = ResolveFrontendReturnUrl(request, returnUrl),
            Intent = normalizedIntent,
            AuthUrl = $"https://t.me/{username}?start=login_{authRequest.State}",
            ExpiresAt = authRequest.ExpiresAt,
            PollIntervalMs = DefaultPollIntervalMs
        };
    }

    public async Task<ExternalAuthStatusResponseDto> GetTelegramBotStatusForClientAsync(
        string state,
        CancellationToken cancellationToken = default)
    {
        var normalizedState = NormalizeRequired(state, "Invalid state");
        var authRequest = await _context.TelegramAuthRequests
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.State == normalizedState, cancellationToken);

        if (authRequest is null)
        {
            throw new KeyNotFoundException("Authorization request not found");
        }

        if (authRequest.ExpiresAt <= DateTime.UtcNow
            && authRequest.Status is not ("completed" or "consumed" or "failed" or "expired"))
        {
            authRequest.Status = "expired";
            await _context.SaveChangesAsync(cancellationToken);
        }

        if (!string.Equals(authRequest.Status, "completed", StringComparison.Ordinal))
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = authRequest.Status,
                Completed = false,
                Provider = "telegram",
                Detail = authRequest.Error
            };
        }

        if (!authRequest.UserId.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "pending",
                Completed = false,
                Provider = "telegram"
            };
        }

        if (authRequest.ConsumedAt.HasValue)
        {
            return new ExternalAuthStatusResponseDto
            {
                Status = "consumed",
                Completed = false,
                Provider = "telegram"
            };
        }

        if (string.Equals(authRequest.Intent, "link", StringComparison.Ordinal))
        {
            authRequest.ConsumedAt = DateTime.UtcNow;
            authRequest.Status = "consumed";
            await _context.SaveChangesAsync(cancellationToken);

            return new ExternalAuthStatusResponseDto
            {
                Status = "completed",
                Completed = true,
                Linked = true,
                Provider = "telegram",
                ReturnUrl = "/?ppTab=profile"
            };
        }

        return await GetTelegramBotStatusAsync(state, cancellationToken);
    }

    public async Task<bool> TryHandleTelegramBotAuthUpdateAsync(
        JsonElement update,
        Func<long, string, CancellationToken, Task> sendMessageAsync,
        CancellationToken cancellationToken = default)
    {
        if (!update.TryGetProperty("message", out var messageElement)
            || messageElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var text = ReadString(messageElement, "text");
        if (string.IsNullOrWhiteSpace(text) || !text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var state = ExtractTelegramState(text);
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        var chatId = ReadInt64(messageElement, "chat", "id");
        var telegramUserId = ReadInt64(messageElement, "from", "id");
        if (chatId == 0 || telegramUserId == 0)
        {
            return true;
        }

        var authRequest = await _context.TelegramAuthRequests
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.State == state, cancellationToken);
        if (authRequest is null)
        {
            await sendMessageAsync(chatId, "Ссылка авторизации не найдена. Запросите новую на сайте.", cancellationToken);
            return true;
        }

        if (authRequest.ExpiresAt <= DateTime.UtcNow)
        {
            authRequest.Status = "expired";
            authRequest.Error = "Срок действия ссылки истек.";
            await _context.SaveChangesAsync(cancellationToken);
            await sendMessageAsync(chatId, "Ссылка авторизации устарела. Вернитесь на сайт и начните вход заново.", cancellationToken);
            return true;
        }

        if (authRequest.Status is "completed" or "consumed")
        {
            await sendMessageAsync(chatId, "Эта ссылка уже использована. Если нужно, начните вход заново на сайте.", cancellationToken);
            return true;
        }

        var fullName = string.Join(" ", new[]
        {
            ReadString(messageElement, "from", "first_name"),
            ReadString(messageElement, "from", "last_name")
        }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        var profile = new ExternalIdentityProfile(
            Provider: "telegram",
            ProviderUserId: telegramUserId.ToString(),
            Email: null,
            EmailVerified: false,
            Username: ReadString(messageElement, "from", "username"),
            DisplayName: fullName,
            AvatarUrl: null);

        User? user = null;
        if (string.Equals(authRequest.Intent, "link", StringComparison.Ordinal))
        {
            if (!authRequest.UserId.HasValue)
            {
                authRequest.Status = "failed";
                authRequest.Error = "Пользователь для привязки не найден.";
                authRequest.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
                await sendMessageAsync(chatId, "Не удалось завершить привязку. Повторите попытку из профиля.", cancellationToken);
                return true;
            }

            await AttachExternalIdentityAsync(authRequest.UserId.Value, profile, chatId, cancellationToken);
        }
        else
        {
            user = await ResolveOrCreateExternalUserAsync(profile, cancellationToken);
            authRequest.UserId = user.Id;
        }

        authRequest.ChatId = chatId;
        authRequest.TelegramUserId = telegramUserId.ToString();
        authRequest.Status = "completed";
        authRequest.Error = null;
        authRequest.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var answer = string.Equals(authRequest.Intent, "link", StringComparison.Ordinal)
            ? "Telegram успешно привязан к вашему профилю. Вернитесь на сайт."
            : !user!.IsActive
                ? "Аккаунт отключен. Обратитесь к администратору."
                : !user.IsApproved
                    ? "Аккаунт создан и ожидает подтверждения администратором. Вернитесь на сайт."
                    : "Вход подтвержден. Вернитесь на сайт, авторизация завершится автоматически.";
        await sendMessageAsync(chatId, answer, cancellationToken);
        return true;
    }

    public async Task HandleTelegramWebhookFlowAsync(
        JsonDocument updateDocument,
        HttpRequest request,
        CancellationToken cancellationToken = default)
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
            authRequest.Error = "Срок действия ссылки истек.";
            await _context.SaveChangesAsync(cancellationToken);
            await SendTelegramMessageAsync(chatId, "Ссылка авторизации устарела. Вернитесь на сайт и начните вход заново.", cancellationToken);
            return;
        }

        if (authRequest.Status is "completed" or "consumed")
        {
            await SendTelegramMessageAsync(chatId, "Эта ссылка уже использована. Если нужно, начните вход заново на сайте.", cancellationToken);
            return;
        }

        var fullName = string.Join(" ", new[]
        {
            ReadString(messageElement, "from", "first_name"),
            ReadString(messageElement, "from", "last_name")
        }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();

        var profile = new ExternalIdentityProfile(
            Provider: "telegram",
            ProviderUserId: telegramUserId.ToString(),
            Email: null,
            EmailVerified: false,
            Username: ReadString(messageElement, "from", "username"),
            DisplayName: fullName,
            AvatarUrl: null);

        User? user = null;
        if (string.Equals(authRequest.Intent, "link", StringComparison.Ordinal))
        {
            if (!authRequest.UserId.HasValue)
            {
                authRequest.Status = "failed";
                authRequest.Error = "Пользователь для привязки не найден.";
                authRequest.CompletedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
                await SendTelegramMessageAsync(chatId, "Не удалось завершить привязку. Повторите попытку из профиля.", cancellationToken);
                return;
            }

            await AttachExternalIdentityAsync(authRequest.UserId.Value, profile, chatId, cancellationToken);
        }
        else
        {
            user = await ResolveOrCreateExternalUserAsync(profile, cancellationToken);
            authRequest.UserId = user.Id;
        }

        authRequest.ChatId = chatId;
        authRequest.TelegramUserId = telegramUserId.ToString();
        authRequest.Status = "completed";
        authRequest.Error = null;
        authRequest.CompletedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        var answer = string.Equals(authRequest.Intent, "link", StringComparison.Ordinal)
            ? "Telegram успешно привязан к вашему профилю. Вернитесь на сайт."
            : !user!.IsActive
                ? "Аккаунт отключен. Обратитесь к администратору."
                : !user.IsApproved
                    ? "Аккаунт создан и ожидает подтверждения администратора. Вернитесь на сайт."
                    : "Вход подтвержден. Вернитесь на сайт, авторизация завершится автоматически.";
        await SendTelegramMessageAsync(chatId, answer, cancellationToken);
    }
}
