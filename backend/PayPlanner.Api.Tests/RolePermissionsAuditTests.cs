using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class RolePermissionsAuditTests
{
    [Fact]
    public async Task GetAudit_ReturnsReadyRole_WhenCatalogPermissionsAreComplete()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "Legal team" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        db.Users.Add(new User
        {
            Email = "lawyer@example.com",
            PasswordHash = "hash",
            FullName = "Lawyer",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var response = await controller.GetAudit(CancellationToken.None);

        var report = AssertOk<RolePermissionAuditReportDto>(response);
        var roleAudit = Assert.Single(report.Roles);
        Assert.Equal(1, report.TotalRoles);
        Assert.Equal(1, report.ReadyRoles);
        Assert.Equal(0, report.RolesWithIssues);
        Assert.Equal(0, report.RolesWithBroadDataScope);
        Assert.False(roleAudit.HasIssues);
        Assert.Equal(RolePermissionCatalog.AllSections.Length, roleAudit.ConfiguredSectionsCount);
        Assert.Equal(1, roleAudit.AssignedUsersCount);
        Assert.Empty(roleAudit.MissingSections);
        Assert.Empty(roleAudit.UnknownSections);
        Assert.Empty(roleAudit.DuplicateSections);
        Assert.Empty(roleAudit.InvalidViewScopeSections);
    }

    [Fact]
    public async Task GetAudit_FlagsIncompleteLegacyAndUnsafePermissions()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "legacy-lawyer", Description = "Imported role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Clients,
                CanView = true,
                ViewScope = "global",
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Tasks,
                CanView = false,
                CanEdit = true,
                ViewScope = DataVisibilityScope.Own,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Cases,
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = "legacySection",
                CanView = true,
                ViewScope = DataVisibilityScope.All,
            },
            new RolePermission
            {
                RoleId = role.Id,
                Section = RolePermissionCatalog.Audit,
                CanView = true,
                CanCreate = true,
                ViewScope = DataVisibilityScope.All,
            });
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var response = await controller.GetAudit(CancellationToken.None);

        var report = AssertOk<RolePermissionAuditReportDto>(response);
        var roleAudit = Assert.Single(report.Roles);
        Assert.Equal(0, report.ReadyRoles);
        Assert.Equal(1, report.RolesWithIssues);
        Assert.Equal(1, report.RolesWithBroadDataScope);
        Assert.True(roleAudit.HasIssues);
        Assert.Contains(RolePermissionCatalog.Calendar, roleAudit.MissingSections);
        Assert.Contains("legacySection", roleAudit.UnknownSections);
        Assert.Contains("clients:global", roleAudit.InvalidViewScopeSections);
        Assert.Contains(RolePermissionCatalog.Tasks, roleAudit.CapabilityWithoutViewSections);
        Assert.Contains(RolePermissionCatalog.Audit, roleAudit.RestrictedCapabilitySections);
        Assert.Contains(RolePermissionCatalog.Cases, roleAudit.BroadDataScopeSections);
    }

    [Fact]
    public async Task ExportAuditCsv_ReturnsRoleAuditRows()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "Legal team" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());

        var response = await controller.ExportAuditCsv(CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(response);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.EndsWith(".csv", file.FileDownloadName);

        var csv = Encoding.UTF8.GetString(file.FileContents);
        Assert.Contains("\"RoleId\";\"RoleName\";\"IsSystemRole\"", csv);
        Assert.Contains($"\"{role.Id}\";\"lawyer\";\"false\"", csv);
    }

    [Fact]
    public async Task GetAudit_ForbidsUserWithoutRoleViewPermission()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "viewer", Description = "No role access" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "viewer@example.com",
            PasswordHash = "hash",
            FullName = "Viewer",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var response = await controller.GetAudit(CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task Update_SynchronizesSpecificCalendarVisibleUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "Secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        var first = new User
        {
            Email = "calendar-first@example.com",
            PasswordHash = "hash",
            FullName = "Первый сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var second = new User
        {
            Email = "calendar-second@example.com",
            PasswordHash = "hash",
            FullName = "Второй сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.AddRange(first, second);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());
        var request = new RolePermissionsDto
        {
            Calendar = new CalendarPermissionsDto
            {
                CanView = true,
                CanViewAnalytics = true,
                ViewScope = DataVisibilityScope.Specific,
                VisibleUserIds = new List<int> { first.Id, first.Id, second.Id },
            },
        };

        var updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(updateResult);
        var storedIds = await db.RoleCalendarVisibleUsers
            .AsNoTracking()
            .Where(item => item.RoleId == role.Id)
            .OrderBy(item => item.UserId)
            .Select(item => item.UserId)
            .ToListAsync();
        Assert.Equal(new[] { first.Id, second.Id }, storedIds);

        request.Calendar.VisibleUserIds = new List<int> { second.Id };
        updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(updateResult);
        var response = await controller.Get(role.Id, CancellationToken.None);
        var dto = AssertOk<RolePermissionsDto>(response);
        Assert.Equal(DataVisibilityScope.Specific, dto.Calendar.ViewScope);
        Assert.Equal(new[] { second.Id }, dto.Calendar.VisibleUserIds);
    }

    [Fact]
    public async Task Update_RejectsInactiveCalendarVisibleUser()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "Secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        var inactive = new User
        {
            Email = "calendar-inactive-role@example.com",
            PasswordHash = "hash",
            FullName = "Неактивный сотрудник",
            RoleId = role.Id,
            IsActive = false,
            IsApproved = true,
        };
        db.Users.Add(inactive);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateAdminPrincipal());
        var request = new RolePermissionsDto
        {
            Calendar = new CalendarPermissionsDto
            {
                CanView = true,
                ViewScope = DataVisibilityScope.Specific,
                VisibleUserIds = new List<int> { inactive.Id },
            },
        };

        var updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(updateResult);
        Assert.Empty(await db.RoleCalendarVisibleUsers.AsNoTracking().ToListAsync());
    }

    private static RolePermissionsController CreateController(PaymentContext db, ClaimsPrincipal user)
        => new(db, new RoleManagementAccessService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };

    private static ClaimsPrincipal CreateAdminPrincipal()
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ClaimTypes.Name, "Admin"),
                new Claim(ClaimTypes.Role, "admin"),
            },
            "test"));

    private static ClaimsPrincipal CreateUserPrincipal(int userId)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "Viewer"),
            },
            "test"));

    private static T AssertOk<T>(ActionResult<T> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<T>(ok.Value);
    }

    private static async Task<PaymentContext> CreateContextAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection)
            .Options;

        var db = new PaymentContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }
}
