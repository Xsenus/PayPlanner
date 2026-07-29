using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services.Notifications;

public sealed class SystemSmsNotificationService : ISmsNotificationService
{
    public const string EnabledSettingKey = "notifications.sms.enabled";
    public const string ProviderKeySettingKey = "notifications.sms.providerKey";
    public const string SenderNameSettingKey = "notifications.sms.senderName";
    public const string ApiTokenSettingKey = "notifications.sms.apiToken";
    public const string DailyLimitSettingKey = "notifications.sms.dailyLimit";
    public const string MaxMessageLengthSettingKey = "notifications.sms.maxMessageLength";
    public const string NotesSettingKey = "notifications.sms.notes";

    private const int DefaultDailyLimit = 100;
    private const int DefaultMaxMessageLength = 480;

    private static readonly string[] SettingKeys =
    [
        EnabledSettingKey,
        ProviderKeySettingKey,
        SenderNameSettingKey,
        ApiTokenSettingKey,
        DailyLimitSettingKey,
        MaxMessageLengthSettingKey,
        NotesSettingKey
    ];

    private readonly PaymentContext _context;

    public SystemSmsNotificationService(PaymentContext context)
    {
        _context = context;
    }

    public async Task<SmsNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        return BuildSettings(settings);
    }

    public async Task<SmsNotificationSettingsDto> UpdateSettingsAsync(
        UpdateSmsNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var existing = await LoadSettingsAsync(cancellationToken);

        await UpsertSettingAsync(EnabledSettingKey, request.Enabled ? "true" : "false", now, cancellationToken);
        await UpsertSettingAsync(ProviderKeySettingKey, NormalizeOptional(request.ProviderKey) ?? string.Empty, now, cancellationToken);
        await UpsertSettingAsync(SenderNameSettingKey, NormalizeOptional(request.SenderName) ?? string.Empty, now, cancellationToken);
        await UpsertSettingAsync(DailyLimitSettingKey, NormalizeLimit(request.DailyLimit, DefaultDailyLimit, 1, 100000).ToString(), now, cancellationToken);
        await UpsertSettingAsync(MaxMessageLengthSettingKey, NormalizeLimit(request.MaxMessageLength, DefaultMaxMessageLength, 70, 1000).ToString(), now, cancellationToken);
        await UpsertSettingAsync(NotesSettingKey, NormalizeNotes(request.Notes), now, cancellationToken);

        if (!request.KeepExistingApiToken || !string.IsNullOrWhiteSpace(request.ApiToken))
        {
            await UpsertSettingAsync(ApiTokenSettingKey, NormalizeOptional(request.ApiToken) ?? string.Empty, now, cancellationToken);
        }
        else if (!existing.ContainsKey(ApiTokenSettingKey))
        {
            await UpsertSettingAsync(ApiTokenSettingKey, string.Empty, now, cancellationToken);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var updated = await LoadSettingsAsync(cancellationToken);
        return BuildSettings(updated);
    }

    public async Task<SmsSendResult> SendMessageAsync(
        string phoneNumber,
        string text,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        var status = string.IsNullOrWhiteSpace(settings.StatusText)
            ? "SMS-уведомления не отправляются: адаптер провайдера еще не подключен."
            : settings.StatusText;

        return new SmsSendResult(false, NotificationDeliveryStatus.Skipped, status);
    }

    private SmsNotificationSettingsDto BuildSettings(IReadOnlyDictionary<string, string> settings)
    {
        var enabled = ParseBool(settings.GetValueOrDefault(EnabledSettingKey), false);
        var providerKey = NormalizeOptional(settings.GetValueOrDefault(ProviderKeySettingKey)) ?? string.Empty;
        var senderName = NormalizeOptional(settings.GetValueOrDefault(SenderNameSettingKey)) ?? string.Empty;
        var apiToken = NormalizeOptional(settings.GetValueOrDefault(ApiTokenSettingKey)) ?? string.Empty;
        var dailyLimit = NormalizeLimit(ParseNullableInt(settings.GetValueOrDefault(DailyLimitSettingKey)), DefaultDailyLimit, 1, 100000);
        var maxMessageLength = NormalizeLimit(ParseNullableInt(settings.GetValueOrDefault(MaxMessageLengthSettingKey)), DefaultMaxMessageLength, 70, 1000);
        var notes = NormalizeNotes(settings.GetValueOrDefault(NotesSettingKey));
        var apiTokenSet = !string.IsNullOrWhiteSpace(apiToken);
        var providerConfigured = !string.IsNullOrWhiteSpace(providerKey) && apiTokenSet;
        const bool adapterConfigured = false;
        var ready = enabled
            && providerConfigured
            && adapterConfigured
            && !string.IsNullOrWhiteSpace(senderName);

        return new SmsNotificationSettingsDto
        {
            Enabled = enabled,
            Ready = ready,
            ProviderConfigured = providerConfigured,
            AdapterConfigured = adapterConfigured,
            ApiTokenSet = apiTokenSet,
            ProviderKey = providerKey,
            SenderName = senderName,
            DailyLimit = dailyLimit,
            MaxMessageLength = maxMessageLength,
            Notes = notes,
            StatusText = BuildStatusText(enabled, providerKey, senderName, apiTokenSet, adapterConfigured)
        };
    }

    private static string BuildStatusText(
        bool enabled,
        string providerKey,
        string senderName,
        bool apiTokenSet,
        bool adapterConfigured)
    {
        if (!enabled)
        {
            return "SMS-уведомления отключены. Настройки можно заполнить заранее, но сообщения не будут отправляться.";
        }

        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return "Выберите SMS-провайдера перед включением отправки.";
        }

        if (string.IsNullOrWhiteSpace(senderName))
        {
            return "Укажите имя отправителя SMS.";
        }

        if (!apiTokenSet)
        {
            return "Укажите API-токен SMS-провайдера.";
        }

        if (!adapterConfigured)
        {
            return "Настройки SMS сохранены, но отправка еще недоступна: адаптер выбранного провайдера не подключен.";
        }

        return "SMS-канал готов к отправке.";
    }

    private async Task<Dictionary<string, string>> LoadSettingsAsync(CancellationToken cancellationToken)
        => await _context.SystemSettings
            .AsNoTracking()
            .Where(setting => SettingKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

    private async Task UpsertSettingAsync(string key, string value, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var setting = await _context.SystemSettings.FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (setting is null)
        {
            _context.SystemSettings.Add(new SystemSetting
            {
                Key = key,
                Value = value,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            });
            return;
        }

        setting.Value = value;
        setting.UpdatedAt = nowUtc;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string NormalizeNotes(string? value)
    {
        var normalized = NormalizeOptional(value) ?? string.Empty;
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static int NormalizeLimit(int? value, int fallback, int min, int max)
        => Math.Clamp(value.GetValueOrDefault(fallback), min, max);

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;
}
