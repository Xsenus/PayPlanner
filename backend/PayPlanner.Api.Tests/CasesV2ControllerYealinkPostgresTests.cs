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
    [InlineData("client", "actor")]
    [InlineData("external", "actor")]
    [InlineData("client", "role")]
    [InlineData("external", "role")]
    [InlineData("client", "source")]
    [InlineData("external", "source")]
    public async Task YealinkBoundary_PgPinsApprovalAdminRoleAndUpdatedSources(string kind, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedYealinkAsync(seed); var called = false; var committed = false; string? blocked = null;
        var hook = new YealinkBeforeSave(async ct => { called = true; await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); try { if (target == "actor") await concurrent.Users.Where(x => x.Id == data.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false), ct); else if (target == "role") await concurrent.Roles.Where(x => x.Id == data.Actor.RoleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "lawyer"), ct); else if (kind == "client") await concurrent.Clients.Where(x => x.Id == data.Client).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct); else await concurrent.ExternalContacts.Where(x => x.Id == data.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct); await tx.CommitAsync(ct); committed = true; } catch (Exception error) { Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException; if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } pg) blocked = pg.SqlState; else throw; } });
        await using var request = Open(hook); var result = await RunYealinkAsync(request, data, kind); Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"kind={kind}; target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}"); Assert.IsType<OkObjectResult>(result);
    }
    [Theory]
    [InlineData("client", "source")]
    [InlineData("external", "source")]
    [InlineData("client", "actor")]
    [InlineData("external", "role")]
    public async Task YealinkBoundary_PgBusyDependencyRejectsWithoutPartialFile(string kind, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await using var seed = Open(); var data = await SeedYealinkAsync(seed); var before = await PartySnapshotAsync(seed); await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync();
        if (target == "actor") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {data.Actor.Id} FOR UPDATE"); else if (target == "role") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Roles\" WHERE \"Id\" = {data.Actor.RoleId} FOR UPDATE"); else if (kind == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {data.Client} FOR UPDATE"); else await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {data.Contact} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await RunYealinkAsync(request, data, kind)); Assert.Equal(before, await PartySnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData("new-client")]
    [InlineData("new-external")]
    public async Task YealinkBoundary_PgConcurrentImportsKeepOneContactPerPhone(string kind)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedYealinkAsync(seed); IActionResult? competing = null;
        var hook = new YealinkBeforeSave(async ct => { await using var concurrent = Open(); competing = await RunYealinkAsync(concurrent, data, kind, ct: ct); }); await using var request = Open(hook); var result = await RunYealinkAsync(request, data, kind); await using var verify = Open(); var first = await verify.Clients.CountAsync(x => x.Phone == "79001110099") + await verify.ExternalContacts.CountAsync(x => x.Phone == "79001110099"); var second = await verify.ExternalContacts.CountAsync(x => x.Phone == "79001110077"); Assert.True(competing is OkObjectResult && result is ConflictObjectResult && first == 1 && second == 1, $"kind={kind}; competing={competing?.GetType().Name}; outer={result?.GetType().Name}; first={first}; second={second}");
    }
    [Theory]
    [InlineData("client")]
    [InlineData("external")]
    public async Task YealinkBoundary_PgImportFirstBlocksActualDeletion(string kind)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedYealinkAsync(seed); IActionResult? deletion = null;
        var hook = new YealinkBeforeSave(async ct => { await using var concurrent = Open(); deletion = kind == "client" ? await ClientsControllerTests.AtomicCreateController(concurrent, data.Actor.Id).Delete(data.Client, ct) : await RunExternalWriteAsync(concurrent, data, 2, ct: ct); }); await using var request = Open(hook); var result = await RunYealinkAsync(request, data, kind); await using var verify = Open(); var active = kind == "client" ? await verify.Clients.AnyAsync(x => x.Id == data.Client) : await verify.ExternalContacts.AnyAsync(x => x.Id == data.Contact); Assert.True(deletion is ConflictObjectResult && active, $"kind={kind}; deletion={deletion?.GetType().Name}; active={active}; result={result?.GetType().Name}"); Assert.IsType<OkObjectResult>(result);
    }
    [Theory]
    [InlineData("client")]
    [InlineData("external")]
    public async Task YealinkBoundary_PgDeletionFirstRejectsImportWithoutPartialFile(string kind)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedYealinkAsync(seed); IActionResult? import = null;
        async Task Compete(CancellationToken ct) { await using var concurrent = Open(); import = await RunYealinkAsync(concurrent, data, kind, ct: ct); }
        IInterceptor hook = kind == "client" ? new ClientBatchBeforeSave(Compete) : new ExternalWriteBeforeSave(Compete); await using var request = Open(hook); Assert.IsType<NoContentResult>(kind == "client" ? await ClientsControllerTests.AtomicCreateController(request, data.Actor.Id).Delete(data.Client, default) : await RunExternalWriteAsync(request, data, 2)); Assert.IsType<ConflictObjectResult>(import); await using var verify = Open(); Assert.False(await verify.ExternalContacts.AnyAsync(x => x.Phone == "79001110077"));
    }
    private sealed class YealinkBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { if (!called && data.Context!.ChangeTracker.Entries().Any(x => x.Entity is Client or ExternalContact && x.State is EntityState.Added or EntityState.Modified)) { called = true; await action(ct); } return result; }
    }
}
