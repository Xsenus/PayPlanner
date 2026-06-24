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
