namespace PayPlanner.Api.Models.Auth;

public class UserProfileDto
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? Patronymic { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public DateTime? HireDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public List<UserContactDto> Contacts { get; set; } = new();
    public List<UserSocialProfileDto> SocialProfiles { get; set; } = new();
}

public class UserContactDto
{
    public int Id { get; set; }
    public string ContactType { get; set; } = string.Empty;
    public string ContactValue { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
}

public class UserSocialProfileDto
{
    public int Id { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
}

public class CreateUserProfileRequest
{
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string? Patronymic { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public DateTime? HireDate { get; set; }
    public DateTime? TerminationDate { get; set; }
    public List<UserContactDto> Contacts { get; set; } = new();
    public List<UserSocialProfileDto> SocialProfiles { get; set; } = new();
}

public class ActivityLogDto
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string UserFullName { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime Timestamp { get; set; }
}

public class ActivityLogFilterRequest
{
    public string? UserId { get; set; }
    public string? ActionType { get; set; }
    public string? Section { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
