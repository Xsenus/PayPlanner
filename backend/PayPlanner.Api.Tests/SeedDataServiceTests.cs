using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public class SeedDataServiceTests
{
    [Fact]
    public async Task SeedAsync_WhenRunTwiceOnEmptyDatabaseKeepsCoreDataIdempotent()
    {
        await using var db = await CreateContextAsync();

        await SeedDataService.SeedAsync(db);
        var firstSnapshot = await CreateSnapshotAsync(db);

        db.ChangeTracker.Clear();
        await SeedDataService.SeedAsync(db);
        var secondSnapshot = await CreateSnapshotAsync(db);

        Assert.Equal(firstSnapshot, secondSnapshot);

        Assert.Equal(2, firstSnapshot.Roles);
        Assert.Equal(RolePermissionCatalog.AllSections.Length * firstSnapshot.Roles, firstSnapshot.RolePermissions);
        Assert.Equal(firstSnapshot.RolePermissions, firstSnapshot.DistinctRolePermissionPairs);
        Assert.Equal(1, firstSnapshot.AdminUsers);
        Assert.True(await db.DocumentStatuses.AnyAsync(item => item.Code == DocumentWorkflowStatus.RequiresClientSignature));

        var userPaymentsPermission = await db.RolePermissions
            .AsNoTracking()
            .SingleAsync(item => item.Role != null && item.Role.Name == "user" && item.Section == RolePermissionCatalog.Payments);
        var adminPaymentsPermission = await db.RolePermissions
            .AsNoTracking()
            .SingleAsync(item => item.Role != null && item.Role.Name == "admin" && item.Section == RolePermissionCatalog.Payments);

        Assert.False(userPaymentsPermission.CanView);
        Assert.False(userPaymentsPermission.CanCreate);
        Assert.True(adminPaymentsPermission.CanView);
        Assert.True(adminPaymentsPermission.CanCreate);
    }

    [Fact]
    public async Task SeedAsync_ForLawyerRoleAddsSafeOperationalPermissions()
    {
        await using var db = await CreateContextAsync();
        db.Roles.Add(new Role { Name = "\u042e\u0440\u0438\u0441\u0442", Description = "\u0422\u0435\u0441\u0442\u043e\u0432\u0430\u044f \u0440\u043e\u043b\u044c \u044e\u0440\u0438\u0441\u0442\u0430" });
        await db.SaveChangesAsync();

        await SeedDataService.SeedAsync(db);

        var lawyerRole = await db.Roles
            .AsNoTracking()
            .SingleAsync(item => item.Name == "\u042e\u0440\u0438\u0441\u0442");
        var permissions = await db.RolePermissions
            .AsNoTracking()
            .Where(item => item.RoleId == lawyerRole.Id)
            .ToDictionaryAsync(item => item.Section);

        Assert.Equal(RolePermissionCatalog.AllSections.Length, permissions.Count);
        Assert.True(permissions[RolePermissionCatalog.Tasks].CanView);
        Assert.True(permissions[RolePermissionCatalog.Tasks].CanCreate);
        Assert.True(permissions[RolePermissionCatalog.Calendar].CanView);
        Assert.True(permissions[RolePermissionCatalog.Clients].CanView);
        Assert.True(permissions[RolePermissionCatalog.Cases].CanView);
        Assert.True(permissions[RolePermissionCatalog.Notifications].CanView);
        Assert.Equal(DataVisibilityScope.Own, permissions[RolePermissionCatalog.Tasks].ViewScope);
        Assert.Equal(DataVisibilityScope.Own, permissions[RolePermissionCatalog.Clients].ViewScope);
        Assert.Equal(DataVisibilityScope.Own, permissions[RolePermissionCatalog.Cases].ViewScope);

        foreach (var section in new[]
        {
            RolePermissionCatalog.Payments,
            RolePermissionCatalog.Accounts,
            RolePermissionCatalog.Acts,
            RolePermissionCatalog.Contracts,
            RolePermissionCatalog.Reports,
            RolePermissionCatalog.Roles,
            RolePermissionCatalog.Audit,
            RolePermissionCatalog.Integrations,
            RolePermissionCatalog.YandexDisk,
            RolePermissionCatalog.S3Storage,
        })
        {
            Assert.False(permissions[section].CanView);
            Assert.False(permissions[section].CanCreate);
            Assert.False(permissions[section].CanEdit);
            Assert.False(permissions[section].CanDelete);
            Assert.False(permissions[section].CanExport);
        }
    }

    [Fact]
    public async Task SeedAsync_ForLawyerRoleDoesNotOverwriteExistingManualPermissions()
    {
        await using var db = await CreateContextAsync();
        var role = new Role { Name = "lawyer", Description = "Manual lawyer role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();

        db.RolePermissions.Add(new RolePermission
        {
            RoleId = role.Id,
            Section = RolePermissionCatalog.Payments,
            CanView = true,
            CanCreate = true,
            CanEdit = false,
            CanDelete = false,
            CanExport = false,
            ViewScope = DataVisibilityScope.Own,
        });
        await db.SaveChangesAsync();

        await SeedDataService.SeedAsync(db);

        var paymentPermission = await db.RolePermissions
            .AsNoTracking()
            .SingleAsync(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Payments);
        var accountsPermission = await db.RolePermissions
            .AsNoTracking()
            .SingleAsync(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Accounts);
        var tasksPermission = await db.RolePermissions
            .AsNoTracking()
            .SingleAsync(item => item.RoleId == role.Id && item.Section == RolePermissionCatalog.Tasks);

        Assert.True(paymentPermission.CanView);
        Assert.True(paymentPermission.CanCreate);
        Assert.False(accountsPermission.CanView);
        Assert.True(tasksPermission.CanView);
        Assert.True(tasksPermission.CanCreate);
        Assert.Equal(RolePermissionCatalog.AllSections.Length, await db.RolePermissions.CountAsync(item => item.RoleId == role.Id));
    }

    private static async Task<SeedSnapshot> CreateSnapshotAsync(PaymentContext db)
    {
        var rolePermissionPairs = await db.RolePermissions
            .AsNoTracking()
            .Select(item => new { item.RoleId, item.Section })
            .ToListAsync();

        return new SeedSnapshot(
            Roles: await db.Roles.CountAsync(),
            RolePermissions: rolePermissionPairs.Count,
            DistinctRolePermissionPairs: rolePermissionPairs
                .DistinctBy(item => (item.RoleId, item.Section))
                .Count(),
            AdminUsers: await db.Users.CountAsync(item => item.Email == "admin@payplanner.local"),
            DealTypes: await db.DealTypes.CountAsync(),
            IncomeTypes: await db.IncomeTypes.CountAsync(),
            PaymentSources: await db.PaymentSources.CountAsync(),
            PaymentStatuses: await db.PaymentStatuses.CountAsync(),
            DocumentStatuses: await db.DocumentStatuses.CountAsync(),
            Countries: await db.Countries.CountAsync(),
            Timezones: await db.Timezones.CountAsync(),
            Clients: await db.Clients.CountAsync(),
            Cases: await db.ClientCases.CountAsync(),
            Payments: await db.Payments.CountAsync());
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

    private sealed record SeedSnapshot(
        int Roles,
        int RolePermissions,
        int DistinctRolePermissionPairs,
        int AdminUsers,
        int DealTypes,
        int IncomeTypes,
        int PaymentSources,
        int PaymentStatuses,
        int DocumentStatuses,
        int Countries,
        int Timezones,
        int Clients,
        int Cases,
        int Payments);
}
