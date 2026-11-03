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
    [InlineData(0, "actor")]
    [InlineData(0, "permission")]
    [InlineData(0, "legal")]
    [InlineData(0, "parent")]
    [InlineData(1, "source")]
    [InlineData(1, "owner")]
    [InlineData(2, "permission")]
    [InlineData(2, "source")]
    public async Task ExternalWriteBoundary_PgPinsActionAndDependenciesUntilCommit(int op, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var other = await CreateUserAsync(seed, "external-race@example.local", "Другой владелец"); await PrepareExternalWriteOwnerAsync(seed, data, other, target); var called = false; var committed = false; string? blocked = null;
        var hook = new ExternalWriteBeforeSave(async ct =>
        {
            called = true; await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); try
            {
                if (target == "actor") await concurrent.Users.Where(x => x.Id == data.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false), ct);
                if (target == "permission") await concurrent.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false).SetProperty(x => x.CanDelete, false), ct);
                if (target == "legal") await concurrent.LegalEntities.Where(x => x.Id == data.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ShortName, "Изменено"), ct);
                if (target == "parent") await concurrent.ExternalContacts.Where(x => x.Id == data.Parent).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContactType, "Person"), ct);
                if (target == "source") await concurrent.ExternalContacts.Where(x => x.Id == data.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id), ct);
                if (target == "owner") await concurrent.Users.Where(x => x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);
                await tx.CommitAsync(ct); committed = true;
            }
            catch (Exception error) { Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException; if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw; }
        });
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null; try { result = await RunExternalWriteAsync(request, data, op); } catch (Exception e) { fault = e; }
        Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"op={op}; target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}; error={fault?.GetType().Name}"); Assert.Null(fault); Assert.True(op == 0 ? result is CreatedResult : op == 1 ? result is OkObjectResult : result is NoContentResult); Assert.Null(request.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(0, "permission")]
    [InlineData(0, "legal")]
    [InlineData(0, "parent")]
    [InlineData(1, "source")]
    [InlineData(1, "owner")]
    [InlineData(2, "source")]
    public async Task ExternalWriteBoundary_PgRefusesBusyRowsWithoutPartialChange(int op, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var other = await CreateUserAsync(seed, "external-busy@example.local", "Владелец"); await PrepareExternalWriteOwnerAsync(seed, data, other, target); var before = await ExternalWriteSnapshotAsync(seed); await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync();
        if (target is "source" or "parent") { var id = target == "source" ? data.Contact : data.Parent; await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {id} FOR UPDATE"); }
        else if (target == "permission") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {data.Actor.RoleId} FOR UPDATE");
        else if (target == "legal") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {data.Legal} FOR UPDATE");
        else await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {other.Id} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await RunExternalWriteAsync(request, data, op)); Assert.Equal(before, await ExternalWriteSnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public async Task ExternalWriteBoundary_PgOrganizationsCannotBothClaimSameInn(int firstOp, int secondOp)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var input = ExternalWriteInput(data, true); input.Inn = "7700000108"; var called = false; IActionResult? second = null;
        var hook = new ExternalWriteBeforeSave(async ct => { called = true; await using var concurrent = Open(); second = await RunExternalWriteAsync(concurrent, data, secondOp, input, data.Parent, ct: ct); }); await using var request = Open(hook); var first = await RunExternalWriteAsync(request, data, firstOp, input); await using var verify = Open(); var count = await verify.ExternalContacts.CountAsync(x => x.Inn == input.Inn); Assert.True(called); Assert.True(count == 1 && (first is ConflictObjectResult || second is ConflictObjectResult), $"first={first?.GetType().Name}; second={second?.GetType().Name}; matchingInn={count}"); Assert.True(first is CreatedResult or OkObjectResult || second is CreatedResult or OkObjectResult);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExternalWriteBoundary_PgWriteFirstBlocksPromotionWithoutOrphan(int op)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var count = await seed.Clients.CountAsync(); var called = false; IActionResult? promotion = null;
        var hook = new ExternalWriteBeforeSave(async ct => { called = true; await using var concurrent = Open(); promotion = (await ClientContactController(concurrent, data.Actor).PromoteExternalContact(data.Contact, PromotionInput(), ct)).Result; }); await using var request = Open(hook); var result = await RunExternalWriteAsync(request, data, op, ExternalWriteInput(data, true)); await using var verify = Open(); var delta = await verify.Clients.CountAsync() - count; Assert.True(called); Assert.True(promotion is ConflictObjectResult && delta == 0, $"op={op}; promotion={promotion?.GetType().Name}; newClients={delta}"); Assert.True(op == 1 ? result is OkObjectResult : result is NoContentResult);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExternalWriteBoundary_PgPromotionFirstRejectsCompetingWrite(int op)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var called = false; IActionResult? competing = null;
        var hook = new PromotionBeforeSave(async ct => { called = true; await using var concurrent = Open(); competing = await RunExternalWriteAsync(concurrent, data, op, ExternalWriteInput(data, true), ct: ct); }); await using var request = Open(hook); Assert.IsType<OkObjectResult>((await ClientContactController(request, data.Actor).PromoteExternalContact(data.Contact, PromotionInput(), default)).Result); Assert.True(called); Assert.IsType<ConflictObjectResult>(competing); await using var verify = Open(); Assert.True(await verify.ExternalContacts.AnyAsync(x => x.Id == data.Contact && x.ContactType == "Person" && x.LinkedClientId != null));
    }
    private static async Task PrepareExternalWriteOwnerAsync(PaymentContext db, PromotionSeed data, User other, string target)
    {
        await db.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, target == "owner" ? DataVisibilityScope.All : DataVisibilityScope.Own)); if (target == "owner") await db.ExternalContacts.Where(x => x.Id == data.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id));
    }
    private sealed class ExternalWriteBeforeSave(Func<CancellationToken, Task> callback) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { if (!called && data.Context!.ChangeTracker.Entries<ExternalContact>().Any(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)) { called = true; await callback(ct); } return result; }
    }
}
