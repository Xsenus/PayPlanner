using System.Security.Claims;
using System.Text.Json;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public static class CalendarExportAudit
{
    public const string ObjectType = "calendar-sync";
    public const string ExportedAction = "Exported";
    public const string SettingsUpdatedAction = "Updated";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ObjectAuditEvent CreateExportEvent(
        ClaimsPrincipal user,
        string calendarKind,
        string description,
        int exportedEventCount,
        IReadOnlyList<int> reminderMinutes,
        object filters)
        => new()
        {
            ObjectType = ObjectType,
            ObjectId = 0,
            Action = ExportedAction,
            Description = description,
            ActorUserId = user.GetUserId(),
            ActorUserName = user.GetDisplayName(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                calendarKind,
                exportedEventCount,
                reminderMinutes,
                filters,
            }, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        };

    public static ObjectAuditEvent CreateSettingsUpdatedEvent(
        ClaimsPrincipal user,
        CalendarSyncSettingsDto previousSettings,
        CalendarSyncSettingsDto nextSettings)
    {
        var changes = BuildSettingsChanges(previousSettings, nextSettings);

        return new ObjectAuditEvent
        {
            ObjectType = ObjectType,
            ObjectId = 0,
            Action = SettingsUpdatedAction,
            Description = BuildSettingsUpdatedDescription(nextSettings),
            ActorUserId = user.GetUserId(),
            ActorUserName = user.GetDisplayName(),
            MetadataJson = JsonSerializer.Serialize(new
            {
                changedFields = changes.Select(item => item.Field).ToArray(),
                changes,
                settings = new
                {
                    nextSettings.ExternalCalendarSyncEnabled,
                    nextSettings.GoogleCalendarSyncEnabled,
                    nextSettings.YandexCalendarSyncEnabled,
                    nextSettings.GoogleSyncIntervalMinutes,
                    nextSettings.YandexSyncIntervalMinutes,
                    nextSettings.ManualFullSyncCooldownMinutes,
                    nextSettings.SyncMode,
                    nextSettings.ConflictResolutionMode,
                    nextSettings.UserControlMode,
                    nextSettings.DefaultSyncFromDate,
                    nextSettings.InactiveUserAction,
                    nextSettings.RequireManualConfirmation,
                    nextSettings.DryRunConfirmed,
                    nextSettings.Notes,
                },
            }, JsonOptions),
            CreatedAt = DateTime.UtcNow,
        };
    }

    private static IReadOnlyList<CalendarSyncSettingsChange> BuildSettingsChanges(
        CalendarSyncSettingsDto previousSettings,
        CalendarSyncSettingsDto nextSettings)
    {
        var changes = new List<CalendarSyncSettingsChange>();

        AddIfChanged(changes, "ExternalCalendarSyncEnabled", previousSettings.ExternalCalendarSyncEnabled?.ToString(), nextSettings.ExternalCalendarSyncEnabled?.ToString());
        AddIfChanged(changes, "GoogleCalendarSyncEnabled", previousSettings.GoogleCalendarSyncEnabled?.ToString(), nextSettings.GoogleCalendarSyncEnabled?.ToString());
        AddIfChanged(changes, "YandexCalendarSyncEnabled", previousSettings.YandexCalendarSyncEnabled?.ToString(), nextSettings.YandexCalendarSyncEnabled?.ToString());
        AddIfChanged(changes, "GoogleSyncIntervalMinutes", previousSettings.GoogleSyncIntervalMinutes.ToString(), nextSettings.GoogleSyncIntervalMinutes.ToString());
        AddIfChanged(changes, "YandexSyncIntervalMinutes", previousSettings.YandexSyncIntervalMinutes.ToString(), nextSettings.YandexSyncIntervalMinutes.ToString());
        AddIfChanged(changes, "ManualFullSyncCooldownMinutes", previousSettings.ManualFullSyncCooldownMinutes.ToString(), nextSettings.ManualFullSyncCooldownMinutes.ToString());
        AddIfChanged(changes, "SyncMode", previousSettings.SyncMode, nextSettings.SyncMode);
        AddIfChanged(changes, "ConflictResolutionMode", previousSettings.ConflictResolutionMode, nextSettings.ConflictResolutionMode);
        AddIfChanged(changes, "UserControlMode", previousSettings.UserControlMode, nextSettings.UserControlMode);
        AddIfChanged(changes, "DefaultSyncFromDate", previousSettings.DefaultSyncFromDate?.ToString("O"), nextSettings.DefaultSyncFromDate?.ToString("O"));
        AddIfChanged(changes, "InactiveUserAction", previousSettings.InactiveUserAction, nextSettings.InactiveUserAction);
        AddIfChanged(changes, "RequireManualConfirmation", previousSettings.RequireManualConfirmation.ToString(), nextSettings.RequireManualConfirmation.ToString());
        AddIfChanged(changes, "DryRunConfirmed", previousSettings.DryRunConfirmed.ToString(), nextSettings.DryRunConfirmed.ToString());
        AddIfChanged(changes, "Notes", previousSettings.Notes, nextSettings.Notes);

        return changes;
    }

    private static void AddIfChanged(
        List<CalendarSyncSettingsChange> changes,
        string field,
        string? oldValue,
        string? newValue)
    {
        var normalizedOldValue = oldValue ?? string.Empty;
        var normalizedNewValue = newValue ?? string.Empty;
        if (string.Equals(normalizedOldValue, normalizedNewValue, StringComparison.Ordinal))
        {
            return;
        }

        changes.Add(new CalendarSyncSettingsChange(field, normalizedOldValue, normalizedNewValue));
    }

    private static string BuildSettingsUpdatedDescription(CalendarSyncSettingsDto settings)
    {
        var syncMode = settings.SyncMode switch
        {
            "crmMaster" => "CRM главная",
            "externalMaster" => "внешний календарь главный",
            "hybrid" => "гибридная модель",
            _ => "модель не выбрана",
        };
        var conflictMode = settings.ConflictResolutionMode switch
        {
            "crmWins" => "оставлять версию CRM",
            "externalWins" => "принимать внешний календарь",
            "latestWins" => "брать последнее изменение",
            _ => "ручное подтверждение",
        };

        return $"Обновлены правила внешних календарей: {syncMode}, спорные изменения - {conflictMode}, повторная полная синхронизация - через {settings.ManualFullSyncCooldownMinutes} мин.";
    }

    private sealed record CalendarSyncSettingsChange(string Field, string OldValue, string NewValue);
}
