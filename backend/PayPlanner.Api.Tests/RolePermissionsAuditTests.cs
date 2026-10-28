using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public class RolePermissionsAuditTests
{
    [Fact]
    public async Task UpdateAndReset_PublishOnlyActiveApprovedUsersOfChangedRole_AfterSuccessfulWrite()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "realtime-role" };
        var otherRole = new Role { Name = "other-role" };
        db.Roles.AddRange(role, otherRole);
        await db.SaveChangesAsync();
        var allowed = new User { Email = "allowed@local", PasswordHash = "hash", FullName = "Allowed", RoleId = role.Id, IsActive = true, IsApproved = true };
        db.Users.AddRange(allowed,
            new User { Email = "inactive@local", PasswordHash = "hash", FullName = "Inactive", RoleId = role.Id, IsActive = false, IsApproved = true },
            new User { Email = "pending@local", PasswordHash = "hash", FullName = "Pending", RoleId = role.Id, IsActive = true, IsApproved = false },
            new User { Email = "other@local", PasswordHash = "hash", FullName = "Other", RoleId = otherRole.Id, IsActive = true, IsApproved = true });
        await db.SaveChangesAsync();
        var admin = await CreateAdminUserAsync(db);
        var publisher = new RecordingRoleChangePublisher();
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id), roleChangePublisher: publisher);
        Assert.IsType<NoContentResult>(await controller.Update(role.Id, RolePermissionCatalog.CreateDto(allowRoleManagement: false), CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.Reset(role.Id, CancellationToken.None));
        Assert.Equal(2, publisher.Calls.Count);
        Assert.All(publisher.Calls, ids => Assert.Equal(new[] { allowed.Id }, ids));
        var unauthorized = CreateController(db, CreateUserPrincipal(allowed.Id), roleChangePublisher: publisher);
        Assert.IsType<ForbidResult>(await unauthorized.Reset(role.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Reset(int.MaxValue, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.Reset(admin.RoleId, CancellationToken.None));
        Assert.Equal(2, publisher.Calls.Count);
    }

    [Fact]
    public async Task Update_RevokesAccountingGroupForExistingSessionWithoutRemovingClientAccess()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "group-secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User
        {
            Email = "group-secretary@example.com",
            PasswordHash = "hash",
            FullName = "Secretary",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.Add(user);
        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        await db.SaveChangesAsync();

        var principal = CreateUserPrincipal(user.Id);
        Assert.True(await db.HasUserSectionPermissionAsync(principal, RolePermissionCatalog.Accounts, p => p.CanView));
        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));
        var request = RolePermissionCatalog.CreateDto(allowRoleManagement: false);
        request.Accounts = new MenuSectionPermissionsDto();
        request.Acts = new MenuSectionPermissionsDto();
        request.Payments = new MenuSectionPermissionsDto();
        request.Contracts = new MenuSectionPermissionsDto();

        Assert.IsType<NoContentResult>(await controller.Update(role.Id, request, CancellationToken.None));
        db.ChangeTracker.Clear();
        foreach (var section in new[] { RolePermissionCatalog.Accounts, RolePermissionCatalog.Acts, RolePermissionCatalog.Payments, RolePermissionCatalog.Contracts })
        {
            Assert.False(await db.HasUserSectionPermissionAsync(principal, section, p => p.CanView));
            Assert.False(await db.HasUserSectionPermissionAsync(principal, section, p => p.CanEdit));
            Assert.False(await db.HasUserSectionPermissionAsync(principal, section, p => p.CanExport));
        }
        Assert.True(await db.HasUserSectionPermissionAsync(principal, RolePermissionCatalog.Clients, p => p.CanView));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_PreservesSeparateCompanyProfileRightsAndRejectsUnsupportedCapabilities(bool view)
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "profile-editor" };
        db.Roles.Add(role); await db.SaveChangesAsync();
        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));
        var request = RolePermissionCatalog.CreateDto(allowRoleManagement: false);
        Assert.False(request.CompanyProfile.CanView);
        request.CompanyProfile = new MenuSectionPermissionsDto { CanView = view, CanEdit = true, CanCreate = true, CanDelete = true, CanExport = true };
        Assert.IsType<NoContentResult>(await controller.Update(role.Id, request, CancellationToken.None));
        var dto = AssertOk<RolePermissionsDto>(await controller.Get(role.Id, CancellationToken.None));
        Assert.Equal(view, dto.CompanyProfile.CanView); Assert.Equal(view, dto.CompanyProfile.CanEdit);
        Assert.False(dto.CompanyProfile.CanCreate); Assert.False(dto.CompanyProfile.CanDelete); Assert.False(dto.CompanyProfile.CanExport);
        var persisted = await db.RolePermissions.SingleAsync(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.CompanyProfile);
        Assert.Equal(view, persisted.CanView); Assert.Equal(view, persisted.CanEdit);
        Assert.Equal(request.Accounts.CanView, dto.Accounts.CanView); Assert.Equal(request.Acts.CanView, dto.Acts.CanView);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Update_PreservesRequestAnalyticsOnlyWhenViewingIsAllowed(bool canView)
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "request-analyst" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        await db.SaveChangesAsync();
        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));
        var request = RolePermissionCatalog.CreateDto(allowRoleManagement: false);
        request.Requests.CanView = canView;
        request.Requests.CanViewAnalytics = true;
        request.Requests.ViewScope = DataVisibilityScope.Own;

        Assert.IsType<NoContentResult>(await controller.Update(role.Id, request, CancellationToken.None));
        var dto = AssertOk<RolePermissionsDto>(await controller.Get(role.Id, CancellationToken.None));
        Assert.Equal(canView, dto.Requests.CanViewAnalytics);
        Assert.Equal(DataVisibilityScope.Own, dto.Requests.ViewScope);
        var audit = AssertOk<RolePermissionAuditReportDto>(await controller.GetAudit(CancellationToken.None));
        Assert.Empty(Assert.Single(audit.Roles, item => item.RoleName == role.Name).RestrictedCapabilitySections);
    }

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

        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));

        var response = await controller.GetAudit(CancellationToken.None);

        var report = AssertOk<RolePermissionAuditReportDto>(response);
        var roleAudit = Assert.Single(report.Roles, item => item.RoleName == "lawyer");
        Assert.Equal(2, report.TotalRoles);
        Assert.Equal(1, report.ReadyRoles);
        Assert.Equal(1, report.RolesWithIssues);
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

        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));

        var response = await controller.GetAudit(CancellationToken.None);

        var report = AssertOk<RolePermissionAuditReportDto>(response);
        var roleAudit = Assert.Single(report.Roles, item => item.RoleName == "legacy-lawyer");
        Assert.Equal(0, report.ReadyRoles);
        Assert.Equal(2, report.RolesWithIssues);
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
    public async Task Get_ReturnsClosedSections_WhenLegacyRoleMissesPermissions()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "legacy-lawyer-detail", Description = "Imported lawyer role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All,
        });
        await db.SaveChangesAsync();

        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));

        var response = await controller.Get(role.Id, CancellationToken.None);

        var dto = AssertOk<RolePermissionsDto>(response);
        Assert.True(dto.Clients.CanView);
        Assert.False(dto.Cases.CanView);
        Assert.False(dto.Payments.CanView);
        Assert.False(dto.Roles.CanView);
        Assert.Equal(DataVisibilityScope.Own, dto.Cases.ViewScope);
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

        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));

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
    public async Task GetAudit_ForbidsUserWithoutAuditViewPermission()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "role-viewer", Description = "Role viewer without audit" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Roles,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });
        var user = new User
        {
            Email = "role-viewer@example.com",
            PasswordHash = "hash",
            FullName = "Role Viewer",
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
    public async Task ExportAuditCsv_ForbidsUserWithoutAuditViewPermission()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "role-export-viewer", Description = "Role viewer without audit export" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Roles,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });
        var user = new User
        {
            Email = "role-export-viewer@example.com",
            PasswordHash = "hash",
            FullName = "Role Export Viewer",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id));

        var response = await controller.ExportAuditCsv(CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
    }

    [Fact]
    public async Task GetAudit_ForbidsAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "viewer-stale-admin", Description = "No role access" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        var user = new User
        {
            Email = "viewer-stale-admin@example.com",
            PasswordHash = "hash",
            FullName = "Viewer Stale Admin",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var controller = CreateController(db, CreateUserPrincipal(user.Id, includeAdminRoleClaim: true));

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

        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));
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
    public async Task Update_PreservesIndependentOtherTaskPermissions()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "task-supervisor", Description = "Task supervisor" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        await db.SaveChangesAsync();

        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));
        var request = RolePermissionCatalog.CreateDto(allowRoleManagement: false);
        request.Tasks.CanEditOthers = true;
        request.Tasks.CanCompleteOthers = false;
        request.Tasks.CanDeleteOthers = true;

        var updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(updateResult);
        var response = await controller.Get(role.Id, CancellationToken.None);
        var dto = AssertOk<RolePermissionsDto>(response);
        Assert.True(dto.Tasks.CanEditOthers);
        Assert.False(dto.Tasks.CanCompleteOthers);
        Assert.True(dto.Tasks.CanDeleteOthers);
    }

    [Fact]
    public async Task Update_NotifiesRoleUsersWhenSpecificCalendarVisibleUsersChanged()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary-notified", Description = "Secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.AddRange(RolePermissionCatalog.CreateEntities(role.Id, allowRoleManagement: false));
        var first = new User
        {
            Email = "calendar-notify-first@example.com",
            PasswordHash = "hash",
            FullName = "Первый сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var second = new User
        {
            Email = "calendar-notify-second@example.com",
            PasswordHash = "hash",
            FullName = "Второй сотрудник",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        db.Users.AddRange(first, second);
        await db.SaveChangesAsync();
        db.RoleCalendarVisibleUsers.Add(new RoleCalendarVisibleUser
        {
            RoleId = role.Id,
            UserId = first.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var notificationService = new RecordingNotificationService();
        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id), notificationService);
        var request = new RolePermissionsDto
        {
            Calendar = new CalendarPermissionsDto
            {
                CanView = true,
                ViewScope = DataVisibilityScope.Specific,
                VisibleUserIds = new List<int> { second.Id },
            },
        };

        var updateResult = await controller.Update(role.Id, request, CancellationToken.None);

        Assert.IsType<NoContentResult>(updateResult);
        var notification = Assert.Single(notificationService.CalendarAccessChanges);
        Assert.Equal(role.Id, notification.RoleId);
        Assert.Equal([second.Id], notification.AddedVisibleUserIds);
        Assert.Equal([first.Id], notification.RemovedVisibleUserIds);
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

        var admin = await CreateAdminUserAsync(db);
        var controller = CreateController(db, CreateAdminPrincipal(admin.Id));
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

    private static RolePermissionsController CreateController(
        PaymentContext db,
        ClaimsPrincipal user,
        INotificationService? notificationService = null,
        IRolePermissionsChangePublisher? roleChangePublisher = null)
        => new(db, new RoleManagementAccessService(db), notificationService, roleChangePublisher)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };

    private sealed class RecordingRoleChangePublisher : IRolePermissionsChangePublisher
    {
        public List<int[]> Calls { get; } = [];
        public Task PublishRolePermissionsChangedAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken = default)
        {
            Calls.Add(userIds.ToArray());
            return Task.CompletedTask;
        }
    }

    private static ClaimsPrincipal CreateAdminPrincipal(int userId)
        => new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, "Admin"),
                new Claim(ClaimTypes.Role, "admin"),
            },
            "test"));

    private static ClaimsPrincipal CreateUserPrincipal(int userId, bool includeAdminRoleClaim = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "Viewer"),
        };
        if (includeAdminRoleClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static T AssertOk<T>(ActionResult<T> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<T>(ok.Value);
    }

    private static async Task<User> CreateAdminUserAsync(PaymentContext db)
    {
        var role = new Role { Name = "admin", Description = "Администратор" };
        var user = new User
        {
            Email = $"admin-{Guid.NewGuid():N}@example.com",
            PasswordHash = "hash",
            FullName = "Admin",
            Role = role,
            IsActive = true,
            IsApproved = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
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

    private sealed class RecordingNotificationService : INotificationService
    {
        public List<(int RoleId, IReadOnlyCollection<int> AddedVisibleUserIds, IReadOnlyCollection<int> RemovedVisibleUserIds)> CalendarAccessChanges { get; } = [];

        public Task<NotificationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationChannelDto>> GetChannelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationChannelDto>>(Array.Empty<NotificationChannelDto>());

        public Task<NotificationSettingsDto> UpdateEmailSettingsAsync(PayPlanner.Api.Models.Requests.UpdateEmailNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateTelegramSettingsAsync(PayPlanner.Api.Models.Requests.UpdateTelegramNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationSettingsDto> UpdateSmsSettingsAsync(PayPlanner.Api.Models.Requests.UpdateSmsNotificationSettingsRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationTemplateDto>> GetTemplatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationTemplateDto>>(Array.Empty<NotificationTemplateDto>());

        public Task<NotificationTemplateDto> UpdateTemplateAsync(string key, PayPlanner.Api.Models.Requests.UpdateNotificationTemplateRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationTemplateDto> ResetTemplateAsync(string key, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<NotificationMessage> RenderTemplateAsync(string key, string fallbackSubject, string fallbackBody, IReadOnlyDictionary<string, string?> values, bool isSensitive = false, string purpose = NotificationPurposes.ManualTest, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationMessage(fallbackSubject, fallbackBody, IsSensitive: isSensitive, Purpose: purpose));

        public Task<IReadOnlyList<NotificationDelivery>> SendToUserAsync(int userId, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> SendAsync(NotificationRecipient recipient, NotificationMessage message, IEnumerable<string>? channels = null, string? eventKey = null, CancellationToken cancellationToken = default, int? senderId = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> RetryDeliveryAsync(int deliveryId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<bool> NotifyAsync(NotificationEventEnvelope notificationEvent, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<NotificationSettingsDto> UpsertSenderAsync(PayPlanner.Api.Models.Requests.UpsertNotificationSenderRequest request, int? senderId = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteSenderAsync(int senderId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPendingRegistrationAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationApprovedAsync(User user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyRegistrationRejectedAsync(User user, string? reason, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyPasswordIssuedAsync(User user, string password, IEnumerable<string>? channels = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskAssignedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReminderAsync(TaskJournalItem task, string reminderKey, DateTime nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCompletedAsync(TaskJournalItem task, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCancelledAsync(TaskJournalItem task, string actorName, int actorUserId, string action, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskScheduleConflictAsync(TaskJournalItem task, IReadOnlyList<TaskJournalItem> conflicts, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalRequestedAsync(TaskJournalItem task, string actorName, int actorUserId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalDecisionAsync(TaskJournalItem task, TaskJournalParticipant approver, string decision, string actorName, int actorUserId, string? resolution = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineReviewerAssignedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRequestedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeApprovedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskDeadlineChangeRejectedAsync(TaskJournalItem task, TaskJournalDeadlineChangeRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskApprovalOverdueAsync(TaskJournalItem task, DateTime nowUtc, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskReassignedAsync(TaskJournalItem task, int? previousAssigneeUserId, string? previousAssigneeName, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? additionalRecipientUserIds = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskChangedAsync(TaskJournalItem task, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, string? comment = null, CancellationToken cancellationToken = default, IReadOnlyCollection<int>? additionalRecipientUserIds = null)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyTaskCommentAsync(TaskJournalItem task, TaskJournalComment comment, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task<IReadOnlyList<NotificationDelivery>> NotifyCaseInactivityControlTaskCreatedAsync(TaskJournalItem task, int daysWithoutActiveTask, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NotificationDelivery>>(Array.Empty<NotificationDelivery>());

        public Task NotifyCalendarAccessChangedAsync(int roleId, IReadOnlyCollection<int> addedVisibleUserIds, IReadOnlyCollection<int> removedVisibleUserIds, string actorName, int actorUserId, CancellationToken cancellationToken = default)
        {
            CalendarAccessChanges.Add((roleId, addedVisibleUserIds.ToArray(), removedVisibleUserIds.ToArray()));
            return Task.CompletedTask;
        }

        public Task NotifyExternalCalendarSyncWarningAsync(string providerName, string operation, string summary, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteMentionAsync(int noteId, int boardId, string boardTitle, string noteText, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteAssignedAsync(int noteId, int boardId, string boardTitle, string noteText, int assignedUserId, string actorName, int actorUserId, DateTime? reminderAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteReminderAsync(StickyNote note, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyStickyNoteImportantChangeAsync(int noteId, int boardId, string boardTitle, string noteText, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, int noteVersion, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentAssignedAsync(Payment payment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyPaymentChangedAsync(Payment payment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceCreatedAsync(Payment invoicePayment, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyInvoiceChangedAsync(Payment invoicePayment, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActCreatedAsync(Act act, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyActChangedAsync(Act act, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractCreatedAsync(Contract contract, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractChangedAsync(Contract contract, IReadOnlyList<string> changeLabels, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyContractExpiringAsync(Contract contract, int daysLeft, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseCourtEventAsync(ClientCase clientCase, ClientCaseCourtEvent courtEvent, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseProceduralDeadlineAsync(ClientCase clientCase, ClientCaseProceduralDeadline deadline, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseConversationAsync(ClientCase clientCase, string messageText, int attachmentCount, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, DateTime occurredAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseDocumentAsync(ClientCase clientCase, ClientCaseDocument document, string action, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessRequestedAsync(ClientCase clientCase, string requesterName, int requesterUserId, string? reason, DateTime requestedAt, IReadOnlyCollection<int> managerUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseAccessResolvedAsync(ClientCase clientCase, bool granted, string actorName, int actorUserId, int requestedByUserId, string requestedByUserName, string? requestReason, string? comment, DateTime resolvedAt, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCourtSyncFailedAsync(ClientCase clientCase, string providerName, string summary, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCourtSyncStalledAsync(ClientCase clientCase, string providerName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseHearingDateChangedAsync(ClientCase clientCase, DateTime? previousAt, DateTime currentAt, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, DateTime changedAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task NotifyCaseDecisionDateChangedAsync(ClientCase clientCase, DateTime? previousAt, DateTime? currentAt, string actorName, int actorUserId, IReadOnlyCollection<int> recipientUserIds, DateTime changedAtUtc, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
