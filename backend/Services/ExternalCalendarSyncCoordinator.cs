using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services;

public sealed class ExternalCalendarSyncCoordinator : IExternalCalendarSyncCoordinator
{
    public const string CrmProvider = "crm";
    public const string GoogleProvider = "google-calendar";
    public const string YandexProvider = "yandex-calendar";
    private const string CalendarSyncSettingsKey = "calendarSync.settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ExternalCalendarSyncCoordinator> _logger;

    public ExternalCalendarSyncCoordinator(
        IServiceProvider serviceProvider,
        ILogger<ExternalCalendarSyncCoordinator> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task SyncTaskForConnectedUsersAsync(int taskId, string sourceProvider = CrmProvider, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(sourceProvider, GoogleProvider, StringComparison.Ordinal))
        {
            var google = _serviceProvider.GetService<IGoogleCalendarSyncService>();
            if (google is not null)
            {
                await RunProviderAsync(
                    "Google Calendar",
                    "Синхронизация задачи",
                    taskId,
                    () => google.SyncTaskForConnectedUsersAsync(taskId, cancellationToken),
                    cancellationToken);
            }
        }

        if (!string.Equals(sourceProvider, YandexProvider, StringComparison.Ordinal))
        {
            var yandex = _serviceProvider.GetService<IYandexCalendarSyncService>();
            if (yandex is not null)
            {
                await RunProviderAsync(
                    "Яндекс Календарь",
                    "Синхронизация задачи",
                    taskId,
                    () => yandex.SyncTaskForConnectedUsersAsync(taskId, cancellationToken),
                    cancellationToken);
            }
        }
    }

    public async Task DeleteTaskForConnectedUsersAsync(int taskId, string sourceProvider = CrmProvider, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();

        if (!string.Equals(sourceProvider, GoogleProvider, StringComparison.Ordinal))
        {
            var google = _serviceProvider.GetService<IGoogleCalendarSyncService>();
            if (google is not null)
            {
                try
                {
                    await google.DeleteTaskForConnectedUsersAsync(taskId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"Google Calendar: {ex.Message}");
                    _logger.LogWarning(ex, "Google Calendar task deletion failed for task {TaskId}", taskId);
                }
            }
        }

        if (!string.Equals(sourceProvider, YandexProvider, StringComparison.Ordinal))
        {
            var yandex = _serviceProvider.GetService<IYandexCalendarSyncService>();
            if (yandex is not null)
            {
                try
                {
                    await yandex.DeleteTaskForConnectedUsersAsync(taskId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures.Add($"Яндекс.Календарь: {ex.Message}");
                    _logger.LogWarning(ex, "Yandex Calendar task deletion failed for task {TaskId}", taskId);
                }
            }
        }

        if (failures.Count > 0)
        {
            await NotifyCalendarWarningAsync(
                "Внешние календари",
                "Удаление задачи",
                $"Не удалось удалить задачу #{taskId} из одного или нескольких внешних календарей.",
                string.Join("; ", failures),
                cancellationToken);
            throw new InvalidOperationException($"ExternalCalendarTaskDeleteFailed: {string.Join("; ", failures)}");
        }
    }

    public async Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();

        var google = _serviceProvider.GetService<IGoogleCalendarSyncService>();
        if (google is not null)
        {
            try
            {
                await google.CleanupDeletedUserAsync(userId, actorUserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"Google Calendar: {ex.Message}");
                _logger.LogWarning(ex, "Google Calendar cleanup failed for deleted user {UserId}", userId);
            }
        }

        var yandex = _serviceProvider.GetService<IYandexCalendarSyncService>();
        if (yandex is not null)
        {
            try
            {
                await yandex.CleanupDeletedUserAsync(userId, actorUserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"Яндекс.Календарь: {ex.Message}");
                _logger.LogWarning(ex, "Yandex Calendar cleanup failed for deleted user {UserId}", userId);
            }
        }

        if (failures.Count > 0)
        {
            await NotifyCalendarWarningAsync(
                "Внешние календари",
                "Очистка данных пользователя",
                $"Не удалось полностью очистить внешние календари пользователя #{userId}.",
                string.Join("; ", failures),
                cancellationToken);
            throw new InvalidOperationException($"ExternalCalendarCleanupFailed: {string.Join("; ", failures)}");
        }
    }

    public async Task HandleUserAccessRevokedAsync(int userId, int actorUserId, CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        switch (settings.InactiveUserAction)
        {
            case "cleanupExternalData":
                try
                {
                    await CleanupDeletedUserAsync(userId, actorUserId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "External calendar cleanup failed after access revocation for user {UserId}. Sync will be disabled instead.",
                        userId);
                    await DisableUserSyncAsync(userId, cancellationToken);
                }
                break;
            case "disableSyncOnly":
                await DisableUserSyncAsync(userId, cancellationToken);
                break;
            case "keepConnection":
                _logger.LogInformation("Calendar sync access revocation policy keeps connection for user {UserId}", userId);
                break;
        }
    }

    private async Task RunProviderAsync(
        string providerName,
        string operation,
        int taskId,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{ProviderName} task propagation failed for task {TaskId}", providerName, taskId);
            await NotifyCalendarWarningAsync(
                providerName,
                operation,
                $"Не удалось синхронизировать задачу #{taskId} во внешний календарь.",
                ex.Message,
                cancellationToken);
        }
    }

    private async Task NotifyCalendarWarningAsync(
        string providerName,
        string operation,
        string summary,
        string? details,
        CancellationToken cancellationToken)
    {
        var notificationService = _serviceProvider.GetService<INotificationService>();
        if (notificationService is null)
        {
            return;
        }

        try
        {
            await notificationService.NotifyExternalCalendarSyncWarningAsync(
                providerName,
                operation,
                summary,
                details,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "External calendar admin warning notification failed.");
        }
    }

    private async Task<CalendarSyncSettingsDto> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var db = _serviceProvider.GetService<PaymentContext>();
        if (db is null)
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings();
        }

        var setting = await db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Key == CalendarSyncSettingsKey, cancellationToken);
        if (setting is null || string.IsNullOrWhiteSpace(setting.Value))
        {
            return CalendarSyncProviderRegistry.BuildDefaultSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CalendarSyncSettingsDto>(setting.Value, JsonOptions);
            if (settings is not null)
            {
                return CalendarSyncProviderRegistry.NormalizeSettings(settings);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Stored calendar sync settings are invalid. Defaults will be used for access revocation policy.");
        }

        return CalendarSyncProviderRegistry.BuildDefaultSettings();
    }

    private async Task DisableUserSyncAsync(int userId, CancellationToken cancellationToken)
    {
        var db = _serviceProvider.GetService<PaymentContext>();
        if (db is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var googleConnections = await db.GoogleCalendarConnections
            .Where(item => item.UserId == userId)
            .ToListAsync(cancellationToken);
        foreach (var connection in googleConnections)
        {
            connection.IsEnabled = false;
            connection.SyncEnabled = false;
            connection.UpdatedAt = now;
            db.GoogleCalendarSyncLogs.Add(new()
            {
                ConnectionId = connection.Id,
                Direction = "system",
                Operation = "disable-sync",
                Status = "success",
                Message = "Синхронизация отключена из-за потери доступа сотрудника.",
                CreatedAt = now,
            });
        }

        var yandexConnections = await db.YandexCalendarConnections
            .Where(item => item.UserId == userId)
            .ToListAsync(cancellationToken);
        foreach (var connection in yandexConnections)
        {
            connection.IsEnabled = false;
            connection.SyncEnabled = false;
            connection.UpdatedAt = now;
            db.YandexCalendarSyncLogs.Add(new()
            {
                ConnectionId = connection.Id,
                Direction = "system",
                Operation = "disable-sync",
                Status = "success",
                Message = "Синхронизация отключена из-за потери доступа сотрудника.",
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
