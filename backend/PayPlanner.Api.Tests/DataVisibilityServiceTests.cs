using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class DataVisibilityServiceTests
{
    [Fact]
    public async Task IsUserAdminOrLegacyFallbackAsync_UsesDatabaseRoleForExistingUser()
    {
        await using var db = await CreateContextAsync();
        var adminRole = new Role { Name = "admin", Description = "admin" };
        db.Roles.Add(adminRole);
        await db.SaveChangesAsync();
        var admin = new User
        {
            Email = "db-admin@example.com",
            PasswordHash = "hash",
            FullName = "Database Admin",
            RoleId = adminRole.Id,
            IsActive = true,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        var isAdmin = await db.IsUserAdminOrLegacyFallbackAsync(CreatePrincipal(admin.Id));

        Assert.True(isAdmin);
    }

    [Fact]
    public async Task IsUserAdminOrLegacyFallbackAsync_IgnoresAdminClaimForExistingNonAdminUser()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "manager", Description = "manager" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        var user = new User
        {
            Email = "db-manager@example.com",
            PasswordHash = "hash",
            FullName = "Database Manager",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var isAdmin = await db.IsUserAdminOrLegacyFallbackAsync(CreatePrincipal(user.Id, isAdminClaim: true));

        Assert.False(isAdmin);
    }

    [Fact]
    public async Task IsUserAdminOrLegacyFallbackAsync_ReturnsFalseForMissingUserWhenDatabaseHasUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "manager", Description = "manager" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            Email = "existing-admin-helper-user@example.com",
            PasswordHash = "hash",
            FullName = "Existing User",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var isAdmin = await db.IsUserAdminOrLegacyFallbackAsync(CreatePrincipal(987654, isAdminClaim: true));

        Assert.False(isAdmin);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_IgnoresAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "lawyer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            ViewScope = DataVisibilityScope.Own,
        });

        var user = new User
        {
            Email = "stale-admin-claim@example.com",
            PasswordHash = "hash",
            FullName = "Stale Admin Claim",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, isAdminClaim: true);

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Cases);

        Assert.NotNull(access);
        Assert.False(access!.IsAdmin);
        Assert.Equal(DataVisibilityScope.Own, access.ViewScope);
        Assert.False(access.CanSeeUser(user.Id + 1000));
    }

    [Fact]
    public async Task HasUserSectionPermissionAsync_IgnoresAdminClaimWhenDatabaseRoleIsNotAdmin()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "viewer", Description = "viewer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Cases,
            CanView = true,
            CanCreate = false,
            CanEdit = false,
            CanDelete = false,
            ViewScope = DataVisibilityScope.Own,
        });

        var user = new User
        {
            Email = "stale-section-admin@example.com",
            PasswordHash = "hash",
            FullName = "Stale Section Admin",
            RoleId = role.Id,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var principal = CreatePrincipal(user.Id, isAdminClaim: true);

        var canCreate = await db.HasUserSectionPermissionAsync(
            principal,
            RolePermissionCatalog.Cases,
            static permission => permission.CanCreate);

        Assert.False(canCreate);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_ReturnsNullForMissingUserWhenDatabaseHasUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "lawyer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            Email = "existing-user@example.com",
            PasswordHash = "hash",
            FullName = "Existing User",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var access = await db.ResolveDataVisibilityAsync(
            CreatePrincipal(987654, isAdminClaim: true),
            RolePermissionCatalog.Cases);

        Assert.Null(access);
    }

    [Fact]
    public async Task HasUserSectionPermissionAsync_ReturnsFalseForMissingUserWhenDatabaseHasUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "viewer", Description = "viewer" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.Users.Add(new User
        {
            Email = "existing-section-user@example.com",
            PasswordHash = "hash",
            FullName = "Existing Section User",
            RoleId = role.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var canCreate = await db.HasUserSectionPermissionAsync(
            CreatePrincipal(987654, isAdminClaim: true),
            RolePermissionCatalog.Cases,
            static permission => permission.CanCreate);

        Assert.False(canCreate);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_UsesDepartmentDictionaryAndLegacyNameFallback()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "secretary" };
        var department = new DepartmentDictionaryItem { Name = "Секретариат", IsActive = true };
        var anotherDepartment = new DepartmentDictionaryItem { Name = "Юристы", IsActive = true };
        db.Roles.Add(role);
        db.Departments.AddRange(department, anotherDepartment);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            ViewScope = DataVisibilityScope.Department,
        });

        var current = new User
        {
            Email = "current@example.com",
            PasswordHash = "hash",
            FullName = "Current",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        var teammate = new User
        {
            Email = "teammate@example.com",
            PasswordHash = "hash",
            FullName = "Teammate",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        var legacyTeammate = new User
        {
            Email = "legacy@example.com",
            PasswordHash = "hash",
            FullName = "Legacy",
            RoleId = role.Id,
            IsActive = true,
            Department = department.Name,
        };
        var other = new User
        {
            Email = "other@example.com",
            PasswordHash = "hash",
            FullName = "Other",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = anotherDepartment.Id,
            Department = anotherDepartment.Name,
        };
        db.Users.AddRange(current, teammate, legacyTeammate, other);
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, current.Id.ToString()),
        }, "test"));

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Clients);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Department, access!.ViewScope);
        Assert.Contains(current.Id, access.VisibleUserIds);
        Assert.Contains(teammate.Id, access.VisibleUserIds);
        Assert.Contains(legacyTeammate.Id, access.VisibleUserIds);
        Assert.DoesNotContain(other.Id, access.VisibleUserIds);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_UsesClientPermissionFallbackForCases()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "lawyer" };
        var department = new DepartmentDictionaryItem { Name = "Юридический отдел", IsActive = true };
        db.Roles.Add(role);
        db.Departments.Add(department);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            CanCreate = true,
            CanEdit = false,
            ViewScope = DataVisibilityScope.Department,
        });

        var current = new User
        {
            Email = "current-cases@example.com",
            PasswordHash = "hash",
            FullName = "Current Cases",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        var teammate = new User
        {
            Email = "teammate-cases@example.com",
            PasswordHash = "hash",
            FullName = "Teammate Cases",
            RoleId = role.Id,
            IsActive = true,
            DepartmentId = department.Id,
            Department = department.Name,
        };
        db.Users.AddRange(current, teammate);
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, current.Id.ToString()),
        }, "test"));

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Cases);
        var permission = await db.GetUserSectionPermissionAsync(current.Id, RolePermissionCatalog.Cases);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Department, access!.ViewScope);
        Assert.Contains(current.Id, access.VisibleUserIds);
        Assert.Contains(teammate.Id, access.VisibleUserIds);
        Assert.NotNull(permission);
        Assert.Equal(RolePermissionCatalog.Clients, permission!.Section);
        Assert.False(permission.CanEdit);
    }

    [Fact]
    public async Task ResolveDataVisibilityAsync_UsesSpecificCalendarUsers()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "secretary", Description = "secretary" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            ViewScope = DataVisibilityScope.Specific,
        });

        var current = new User
        {
            Email = "calendar-current@example.com",
            PasswordHash = "hash",
            FullName = "Current",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var selected = new User
        {
            Email = "calendar-selected@example.com",
            PasswordHash = "hash",
            FullName = "Selected",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var notSelected = new User
        {
            Email = "calendar-not-selected@example.com",
            PasswordHash = "hash",
            FullName = "Not Selected",
            RoleId = role.Id,
            IsActive = true,
            IsApproved = true,
        };
        var inactiveSelected = new User
        {
            Email = "calendar-inactive@example.com",
            PasswordHash = "hash",
            FullName = "Inactive",
            RoleId = role.Id,
            IsActive = false,
            IsApproved = true,
        };
        db.Users.AddRange(current, selected, notSelected, inactiveSelected);
        await db.SaveChangesAsync();

        db.RoleCalendarVisibleUsers.AddRange(
            new RoleCalendarVisibleUser { RoleId = role.Id, UserId = selected.Id },
            new RoleCalendarVisibleUser { RoleId = role.Id, UserId = inactiveSelected.Id });
        await db.SaveChangesAsync();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, current.Id.ToString()),
        }, "test"));

        var access = await db.ResolveDataVisibilityAsync(principal, RolePermissionCatalog.Calendar);

        Assert.NotNull(access);
        Assert.Equal(DataVisibilityScope.Specific, access!.ViewScope);
        Assert.Contains(current.Id, access.VisibleUserIds);
        Assert.Contains(selected.Id, access.VisibleUserIds);
        Assert.DoesNotContain(notSelected.Id, access.VisibleUserIds);
        Assert.DoesNotContain(inactiveSelected.Id, access.VisibleUserIds);
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

    private static ClaimsPrincipal CreatePrincipal(int userId, bool isAdminClaim = false)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (isAdminClaim)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
