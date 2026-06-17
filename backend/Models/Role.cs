namespace PayPlanner.Api.Models;

public class Role
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PermissionsUpdatedAt { get; set; }
    public int? PermissionsUpdatedByUserId { get; set; }
    public string? PermissionsUpdatedByUserName { get; set; }

    public ICollection<User> Users { get; set; } = new List<User>();
    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
    public ICollection<RoleCalendarVisibleUser> CalendarVisibleUsers { get; set; } = new List<RoleCalendarVisibleUser>();
}
