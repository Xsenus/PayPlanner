using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public interface IYandexCalendarSyncService
{
    Task<YandexCalendarStatusDto> GetStatusAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default);
    Task<YandexCalendarStartAuthResponseDto> StartAuthAsync(int userId, StartYandexCalendarAuthRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default);
    Task<YandexCalendarAuthStatusDto> GetAuthStatusAsync(int userId, string state, CancellationToken cancellationToken = default);
    Task<string> HandleCallbackAsync(string? state, string? code, string? error, string? errorDescription, HttpRequest request, CancellationToken cancellationToken = default);
    Task<YandexCalendarStatusDto> UpdateSettingsAsync(int userId, UpdateYandexCalendarSettingsRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default);
    Task<YandexCalendarStatusDto> DisconnectAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default);
    Task<YandexCalendarSyncNowResultDto> SyncNowAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default);
    Task<YandexCalendarSchedulerRunResult> SyncDueConnectionsAsync(int batchSize, CancellationToken cancellationToken = default);
    Task SyncTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default);
    Task DeleteTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default);
    Task CleanupDeletedUserAsync(int userId, int actorUserId, CancellationToken cancellationToken = default);
}

public sealed record YandexCalendarSchedulerRunResult(int ProcessedConnections, int ProcessedTasks, int ImportedEvents, int ErrorCount);
