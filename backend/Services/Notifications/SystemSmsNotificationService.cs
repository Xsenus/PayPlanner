using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
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
    public const string ProviderAccountSettingKey = "notifications.sms.providerAccount";
    public const string SenderNameSettingKey = "notifications.sms.senderName";
    public const string ApiTokenSettingKey = "notifications.sms.apiToken";
    public const string DailyLimitSettingKey = "notifications.sms.dailyLimit";
    public const string RecipientDailyLimitSettingKey = "notifications.sms.recipientDailyLimit";
    public const string BurstLimitPerMinuteSettingKey = "notifications.sms.burstLimitPerMinute";
    public const string MaxMessageLengthSettingKey = "notifications.sms.maxMessageLength";
    public const string LowBalanceThresholdSettingKey = "notifications.sms.lowBalanceThreshold";
    public const string QuietHoursEnabledSettingKey = "notifications.sms.quietHours.enabled";
    public const string QuietHoursStartSettingKey = "notifications.sms.quietHours.start";
    public const string QuietHoursEndSettingKey = "notifications.sms.quietHours.end";
    public const string QuietHoursTimeZoneSettingKey = "notifications.sms.quietHours.timeZone";
    public const string NotesSettingKey = "notifications.sms.notes";
    public const string DiagnosticsSettingKey = "notifications.sms.diagnostics";
    public const string EmergencyStopSettingKey = "notifications.sms.emergencyStop";
    public const string DisabledEventTypesSettingKey = "notifications.sms.disabledEventTypes";
    public const string DefaultQuietHoursStart = "20:00";
    public const string DefaultQuietHoursEnd = "09:00";

    private const int DefaultDailyLimit = 100;
    private const int DefaultRecipientDailyLimit = 5;
    private const int DefaultBurstLimitPerMinute = 20;
    private const int DefaultMaxMessageLength = 480;
    private const decimal DefaultLowBalanceThreshold = 500m;
    internal const string ProtectedTokenPrefix = "dp:v1:";
    internal const string ApiTokenProtectorPurpose = "PayPlanner.Notifications.Sms.ApiToken.v1";

    private static readonly IDataProtectionProvider FallbackDataProtectionProvider =
        new EphemeralDataProtectionProvider();
    private static readonly string[] SettingKeys =
    [
        EnabledSettingKey,
        ProviderKeySettingKey,
        ProviderAccountSettingKey,
        SenderNameSettingKey,
        ApiTokenSettingKey,
        DailyLimitSettingKey,
        RecipientDailyLimitSettingKey,
        BurstLimitPerMinuteSettingKey,
        MaxMessageLengthSettingKey,
        LowBalanceThresholdSettingKey,
        QuietHoursEnabledSettingKey,
        QuietHoursStartSettingKey,
        QuietHoursEndSettingKey,
        QuietHoursTimeZoneSettingKey,
        NotesSettingKey,
        DiagnosticsSettingKey,
        EmergencyStopSettingKey,
        DisabledEventTypesSettingKey
    ];

    private readonly PaymentContext _context;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IDataProtector _apiTokenProtector;
    private readonly IReadOnlyDictionary<string, ISmsProvider> _providers;
    private readonly IReadOnlyDictionary<string, SmsProviderResilienceExecutor> _providerExecutors;

    public SystemSmsNotificationService(
        PaymentContext context,
        IHttpContextAccessor? httpContextAccessor = null,
        IDataProtectionProvider? dataProtectionProvider = null,
        IEnumerable<ISmsProvider>? providers = null)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _apiTokenProtector = (dataProtectionProvider ?? FallbackDataProtectionProvider)
            .CreateProtector(ApiTokenProtectorPurpose);
        _providers = (providers ?? [])
            .GroupBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        _providerExecutors = _providers.ToDictionary(
            item => item.Key,
            item => new SmsProviderResilienceExecutor(item.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<SmsNotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var result = BuildSettings(settings);
        result.Diagnostics = await BuildDiagnosticsAsync(settings, cancellationToken);
        return result;
    }

    public async Task<SmsNotificationSettingsDto> UpdateSettingsAsync(
        UpdateSmsNotificationSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var existing = await _context.SystemSettings
            .Where(setting => SettingKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, cancellationToken);
        var previousValues = existing.ToDictionary(
            item => item.Key,
            item => item.Value.Value,
            StringComparer.Ordinal);
        var previousSettings = BuildSettings(previousValues);
        var previousApiToken = UnprotectApiToken(previousValues.GetValueOrDefault(ApiTokenSettingKey));

        UpsertSetting(existing, EnabledSettingKey, request.Enabled ? "true" : "false", now);
        UpsertSetting(existing, ProviderKeySettingKey, NormalizeOptional(request.ProviderKey) ?? string.Empty, now);
        UpsertSetting(existing, ProviderAccountSettingKey, NormalizeOptional(request.ProviderAccount) ?? string.Empty, now);
        UpsertSetting(existing, SenderNameSettingKey, NormalizeOptional(request.SenderName) ?? string.Empty, now);
        UpsertSetting(existing, DailyLimitSettingKey, NormalizeLimit(request.DailyLimit, DefaultDailyLimit, 1, 100000).ToString(), now);
        UpsertSetting(
            existing,
            RecipientDailyLimitSettingKey,
            NormalizeLimit(request.RecipientDailyLimit, DefaultRecipientDailyLimit, 1, 1000).ToString(),
            now);
        UpsertSetting(
            existing,
            BurstLimitPerMinuteSettingKey,
            NormalizeLimit(request.BurstLimitPerMinute, DefaultBurstLimitPerMinute, 1, 10000).ToString(),
            now);
        UpsertSetting(existing, MaxMessageLengthSettingKey, NormalizeLimit(request.MaxMessageLength, DefaultMaxMessageLength, 70, 1000).ToString(), now);
        UpsertSetting(
            existing,
            LowBalanceThresholdSettingKey,
            NormalizeMoneyThreshold(request.LowBalanceThreshold, DefaultLowBalanceThreshold)
                .ToString(CultureInfo.InvariantCulture),
            now);
        if (request.EmergencyStop.HasValue)
        {
            UpsertSetting(existing, EmergencyStopSettingKey, request.EmergencyStop.Value ? "true" : "false", now);
        }
        else if (!existing.ContainsKey(EmergencyStopSettingKey))
        {
            UpsertSetting(existing, EmergencyStopSettingKey, "false", now);
        }

        if (request.DisabledEventTypes is not null)
        {
            UpsertSetting(
                existing,
                DisabledEventTypesSettingKey,
                JsonSerializer.Serialize(NormalizeDisabledEventTypes(request.DisabledEventTypes)),
                now);
        }
        else if (!existing.ContainsKey(DisabledEventTypesSettingKey))
        {
            UpsertSetting(existing, DisabledEventTypesSettingKey, "[]", now);
        }
        UpsertSetting(existing, QuietHoursEnabledSettingKey, request.QuietHoursEnabled ? "true" : "false", now);
        UpsertSetting(existing, QuietHoursStartSettingKey, NormalizeTime(request.QuietHoursStart, DefaultQuietHoursStart), now);
        UpsertSetting(existing, QuietHoursEndSettingKey, NormalizeTime(request.QuietHoursEnd, DefaultQuietHoursEnd), now);
        UpsertSetting(existing, QuietHoursTimeZoneSettingKey, NormalizeTimeZone(request.QuietHoursTimeZone), now);
        UpsertSetting(existing, NotesSettingKey, NormalizeNotes(request.Notes), now);

        if (!request.KeepExistingApiToken || !string.IsNullOrWhiteSpace(request.ApiToken))
        {
            UpsertSetting(
                existing,
                ApiTokenSettingKey,
                ProtectApiToken(NormalizeOptional(request.ApiToken)),
                now);
        }
        else if (!existing.ContainsKey(ApiTokenSettingKey))
        {
            UpsertSetting(existing, ApiTokenSettingKey, string.Empty, now);
        }

        var nextValues = existing.ToDictionary(
            item => item.Key,
            item => item.Value.Value,
            StringComparer.Ordinal);
        var nextSettings = BuildSettings(nextValues);
        var nextApiToken = UnprotectApiToken(nextValues.GetValueOrDefault(ApiTokenSettingKey));
        var auditEvent = NotificationSettingsAudit.CreateSmsSettingsUpdatedEvent(
            _httpContextAccessor?.HttpContext?.User,
            previousSettings,
            nextSettings,
            !string.Equals(previousApiToken, nextApiToken, StringComparison.Ordinal));
        if (auditEvent is not null)
        {
            _context.ObjectAuditEvents.Add(auditEvent);
        }

        await _context.SaveChangesAsync(cancellationToken);

        var updated = await LoadSettingsAsync(cancellationToken);
        var result = BuildSettings(updated);
        result.Diagnostics = await BuildDiagnosticsAsync(updated, cancellationToken);
        return result;
    }

    public async Task SaveDiagnosticsAsync(
        SmsProviderDiagnosticsSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var setting = await _context.SystemSettings
            .FirstOrDefaultAsync(item => item.Key == DiagnosticsSettingKey, cancellationToken);
        var now = DateTime.UtcNow;
        var value = JsonSerializer.Serialize(snapshot);
        if (setting is null)
        {
            _context.SystemSettings.Add(new SystemSetting
            {
                Key = DiagnosticsSettingKey,
                Value = value,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SmsDeliveryPolicy> GetDeliveryPolicyAsync(CancellationToken cancellationToken = default)
    {
        var values = await _context.SystemSettings
            .AsNoTracking()
            .Where(item =>
                item.Key == EmergencyStopSettingKey
                || item.Key == DisabledEventTypesSettingKey)
            .ToDictionaryAsync(item => item.Key, item => item.Value, cancellationToken);
        return BuildDeliveryPolicy(values);
    }

    public async Task<SmsSendResult> SendMessageAsync(
        string phoneNumber,
        string text,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var rawSettings = await LoadSettingsAsync(cancellationToken);
        var settings = BuildSettings(rawSettings);
        if (settings.EmergencyStop)
        {
            return new SmsSendResult(
                false,
                NotificationDeliveryStatus.Skipped,
                "SMS не отправлено: включена аварийная остановка канала.",
                ErrorIsSafe: true);
        }

        if (!settings.Ready
            || !_providerExecutors.TryGetValue(settings.ProviderKey, out var executor))
        {
            var status = string.IsNullOrWhiteSpace(settings.StatusText)
                ? "SMS-уведомления не отправляются: провайдер не готов."
                : settings.StatusText;

            return new SmsSendResult(
                false,
                NotificationDeliveryStatus.Skipped,
                status,
                ErrorIsSafe: true);
        }

        try
        {
            var result = await executor.SendAsync(
                new SmsProviderSendRequest(
                    phoneNumber,
                    text,
                    settings.SenderName,
                    idempotencyKey,
                    settings.ProviderAccount,
                    UnprotectApiToken(rawSettings.GetValueOrDefault(ApiTokenSettingKey))),
                cancellationToken);
            return result.Accepted
                ? new SmsSendResult(
                    true,
                    NotificationDeliveryStatus.Sent)
                : new SmsSendResult(
                    false,
                    NotificationDeliveryStatus.Failed,
                    BuildSafeProviderError(result.ErrorCode),
                    ErrorIsSafe: true);
        }
        catch (ArgumentException ex)
        {
            return new SmsSendResult(
                false,
                NotificationDeliveryStatus.Failed,
                ex.Message,
                ErrorIsSafe: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SmsSendResult(
                false,
                NotificationDeliveryStatus.Failed,
                "SMS-провайдер не ответил за отведенное время.",
                ErrorIsSafe: true);
        }
        catch (HttpRequestException)
        {
            return new SmsSendResult(
                false,
                NotificationDeliveryStatus.Failed,
                "Не удалось связаться с SMS-провайдером.",
                ErrorIsSafe: true);
        }
    }

    private SmsNotificationSettingsDto BuildSettings(IReadOnlyDictionary<string, string> settings)
    {
        var enabled = ParseBool(settings.GetValueOrDefault(EnabledSettingKey), false);
        var providerKey = NormalizeOptional(settings.GetValueOrDefault(ProviderKeySettingKey)) ?? string.Empty;
        var providerAccount = NormalizeOptional(settings.GetValueOrDefault(ProviderAccountSettingKey)) ?? string.Empty;
        var senderName = NormalizeOptional(settings.GetValueOrDefault(SenderNameSettingKey)) ?? string.Empty;
        var apiToken = UnprotectApiToken(settings.GetValueOrDefault(ApiTokenSettingKey));
        var dailyLimit = NormalizeLimit(ParseNullableInt(settings.GetValueOrDefault(DailyLimitSettingKey)), DefaultDailyLimit, 1, 100000);
        var recipientDailyLimit = NormalizeLimit(
            ParseNullableInt(settings.GetValueOrDefault(RecipientDailyLimitSettingKey)),
            DefaultRecipientDailyLimit,
            1,
            1000);
        var burstLimitPerMinute = NormalizeLimit(
            ParseNullableInt(settings.GetValueOrDefault(BurstLimitPerMinuteSettingKey)),
            DefaultBurstLimitPerMinute,
            1,
            10000);
        var maxMessageLength = NormalizeLimit(ParseNullableInt(settings.GetValueOrDefault(MaxMessageLengthSettingKey)), DefaultMaxMessageLength, 70, 1000);
        var lowBalanceThreshold = NormalizeMoneyThreshold(
            ParseNullableDecimal(settings.GetValueOrDefault(LowBalanceThresholdSettingKey)),
            DefaultLowBalanceThreshold);
        var deliveryPolicy = BuildDeliveryPolicy(settings);
        var quietHoursEnabled = ParseBool(settings.GetValueOrDefault(QuietHoursEnabledSettingKey), true);
        var quietHoursStart = NormalizeTime(settings.GetValueOrDefault(QuietHoursStartSettingKey), DefaultQuietHoursStart);
        var quietHoursEnd = NormalizeTime(settings.GetValueOrDefault(QuietHoursEndSettingKey), DefaultQuietHoursEnd);
        var quietHoursTimeZone = NormalizeTimeZone(settings.GetValueOrDefault(QuietHoursTimeZoneSettingKey));
        var notes = NormalizeNotes(settings.GetValueOrDefault(NotesSettingKey));
        var apiTokenSet = !string.IsNullOrWhiteSpace(apiToken);
        var adapterConfigured = _providers.TryGetValue(providerKey, out var provider);
        var providerMetadata = provider as ISmsProviderMetadata;
        var providerConfigured = !string.IsNullOrWhiteSpace(providerKey)
            && apiTokenSet
            && (providerMetadata?.RequiresAccount != true || !string.IsNullOrWhiteSpace(providerAccount));
        var ready = enabled
            && !deliveryPolicy.EmergencyStop
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
            ProviderAccount = providerAccount,
            SenderName = senderName,
            Providers = _providers.Values
                .Select(provider => (
                    Provider: provider,
                    Metadata: provider as ISmsProviderMetadata))
                .OrderBy(item => item.Metadata?.Label ?? item.Provider.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new SmsProviderOptionDto
                {
                    Key = item.Provider.Key,
                    Label = item.Metadata?.Label ?? item.Provider.Key,
                    RequiresAccount = item.Metadata?.RequiresAccount == true,
                })
                .ToArray(),
            DailyLimit = dailyLimit,
            RecipientDailyLimit = recipientDailyLimit,
            BurstLimitPerMinute = burstLimitPerMinute,
            MaxMessageLength = maxMessageLength,
            LowBalanceThreshold = lowBalanceThreshold,
            EmergencyStop = deliveryPolicy.EmergencyStop,
            EventTypes = UserNotificationTypes.SmsEventTypes
                .Select(item => new SmsEventTypeSettingDto
                {
                    Key = item.Key,
                    Label = item.Label,
                    Description = item.Description,
                    Enabled = deliveryPolicy.IsEventEnabled(item.Key)
                })
                .ToList(),
            QuietHoursEnabled = quietHoursEnabled,
            QuietHoursStart = quietHoursStart,
            QuietHoursEnd = quietHoursEnd,
            QuietHoursTimeZone = quietHoursTimeZone,
            Notes = notes,
            StatusText = BuildStatusText(
                enabled,
                deliveryPolicy.EmergencyStop,
                providerKey,
                providerAccount,
                senderName,
                apiTokenSet,
                adapterConfigured,
                providerMetadata?.RequiresAccount == true)
        };
    }

    private static string BuildSafeProviderError(string? errorCode)
        => errorCode switch
        {
            "circuit-open" => "SMS-провайдер временно недоступен. Повторите отправку позже.",
            "provider-timeout" => "SMS-провайдер не ответил за отведенное время.",
            "provider-http-error" => "Не удалось связаться с SMS-провайдером.",
            "credentials-missing" => "Не заполнены учетные данные SMS-провайдера.",
            _ => "SMS-провайдер отклонил сообщение или вернул некорректный ответ.",
        };

    private static string BuildStatusText(
        bool enabled,
        bool emergencyStop,
        string providerKey,
        string providerAccount,
        string senderName,
        bool apiTokenSet,
        bool adapterConfigured,
        bool providerAccountRequired)
    {
        if (emergencyStop)
        {
            return "Включена аварийная остановка: новые SMS не создаются, остальные каналы продолжают работать.";
        }

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

        if (providerAccountRequired && string.IsNullOrWhiteSpace(providerAccount))
        {
            return "Укажите email или логин учетной записи SMS-провайдера.";
        }

        return "SMS-канал готов к отправке.";
    }

    private string ProtectApiToken(string? token)
        => string.IsNullOrWhiteSpace(token)
            ? string.Empty
            : $"{ProtectedTokenPrefix}{_apiTokenProtector.Protect(token)}";

    private string UnprotectApiToken(string? storedValue)
    {
        var normalized = NormalizeOptional(storedValue) ?? string.Empty;
        if (!normalized.StartsWith(ProtectedTokenPrefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        try
        {
            return _apiTokenProtector.Unprotect(normalized[ProtectedTokenPrefix.Length..]);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
    }

    private async Task<Dictionary<string, string>> LoadSettingsAsync(CancellationToken cancellationToken)
        => await _context.SystemSettings
            .AsNoTracking()
            .Where(setting => SettingKeys.Contains(setting.Key))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.Value, cancellationToken);

    private async Task<SmsNotificationDiagnosticsDto> BuildDiagnosticsAsync(
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken)
    {
        SmsProviderDiagnosticsSnapshot? providerSnapshot = null;
        var rawSnapshot = settings.GetValueOrDefault(DiagnosticsSettingKey);
        if (!string.IsNullOrWhiteSpace(rawSnapshot))
        {
            try
            {
                providerSnapshot = JsonSerializer.Deserialize<SmsProviderDiagnosticsSnapshot>(rawSnapshot);
            }
            catch (JsonException)
            {
                providerSnapshot = null;
            }
        }

        var deliveredRateFrom = DateTime.UtcNow.AddDays(-30);
        var deliveryStats = await _context.NotificationDeliveries
            .AsNoTracking()
            .Where(item => item.Channel == NotificationChannels.Sms)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                PendingCount = group.Count(item => item.Status == NotificationDeliveryStatus.Pending),
                FailedCount = group.Count(item =>
                    item.CreatedAt >= deliveredRateFrom
                    && item.Status == NotificationDeliveryStatus.Failed),
                DeliveredCount = group.Count(item =>
                    item.CreatedAt >= deliveredRateFrom
                    && item.Status == NotificationDeliveryStatus.Sent),
                LastSuccessfulDeliveryAt = group
                    .Where(item => item.Status == NotificationDeliveryStatus.Sent)
                    .Max(item => (DateTime?)item.SentAt ?? item.CreatedAt),
                LastFailedDeliveryAt = group
                    .Where(item => item.Status == NotificationDeliveryStatus.Failed)
                    .Max(item => (DateTime?)item.CreatedAt),
                LastDeliveryError = group
                    .Where(item => item.Status == NotificationDeliveryStatus.Failed)
                    .OrderByDescending(item => item.CreatedAt)
                    .Select(item => item.Error)
                    .FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);
        var sampleSize = (deliveryStats?.DeliveredCount ?? 0) + (deliveryStats?.FailedCount ?? 0);

        return new SmsNotificationDiagnosticsDto
        {
            LastCheckedAt = providerSnapshot?.CheckedAtUtc,
            ProviderHealthy = providerSnapshot?.ProviderHealthy,
            Balance = providerSnapshot?.Balance,
            BalanceCurrency = NormalizeCurrency(providerSnapshot?.BalanceCurrency),
            IssueCode = NormalizeOptional(providerSnapshot?.IssueCode),
            Summary = NormalizeOptional(providerSnapshot?.Summary),
            LastSuccessfulDeliveryAt = deliveryStats?.LastSuccessfulDeliveryAt,
            LastFailedDeliveryAt = deliveryStats?.LastFailedDeliveryAt,
            LastDeliveryError = NormalizeDiagnosticError(deliveryStats?.LastDeliveryError),
            PendingCount = deliveryStats?.PendingCount ?? 0,
            FailedCount = deliveryStats?.FailedCount ?? 0,
            DeliveredCount = deliveryStats?.DeliveredCount ?? 0,
            DeliverySampleSize = sampleSize,
            DeliveredRatePercent = sampleSize == 0
                ? null
                : Math.Round((deliveryStats!.DeliveredCount * 100m) / sampleSize, 1)
        };
    }

    private void UpsertSetting(
        IDictionary<string, SystemSetting> settings,
        string key,
        string value,
        DateTime nowUtc)
    {
        if (!settings.TryGetValue(key, out var setting))
        {
            setting = new SystemSetting
            {
                Key = key,
                Value = value,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            };
            settings[key] = setting;
            _context.SystemSettings.Add(setting);
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

    private static string NormalizeCurrency(string? value)
    {
        var normalized = NormalizeOptional(value)?.ToUpperInvariant();
        return normalized is { Length: <= 12 } ? normalized : string.Empty;
    }

    private static SmsDeliveryPolicy BuildDeliveryPolicy(IReadOnlyDictionary<string, string> settings)
        => new(
            ParseBool(settings.GetValueOrDefault(EmergencyStopSettingKey), false),
            NormalizeDisabledEventTypes(ParseStringList(settings.GetValueOrDefault(DisabledEventTypesSettingKey))));

    private static IReadOnlySet<string> NormalizeDisabledEventTypes(IEnumerable<string>? values)
    {
        var knownTypes = UserNotificationTypes.SmsEventTypes
            .Select(item => item.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (values ?? [])
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value) && knownTypes.Contains(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ParseStringList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NormalizeDiagnosticError(string? value)
    {
        var normalized = NormalizeOptional(value);
        if (normalized is null)
        {
            return null;
        }

        var protection = SmsMessageProtector.Protect(new NotificationMessage(
            "Ошибка SMS-доставки",
            normalized));
        return protection.WasReplaced
            ? "Подробности ошибки скрыты. Проверьте настройки и журнал SMS-доставок."
            : protection.Body;
    }

    private static int NormalizeLimit(int? value, int fallback, int min, int max)
        => Math.Clamp(value.GetValueOrDefault(fallback), min, max);

    private static decimal NormalizeMoneyThreshold(decimal? value, decimal fallback)
        => Math.Clamp(value.GetValueOrDefault(fallback), 0m, 10_000_000m);

    private static string NormalizeTime(string? value, string fallback)
        => TimeSpan.TryParseExact(
            value?.Trim(),
            @"hh\:mm",
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture)
            : fallback;

    private static string NormalizeTimeZone(string? value)
        => CalendarTimeZoneHelper.ResolveIanaTimeZoneId(
            CalendarTimeZoneHelper.ResolveTimeZone(value));

    private static bool ParseBool(string? value, bool fallback)
        => bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    private static decimal? ParseNullableDecimal(string? value)
        => decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
}
