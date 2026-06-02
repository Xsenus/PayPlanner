using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public static class CalendarSyncProviderRegistry
{
    public static CalendarSyncSettingsDto BuildDefaultSettings(bool externalCalendarSyncEnabled = false)
        => new()
        {
            ExternalCalendarSyncEnabled = externalCalendarSyncEnabled,
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

        return new CalendarSyncSettingsDto
        {
            ExternalCalendarSyncEnabled = source.ExternalCalendarSyncEnabled ?? externalCalendarSyncEnabledFallback,
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

    public static IReadOnlyList<CalendarSyncProviderDto> BuildProviders(bool externalCalendarSyncEnabled)
    {
        var externalStatus = externalCalendarSyncEnabled ? "requires-setup" : "disabled";
        var externalMessage = externalCalendarSyncEnabled
            ? "Контур включен администратором. Перед запуском нужна настройка OAuth, dry-run и подтверждение модели конфликтов."
            : "Интеграция с внешним календарем выключена в админских настройках.";

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
                Status = externalStatus,
                IsAvailable = false,
                FeatureFlagEnabled = externalCalendarSyncEnabled,
                RequiresOAuth = true,
                SupportsManualExport = false,
                SupportsBackgroundSync = true,
                SupportsTwoWaySync = true,
                SupportedTargets = ["Календарь задач", "Календарь дел"],
                Message = externalMessage,
            },
            new CalendarSyncProviderDto
            {
                Key = "yandex-calendar",
                Label = "Яндекс Календарь",
                Kind = "yandex",
                Status = externalStatus,
                IsAvailable = false,
                FeatureFlagEnabled = externalCalendarSyncEnabled,
                RequiresOAuth = true,
                SupportsManualExport = false,
                SupportsBackgroundSync = true,
                SupportsTwoWaySync = true,
                SupportedTargets = ["Календарь задач", "Календарь дел"],
                Message = externalMessage,
            },
        ];
    }

    public static CalendarSyncReadinessDto BuildReadiness(
        bool externalCalendarSyncEnabled,
        CalendarSyncSettingsDto? settings = null)
    {
        var normalizedSettings = NormalizeSettings(settings);
        var conflictPolicyReady = normalizedSettings.SyncMode != "notConfigured";
        var dryRunReady = normalizedSettings.DryRunConfirmed;
        var externalBlockedStatus = externalCalendarSyncEnabled ? "pending" : "disabled";
        var externalBlockedSeverity = externalCalendarSyncEnabled ? "warning" : "info";
        var externalBlockedPrefix = externalCalendarSyncEnabled
            ? "Внешний календарный контур включен, но перед подключением нужно "
            : "Внешний календарный контур выключен. Перед подключением нужно ";

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
                Key = "feature-flag",
                Label = "Внешние календари в админке",
                Status = externalCalendarSyncEnabled ? "ready" : "disabled",
                IsReady = externalCalendarSyncEnabled,
                IsRequired = true,
                Severity = externalCalendarSyncEnabled ? "success" : "warning",
                Message = externalCalendarSyncEnabled
                    ? "Внешний календарный контур включен для диагностики и подготовки подключения."
                    : "Google Calendar и Яндекс Календарь остаются выключенными, пока администратор не включит внешний календарный контур.",
            },
            new()
            {
                Key = "google-oauth",
                Label = "Google Calendar OAuth",
                Status = externalBlockedStatus,
                IsReady = false,
                IsRequired = true,
                Severity = externalBlockedSeverity,
                Message = externalBlockedPrefix + "утвердить OAuth-приложение Google Calendar, redirect URI и набор разрешений.",
            },
            new()
            {
                Key = "yandex-oauth",
                Label = "Яндекс Календарь OAuth",
                Status = externalBlockedStatus,
                IsReady = false,
                IsRequired = true,
                Severity = externalBlockedSeverity,
                Message = externalBlockedPrefix + "утвердить OAuth-приложение Яндекс Календаря, redirect URI и набор разрешений.",
            },
            new()
            {
                Key = "conflict-policy",
                Label = "Правила конфликтов",
                Status = conflictPolicyReady ? "ready" : "pending",
                IsReady = conflictPolicyReady,
                IsRequired = true,
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
                IsRequired = true,
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
            ReadyToConnect = items.Where(item => item.IsRequired).All(item => item.IsReady),
            Items = items,
        };
    }

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
