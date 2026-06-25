using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Filters;
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
        yield return [RolePermissionCatalog.Integrations];
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
    [MemberData(nameof(CriticalSections))]
    public async Task SectionActionPermissions_DenyMissingSection_ForExistingRegularUser(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-missing-actions", new RolePermission
        {
            Section = RolePermissionCatalog.Notifications,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });
        var principal = CreatePrincipal(user);

        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanView));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanCreate));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanEdit));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanDelete));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanExport));
    }

    [Fact]
    public async Task SectionActionPermissions_DoNotOpenCasesFromLegacyClientPermissionFallback()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "legacy-client-only", new RolePermission
        {
            Section = RolePermissionCatalog.Clients,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All
        });
        var principal = CreatePrincipal(user);

        var casesPermission = await db.GetUserSectionPermissionAsync(user.Id, RolePermissionCatalog.Cases);

        Assert.Null(casesPermission);
        Assert.False(await db.HasUserSectionPermissionAsync(principal, RolePermissionCatalog.Cases, static permission => permission.CanView));
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
    public async Task DataVisibility_DeniesRegularUserWithoutConfiguredSection(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-missing-visibility", new RolePermission
        {
            Section = RolePermissionCatalog.Notifications,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
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
    public async Task SectionActionPermissions_DenyActionsWhenViewIsDisabledEvenIfActionsAreEnabled(string section)
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, $"{section}-view-disabled-actions-enabled", new RolePermission
        {
            Section = section,
            CanView = false,
            CanCreate = true,
            CanEdit = true,
            CanDelete = true,
            CanExport = true,
            ViewScope = DataVisibilityScope.All
        });
        var principal = CreatePrincipal(user);

        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanCreate));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanEdit));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanDelete));
        Assert.False(await db.HasUserSectionPermissionAsync(principal, section, static permission => permission.CanExport));
    }

    [Fact]
    public async Task RequireSectionPermissionFilter_ForbidsClosedSectionBeforeActionRuns()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "filter-payments-denied", new RolePermission
        {
            Section = RolePermissionCatalog.Payments,
            CanView = false,
            ViewScope = DataVisibilityScope.Own
        });
        var filter = new RequireSectionPermissionFilter(
            RolePermissionCatalog.Payments,
            RequireSectionPermissionOperations.View,
            db);
        var context = CreateActionExecutingContext(CreatePrincipal(user));
        var nextWasCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextWasCalled = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.False(nextWasCalled);
        Assert.IsType<ForbidResult>(context.Result);
    }

    [Fact]
    public async Task RequireSectionPermissionFilter_AllowsOpenSectionToRunAction()
    {
        await using var db = await CreateContextAsync();
        var user = await CreateUserAsync(db, "filter-payments-allowed", new RolePermission
        {
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            ViewScope = DataVisibilityScope.Own
        });
        var filter = new RequireSectionPermissionFilter(
            RolePermissionCatalog.Payments,
            RequireSectionPermissionOperations.View,
            db);
        var context = CreateActionExecutingContext(CreatePrincipal(user));
        var nextWasCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextWasCalled = true;
            return Task.FromResult(CreateActionExecutedContext(context));
        });

        Assert.True(nextWasCalled);
        Assert.Null(context.Result);
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

    [Fact]
    public async Task AcceptedOperationalRoles_RespectClientCaseTaskAccessMatrix()
    {
        await using var db = await CreateContextAsync();
        var secretary = await CreateUserWithPermissionsAsync(db, "secretary-operations", "Секретарь",
        [
            WorkLimited(RolePermissionCatalog.Clients),
            WorkLimited(RolePermissionCatalog.Cases),
            WorkLimited(RolePermissionCatalog.Tasks),
            CalendarLimited(),
            NotificationLimited(),
            Denied(RolePermissionCatalog.Roles),
            Denied(RolePermissionCatalog.Audit),
        ]);
        var lawyer = await CreateUserWithPermissionsAsync(db, "lawyer-operations", "Юрист",
        [
            WorkLimited(RolePermissionCatalog.Clients),
            WorkLimited(RolePermissionCatalog.Cases),
            WorkLimited(RolePermissionCatalog.Tasks),
            CalendarLimited(),
            NotificationLimited(),
            Denied(RolePermissionCatalog.Roles),
            Denied(RolePermissionCatalog.Audit),
        ]);
        var accountant = await CreateUserWithPermissionsAsync(db, "accountant-operations", "Бухгалтер",
        [
            ReadOnly(RolePermissionCatalog.Clients),
            ReadOnly(RolePermissionCatalog.Cases),
            NotificationLimited(),
            Denied(RolePermissionCatalog.Roles),
            Denied(RolePermissionCatalog.Audit),
        ]);

        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Clients, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Cases, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Tasks, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Calendar, true, true, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Notifications, true, false, true, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Roles, false, false, false, false, false);
        await AssertSectionAsync(db, secretary, RolePermissionCatalog.Audit, false, false, false, false, false);
        await AssertVisibilityScopeAsync(db, secretary, RolePermissionCatalog.Clients, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, secretary, RolePermissionCatalog.Cases, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, secretary, RolePermissionCatalog.Tasks, DataVisibilityScope.Own);

        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Clients, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Cases, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Tasks, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Calendar, true, true, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Notifications, true, false, true, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Roles, false, false, false, false, false);
        await AssertSectionAsync(db, lawyer, RolePermissionCatalog.Audit, false, false, false, false, false);
        await AssertVisibilityScopeAsync(db, lawyer, RolePermissionCatalog.Clients, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, lawyer, RolePermissionCatalog.Cases, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, lawyer, RolePermissionCatalog.Tasks, DataVisibilityScope.Own);

        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Clients, true, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Cases, true, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Notifications, true, false, true, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Roles, false, false, false, false, false);
        await AssertSectionAsync(db, accountant, RolePermissionCatalog.Audit, false, false, false, false, false);
        await AssertVisibilityScopeAsync(db, accountant, RolePermissionCatalog.Clients, DataVisibilityScope.Own);
        await AssertVisibilityScopeAsync(db, accountant, RolePermissionCatalog.Cases, DataVisibilityScope.Own);
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

    private static RolePermission ReadOnly(string section)
        => new()
        {
            Section = section,
            CanView = true,
            CanCreate = false,
            CanEdit = false,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission WorkLimited(string section)
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

    private static RolePermission CalendarLimited()
        => new()
        {
            Section = RolePermissionCatalog.Calendar,
            CanView = true,
            CanCreate = true,
            CanEdit = true,
            CanDelete = false,
            CanExport = false,
            CanViewAnalytics = false,
            ViewScope = DataVisibilityScope.Own,
        };

    private static RolePermission NotificationLimited()
        => new()
        {
            Section = RolePermissionCatalog.Notifications,
            CanView = true,
            CanCreate = false,
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

    private static async Task AssertVisibilityScopeAsync(
        PaymentContext db,
        User user,
        string section,
        string expectedScope)
    {
        var access = await db.ResolveDataVisibilityAsync(CreatePrincipal(user), section);

        Assert.NotNull(access);
        Assert.Equal(expectedScope, access.ViewScope);
        Assert.False(access.IsAdmin);
        Assert.True(access.CanSeeUser(user.Id));
        Assert.False(access.CanSeeUser(user.Id + 10_000));
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

    private static ActionExecutingContext CreateActionExecutingContext(ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext { User = principal };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), controller: new object());
    }

    private static ActionExecutedContext CreateActionExecutedContext(ActionExecutingContext context)
        => new(context, [], controller: new object());

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
