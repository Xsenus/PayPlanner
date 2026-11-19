using System.Text.Json.Serialization;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Models.Responses;

public class ContractExpirationRecipientSettingsDto
{
    public bool DepartmentManagersEnabled { get; set; }
    public int[] RoleIds { get; set; } = [];
    public int[] UserIds { get; set; } = [];
    public List<ContractExpirationDepartmentManager> DepartmentManagers { get; set; } = [];
    public IReadOnlyList<ContractExpirationRecipientOptionDto> SelectedRoles { get; set; } = [];
    public IReadOnlyList<ContractExpirationRecipientOptionDto> SelectedUsers { get; set; } = [];
    public IReadOnlyList<ContractExpirationRecipientOptionDto> SelectedDepartments { get; set; } = [];
}

public class ContractExpirationRecipientOptionDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DepartmentId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DepartmentName { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RoleId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleName { get; set; }
}

public class ContractExpirationRecipientOptionsPageDto
{
    public IReadOnlyList<ContractExpirationRecipientOptionDto> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
}
