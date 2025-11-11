namespace PayPlanner.Api.Models;

public class MenuSectionPermissionsDto
{
    public bool CanView { get; set; }
    public bool CanCreate { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public bool CanExport { get; set; }
}

public class CalendarPermissionsDto : MenuSectionPermissionsDto
{
    public bool CanViewAnalytics { get; set; }
}

public class RolePermissionsDto
{
    public CalendarPermissionsDto Calendar { get; set; } = new();
    public MenuSectionPermissionsDto Reports { get; set; } = new();
    public MenuSectionPermissionsDto Calculator { get; set; } = new();
    public MenuSectionPermissionsDto Clients { get; set; } = new();
    public MenuSectionPermissionsDto LegalEntities { get; set; } = new();
    public MenuSectionPermissionsDto Accounts { get; set; } = new();
    public MenuSectionPermissionsDto Acts { get; set; } = new();
    public MenuSectionPermissionsDto Contracts { get; set; } = new();
    public MenuSectionPermissionsDto Dictionaries { get; set; } = new();
}
