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
