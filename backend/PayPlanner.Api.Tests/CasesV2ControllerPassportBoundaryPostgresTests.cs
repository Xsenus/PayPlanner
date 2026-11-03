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
    [Theory]
    [InlineData("actor")]
    [InlineData("permission")]
    [InlineData("client")]
    [InlineData("passport")]
    [InlineData("department")]
    [InlineData("first-permission")]
    public async Task PassportBoundary_PgPinsAccessParentOwnerAndPassport(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, passport) = await SeedPassportBoundaryAsync(seed, true); var other = await CreateUserAsync(seed, "passport-race@example.local", "Другой сотрудник");
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Отдел паспорта" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Department));
            await seed.Users.Where(row => row.Id == actor.Id || row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id));
        }
        if (target == "first-permission") await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        var called = false; var committed = false; string? blocked = null;
        var hook = new PassportBoundaryBeforeSave(async ct =>
        {
            called = true; await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            try
            {
                if (target == "actor") await concurrent.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false), ct);
                if (target == "permission") await concurrent.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
                if (target == "client") await concurrent.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id), ct);
                if (target == "passport") await concurrent.ClientPassports.Where(row => row.Id == passport).ExecuteUpdateAsync(set => set.SetProperty(row => row.Number, "CHANGED"), ct);
                if (target == "department") await concurrent.Users.Where(row => row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                if (target == "first-permission") { concurrent.RolePermissions.Add(new RolePermission { RoleId = actor.RoleId, Section = RolePermissionCatalog.Clients, CanView = true, CanEdit = false }); await concurrent.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null;
        try { result = await PassportBoundaryController(request, actor).Upsert(client, PassportBoundaryInput(), default); } catch (Exception caught) { fault = caught; }
        Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}; error={fault?.GetType().Name}"); Assert.Null(fault); Assert.IsType<OkObjectResult>(result); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open(); Assert.Equal("999999", await verify.ClientPassports.Where(row => row.Id == passport).Select(row => row.Number).SingleAsync());
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("permission")]
    [InlineData("client")]
    [InlineData("passport")]
    [InlineData("owner")]
    public async Task PassportBoundary_PgRefusesBusyRowsBeforeProfileMutation(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, _, client, passport) = await SeedPassportBoundaryAsync(seed, true); var before = await ClientBatchSnapshotAsync(seed);
        var owner = actor.Id;
        if (target == "owner")
        {
            var other = await CreateUserAsync(seed, "passport-owner@example.local", "Владелец профиля"); owner = other.Id;
            var department = new DepartmentDictionaryItem { Name = "Общий отдел" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.Users.Where(row => row.Id == actor.Id || row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id));
            await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Department));
            await seed.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id)); before = await ClientBatchSnapshotAsync(seed);
        }
        await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (target == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {client} FOR UPDATE");
        else if (target == "passport") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientPassports\" WHERE \"Id\" = {passport} FOR UPDATE");
        else if (target == "permission") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {actor.RoleId} FOR UPDATE");
        else { var id = target == "actor" ? actor.Id : owner; await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {id} FOR UPDATE"); }
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await PassportBoundaryController(request, actor).Upsert(client, PassportBoundaryInput(), default)); Assert.Equal(before, await ClientBatchSnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }

    [Fact]
    public async Task PassportBoundary_PgConcurrentFirstPassportCreatesOnlyOnePrimary()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(seed, false); var called = false; IActionResult? competing = null;
        var hook = new PassportBoundaryBeforeSave(async ct => { called = true; await using var concurrent = Open(); competing = await PassportBoundaryController(concurrent, actor).Upsert(client, PassportBoundaryInput(), ct); });
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null; try { result = await PassportBoundaryController(request, actor).Upsert(client, PassportBoundaryInput(), default); } catch (Exception caught) { fault = caught; }
        Assert.True(called); Assert.True(competing is ConflictObjectResult, $"competing={competing?.GetType().Name}; error={fault?.GetType().Name}"); Assert.Null(fault); Assert.IsType<OkObjectResult>(result); await using var verify = Open(); Assert.Equal(1, await verify.ClientPassports.CountAsync(row => row.ClientId == client && row.IsPrimary));
    }

    [Fact]
    public async Task PassportBoundary_PgUpsertBlocksParentDeletionUntilCommit()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(seed, false); var called = false; IActionResult? deletion = null;
        var hook = new PassportBoundaryBeforeSave(async ct => { called = true; await using var concurrent = Open(); deletion = await ClientsControllerTests.AtomicCreateController(concurrent, actor.Id).Delete(client, ct); });
        await using var request = Open(hook); Assert.IsType<OkObjectResult>(await PassportBoundaryController(request, actor).Upsert(client, PassportBoundaryInput(), default)); Assert.True(called); Assert.IsType<ConflictObjectResult>(deletion);
        await using var verify = Open(); Assert.True(await verify.Clients.AnyAsync(row => row.Id == client)); Assert.Equal(1, await verify.ClientPassports.CountAsync(row => row.ClientId == client));
    }

    [Fact]
    public async Task PassportBoundary_PgDeletionAlreadyInFlightRejectsPassportBeforeInsert()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(seed, false); var called = false; IActionResult? passport = null;
        var hook = new ClientBatchBeforeSave(async ct => { called = true; await using var concurrent = Open(); passport = await PassportBoundaryController(concurrent, actor).Upsert(client, PassportBoundaryInput(), ct); });
        await using var request = Open(hook); Assert.IsType<NoContentResult>(await ClientsControllerTests.AtomicCreateController(request, actor.Id).Delete(client, default)); Assert.True(called); Assert.IsType<ConflictObjectResult>(passport);
        await using var verify = Open(); Assert.False(await verify.Clients.AnyAsync(row => row.Id == client)); Assert.False(await verify.ClientPassports.AnyAsync(row => row.ClientId == client));
    }

    [Fact]
    public async Task PassportBoundary_PgPreviouslyTrackedRevokedEditDoesNotAuthorizeWrite()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedPassportBoundaryAsync(seed, true); await using var request = Open(); await request.Users.Include(row => row.Role)!.ThenInclude(row => row!.Permissions).SingleAsync(row => row.Id == actor.Id);
        await using (var concurrent = Open()) await concurrent.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        var before = await ClientBatchSnapshotAsync(request); Assert.IsType<ForbidResult>(await PassportBoundaryController(request, actor).Upsert(client, PassportBoundaryInput(), default)); Assert.Equal(before, await ClientBatchSnapshotAsync(request));
    }

    private sealed class PassportBoundaryBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { if (!called && eventData.Context!.ChangeTracker.Entries<ClientPassport>().Any(entry => entry.State is EntityState.Added or EntityState.Modified)) { called = true; await action(cancellationToken); } return result; }
    }
}
