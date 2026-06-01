using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public interface IGoogleCalendarSyncService
{
    Task<GoogleCalendarStatusDto> GetStatusAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default);
    Task<GoogleCalendarStartAuthResponseDto> StartAuthAsync(int userId, StartGoogleCalendarAuthRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default);
    Task<GoogleCalendarAuthStatusDto> GetAuthStatusAsync(int userId, string state, CancellationToken cancellationToken = default);
    Task<string> HandleCallbackAsync(string? state, string? code, string? error, string? errorDescription, HttpRequest request, CancellationToken cancellationToken = default);
    Task<GoogleCalendarStatusDto> UpdateSettingsAsync(int userId, UpdateGoogleCalendarSettingsRequest request, HttpRequest httpRequest, CancellationToken cancellationToken = default);
    Task<GoogleCalendarStatusDto> DisconnectAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default);
    Task<GoogleCalendarSyncNowResultDto> SyncNowAsync(int userId, HttpRequest request, CancellationToken cancellationToken = default);
    Task<GoogleCalendarSchedulerRunResult> SyncDueConnectionsAsync(int batchSize, CancellationToken cancellationToken = default);
    Task SyncTaskForConnectedUsersAsync(int taskId, CancellationToken cancellationToken = default);
    Task HandleWebhookAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

public sealed record GoogleCalendarSchedulerRunResult(int ProcessedConnections, int ProcessedTasks, int ImportedEvents, int ErrorCount);
