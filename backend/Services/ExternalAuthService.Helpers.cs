using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;

namespace PayPlanner.Api.Services;

public sealed partial class ExternalAuthService
{
    private static bool ValidateTelegramWidgetPayload(TelegramWidgetLoginRequest payload, string botToken)
    {
        if (string.IsNullOrWhiteSpace(payload.Id)
            || string.IsNullOrWhiteSpace(payload.AuthDate)
            || string.IsNullOrWhiteSpace(payload.Hash)
            || string.IsNullOrWhiteSpace(botToken))
        {
            return false;
        }

        if (!long.TryParse(payload.AuthDate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var authDateUnix))
        {
            return false;
        }

        var authDate = DateTimeOffset.FromUnixTimeSeconds(authDateUnix);
        if (authDate < DateTimeOffset.UtcNow.AddDays(-1) || authDate > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return false;
        }

        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = payload.AuthDate.Trim(),
            ["id"] = payload.Id.Trim()
        };

        if (!string.IsNullOrWhiteSpace(payload.FirstName))
        {
            fields["first_name"] = payload.FirstName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(payload.LastName))
        {
            fields["last_name"] = payload.LastName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(payload.Username))
        {
            fields["username"] = payload.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(payload.PhotoUrl))
        {
            fields["photo_url"] = payload.PhotoUrl.Trim();
        }

        var dataCheckString = string.Join('\n', fields.Select(x => $"{x.Key}={x.Value}"));
        var secretKey = SHA256.HashData(Encoding.UTF8.GetBytes(botToken.Trim()));
        using var hmac = new HMACSHA256(secretKey);
        var expectedHash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(dataCheckString))).ToLowerInvariant();
        var actualHash = payload.Hash.Trim().ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectedHash),
            Encoding.ASCII.GetBytes(actualHash));
    }

    private async Task<string?> GetSettingOrConfigAsync(string key, string? configPath, CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeRequired(key, "System setting key is invalid.");
        var setting = await _context.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == normalizedKey, cancellationToken);

        if (setting is not null)
        {
            var value = setting.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            return null;
        }

        var configValue = _configuration[configPath];
        return string.IsNullOrWhiteSpace(configValue) ? null : configValue.Trim();
    }

    /// <summary>
    /// Загружает только запрошенные ключи настроек одной проекцией без tracking.
    /// Снимок используется в пределах одного HTTP-запроса и не становится глобальным кэшем,
    /// поэтому изменения администратора видны сразу на следующем обращении.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadSettingsSnapshotAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _context.SystemSettings
            .AsNoTracking()
            .Where(setting => keys.Contains(setting.Key))
            .Select(setting => new { setting.Key, setting.Value })
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal, cancellationToken);
    }

    private string? GetSettingOrConfig(
        IReadOnlyDictionary<string, string> settings,
        string key,
        string? configPath)
    {
        if (settings.TryGetValue(key, out var settingValue) && !string.IsNullOrWhiteSpace(settingValue))
        {
            return settingValue.Trim();
        }

        var configValue = string.IsNullOrWhiteSpace(configPath) ? null : _configuration[configPath];
        return string.IsNullOrWhiteSpace(configValue) ? null : configValue.Trim();
    }

    private bool GetBooleanSettingOrConfig(
        IReadOnlyDictionary<string, string> settings,
        string key,
        string? configPath,
        bool fallback)
    {
        var raw = GetSettingOrConfig(settings, key, configPath);
        return raw?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private async Task<bool> GetBooleanSettingOrConfigAsync(string key, string? configPath, bool fallback, CancellationToken cancellationToken)
    {
        var raw = await GetSettingOrConfigAsync(key, configPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private async Task UpsertSystemSettingAsync(string key, string value, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeRequired(key, "System setting key is invalid.");
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(x => x.Key == normalizedKey, cancellationToken);
        if (setting is null)
        {
            _context.SystemSettings.Add(new SystemSetting
            {
                Key = normalizedKey,
                Value = value,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = nowUtc;
    }

    private static string BuildDisplayName(ExternalIdentityProfile profile)
    {
        var displayName = NormalizeOptional(profile.DisplayName);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        var username = NormalizeOptional(profile.Username);
        if (!string.IsNullOrWhiteSpace(username))
        {
            return username;
        }

        var email = TechnicalEmailHelper.NormalizeRealEmail(profile.Email);
        if (!string.IsNullOrWhiteSpace(email))
        {
            return email;
        }

        return $"{TechnicalEmailHelper.NormalizeProvider(profile.Provider)} user";
    }

    private static string? FormatTelegramUsername(string? username)
    {
        var normalized = NormalizeOptional(username);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized.StartsWith("@", StringComparison.Ordinal) ? normalized : "@" + normalized;
    }

    private static string NormalizeReturnUrl(string? returnUrl)
    {
        var normalized = NormalizeOptional(returnUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "/";
        }

        if (normalized.Contains("://", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return "/";
        }

        if (!normalized.StartsWith("/", StringComparison.Ordinal))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private string ResolveFrontendReturnUrl(HttpRequest request, string? returnUrl)
    {
        var normalizedPath = NormalizeReturnUrl(returnUrl);
        var configuredFrontendUrl = NormalizeAbsoluteHttpUrl(
            _configuration["App:FrontendUrl"]
            ?? _configuration["Frontend:BaseUrl"]
            ?? _configuration["Frontend:PublicUrl"]);

        if (!string.IsNullOrWhiteSpace(configuredFrontendUrl))
        {
            return CombineAbsoluteUrl(configuredFrontendUrl, normalizedPath);
        }

        var developmentOrigin = ResolveDevelopmentFrontendOrigin(request);
        return string.IsNullOrWhiteSpace(developmentOrigin)
            ? normalizedPath
            : CombineAbsoluteUrl(developmentOrigin, normalizedPath);
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string GenerateToken(int bytesLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(bytesLength);
        return WebEncoders.Base64UrlEncode(bytes);
    }

    private static string NormalizeRequired(string? value, string errorMessage)
    {
        var normalized = NormalizeOptional(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new InvalidOperationException(errorMessage)
            : normalized;
    }

    /// <summary>
    /// Преобразует внутреннее состояние запроса в безопасное сообщение для анонимного клиента.
    /// Текст провайдера и исключения намеренно не возвращаются: они могут содержать технические
    /// адреса, параметры ответа или другие сведения, предназначенные только для журнала сервера.
    /// </summary>
    private static string? BuildPublicAuthErrorDetail(string status)
        => status switch
        {
            "expired" => "Срок действия запроса авторизации истёк. Начните вход заново.",
            "failed" => "Не удалось завершить авторизацию. Повторите попытку.",
            _ => null,
        };

    private static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal | DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private static string BuildAuthRedirectPath(string? returnUrl, string state, string provider)
    {
        var redirectTarget = NormalizeAbsoluteHttpReturnUrl(returnUrl) ?? NormalizeReturnUrl(returnUrl);

        return QueryHelpers.AddQueryString(
            redirectTarget,
            new Dictionary<string, string?>
            {
                ["externalAuthState"] = state,
                ["externalAuthProvider"] = provider
            });
    }

    private static string CombineAbsoluteUrl(string absoluteBaseUrl, string relativePath)
    {
        var baseUri = new Uri(EnsureTrailingSlash(absoluteBaseUrl), UriKind.Absolute);
        var combinedUri = new Uri(baseUri, relativePath.TrimStart('/'));
        return combinedUri.ToString();
    }

    private static string EnsureTrailingSlash(string value)
        => value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    private static string? ResolveDevelopmentFrontendOrigin(HttpRequest request)
    {
        var origin = NormalizeAbsoluteHttpUrl(request.Headers.Origin.ToString());
        if (IsLocalOrigin(origin))
        {
            return origin;
        }

        var referer = NormalizeOptional(request.Headers.Referer.ToString());
        if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            var refererOrigin = NormalizeAbsoluteHttpUrl(refererUri.GetLeftPart(UriPartial.Authority));
            if (IsLocalOrigin(refererOrigin))
            {
                return refererOrigin;
            }
        }

        return null;
    }

    private static string? NormalizeAbsoluteHttpUrl(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (string.IsNullOrWhiteSpace(normalized)
            || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string? NormalizeAbsoluteHttpReturnUrl(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (string.IsNullOrWhiteSpace(normalized)
            || !Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return uri.ToString();
    }

    private static bool IsLocalOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildExternalAuthPopupHtml(bool success, string? message, string? redirectPath = null)
    {
        var title = success ? "Авторизация завершена" : "Ошибка авторизации";
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeMessage = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(message) ? "Вернитесь на сайт." : message.Trim());
        var redirectPathJson = JsonSerializer.Serialize(redirectPath ?? string.Empty);

        return $$"""
<!doctype html>
<html lang="ru">
<head>
  <meta charset="utf-8">
  <title>{{safeTitle}}</title>
  <style>
    body { font-family: Arial, sans-serif; padding: 24px; color: #111827; background: #f8fafc; }
    .card { max-width: 420px; margin: 10vh auto; background: white; border-radius: 16px; padding: 24px; box-shadow: 0 12px 32px rgba(15,23,42,.08); }
    h1 { font-size: 20px; margin: 0 0 12px; }
    p { margin: 0; line-height: 1.5; }
  </style>
</head>
<body>
  <div class="card">
    <h1>{{safeTitle}}</h1>
    <p>{{safeMessage}}</p>
  </div>
  <script>
    var redirectPath = {{redirectPathJson}};

    var deriveRedirectPath = function () {
      if (redirectPath) {
        return redirectPath;
      }

      try {
        var currentUrl = new URL(window.location.href);
        var state = currentUrl.searchParams.get("state");
        var providerMatch = currentUrl.pathname.match(/\/api\/auth\/external\/callback\/([^/?#]+)/i);
        var provider = providerMatch ? providerMatch[1] : "";
        if (!state || !provider) {
          return "";
        }

        var nextUrl = new URL("/", currentUrl.origin);
        nextUrl.searchParams.set("externalAuthState", state);
        nextUrl.searchParams.set("externalAuthProvider", provider.toLowerCase());
        return nextUrl.pathname + nextUrl.search;
      } catch (error) {
        return redirectPath;
      }
    };

    setTimeout(function () {
      if (window.opener && !window.opener.closed) {
        window.close();
        return;
      }

      var nextPath = deriveRedirectPath();
      if (nextPath) {
        window.location.replace(nextPath);
      }
    }, 1200);
  </script>
</body>
</html>
""";
    }

    private static string? ReadString(JsonElement root, params string[] path)
    {
        if (!TryReadPath(root, out var value, path))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => NormalizeOptional(value.GetString()),
            JsonValueKind.Number => value.GetRawText().Trim(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null
        };
    }

    private static long ReadInt64(JsonElement root, params string[] path)
    {
        if (!TryReadPath(root, out var value, path))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static string? ExtractTelegramState(string? commandText)
    {
        var normalized = NormalizeOptional(commandText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var parts = normalized.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var payload = parts[1].Trim();
        const string prefix = "login_";
        return payload.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? NormalizeOptional(payload[prefix.Length..])
            : null;
    }

    private static string? GetProviderErrorDetail(string? responseBody, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.TryGetProperty("error_description", out var descriptionElement))
                {
                    return NormalizeOptional(descriptionElement.GetString());
                }

                if (document.RootElement.TryGetProperty("error", out var errorElement))
                {
                    if (errorElement.ValueKind == JsonValueKind.String)
                    {
                        return NormalizeOptional(errorElement.GetString());
                    }

                    if (errorElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var nestedName in new[] { "error_description", "error_msg", "message", "description" })
                        {
                            if (errorElement.TryGetProperty(nestedName, out var nestedValue))
                            {
                                return NormalizeOptional(nestedValue.GetString());
                            }
                        }
                    }
                }

                if (document.RootElement.TryGetProperty("message", out var messageElement))
                {
                    return NormalizeOptional(messageElement.GetString());
                }

                if (document.RootElement.TryGetProperty("description", out var fallbackDescription))
                {
                    return NormalizeOptional(fallbackDescription.GetString());
                }
            }
            catch (JsonException)
            {
            }
        }

        return NormalizeOptional(fallback);
    }

    private static string? ReadExternalPhone(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            var phone = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object => TryReadNestedPhone(value),
                _ => null
            };

            var normalized = NormalizeOptional(phone);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? ReadExternalString(JsonElement root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            var result = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };

            var normalized = NormalizeOptional(result);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? TryReadNestedPhone(JsonElement root)
    {
        foreach (var propertyName in new[] { "number", "formatted", "value", "phone_number" })
        {
            if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var normalized = NormalizeOptional(value.GetString());
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static bool TryReadPath(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }
}
