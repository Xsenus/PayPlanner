using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public static class CalendarSyncProviderRegistry
{
    public const int DefaultProviderSyncIntervalMinutes = 10;
    public const int MinProviderSyncIntervalMinutes = 5;
    public const int MaxProviderSyncIntervalMinutes = 1440;

    public static CalendarSyncSettingsDto BuildDefaultSettings(bool externalCalendarSyncEnabled = false)
        => new()
        {
            ExternalCalendarSyncEnabled = externalCalendarSyncEnabled,
            GoogleCalendarSyncEnabled = externalCalendarSyncEnabled,
            YandexCalendarSyncEnabled = externalCalendarSyncEnabled,
            GoogleSyncIntervalMinutes = DefaultProviderSyncIntervalMinutes,
            YandexSyncIntervalMinutes = DefaultProviderSyncIntervalMinutes,
            SyncMode = "notConfigured",
            ConflictResolutionMode = "manualConfirmation",
            UserControlMode = "userManaged",
            DefaultSyncFromDate = null,
            InactiveUserAction = "cleanupExternalData",
            RequireManualConfirmation = true,
            DryRunConfirmed = false,
            Notes = string.Empty,
        };

    public static CalendarSyncSettingsDto NormalizeSettings(
        CalendarSyncSettingsDto? settings,
        bool externalCalendarSyncEnabledFallback = false)
    {
        var source = settings ?? BuildDefaultSettings(externalCalendarSyncEnabledFallback);
        var syncMode = NormalizeKnownValue(
            source.SyncMode,
            "notConfigured",
            ["notConfigured", "crmMaster", "externalMaster", "hybrid"]);
        var conflictResolutionMode = NormalizeKnownValue(
            source.ConflictResolutionMode,
            "manualConfirmation",
            ["manualConfirmation", "crmWins", "externalWins", "latestWins"]);
        var userControlMode = NormalizeKnownValue(
            source.UserControlMode,
            "userManaged",
            ["userManaged", "adminManaged"]);
        var inactiveUserAction = NormalizeKnownValue(
            source.InactiveUserAction,
            "cleanupExternalData",
            ["cleanupExternalData", "disableSyncOnly", "keepConnection"]);
        var requireManualConfirmation = source.RequireManualConfirmation || conflictResolutionMode == "manualConfirmation";
        var legacyExternalCalendarSyncEnabled = source.ExternalCalendarSyncEnabled ?? externalCalendarSyncEnabledFallback;
        var googleCalendarSyncEnabled = source.GoogleCalendarSyncEnabled ?? legacyExternalCalendarSyncEnabled;
        var yandexCalendarSyncEnabled = source.YandexCalendarSyncEnabled ?? legacyExternalCalendarSyncEnabled;

        return new CalendarSyncSettingsDto
        {
            ExternalCalendarSyncEnabled = googleCalendarSyncEnabled || yandexCalendarSyncEnabled,
            GoogleCalendarSyncEnabled = googleCalendarSyncEnabled,
            YandexCalendarSyncEnabled = yandexCalendarSyncEnabled,
            GoogleSyncIntervalMinutes = NormalizeProviderInterval(source.GoogleSyncIntervalMinutes),
            YandexSyncIntervalMinutes = NormalizeProviderInterval(source.YandexSyncIntervalMinutes),
            SyncMode = syncMode,
            ConflictResolutionMode = conflictResolutionMode,
            UserControlMode = userControlMode,
            DefaultSyncFromDate = NormalizeSyncFromDate(source.DefaultSyncFromDate),
            InactiveUserAction = inactiveUserAction,
            RequireManualConfirmation = requireManualConfirmation,
            DryRunConfirmed = source.DryRunConfirmed,
            Notes = NormalizeNotes(source.Notes),
            UpdatedAt = source.UpdatedAt,
        };
    }

    public static int NormalizeProviderInterval(int value)
        => Math.Clamp(value <= 0 ? DefaultProviderSyncIntervalMinutes : value, MinProviderSyncIntervalMinutes, MaxProviderSyncIntervalMinutes);

    public static IReadOnlyList<CalendarSyncProviderDto> BuildProviders(bool externalCalendarSyncEnabled)
        => BuildProviders(externalCalendarSyncEnabled, externalCalendarSyncEnabled);

    public static IReadOnlyList<CalendarSyncProviderDto> BuildProviders(
        bool googleCalendarSyncEnabled,
        bool yandexCalendarSyncEnabled)
    {
        var googleStatus = BuildProviderStatus(googleCalendarSyncEnabled);
        var yandexStatus = BuildProviderStatus(yandexCalendarSyncEnabled);

        return
        [
            new CalendarSyncProviderDto
            {
                Key = "ics-export",
                Label = "ICS-файлы",
                Kind = "ics",
                Status = "active",
                IsAvailable = true,
                FeatureFlagEnabled = true,
                RequiresOAuth = false,
                SupportsManualExport = true,
                SupportsBackgroundSync = false,
                SupportsTwoWaySync = false,
                SupportedTargets = ["Календарь задач", "Календарь дел"],
                Message = "Односторонняя выгрузка ICS доступна из журнала задач и календаря дел. Выгрузки фиксируются в журнале действий.",
            },
            new CalendarSyncProviderDto
            {
                Key = "google-calendar",
                Label = "Google Calendar",
                Kind = "google",
                Status = googleStatus,
                IsAvailable = false,
                FeatureFlagEnabled = googleCalendarSyncEnabled,
                RequiresOAuth = true,
                SupportsManualExport = false,
                SupportsBackgroundSync = true,
                SupportsTwoWaySync = true,
                SupportedTargets = ["Календарь задач", "Календарь дел"],
                Message = BuildProviderMessage("Google Calendar", googleCalendarSyncEnabled),
            },
            new CalendarSyncProviderDto
            {
                Key = "yandex-calendar",
                Label = "Яндекс Календарь",
                Kind = "yandex",
                Status = yandexStatus,
                IsAvailable = false,
                FeatureFlagEnabled = yandexCalendarSyncEnabled,
                RequiresOAuth = true,
                SupportsManualExport = false,
                SupportsBackgroundSync = true,
                SupportsTwoWaySync = true,
                SupportedTargets = ["Календарь задач", "Календарь дел"],
                Message = BuildProviderMessage("Яндекс Календарь", yandexCalendarSyncEnabled),
            },
        ];
    }

    public static CalendarSyncReadinessDto BuildReadiness(
        bool externalCalendarSyncEnabled,
        CalendarSyncSettingsDto? settings = null)
        => BuildReadiness(externalCalendarSyncEnabled, externalCalendarSyncEnabled, settings);

    public static CalendarSyncReadinessDto BuildReadiness(
        bool googleCalendarSyncEnabled,
        bool yandexCalendarSyncEnabled,
        CalendarSyncSettingsDto? settings = null)
    {
        var normalizedSettings = NormalizeSettings(settings, googleCalendarSyncEnabled || yandexCalendarSyncEnabled);
        googleCalendarSyncEnabled = normalizedSettings.GoogleCalendarSyncEnabled ?? googleCalendarSyncEnabled;
        yandexCalendarSyncEnabled = normalizedSettings.YandexCalendarSyncEnabled ?? yandexCalendarSyncEnabled;
        var externalCalendarSyncEnabled = googleCalendarSyncEnabled || yandexCalendarSyncEnabled;
        var conflictPolicyReady = normalizedSettings.SyncMode != "notConfigured";
        var dryRunReady = normalizedSettings.DryRunConfirmed;
        var googleStatus = googleCalendarSyncEnabled ? "pending" : "disabled";
        var yandexStatus = yandexCalendarSyncEnabled ? "pending" : "disabled";
        var googleSeverity = googleCalendarSyncEnabled ? "warning" : "info";
        var yandexSeverity = yandexCalendarSyncEnabled ? "warning" : "info";

        var items = new List<CalendarSyncReadinessItemDto>
        {
            new()
            {
                Key = "ics-fallback",
                Label = "ICS-выгрузка",
                Status = "ready",
                IsReady = true,
                IsRequired = false,
                Severity = "success",
                Message = "Односторонняя выгрузка календаря задач и дел в ICS доступна и остается безопасным резервным вариантом.",
            },
            new()
            {
                Key = "google-admin-toggle",
                Label = "Google Calendar в админке",
                Status = googleCalendarSyncEnabled ? "ready" : "disabled",
                IsReady = googleCalendarSyncEnabled,
                IsRequired = googleCalendarSyncEnabled,
                Severity = googleCalendarSyncEnabled ? "success" : "info",
                Message = googleCalendarSyncEnabled
                    ? "Google Calendar включен администратором для диагностики и подготовки подключения."
                    : "Google Calendar выключен в админских настройках и не блокирует подключение других провайдеров.",
            },
            new()
            {
                Key = "yandex-admin-toggle",
                Label = "Яндекс Календарь в админке",
                Status = yandexCalendarSyncEnabled ? "ready" : "disabled",
                IsReady = yandexCalendarSyncEnabled,
                IsRequired = yandexCalendarSyncEnabled,
                Severity = yandexCalendarSyncEnabled ? "success" : "info",
                Message = yandexCalendarSyncEnabled
                    ? "Яндекс Календарь включен администратором для диагностики и подготовки подключения."
                    : "Яндекс Календарь выключен в админских настройках и не блокирует подключение других провайдеров.",
            },
            new()
            {
                Key = "google-oauth",
                Label = "Google Calendar OAuth",
                Status = googleStatus,
                IsReady = false,
                IsRequired = googleCalendarSyncEnabled,
                Severity = googleSeverity,
                Message = googleCalendarSyncEnabled
                    ? "Google Calendar включен, но перед подключением нужно утвердить OAuth-приложение, redirect URI и набор разрешений."
                    : "Проверка OAuth Google Calendar пропущена, пока провайдер выключен в админке.",
            },
            new()
            {
                Key = "yandex-oauth",
                Label = "Яндекс Календарь OAuth",
                Status = yandexStatus,
                IsReady = false,
                IsRequired = yandexCalendarSyncEnabled,
                Severity = yandexSeverity,
                Message = yandexCalendarSyncEnabled
                    ? "Яндекс Календарь включен, но перед подключением нужно утвердить OAuth-приложение, redirect URI и набор разрешений."
                    : "Проверка OAuth Яндекс Календаря пропущена, пока провайдер выключен в админке.",
            },
            new()
            {
                Key = "conflict-policy",
                Label = "Правила конфликтов",
                Status = conflictPolicyReady ? "ready" : "pending",
                IsReady = conflictPolicyReady,
                IsRequired = externalCalendarSyncEnabled,
                Severity = conflictPolicyReady ? "success" : "warning",
                Message = conflictPolicyReady
                    ? BuildConflictPolicyReadyMessage(normalizedSettings)
                    : "Нужно зафиксировать модель: CRM главная, внешний календарь главный или гибрид с ручным подтверждением спорных изменений.",
            },
            new()
            {
                Key = "dry-run",
                Label = "Dry-run на копии базы",
                Status = dryRunReady ? "ready" : "pending",
                IsReady = dryRunReady,
                IsRequired = externalCalendarSyncEnabled,
                Severity = dryRunReady ? "success" : "warning",
                Message = dryRunReady
                    ? "Dry-run на копии базы отмечен как выполненный. Перед боевым запуском все равно проверьте OAuth и ограничения доступа."
                    : "Перед включением фоновой синхронизации нужно проверить выгрузку, обновления и возможные конфликты на копии локальной базы.",
            },
        };

        return new CalendarSyncReadinessDto
        {
            GeneratedAt = DateTime.UtcNow,
            ExternalCalendarSyncEnabled = externalCalendarSyncEnabled,
            GoogleCalendarSyncEnabled = googleCalendarSyncEnabled,
            YandexCalendarSyncEnabled = yandexCalendarSyncEnabled,
            ReadyToConnect = externalCalendarSyncEnabled && items.Where(item => item.IsRequired).All(item => item.IsReady),
            Items = items,
        };
    }

    private static string BuildProviderStatus(bool enabled)
        => enabled ? "requires-setup" : "disabled";

    private static string BuildProviderMessage(string providerName, bool enabled)
        => enabled
            ? $"{providerName} включен администратором. Перед запуском нужна настройка OAuth, dry-run и подтверждение модели конфликтов."
            : $"{providerName} выключен в админских настройках.";

    private static string NormalizeKnownValue(string? value, string fallback, IReadOnlyCollection<string> allowedValues)
    {
        var normalized = (value ?? string.Empty).Trim();
        return allowedValues.Contains(normalized, StringComparer.Ordinal) ? normalized : fallback;
    }

    private static string NormalizeNotes(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static DateTime? NormalizeSyncFromDate(DateTime? value)
        => value?.Date;

    private static string BuildConflictPolicyReadyMessage(CalendarSyncSettingsDto settings)
    {
        var syncMode = settings.SyncMode switch
        {
            "crmMaster" => "CRM считается главным источником событий",
            "externalMaster" => "внешний календарь считается главным источником событий",
            "hybrid" => "используется гибридная модель синхронизации",
            _ => "модель синхронизации зафиксирована",
        };
        var confirmation = settings.RequireManualConfirmation
            ? "спорные изменения требуют ручного подтверждения"
            : "спорные изменения будут обрабатываться по выбранному правилу";

        return $"{syncMode}, {confirmation}.";
    }
}
