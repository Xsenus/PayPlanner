namespace PayPlanner.Api.Services;

public sealed class ExternalCalendarSyncCoordinator : IExternalCalendarSyncCoordinator
{
    public const string CrmProvider = "crm";
    public const string GoogleProvider = "google-calendar";
    public const string YandexProvider = "yandex-calendar";

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
                    taskId,
                    () => google.SyncTaskForConnectedUsersAsync(taskId, cancellationToken));
            }
        }

        if (!string.Equals(sourceProvider, YandexProvider, StringComparison.Ordinal))
        {
            var yandex = _serviceProvider.GetService<IYandexCalendarSyncService>();
            if (yandex is not null)
            {
                await RunProviderAsync(
                    "Яндекс Календарь",
                    taskId,
                    () => yandex.SyncTaskForConnectedUsersAsync(taskId, cancellationToken));
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
            throw new InvalidOperationException($"ExternalCalendarCleanupFailed: {string.Join("; ", failures)}");
        }
    }

    private async Task RunProviderAsync(string providerName, int taskId, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{ProviderName} task propagation failed for task {TaskId}", providerName, taskId);
        }
    }
}
