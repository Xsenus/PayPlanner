using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.UserActivity;

public record ActivityLogEntry
{
    public int? UserId { get; init; }
    public string? UserEmail { get; init; }
    public string? UserFullName { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string? Section { get; init; }
    public string? ObjectType { get; init; }
    public string? ObjectId { get; init; }
    public string? Description { get; init; }
    public UserActivityStatus Status { get; init; } = UserActivityStatus.Info;
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? HttpMethod { get; init; }
    public string? Path { get; init; }
    public string? QueryString { get; init; }
    public int? HttpStatusCode { get; init; }
    public long? DurationMs { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
    public DateTime? CreatedAtUtc { get; init; }
}

public interface IUserActivityService
{
    Task<UserActivityLog?> TryWriteAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default);
}
