using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public static class MaxMessengerScenarios
{
    public const string Notifications = "notifications";
    public const string Documents = "documents";
    public const string Correspondence = "correspondence";
    public const string Confirmations = "confirmations";

    public static readonly IReadOnlyList<MaxScenarioDto> All =
    [
        new()
        {
            Key = Notifications,
            Label = "Уведомления",
            Description = "Короткие уведомления сотрудников без конфиденциальных данных.",
            RequiresVerifiedLink = true,
        },
        new()
        {
            Key = Documents,
            Label = "Документы",
            Description = "Одноразовые ссылки на разрешённые документы вместо публичных файлов.",
            RequiresVerifiedLink = true,
        },
        new()
        {
            Key = Correspondence,
            Label = "Переписка",
            Description = "Сообщения, связанные с делом, после проверенной привязки пользователя.",
            RequiresVerifiedLink = true,
        },
        new()
        {
            Key = Confirmations,
            Label = "Подтверждения",
            Description = "Кнопки перехода к отдельному защищённому подтверждению; реакция в MAX сама по себе не является подписью.",
            RequiresVerifiedLink = true,
        },
    ];
}

public sealed record MaxApiResult(
    bool Success,
    string? ExternalMessageId = null,
    string? ErrorCode = null,
    bool IsTransient = false);

public sealed record MaxUploadSlotResult(
    bool Success,
    string? UploadUrl = null,
    string? Token = null,
    string? ErrorCode = null);

public interface IMaxApiClient
{
    Task<MaxApiResult> CheckHealthAsync(string botToken, CancellationToken cancellationToken = default);
    Task<MaxApiResult> SendTextAsync(
        long userId,
        string text,
        string botToken,
        string? actionText = null,
        Uri? actionUrl = null,
        CancellationToken cancellationToken = default);
    Task<MaxUploadSlotResult> CreateUploadSlotAsync(
        string mediaType,
        string botToken,
        CancellationToken cancellationToken = default);
}

public sealed class MaxApiClient(HttpClient httpClient) : IMaxApiClient
{
    public const int MaxTextLength = 4000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MaxApiResult> CheckHealthAsync(
        string botToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new MaxApiResult(false, ErrorCode: "credentials-missing");
        }

        using var request = CreateRequest(HttpMethod.Get, "me", botToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode
            ? new MaxApiResult(true)
            : ClassifyFailure(response.StatusCode);
    }

    public async Task<MaxApiResult> SendTextAsync(
        long userId,
        string text,
        string botToken,
        string? actionText = null,
        Uri? actionUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        var normalizedText = text?.Trim() ?? string.Empty;
        if (normalizedText.Length is 0 or > MaxTextLength)
        {
            throw new ArgumentOutOfRangeException(nameof(text), $"MAX message text must contain 1-{MaxTextLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new MaxApiResult(false, ErrorCode: "credentials-missing");
        }

        object? attachments = null;
        if (!string.IsNullOrWhiteSpace(actionText) && actionUrl is not null)
        {
            if (actionUrl.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException("MAX action URL must use HTTPS.", nameof(actionUrl));
            }

            attachments = new[]
            {
                new
                {
                    type = "inline_keyboard",
                    payload = new
                    {
                        buttons = new[]
                        {
                            new[]
                            {
                                new
                                {
                                    type = "link",
                                    text = actionText.Trim(),
                                    url = actionUrl.AbsoluteUri,
                                },
                            },
                        },
                    },
                },
            };
        }

        using var request = CreateRequest(
            HttpMethod.Post,
            $"messages?user_id={userId}",
            botToken,
            JsonContent.Create(new
            {
                text = normalizedText,
                attachments,
                notify = true,
            }, options: JsonOptions));
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ClassifyFailure(response.StatusCode);
        }

        try
        {
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var messageId = json.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("body", out var body)
                && body.ValueKind == JsonValueKind.Object
                && body.TryGetProperty("mid", out var id)
                    ? id.ToString()
                    : null;
            return new MaxApiResult(true, messageId);
        }
        catch (JsonException)
        {
            return new MaxApiResult(false, ErrorCode: "invalid-response", IsTransient: true);
        }
    }

    public async Task<MaxUploadSlotResult> CreateUploadSlotAsync(
        string mediaType,
        string botToken,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = mediaType?.Trim().ToLowerInvariant();
        if (normalizedType is not ("file" or "image" or "video" or "audio"))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaType));
        }

        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new MaxUploadSlotResult(false, ErrorCode: "credentials-missing");
        }

        using var request = CreateRequest(
            HttpMethod.Post,
            $"uploads?type={Uri.EscapeDataString(normalizedType)}",
            botToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var failure = ClassifyFailure(response.StatusCode);
            return new MaxUploadSlotResult(false, ErrorCode: failure.ErrorCode);
        }

        try
        {
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var root = json.RootElement;
            var uploadUrl = root.TryGetProperty("url", out var url) ? url.GetString() : null;
            var token = root.TryGetProperty("token", out var uploadToken) ? uploadToken.GetString() : null;
            return Uri.TryCreate(uploadUrl, UriKind.Absolute, out var parsed)
                && parsed.Scheme == Uri.UriSchemeHttps
                    ? new MaxUploadSlotResult(true, parsed.AbsoluteUri, token)
                    : new MaxUploadSlotResult(false, ErrorCode: "invalid-upload-url");
        }
        catch (JsonException)
        {
            return new MaxUploadSlotResult(false, ErrorCode: "invalid-response");
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string uri,
        string botToken,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("Authorization", botToken.Trim());
        return request;
    }

    private static MaxApiResult ClassifyFailure(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.Unauthorized => new(false, ErrorCode: "unauthorized"),
            HttpStatusCode.TooManyRequests => new(false, ErrorCode: "rate-limited", IsTransient: true),
            HttpStatusCode.ServiceUnavailable => new(false, ErrorCode: "service-unavailable", IsTransient: true),
            _ when (int)statusCode >= 500 => new(false, ErrorCode: "provider-error", IsTransient: true),
            _ => new(false, ErrorCode: "request-rejected"),
        };
}

public interface IMaxNotificationService
{
    Task<MaxNotificationDiagnosticsDto> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<MaxNotificationDiagnosticsDto> UpdateSettingsAsync(
        UpdateMaxNotificationSettingsRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SystemMaxNotificationService(
    PaymentContext context,
    IDataProtectionProvider dataProtectionProvider) : IMaxNotificationService
{
    internal const string ProtectedPrefix = "dp:v1:";
    internal const string ProtectorPurpose = "PayPlanner.Notifications.Max.Secrets.v1";
    public const string EnabledKey = "notifications.max.enabled";
    public const string BotTokenKey = "notifications.max.botToken";
    public const string WebhookSecretKey = "notifications.max.webhookSecret";
    public const string NotificationsEnabledKey = "notifications.max.scenarios.notifications";
    public const string DocumentsEnabledKey = "notifications.max.scenarios.documents";
    public const string CorrespondenceEnabledKey = "notifications.max.scenarios.correspondence";
    public const string ConfirmationsEnabledKey = "notifications.max.scenarios.confirmations";
    public const string EmergencyStopKey = "notifications.max.emergencyStop";
    public const string TechnicalPayloadRetentionDaysKey = "notifications.max.technicalPayloadRetentionDays";
    private static readonly string[] Keys =
    [
        EnabledKey,
        BotTokenKey,
        WebhookSecretKey,
        NotificationsEnabledKey,
        DocumentsEnabledKey,
        CorrespondenceEnabledKey,
        ConfirmationsEnabledKey,
        EmergencyStopKey,
        TechnicalPayloadRetentionDaysKey,
    ];
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    public async Task<MaxNotificationDiagnosticsDto> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = Build(await LoadAsync(cancellationToken));
        var deliveryMetrics = await context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == NotificationChannels.Max)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                LastSuccessfulAt = group
                    .Where(item => item.Status == NotificationDeliveryStatus.Sent)
                    .Max(item => (DateTime?)(item.SentAt ?? item.CreatedAt)),
                LastFailedAt = group
                    .Where(item => item.Status == NotificationDeliveryStatus.Failed)
                    .Max(item => (DateTime?)item.CreatedAt),
            })
            .SingleOrDefaultAsync(cancellationToken);
        result.LastSuccessfulDeliveryAt = deliveryMetrics?.LastSuccessfulAt;
        result.LastFailedDeliveryAt = deliveryMetrics?.LastFailedAt;
        result.LastDeliveryError = deliveryMetrics?.LastFailedAt is null
            ? null
            : "Последняя доставка завершилась ошибкой. Подробности доступны в журнале доставок.";
        return result;
    }

    public async Task<MaxNotificationDiagnosticsDto> UpdateSettingsAsync(
        UpdateMaxNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.WebhookSecret)
            && !IsValidWebhookSecret(request.WebhookSecret.Trim()))
        {
            throw new ArgumentException(
                "Webhook-secret MAX должен содержать 5–256 латинских букв, цифр, дефисов или символов подчёркивания.",
                nameof(request));
        }

        var existing = await context.SystemSettings
            .Where(item => Keys.Contains(item.Key))
            .ToDictionaryAsync(item => item.Key, cancellationToken);
        var now = DateTime.UtcNow;
        Upsert(existing, EnabledKey, request.Enabled ? "true" : "false", now);
        Upsert(existing, NotificationsEnabledKey, request.NotificationsEnabled ? "true" : "false", now);
        Upsert(existing, DocumentsEnabledKey, request.DocumentsEnabled ? "true" : "false", now);
        Upsert(existing, CorrespondenceEnabledKey, request.CorrespondenceEnabled ? "true" : "false", now);
        Upsert(existing, ConfirmationsEnabledKey, request.ConfirmationsEnabled ? "true" : "false", now);
        Upsert(existing, EmergencyStopKey, request.EmergencyStop ? "true" : "false", now);
        Upsert(
            existing,
            TechnicalPayloadRetentionDaysKey,
            Math.Clamp(request.TechnicalPayloadRetentionDays ?? 30, 1, 90).ToString(),
            now);
        UpdateSecret(existing, BotTokenKey, request.BotToken, request.KeepExistingBotToken, now);
        UpdateSecret(existing, WebhookSecretKey, request.WebhookSecret, request.KeepExistingWebhookSecret, now);
        await context.SaveChangesAsync(cancellationToken);
        return Build(existing.ToDictionary(item => item.Key, item => item.Value.Value, StringComparer.Ordinal));
    }

    private MaxNotificationDiagnosticsDto Build(IReadOnlyDictionary<string, string> settings)
    {
        var enabled = ParseBool(settings.GetValueOrDefault(EnabledKey));
        var emergencyStop = ParseBool(settings.GetValueOrDefault(EmergencyStopKey));
        var botTokenSet = !string.IsNullOrWhiteSpace(Unprotect(settings.GetValueOrDefault(BotTokenKey)));
        var webhookSecretSet = !string.IsNullOrWhiteSpace(Unprotect(settings.GetValueOrDefault(WebhookSecretKey)));
        var notifications = ParseBool(settings.GetValueOrDefault(NotificationsEnabledKey));
        var documents = ParseBool(settings.GetValueOrDefault(DocumentsEnabledKey));
        var correspondence = ParseBool(settings.GetValueOrDefault(CorrespondenceEnabledKey));
        var confirmations = ParseBool(settings.GetValueOrDefault(ConfirmationsEnabledKey));
        var scenarioConfirmed = notifications || documents || correspondence || confirmations;
        var ready = enabled && !emergencyStop && botTokenSet && webhookSecretSet && scenarioConfirmed;
        var scenarios = MaxMessengerScenarios.All
            .Select(item => new MaxScenarioDto
            {
                Key = item.Key,
                Label = item.Label,
                Description = item.Description,
                RequiresVerifiedLink = item.RequiresVerifiedLink,
                Enabled = item.Key switch
                {
                    MaxMessengerScenarios.Notifications => notifications,
                    MaxMessengerScenarios.Documents => documents,
                    MaxMessengerScenarios.Correspondence => correspondence,
                    MaxMessengerScenarios.Confirmations => confirmations,
                    _ => false,
                },
            })
            .ToArray();
        return new MaxNotificationDiagnosticsDto
        {
            Enabled = enabled,
            Ready = ready,
            ApiConfirmed = true,
            BotConfigured = botTokenSet,
            BotTokenSet = botTokenSet,
            WebhookSecretSet = webhookSecretSet,
            ScenariosConfirmed = scenarioConfirmed,
            EmergencyStop = emergencyStop,
            NotificationsEnabled = notifications,
            DocumentsEnabled = documents,
            CorrespondenceEnabled = correspondence,
            ConfirmationsEnabled = confirmations,
            TechnicalPayloadRetentionDays = ParseRetention(settings.GetValueOrDefault(TechnicalPayloadRetentionDaysKey)),
            Scenarios = scenarios,
            AllowedEventTypes = notifications ? ["employee-critical"] : [],
            StatusText = BuildStatus(enabled, emergencyStop, botTokenSet, webhookSecretSet, scenarioConfirmed),
        };
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
        => await context.SystemSettings
            .AsNoTracking()
            .Where(item => Keys.Contains(item.Key))
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);

    private void UpdateSecret(
        Dictionary<string, PayPlanner.Api.Models.SystemSetting> settings,
        string key,
        string? value,
        bool keepExisting,
        DateTime now)
    {
        if (keepExisting && string.IsNullOrWhiteSpace(value))
        {
            if (!settings.ContainsKey(key))
            {
                Upsert(settings, key, string.Empty, now);
            }
            return;
        }

        var protectedValue = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : ProtectedPrefix + _protector.Protect(value.Trim());
        Upsert(settings, key, protectedValue, now);
    }

    private string Unprotect(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!normalized.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        try
        {
            return _protector.Unprotect(normalized[ProtectedPrefix.Length..]);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }

    private void Upsert(
        Dictionary<string, PayPlanner.Api.Models.SystemSetting> settings,
        string key,
        string value,
        DateTime now)
    {
        if (settings.TryGetValue(key, out var setting))
        {
            setting.Value = value;
            setting.UpdatedAt = now;
            return;
        }

        setting = new PayPlanner.Api.Models.SystemSetting
        {
            Key = key,
            Value = value,
            CreatedAt = now,
            UpdatedAt = now,
        };
        settings[key] = setting;
        context.SystemSettings.Add(setting);
    }

    private static bool ParseBool(string? value)
        => bool.TryParse(value, out var parsed) && parsed;

    private static int ParseRetention(string? value)
        => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, 1, 90) : 30;

    private static bool IsValidWebhookSecret(string value)
        => value.Length is >= 5 and <= 256
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string BuildStatus(
        bool enabled,
        bool emergencyStop,
        bool tokenSet,
        bool webhookSecretSet,
        bool scenariosConfirmed)
    {
        if (emergencyStop) return "MAX остановлен аварийным выключателем.";
        if (!enabled) return "MAX выключен. Настройки можно заполнить заранее без внешних запросов.";
        if (!tokenSet) return "Укажите bot token MAX.";
        if (!webhookSecretSet) return "Укажите отдельный секрет Webhook MAX.";
        if (!scenariosConfirmed) return "Выберите хотя бы один разрешённый сценарий MAX.";
        return "Настройки готовы; отправка начнётся только для проверенно привязанных получателей.";
    }
}
