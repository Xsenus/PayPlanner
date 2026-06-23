using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class BackendPermissionMatrixTests
{
    public static IEnumerable<object[]> CriticalSections()
    {
        yield return [RolePermissionCatalog.Payments];
        yield return [RolePermissionCatalog.Accounts];
        yield return [RolePermissionCatalog.Acts];
        yield return [RolePermissionCatalog.Contracts];
        yield return [RolePermissionCatalog.Cases];
        yield return [RolePermissionCatalog.Tasks];
        yield return [RolePermissionCatalog.Reports];
        yield return [RolePermissionCatalog.Roles];
        yield return [RolePermissionCatalog.Audit];
        yield return [RolePermissionCatalog.YandexDisk];
        yield return [RolePermissionCatalog.S3Storage];
    }

    public static IEnumerable<object[]> DataScopedSections()
    {
        foreach (var section in RolePermissionCatalog.DataScopedSections)
        {
            yield return [section];
        }
    }

    [Theory]
    [MemberData(nameof(CriticalSections))]
    public async Task CriticalSectionPermissions_ReturnCanView_ForUserWithSectionAccess(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-viewer", new RolePermission
        {
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.All
        });

        var permission = await db.GetUserSectionPermissionAsync(user.Id, section);

        Assert.NotNull(permission);
        Assert.True(permission.CanView);
    }

    [Theory]
    [MemberData(nameof(CriticalSections))]
    public async Task CriticalSectionPermissions_DenyView_ForUserWithoutSectionAccess(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-blocked", new RolePermission
        {
            Section = section,
            CanView = false,
            ViewScope = DataVisibilityScope.Own
        });

        var permission = await db.GetUserSectionPermissionAsync(user.Id, section);

        Assert.NotNull(permission);
        Assert.False(permission.CanView);
    }

    [Theory]
    [MemberData(nameof(CriticalSections))]
    public async Task CriticalSectionPermissions_ReturnNull_ForUserWithoutConfiguredSection(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-missing");

        var permission = await db.GetUserSectionPermissionAsync(user.Id, section);

        Assert.Null(permission);
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_RestrictsRegularUserToOwnScope_ForDataScopedSections(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-own", new RolePermission
        {
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(user), section);

        Assert.NotNull(access);
        Assert.False(access.IsAdmin);
        Assert.Equal(DataVisibilityScope.Own, access.ViewScope);
        Assert.True(access.CanSeeUser(user.Id));
        Assert.False(access.CanSeeUser(user.Id + 1000));
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_DeniesRegularUserWithoutSectionViewAccess(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-denied", new RolePermission
        {
            Section = section,
            CanView = false,
            ViewScope = DataVisibilityScope.All
        });

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(user), section);

        Assert.Null(access);
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task SectionActionPermissions_RespectExplicitActionDenials(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-actions", new RolePermission
        {
            Section = section,
            CanView = true,
            CanCreate = false,
            CanEdit = true,
            CanDelete = false,
            CanExport = true,
            ViewScope = DataVisibilityScope.All
        });
        var principal = CreatePrincipal(user);

        var canCreate = await db.HasUserSectionPermissionAsync(principal, section, permission => permission.CanCreate);
        var canEdit = await db.HasUserSectionPermissionAsync(principal, section, permission => permission.CanEdit);
        var canDelete = await db.HasUserSectionPermissionAsync(principal, section, permission => permission.CanDelete);

        Assert.False(canCreate);
        Assert.True(canEdit);
        Assert.False(canDelete);
    }

    [Theory]
    [MemberData(nameof(DataScopedSections))]
    public async Task DataVisibility_AllowsAdminAcrossDataScopedSections(string section)
    {
        await using var db = await CreateContextAsync();
        var admin = await CreateUserAsync(db, $"{section}-admin", new RolePermission
        {
            Section = section,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        }, roleName: "admin");

        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(admin, isAdmin: true), section);

        Assert.NotNull(access);
        Assert.True(access.IsAdmin);
        Assert.Equal(DataVisibilityScope.All, access.ViewScope);
        Assert.True(access.CanSeeUser(admin.Id + 1000));
    }

    [Fact]
    public async Task AcceptedFinancialRoles_RespectCashAccessMatrix()
    {
        await using var db = await CreateContextAsync();
        var secretary = await CreateUserWithPermissionsAsync(db, "secretary", "Секретарь",
        [
            Denied(RolePermissionCatalog.Payments),
            Denied(RolePermissionCatalog.Reports),
            DocumentLimited(RolePermissionCatalog.Accounts),
            DocumentLimited(RolePermissionCatalog.Acts),
            DocumentLimited(RolePermissionCatalog.Contracts),
        ]);
        var lawyer = await CreateUserWithPermissionsAsync(db, "lawyer", "Юрист",
        [
            Denied(RolePermissionCatalog.Payments),
            Denied(RolePermissionCatalog.Reports),
            Denied(RolePermissionCatalog.Accounts),
            Denied(RolePermissionCatalog.Acts),
            Denied(RolePermissionCatalog.Contracts),
        ]);
        var accountant = await CreateUserWithPermissionsAsync(db, "accountant", "Бухгалтер",
        [
            DocumentLimited(RolePermissionCatalog.Payments),
            Denied(RolePermissionCatalog.Reports),
            DocumentLimited(RolePermissionCatalog.Accounts),
            DocumentLimited(RolePermissionCatalog.Acts),
            DocumentLimited(RolePermissionCatalog.Contracts),
        ]);

        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Payments, false, false, false, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Reports, false, false, false, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Accounts, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Acts, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Contracts, true, true, true, false, false);

        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Payments, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Reports, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Accounts, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Acts, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Contracts, false, false, false, false, false);

        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Payments, true, true, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Reports, false, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Accounts, true, true, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Acts, true, true, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Contracts, true, true, true, false, false);
    }

    private static RolePermission Denied(string section)
        => new()
        {
            Section = section,
            CanView = false,
            CanCreate = false,
            CanEdit = false,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission DocumentLimited(string section)
        => new()
        {
            Section = section,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static async Task AssertSectionAsync(
        PaymentContext db,
        User user,
        string section,
        bool canView,
        bool canCreate,
        bool canEdit,
        bool canDelete,
        bool canExport)
    {
        var principal = CreatePrincipal(user);
        Assert.Equal(canView, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanView));
        Assert.Equal(canCreate, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanCreate));
        Assert.Equal(canEdit, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanEdit));
        Assert.Equal(canDelete, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanDelete));
        Assert.Equal(canExport, await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanExport));
    }

    private static ClaimsPrincipal CreatePrincipal(User user, bool isAdmin = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName)
        };
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async Task<User> CreateUserAsync(
        PaymentContext db,
        string name,
        RolePermission? permission = null,
        string roleName = "employee")
    {
        var role = new Role { Name = roleName, Description = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        if (permission is not null)
        {
            permission.RoleId = role.Id;
            db.RolePermissions.Add(permission);
        }

        var user = new User
        {
            Email = $"{name}@example.test",
            PasswordHash = "hash",
            FullName = name,
            RoleId = role.Id,
            IsActive = true
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<User> CreateUserWithPermissionsAsync(
        PaymentContext db,
        string name,
        string roleName,
        IEnumerable<RolePermission> permissions)
    {
        var role = new Role { Name = roleName, Description = roleName };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        foreach (var permission in permissions)
        {
            permission.RoleId = role.Id;
            db.RolePermissions.Add(permission);
        }

        var user = new User
        {
            Email = $"{name}@example.test",
            PasswordHash = "hash",
            FullName = name,
            RoleId = role.Id,
            IsActive = true
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
}
