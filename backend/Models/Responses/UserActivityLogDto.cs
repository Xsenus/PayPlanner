using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class UserActivityLogDto
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string? UserFullName { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Section { get; set; }
    public string? ObjectType { get; set; }
    public string? ObjectId { get; set; }
    public string? Description { get; set; }
    public UserActivityStatus Status { get; set; } = UserActivityStatus.Info;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? HttpMethod { get; set; }
    public string? Path { get; set; }
    public string? QueryString { get; set; }
    public int? HttpStatusCode { get; set; }
    public long? DurationMs { get; set; }
    public object? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UserActivityFilterOptionsDto
{
    public IReadOnlyList<UserActivityStatus> Statuses { get; init; } = Array.Empty<UserActivityStatus>();
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Sections { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HttpMethods { get; init; } = Array.Empty<string>();
    public IReadOnlyList<UserActivityActorDto> Actors { get; init; } = Array.Empty<UserActivityActorDto>();
}

public class UserActivityActorDto
{
    public int Id { get; init; }
    public string? FullName { get; init; }
    public string? Email { get; init; }
}
