using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public static class CalendarSyncProviderRegistry
{
    public static CalendarSyncSettingsDto BuildDefaultSettings()
        => new()
        {
            SyncMode = "notConfigured",
            ConflictResolutionMode = "manualConfirmation",
            RequireManualConfirmation = true,
            DryRunConfirmed = false,
            Notes = string.Empty,
        };

    public static CalendarSyncSettingsDto NormalizeSettings(CalendarSyncSettingsDto? settings)
    {
        var source = settings ?? BuildDefaultSettings();
        var syncMode = NormalizeKnownValue(
            source.SyncMode,
            "notConfigured",
            ["notConfigured", "crmMaster", "externalMaster", "hybrid"]);
        var conflictResolutionMode = NormalizeKnownValue(
            source.ConflictResolutionMode,
            "manualConfirmation",
            ["manualConfirmation", "crmWins", "externalWins", "latestWins"]);
        var requireManualConfirmation = source.RequireManualConfirmation || conflictResolutionMode == "manualConfirmation";

        return new CalendarSyncSettingsDto
        {
            SyncMode = syncMode,
            ConflictResolutionMode = conflictResolutionMode,
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
            ? "Feature flag включен. Перед запуском нужна настройка OAuth, dry-run и подтверждение модели конфликтов."
            : "Интеграция с внешним календарем выключена до утверждения модели синхронизации и настройки OAuth.";

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
                Label = "Feature flag внешних календарей",
                Status = externalCalendarSyncEnabled ? "ready" : "disabled",
                IsReady = externalCalendarSyncEnabled,
                IsRequired = true,
                Severity = externalCalendarSyncEnabled ? "success" : "warning",
                Message = externalCalendarSyncEnabled
                    ? "Внешний календарный контур включен для диагностики и подготовки подключения."
                    : "Google Calendar и Яндекс Календарь остаются выключенными до отдельного решения администратора.",
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
