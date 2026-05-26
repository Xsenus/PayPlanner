using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public static class CalendarSyncProviderRegistry
{
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
}
