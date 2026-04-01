using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

public sealed partial class ExternalAuthService
{
    private string BuildExternalAuthorizationUrl(
        ExternalProviderConfiguration configuration,
        string state,
        string redirectUri)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = configuration.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["state"] = state
        };

        if (!string.IsNullOrWhiteSpace(configuration.Scope))
        {
            query["scope"] = configuration.Scope;
        }

        if (string.Equals(configuration.Provider, "google", StringComparison.Ordinal))
        {
            query["access_type"] = "online";
            query["include_granted_scopes"] = "true";
            query["prompt"] = "select_account";
        }
        else if (string.Equals(configuration.Provider, "vk", StringComparison.Ordinal))
        {
            var codeVerifier = BuildVkCodeVerifier(configuration, state);
            query["app_id"] = configuration.ClientId;
            query["sdk_type"] = "vkid";
            query["code_challenge"] = BuildVkCodeChallenge(codeVerifier);
            query["code_challenge_method"] = "s256";
        }

        return QueryHelpers.AddQueryString(configuration.AuthorizationEndpoint, query!);
    }

    private static string BuildAbsoluteUrl(HttpRequest request, string relativePath)
    {
        var normalizedPath = relativePath.StartsWith("/", StringComparison.Ordinal)
            ? relativePath
            : "/" + relativePath;

        var scheme = GetProxyHeaderValue(request, "X-Forwarded-Proto") ?? request.Scheme;
        var host = GetProxyHeaderValue(request, "X-Forwarded-Host") ?? request.Host.Value;
        var pathBase = ResolveExternalPathBase(request);
        return $"{scheme}://{host}{pathBase}{normalizedPath}";
    }

    private static string ResolveExternalPathBase(HttpRequest request)
    {
        var requestPathBase = NormalizePathBase(request.PathBase.Value);
        var forwardedPrefix = NormalizePathBase(GetProxyHeaderValue(request, "X-Forwarded-Prefix"));

        if (string.IsNullOrWhiteSpace(forwardedPrefix))
        {
            return requestPathBase ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(requestPathBase))
        {
            return forwardedPrefix;
        }

        return string.Equals(requestPathBase, forwardedPrefix, StringComparison.Ordinal)
            ? requestPathBase
            : $"{requestPathBase}{forwardedPrefix}";
    }

    private static string? GetProxyHeaderValue(HttpRequest request, string headerName)
    {
        if (!request.Headers.TryGetValue(headerName, out StringValues values))
        {
            return null;
        }

        var rawValue = values.ToString();
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var firstValue = rawValue
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstValue) ? null : firstValue;
    }

    private static string? NormalizePathBase(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            return null;
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        return normalized.TrimEnd('/');
    }

    private async Task<ExternalAccessTokenResult> ExchangeExternalAccessTokenAsync(
        ExternalProviderConfiguration configuration,
        string code,
        string redirectUri,
        string state,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();

        using var request = string.Equals(configuration.Provider, "vk", StringComparison.Ordinal)
            ? BuildVkTokenExchangeRequest(configuration, code, redirectUri, state, deviceId)
            : new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["client_id"] = configuration.ClientId,
                    ["client_secret"] = configuration.ClientSecret,
                    ["redirect_uri"] = redirectUri
                })
            };

        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetProviderErrorDetail(content, response.ReasonPhrase)
                ?? $"Не удалось получить токен {configuration.DisplayName}.");
        }

        using var document = JsonDocument.Parse(content);
        var accessToken = ReadString(document.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException($"{configuration.DisplayName} не вернул access token.");
        }

        return new ExternalAccessTokenResult(
            AccessToken: accessToken,
            ProviderEmail: ReadString(document.RootElement, "email"),
            ProviderUserId: ReadString(document.RootElement, "user_id"));
    }

    private async Task<ExternalIdentityProfile> LoadGoogleProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetProviderErrorDetail(content, response.ReasonPhrase)
                ?? "Google не вернул данные профиля.");
        }

        using var document = JsonDocument.Parse(content);
        var providerUserId = ReadString(document.RootElement, "sub");
        if (string.IsNullOrWhiteSpace(providerUserId))
        {
            throw new InvalidOperationException("Google не вернул идентификатор пользователя.");
        }

        var email = ReadString(document.RootElement, "email");
        var emailVerified = document.RootElement.TryGetProperty("email_verified", out var emailVerifiedElement)
            && emailVerifiedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && emailVerifiedElement.GetBoolean();

        return new ExternalIdentityProfile(
            Provider: "google",
            ProviderUserId: providerUserId,
            Email: email,
            EmailVerified: emailVerified,
            Username: null,
            DisplayName: ReadString(document.RootElement, "name"),
            AvatarUrl: ReadString(document.RootElement, "picture"),
            Phone: ReadExternalPhone(document.RootElement, "phone_number", "phone"),
            PhoneVerified: false);
    }

    private async Task<ExternalIdentityProfile> LoadVkProfileAsync(
        ExternalProviderConfiguration configuration,
        ExternalAccessTokenResult tokenResult,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            QueryHelpers.AddQueryString(
                configuration.UserInfoEndpoint,
                new Dictionary<string, string?>
                {
                    ["client_id"] = configuration.ClientId
                }))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["access_token"] = tokenResult.AccessToken
            })
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetProviderErrorDetail(content, response.ReasonPhrase)
                ?? "VK не вернул данные профиля.");
        }

        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("user", out var userElement)
            || userElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("VK не вернул данные пользователя.");
        }

        var providerUserId = ReadExternalString(userElement, "user_id", "id");
        if (string.IsNullOrWhiteSpace(providerUserId))
        {
            providerUserId = tokenResult.ProviderUserId;
        }

        if (string.IsNullOrWhiteSpace(providerUserId))
        {
            throw new InvalidOperationException("VK не вернул идентификатор пользователя.");
        }

        var firstName = ReadExternalString(userElement, "first_name");
        var lastName = ReadExternalString(userElement, "last_name");
        var displayName = string.Join(" ", new[] { firstName, lastName }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var username = ReadExternalString(userElement, "screen_name", "username");
        var avatarUrl = ReadExternalString(userElement, "avatar", "photo_200");
        var email = ReadExternalString(userElement, "email");
        if (string.IsNullOrWhiteSpace(email))
        {
            email = tokenResult.ProviderEmail;
        }

        return new ExternalIdentityProfile(
            Provider: "vk",
            ProviderUserId: providerUserId,
            Email: email,
            EmailVerified: TechnicalEmailHelper.IsValidRealEmail(email),
            Username: username,
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? username : displayName,
            AvatarUrl: avatarUrl,
            Phone: ReadExternalPhone(userElement, "phone", "phone_number", "mobile_phone", "home_phone"),
            PhoneVerified: false);
    }

    private async Task<ExternalIdentityProfile> LoadYandexProfileAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://login.yandex.ru/info?format=json");
        request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");

        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(GetProviderErrorDetail(content, response.ReasonPhrase)
                ?? "Yandex не вернул данные профиля.");
        }

        using var document = JsonDocument.Parse(content);
        var providerUserId = ReadString(document.RootElement, "id");
        if (string.IsNullOrWhiteSpace(providerUserId))
        {
            throw new InvalidOperationException("Yandex не вернул идентификатор пользователя.");
        }

        var email = ReadString(document.RootElement, "default_email");
        var username = ReadString(document.RootElement, "login");
        var displayName = ReadString(document.RootElement, "real_name") ?? ReadString(document.RootElement, "display_name");
        var avatarUrl = BuildYandexAvatarUrl(document.RootElement);

        return new ExternalIdentityProfile(
            Provider: "yandex",
            ProviderUserId: providerUserId,
            Email: email,
            EmailVerified: TechnicalEmailHelper.IsValidRealEmail(email),
            Username: username,
            DisplayName: displayName,
            AvatarUrl: avatarUrl,
            Phone: ReadExternalPhone(document.RootElement, "default_phone", "phone", "phone_number"),
            PhoneVerified: false);
    }

    private async Task SendTelegramMessageAsync(long chatId, string message, CancellationToken cancellationToken)
    {
        var botToken = await GetSettingOrConfigAsync(TelegramBotTokenKey, "Auth:Telegram:BotToken", cancellationToken);
        if (chatId == 0 || string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.PostAsync(
                $"https://api.telegram.org/bot{botToken}/sendMessage",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = chatId.ToString(CultureInfo.InvariantCulture),
                    ["text"] = message.Trim()
                }),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Telegram sendMessage failed for chat {ChatId}: {Message}",
                    chatId,
                    GetProviderErrorDetail(payload, response.ReasonPhrase));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram sendMessage failed for chat {ChatId}", chatId);
        }
    }

    private static HttpRequestMessage BuildVkTokenExchangeRequest(
        ExternalProviderConfiguration configuration,
        string code,
        string redirectUri,
        string state,
        string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new InvalidOperationException("VK не вернул device_id для обмена кода авторизации.");
        }

        var codeVerifier = BuildVkCodeVerifier(configuration, state);
        var requestUri = QueryHelpers.AddQueryString(
            configuration.TokenEndpoint,
            new Dictionary<string, string?>
            {
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = redirectUri,
                ["client_id"] = configuration.ClientId,
                ["code_verifier"] = codeVerifier,
                ["state"] = state,
                ["device_id"] = deviceId
            });

        return new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code
            })
        };
    }

    private static string BuildVkCodeVerifier(ExternalProviderConfiguration configuration, string state)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(configuration.ClientSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes($"vkid:{configuration.ClientId}:{state}"));
        return WebEncoders.Base64UrlEncode(hash);
    }

    private static string BuildVkCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return WebEncoders.Base64UrlEncode(hash);
    }

    private static string? BuildYandexAvatarUrl(JsonElement root)
    {
        var isAvatarEmpty = root.TryGetProperty("is_avatar_empty", out var avatarEmptyElement)
            && avatarEmptyElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            && avatarEmptyElement.GetBoolean();

        if (isAvatarEmpty)
        {
            return null;
        }

        var avatarId = ReadString(root, "default_avatar_id");
        return string.IsNullOrWhiteSpace(avatarId)
            ? null
            : $"https://avatars.yandex.net/get-yapic/{avatarId}/islands-200";
    }

    private sealed record ExternalProviderConfiguration(
        string Provider,
        string DisplayName,
        string ClientId,
        string ClientSecret,
        string AuthorizationEndpoint,
        string TokenEndpoint,
        string UserInfoEndpoint,
        string Scope);

    private sealed record ExternalAccessTokenResult(
        string AccessToken,
        string? ProviderEmail,
        string? ProviderUserId);
}
