namespace PayPlanner.Api.Models.Responses;

public sealed class RolePermissionAuditReportDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public int TotalRoles { get; set; }
    public int ReadyRoles { get; set; }
    public int RolesWithIssues { get; set; }
    public int RolesWithBroadDataScope { get; set; }
    public IReadOnlyList<string> CatalogSections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> DataScopedSections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<RolePermissionAuditRoleDto> Roles { get; set; } = Array.Empty<RolePermissionAuditRoleDto>();
}

public sealed class RolePermissionAuditRoleDto
{
    public int RoleId { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public bool IsSystemRole { get; set; }
    public int AssignedUsersCount { get; set; }
    public int ConfiguredSectionsCount { get; set; }
    public int TotalSectionsCount { get; set; }
    public bool HasIssues { get; set; }
    public IReadOnlyList<string> MissingSections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> UnknownSections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> DuplicateSections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> InvalidViewScopeSections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> CapabilityWithoutViewSections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> RestrictedCapabilitySections { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> BroadDataScopeSections { get; set; } = Array.Empty<string>();
}
