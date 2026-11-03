using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> PoaPinCases()
    {
        foreach (var operation in new[] { 0, 1, 2 }) foreach (var target in new[] { "actor", "permission", "client" }) yield return new object[] { operation, target };
        yield return new object[] { 1, "power" }; yield return new object[] { 0, "department" }; yield return new object[] { 0, "first-permission" };
    }

    [Theory]
    [MemberData(nameof(PoaPinCases))]
    public async Task PoaBoundary_PgPinsCurrentAccessParentOwnerAndPower(int operation, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, power) = await SeedPoaBoundaryAsync(seed, operation != 0); var other = await CreateUserAsync(seed, "poa-race@example.local", "Другой сотрудник");
        if (target == "department") await PreparePoaDepartmentAsync(seed, actor, other, client);
        if (target == "first-permission") await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        var called = false; var committed = false; string? blocked = null;
        var hook = new PoaBoundaryBeforeSave(async ct =>
        {
            called = true; await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            try
            {
                if (target == "actor") await concurrent.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false), ct);
                if (target == "permission") await concurrent.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false).SetProperty(row => row.CanEdit, false).SetProperty(row => row.CanDelete, false), ct);
                if (target == "client") await concurrent.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id), ct);
                if (target == "power") await concurrent.PowersOfAttorney.Where(row => row.Id == power).ExecuteUpdateAsync(set => set.SetProperty(row => row.Number, "CHANGED"), ct);
                if (target == "department") await concurrent.Users.Where(row => row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                if (target == "first-permission") { concurrent.RolePermissions.Add(new RolePermission { RoleId = actor.RoleId, Section = RolePermissionCatalog.Clients, CanView = true, CanCreate = false }); await concurrent.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null;
        try { result = await WritePoaAsync(request, actor, client, power ?? 0, operation); } catch (Exception caught) { fault = caught; }
        Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"operation={operation}; target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}; error={fault?.GetType().Name}"); Assert.Null(fault);
        if (operation == 0) Assert.IsType<CreatedResult>(result); else if (operation == 1) Assert.IsType<OkObjectResult>(result); else Assert.IsType<NoContentResult>(result);
        Assert.Null(request.Database.CurrentTransaction); await using var verify = Open(); Assert.Equal(operation == 2 ? 0 : 1, await verify.PowersOfAttorney.CountAsync(row => row.ClientId == client));
        if (operation != 2) Assert.Equal("NEW-123", await verify.PowersOfAttorney.Where(row => row.ClientId == client).Select(row => row.Number).SingleAsync());
    }

    public static IEnumerable<object[]> PoaBusyCases()
    {
        foreach (var operation in new[] { 0, 1, 2 }) foreach (var target in new[] { "client", "permission" }) yield return new object[] { operation, target };
        yield return new object[] { 1, "power" }; yield return new object[] { 2, "power" }; yield return new object[] { 0, "owner" };
    }

    [Theory]
    [MemberData(nameof(PoaBusyCases))]
    public async Task PoaBoundary_PgRefusesBusyRowsBeforeAnyMutation(int operation, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, _, client, power) = await SeedPoaBoundaryAsync(seed, operation != 0); var owner = actor.Id;
        if (target == "owner") { var other = await CreateUserAsync(seed, "poa-owner@example.local", "Владелец доверенности"); owner = other.Id; await PreparePoaDepartmentAsync(seed, actor, other, client); }
        var before = await PoaSnapshotAsync(seed); await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (target == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {client} FOR UPDATE");
        else if (target == "power") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"PowersOfAttorney\" WHERE \"Id\" = {power} FOR UPDATE");
        else if (target == "permission") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {actor.RoleId} FOR UPDATE");
        else await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {owner} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await WritePoaAsync(request, actor, client, power ?? 0, operation)); Assert.Equal(before, await PoaSnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }

    [Fact]
    public async Task PoaBoundary_PgCreateBlocksParentDeletionUntilCommit()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedPoaBoundaryAsync(seed, false); var called = false; IActionResult? deletion = null;
        var hook = new PoaBoundaryBeforeSave(async ct => { called = true; await using var concurrent = Open(); deletion = await ClientsControllerTests.AtomicCreateController(concurrent, actor.Id).Delete(client, ct); });
        await using var request = Open(hook); Assert.IsType<CreatedResult>(await WritePoaAsync(request, actor, client, 0, 0)); Assert.True(called); Assert.IsType<ConflictObjectResult>(deletion);
        await using var verify = Open(); Assert.True(await verify.Clients.AnyAsync(row => row.Id == client)); Assert.Equal(1, await verify.PowersOfAttorney.CountAsync(row => row.ClientId == client));
    }

    [Fact]
    public async Task PoaBoundary_PgDeletionAlreadyInFlightRejectsCreateBeforeInsert()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedPoaBoundaryAsync(seed, false); var called = false; IActionResult? power = null;
        var hook = new ClientBatchBeforeSave(async ct => { called = true; await using var concurrent = Open(); power = await WritePoaAsync(concurrent, actor, client, 0, 0, ct); });
        await using var request = Open(hook); Assert.IsType<NoContentResult>(await ClientsControllerTests.AtomicCreateController(request, actor.Id).Delete(client, default)); Assert.True(called); Assert.IsType<ConflictObjectResult>(power);
        await using var verify = Open(); Assert.False(await verify.Clients.AnyAsync(row => row.Id == client)); Assert.False(await verify.PowersOfAttorney.AnyAsync(row => row.ClientId == client));
    }

    private static async Task PreparePoaDepartmentAsync(PaymentContext db, User actor, User owner, int client)
    {
        var department = new DepartmentDictionaryItem { Name = "Отдел доверенностей" }; db.Departments.Add(department); await db.SaveChangesAsync();
        await db.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Department));
        await db.Users.Where(row => row.Id == actor.Id || row.Id == owner.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
        await db.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, owner.Id));
    }

    private sealed class PoaBoundaryBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { if (!called && eventData.Context!.ChangeTracker.Entries<PowerOfAttorney>().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)) { called = true; await action(cancellationToken); } return result; }
    }
}
