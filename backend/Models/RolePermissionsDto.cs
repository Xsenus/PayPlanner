namespace PayPlanner.Api.Models;

public class MenuSectionPermissionsDto
{
    public bool CanView { get; set; }
    public bool CanCreate { get; set; }
    public bool CanEdit { get; set; }
    public bool CanDelete { get; set; }
    public bool CanExport { get; set; }
    public string ViewScope { get; set; } = DataVisibilityScope.All;
}

public class CalendarPermissionsDto : MenuSectionPermissionsDto
{
    public bool CanViewAnalytics { get; set; }
    public List<int>? VisibleUserIds { get; set; }
}

public class TaskSectionPermissionsDto : MenuSectionPermissionsDto
{
    public bool CanEditOthers { get; set; }
    public bool CanCompleteOthers { get; set; }
    public bool CanDeleteOthers { get; set; }
}

public class RolePermissionsDto
{
    public CalendarPermissionsDto Calendar { get; set; } = new();
    public TaskSectionPermissionsDto Tasks { get; set; } = new();
    public MenuSectionPermissionsDto Notes { get; set; } = new();
    public MenuSectionPermissionsDto Reports { get; set; } = new();
    public MenuSectionPermissionsDto Calculator { get; set; } = new();
    public MenuSectionPermissionsDto Clients { get; set; } = new();
    public MenuSectionPermissionsDto Cases { get; set; } = new();
    public MenuSectionPermissionsDto Requests { get; set; } = new();
    public MenuSectionPermissionsDto LegalEntities { get; set; } = new();
    public MenuSectionPermissionsDto Accounts { get; set; } = new();
    public MenuSectionPermissionsDto Acts { get; set; } = new();
    public MenuSectionPermissionsDto Payments { get; set; } = new();
    public MenuSectionPermissionsDto Contracts { get; set; } = new();
    public MenuSectionPermissionsDto Dictionaries { get; set; } = new();
    public MenuSectionPermissionsDto Notifications { get; set; } = new();
    public MenuSectionPermissionsDto Roles { get; set; } = new();
    public MenuSectionPermissionsDto Audit { get; set; } = new();
    public MenuSectionPermissionsDto Integrations { get; set; } = new();
    public MenuSectionPermissionsDto YandexDisk { get; set; } = new();
    public MenuSectionPermissionsDto S3Storage { get; set; } = new();
}
