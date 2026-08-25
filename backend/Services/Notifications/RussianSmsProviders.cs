using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services;

namespace PayPlanner.Api.Services.Notifications;

public interface ISmsProviderMetadata
{
    string Label { get; }
    bool RequiresAccount { get; }
}

public sealed record SmsProviderCredentials(
    string ProviderKey,
    string Account,
    string SenderName,
    string ApiToken)
{
    public bool HasToken => !string.IsNullOrWhiteSpace(ApiToken);
}

public interface ISmsProviderCredentialStore
{
    Task<SmsProviderCredentials> LoadAsync(
        string providerKey,
        CancellationToken cancellationToken = default);
}

public sealed class SmsProviderCredentialStore(
    PaymentContext context,
    IDataProtectionProvider dataProtectionProvider) : ISmsProviderCredentialStore
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector(SystemSmsNotificationService.ApiTokenProtectorPurpose);
    private IReadOnlyDictionary<string, string>? _cachedSettings;

    public async Task<SmsProviderCredentials> LoadAsync(
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        var keys = new[]
        {
            SystemSmsNotificationService.ProviderKeySettingKey,
            SystemSmsNotificationService.ProviderAccountSettingKey,
            SystemSmsNotificationService.SenderNameSettingKey,
            SystemSmsNotificationService.ApiTokenSettingKey,
        };
        var settings = _cachedSettings ??= await context.SystemSettings
            .AsNoTracking()
            .Where(setting => keys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);
        var selectedProvider = settings.GetValueOrDefault(
            SystemSmsNotificationService.ProviderKeySettingKey) ?? string.Empty;
        if (!string.Equals(selectedProvider.Trim(), providerKey, StringComparison.OrdinalIgnoreCase))
        {
            return new SmsProviderCredentials(providerKey, string.Empty, string.Empty, string.Empty);
        }

        return new SmsProviderCredentials(
            providerKey,
            settings.GetValueOrDefault(SystemSmsNotificationService.ProviderAccountSettingKey)?.Trim() ?? string.Empty,
            settings.GetValueOrDefault(SystemSmsNotificationService.SenderNameSettingKey)?.Trim() ?? string.Empty,
            Unprotect(settings.GetValueOrDefault(SystemSmsNotificationService.ApiTokenSettingKey)));
    }

    private string Unprotect(string? storedValue)
    {
        var normalized = storedValue?.Trim() ?? string.Empty;
        if (!normalized.StartsWith(SystemSmsNotificationService.ProtectedTokenPrefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        try
        {
            return _protector.Unprotect(
                normalized[SystemSmsNotificationService.ProtectedTokenPrefix.Length..]);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
    }
}

public static class SmsCredentialProtectionBackfillService
{
    public static async Task EnsureProtectedAsync(
        PaymentContext context,
        IDataProtectionProvider dataProtectionProvider,
        CancellationToken cancellationToken = default)
    {
        var setting = await context.SystemSettings
            .SingleOrDefaultAsync(
                item => item.Key == SystemSmsNotificationService.ApiTokenSettingKey,
                cancellationToken);
        var token = setting?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(token)
            || token.StartsWith(SystemSmsNotificationService.ProtectedTokenPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var protector = dataProtectionProvider.CreateProtector(
            SystemSmsNotificationService.ApiTokenProtectorPurpose);
        setting!.Value = SystemSmsNotificationService.ProtectedTokenPrefix + protector.Protect(token);
        setting.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }
}

public abstract class RussianSmsProviderBase(
    HttpClient httpClient,
    ISmsProviderCredentialStore credentialStore) : ISmsProvider, ISmsProviderMetadata
{
    protected HttpClient HttpClient { get; } = httpClient;
    protected ISmsProviderCredentialStore CredentialStore { get; } = credentialStore;

    public abstract string Key { get; }
    public abstract string Label { get; }
    public abstract bool RequiresAccount { get; }

    public abstract Task<SmsProviderSendResult> SendAsync(
        SmsProviderSendRequest request,
        CancellationToken cancellationToken = default);

    public abstract Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default);

    public virtual Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
        SmsProviderMessageStatusRequest request,
        CancellationToken cancellationToken = default)
        => GetMessageStatusAsync(request.ProviderMessageId, cancellationToken);

    public abstract Task<SmsProviderBalanceResult> GetBalanceAsync(
        CancellationToken cancellationToken = default);

    public async Task<SmsProviderHealthResult> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var balance = await GetBalanceAsync(cancellationToken);
        return new SmsProviderHealthResult(
            balance.IsAvailable,
            DateTime.UtcNow,
            Stopwatch.GetElapsedTime(started),
            balance.ErrorCode,
            balance.ErrorMessage);
    }

    protected static string NormalizePhone(string phoneNumber)
    {
        var digits = RussianPhoneNumberCanonicalizer.NormalizeDigits(phoneNumber);

        if (digits?.Length != 11 || digits[0] != '7')
        {
            throw new ArgumentException(
                "Номер должен быть указан в российском международном формате.",
                nameof(phoneNumber));
        }

        return digits;
    }

    protected static async Task<JsonDocument?> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    protected static SmsProviderSendResult Rejected(string code)
        => new(
            false,
            null,
            SmsProviderMessageStatuses.Rejected,
            ErrorCode: code,
            ErrorMessage: "SMS provider rejected the message.");

    protected static SmsProviderSendResult Unavailable(string code)
        => new(
            false,
            null,
            SmsProviderMessageStatuses.Failed,
            ErrorCode: code,
            ErrorMessage: "SMS provider returned an invalid response.",
            IsTransient: true);

    protected static bool TryReadInt32(JsonElement value, out int result)
        => value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out result)
            : int.TryParse(
                value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out result);

    protected static bool TryReadDecimal(JsonElement value, out decimal result)
        => value.ValueKind == JsonValueKind.Number
            ? value.TryGetDecimal(out result)
            : decimal.TryParse(
                value.ValueKind == JsonValueKind.String ? value.GetString() : null,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out result);

    protected async Task<SmsProviderCredentials> LoadCredentialsAsync(
        CancellationToken cancellationToken)
        => await CredentialStore.LoadAsync(Key, cancellationToken);
}

public sealed class SmsRuProvider(
    HttpClient httpClient,
    ISmsProviderCredentialStore credentialStore)
    : RussianSmsProviderBase(httpClient, credentialStore)
{
    public const string ProviderKey = "sms-ru";
    public override string Key => ProviderKey;
    public override string Label => "SMS.RU";
    public override bool RequiresAccount => false;

    public override async Task<SmsProviderSendResult> SendAsync(
        SmsProviderSendRequest request,
        CancellationToken cancellationToken = default)
    {
        var phone = NormalizePhone(request.PhoneNumber);
        if (string.IsNullOrWhiteSpace(request.ApiToken))
        {
            return Rejected("credentials-missing");
        }

        var form = new Dictionary<string, string>
        {
            ["api_id"] = request.ApiToken,
            ["to"] = phone,
            ["msg"] = request.Text,
            ["json"] = "1",
        };
        if (!string.IsNullOrWhiteSpace(request.SenderName))
        {
            form["from"] = request.SenderName;
        }

        using var response = await HttpClient.PostAsync(
            "sms/send",
            new FormUrlEncodedContent(form),
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode || json is null)
        {
            return Unavailable("sms-ru-invalid-response");
        }

        var root = json.RootElement;
        if (!root.TryGetProperty("status", out var status)
            || !string.Equals(status.GetString(), "OK", StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("sms", out var sms)
            || sms.ValueKind != JsonValueKind.Object
            || !sms.TryGetProperty(phone, out var phoneResult)
            || phoneResult.ValueKind != JsonValueKind.Object
            || !phoneResult.TryGetProperty("status_code", out var statusCode)
            || !TryReadInt32(statusCode, out var numericStatusCode)
            || numericStatusCode != 100)
        {
            return Rejected("sms-ru-rejected");
        }

        var messageId = phoneResult.TryGetProperty("sms_id", out var id)
            ? SmsProviderStatusNormalizer.NormalizeMessageId(id.GetString())
            : null;
        return new SmsProviderSendResult(
            true,
            messageId,
            SmsProviderMessageStatuses.Accepted,
            RawProviderStatus: "100");
    }

    public override async Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadCredentialsAsync(cancellationToken);
        var normalizedId = SmsProviderStatusNormalizer.NormalizeMessageId(providerMessageId)
            ?? throw new ArgumentException("Provider message id is required.", nameof(providerMessageId));
        if (!credentials.HasToken)
        {
            return new SmsProviderMessageStatusResult(
                normalizedId,
                SmsProviderMessageStatuses.Unknown,
                DateTime.UtcNow,
                ErrorCode: "credentials-missing");
        }

        using var response = await HttpClient.PostAsync(
            "sms/status",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["api_id"] = credentials.ApiToken,
                ["sms_id"] = normalizedId,
                ["json"] = "1",
            }),
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode || json is null)
        {
            return new SmsProviderMessageStatusResult(
                normalizedId,
                SmsProviderMessageStatuses.Unknown,
                DateTime.UtcNow,
                ErrorCode: "sms-ru-status-unavailable");
        }

        var rawStatus = json.RootElement.TryGetProperty("sms", out var sms)
            && sms.ValueKind == JsonValueKind.Object
            && sms.TryGetProperty(normalizedId, out var item)
            && item.ValueKind == JsonValueKind.Object
            && item.TryGetProperty("status_code", out var statusCode)
                ? statusCode.ToString()
                : null;
        return new SmsProviderMessageStatusResult(
            normalizedId,
            NormalizeSmsRuStatus(rawStatus),
            DateTime.UtcNow,
            rawStatus);
    }

    public override async Task<SmsProviderBalanceResult> GetBalanceAsync(
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadCredentialsAsync(cancellationToken);
        if (!credentials.HasToken)
        {
            return new SmsProviderBalanceResult(false, null, "RUB", DateTime.UtcNow, "credentials-missing");
        }

        using var response = await HttpClient.PostAsync(
            "my/balance",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["api_id"] = credentials.ApiToken,
                ["json"] = "1",
            }),
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode
            || json is null
            || !json.RootElement.TryGetProperty("balance", out var balance)
            || !TryReadDecimal(balance, out var amount))
        {
            return new SmsProviderBalanceResult(false, null, "RUB", DateTime.UtcNow, "sms-ru-balance-unavailable");
        }

        return new SmsProviderBalanceResult(true, amount, "RUB", DateTime.UtcNow);
    }

    private static string NormalizeSmsRuStatus(string? status)
        => status switch
        {
            "100" or "101" or "102" => SmsProviderMessageStatuses.Accepted,
            "103" => SmsProviderMessageStatuses.Delivered,
            "104" => SmsProviderMessageStatuses.Expired,
            "105" or "106" or "107" or "150" => SmsProviderMessageStatuses.Failed,
            "108" => SmsProviderMessageStatuses.Rejected,
            _ => SmsProviderMessageStatuses.Unknown,
        };
}

public sealed class SmscRuProvider(
    HttpClient httpClient,
    ISmsProviderCredentialStore credentialStore)
    : RussianSmsProviderBase(httpClient, credentialStore)
{
    public const string ProviderKey = "smsc-ru";
    public override string Key => ProviderKey;
    public override string Label => "SMSC.RU";
    public override bool RequiresAccount => false;

    public override async Task<SmsProviderSendResult> SendAsync(
        SmsProviderSendRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ApiToken))
        {
            return Rejected("credentials-missing");
        }

        var form = new Dictionary<string, string>
        {
            ["apikey"] = request.ApiToken,
            ["phones"] = NormalizePhone(request.PhoneNumber),
            ["mes"] = request.Text,
            ["fmt"] = "3",
            ["charset"] = "utf-8",
        };
        if (!string.IsNullOrWhiteSpace(request.SenderName))
        {
            form["sender"] = request.SenderName;
        }

        using var response = await HttpClient.PostAsync(
            "sys/send.php",
            new FormUrlEncodedContent(form),
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode || json is null)
        {
            return Unavailable("smsc-ru-invalid-response");
        }

        var root = json.RootElement;
        if (root.TryGetProperty("error_code", out _)
            || !root.TryGetProperty("id", out var id)
            || id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
        {
            return Rejected("smsc-ru-rejected");
        }

        return new SmsProviderSendResult(
            true,
            SmsProviderStatusNormalizer.NormalizeMessageId(id.ToString()),
            SmsProviderMessageStatuses.Accepted);
    }

    public override Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new SmsProviderMessageStatusResult(
            SmsProviderStatusNormalizer.NormalizeMessageId(providerMessageId)
                ?? throw new ArgumentException("Provider message id is required.", nameof(providerMessageId)),
            SmsProviderMessageStatuses.Unknown,
            DateTime.UtcNow,
            ErrorCode: "smsc-status-requires-recipient"));

    public override async Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
        SmsProviderMessageStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadCredentialsAsync(cancellationToken);
        var normalizedId = SmsProviderStatusNormalizer.NormalizeMessageId(request.ProviderMessageId)
            ?? throw new ArgumentException("Provider message id is required.", nameof(request));
        if (!credentials.HasToken || string.IsNullOrWhiteSpace(request.RecipientAddress))
        {
            return new SmsProviderMessageStatusResult(
                normalizedId,
                SmsProviderMessageStatuses.Unknown,
                DateTime.UtcNow,
                ErrorCode: !credentials.HasToken ? "credentials-missing" : "smsc-status-requires-recipient");
        }

        using var response = await HttpClient.PostAsync(
            "sys/status.php",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["apikey"] = credentials.ApiToken,
                ["phone"] = NormalizePhone(request.RecipientAddress),
                ["id"] = normalizedId,
                ["fmt"] = "3",
                ["charset"] = "utf-8",
            }),
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var rawStatus = response.IsSuccessStatusCode
            && json is not null
            && json.RootElement.TryGetProperty("status", out var status)
                ? status.ToString()
                : null;
        return new SmsProviderMessageStatusResult(
            normalizedId,
            NormalizeSmscStatus(rawStatus),
            DateTime.UtcNow,
            rawStatus,
            ErrorCode: rawStatus is null ? "smsc-status-unavailable" : null);
    }

    public override async Task<SmsProviderBalanceResult> GetBalanceAsync(
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadCredentialsAsync(cancellationToken);
        if (!credentials.HasToken)
        {
            return new SmsProviderBalanceResult(false, null, "RUB", DateTime.UtcNow, "credentials-missing");
        }

        using var response = await HttpClient.PostAsync(
            "sys/balance.php",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["apikey"] = credentials.ApiToken,
                ["fmt"] = "3",
            }),
            cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode
            || json is null
            || !json.RootElement.TryGetProperty("balance", out var balance)
            || !TryReadDecimal(balance, out var amount))
        {
            return new SmsProviderBalanceResult(false, null, "RUB", DateTime.UtcNow, "smsc-balance-unavailable");
        }

        return new SmsProviderBalanceResult(true, amount, "RUB", DateTime.UtcNow);
    }

    private static string NormalizeSmscStatus(string? status)
        => status switch
        {
            "-1" or "0" => SmsProviderMessageStatuses.Accepted,
            "1" or "2" or "4" => SmsProviderMessageStatuses.Delivered,
            "3" => SmsProviderMessageStatuses.Expired,
            "-2" or "-3" or "20" => SmsProviderMessageStatuses.Failed,
            "22" or "23" or "24" or "25" => SmsProviderMessageStatuses.Rejected,
            _ => SmsProviderMessageStatuses.Unknown,
        };
}

public sealed class SmsAeroProvider(
    HttpClient httpClient,
    ISmsProviderCredentialStore credentialStore)
    : RussianSmsProviderBase(httpClient, credentialStore)
{
    public const string ProviderKey = "sms-aero";
    public override string Key => ProviderKey;
    public override string Label => "SMS Aero";
    public override bool RequiresAccount => true;

    public override async Task<SmsProviderSendResult> SendAsync(
        SmsProviderSendRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ApiToken)
            || string.IsNullOrWhiteSpace(request.Account))
        {
            return Rejected("credentials-missing");
        }

        using var message = CreateRequest(
            HttpMethod.Post,
            "v2/sms/send",
            request.Account,
            request.ApiToken,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["number"] = NormalizePhone(request.PhoneNumber),
                ["text"] = request.Text,
                ["sign"] = request.SenderName,
            }));
        using var response = await HttpClient.SendAsync(message, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode || json is null)
        {
            return Unavailable("sms-aero-invalid-response");
        }

        var root = json.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            return Rejected("sms-aero-rejected");
        }

        var messageId = root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("id", out var id)
            && id.ValueKind is JsonValueKind.Number or JsonValueKind.String
                ? SmsProviderStatusNormalizer.NormalizeMessageId(id.ToString())
                : null;
        return new SmsProviderSendResult(
            true,
            messageId,
            SmsProviderMessageStatuses.Accepted);
    }

    public override async Task<SmsProviderMessageStatusResult> GetMessageStatusAsync(
        string providerMessageId,
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadCredentialsAsync(cancellationToken);
        var normalizedId = SmsProviderStatusNormalizer.NormalizeMessageId(providerMessageId)
            ?? throw new ArgumentException("Provider message id is required.", nameof(providerMessageId));
        if (!credentials.HasToken || string.IsNullOrWhiteSpace(credentials.Account))
        {
            return new SmsProviderMessageStatusResult(
                normalizedId,
                SmsProviderMessageStatuses.Unknown,
                DateTime.UtcNow,
                ErrorCode: "credentials-missing");
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            $"v2/sms/status?id={Uri.EscapeDataString(normalizedId)}",
            credentials.Account,
            credentials.ApiToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var rawStatus = response.IsSuccessStatusCode
            && json is not null
            && json.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("status", out var status)
                ? status.GetString()
                : null;
        return new SmsProviderMessageStatusResult(
            normalizedId,
            SmsProviderStatusNormalizer.NormalizeStatus(rawStatus),
            DateTime.UtcNow,
            rawStatus,
            ErrorCode: rawStatus is null ? "sms-aero-status-unavailable" : null);
    }

    public override async Task<SmsProviderBalanceResult> GetBalanceAsync(
        CancellationToken cancellationToken = default)
    {
        var credentials = await LoadCredentialsAsync(cancellationToken);
        if (!credentials.HasToken || string.IsNullOrWhiteSpace(credentials.Account))
        {
            return new SmsProviderBalanceResult(false, null, "RUB", DateTime.UtcNow, "credentials-missing");
        }

        using var request = CreateRequest(
            HttpMethod.Get,
            "v2/balance",
            credentials.Account,
            credentials.ApiToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!response.IsSuccessStatusCode
            || json is null
            || !json.RootElement.TryGetProperty("data", out var data)
            || !TryReadDecimal(data, out var amount))
        {
            return new SmsProviderBalanceResult(false, null, "RUB", DateTime.UtcNow, "sms-aero-balance-unavailable");
        }

        return new SmsProviderBalanceResult(true, amount, "RUB", DateTime.UtcNow);
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string uri,
        string account,
        string apiToken,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content,
        };
        var credentials = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{account}:{apiToken}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        return request;
    }
}
