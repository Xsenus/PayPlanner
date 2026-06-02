namespace PayPlanner.Api.Services;

public interface IExternalCalendarSyncCoordinator
{
    Task SyncTaskForConnectedUsersAsync(int taskId, string sourceProvider = "crm", CancellationToken cancellationToken = default);
    Task DeleteTaskForConnectedUsersAsync(int taskId, string sourceProvider = "crm", CancellationToken cancellationToken = default);
    Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default);
    Task HandleUserAccessRevokedAsync(int userId, int actorUserId, CancellationToken cancellationToken = default);
}
