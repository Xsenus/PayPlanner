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
    [InlineData("client")]
    [InlineData("externalContact")]
    public async Task PartyBoundary_PgAttachFirstBlocksActualSourceDeletion(string kind)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedPartyBoundaryAsync(seed, kind); var called = false; IActionResult? deletion = null;
        var hook = new PartyBeforeSave(async ct => { called = true; await using var concurrent = Open(); deletion = kind == "client" ? await ClientsControllerTests.AtomicCreateController(concurrent, data.Source.Actor.Id).Delete(data.SourceId, ct) : await RunExternalWriteAsync(concurrent, data.Source, 2, ct: ct); }); await using var request = Open(hook); Assert.IsType<CreatedResult>(await RunPartyAsync(request, data, 0)); await using var verify = Open(); var active = kind == "client" ? await verify.Clients.AnyAsync(x => x.Id == data.SourceId) : await verify.ExternalContacts.AnyAsync(x => x.Id == data.SourceId); Assert.True(called); Assert.True(deletion is ConflictObjectResult && active, $"kind={kind}; deletion={deletion?.GetType().Name}; activeSource={active}"); Assert.True(await verify.ClientCaseRelatedParties.AnyAsync(x => x.ClientCaseId == data.Case && x.SourceType == kind));
    }
    [Theory]
    [InlineData("client")]
    [InlineData("externalContact")]
    public async Task PartyBoundary_PgDeletionFirstRejectsAttachBeforeInsert(string kind)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedPartyBoundaryAsync(seed, kind); var called = false; IActionResult? attachment = null;
        async Task Compete(CancellationToken ct) { called = true; await using var concurrent = Open(); attachment = await RunPartyAsync(concurrent, data, 0, ct); }
        IInterceptor hook = kind == "client" ? new ClientBatchBeforeSave(Compete) : new ExternalWriteBeforeSave(Compete); await using var request = Open(hook); Assert.IsType<NoContentResult>(kind == "client" ? await ClientsControllerTests.AtomicCreateController(request, data.Source.Actor.Id).Delete(data.SourceId, default) : await RunExternalWriteAsync(request, data.Source, 2)); Assert.True(called); Assert.IsType<ConflictObjectResult>(attachment); await using var verify = Open(); Assert.Empty(await verify.ClientCaseRelatedParties.Where(x => x.ClientCaseId == data.Case).ToArrayAsync());
    }
    [Theory]
    [InlineData("client", 0)]
    [InlineData("externalContact", 0)]
    [InlineData("legalEntity", 0)]
    [InlineData("externalContact", 1)]
    public async Task PartyBoundary_PgConcurrentAttachOrRestoreKeepsOneCurrentIdentity(string kind, int op)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedPartyBoundaryAsync(seed, kind, op); var called = false; IActionResult? competing = null;
        var hook = new PartyBeforeSave(async ct => { called = true; await using var concurrent = Open(); competing = await RunPartyAsync(concurrent, data, op, ct); }); await using var request = Open(hook); var result = await RunPartyAsync(request, data, op); await using var verify = Open(); var count = await verify.ClientCaseRelatedParties.CountAsync(x => x.ClientCaseId == data.Case && x.SourceType == kind && !x.IsArchived); Assert.True(called); Assert.True(competing is ConflictObjectResult && count == 1, $"kind={kind}; op={op}; competing={competing?.GetType().Name}; currentIdentities={count}"); Assert.True(op == 0 ? result is CreatedResult : result is OkObjectResult);
    }
    [Theory]
    [InlineData("client", 0, "actor")]
    [InlineData("externalContact", 1, "case-permission")]
    [InlineData("client", 0, "source-permission")]
    [InlineData("legalEntity", 0, "source-permission")]
    [InlineData("externalContact", 0, "case")]
    [InlineData("client", 0, "source")]
    [InlineData("externalContact", 1, "source")]
    [InlineData("legalEntity", 1, "source")]
    [InlineData("externalContact", 0, "linked-legal")]
    [InlineData("externalContact", 0, "linked-legal-permission")]
    [InlineData("client", 0, "source-owner")]
    [InlineData("legalEntity", 0, "source-owner")]
    [InlineData("externalContact", 1, "party")]
    public async Task PartyBoundary_PgPinsCaseSourcePermissionsOwnerAndRestoreRow(string kind, int op, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedPartyBoundaryAsync(seed, kind, op); var other = await CreateUserAsync(seed, "party-race@example.local", "Другой владелец"); await PreparePartyOwnerAsync(seed, data, other, target); var called = false; var committed = false; string? blocked = null;
        var hook = new PartyBeforeSave(async ct =>
        {
            called = true; await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); try
            {
                var section = target == "case-permission" ? RolePermissionCatalog.Cases : kind == "legalEntity" || target == "linked-legal-permission" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients;
                if (target == "actor") await concurrent.Users.Where(x => x.Id == data.Source.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false), ct);
                if (target is "case-permission" or "source-permission" or "linked-legal-permission") await concurrent.RolePermissions.Where(x => x.RoleId == data.Source.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false).SetProperty(x => x.CanEdit, false), ct);
                if (target == "case") await concurrent.ClientCases.Where(x => x.Id == data.Case).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct);
                if (target == "source") { if (kind == "client") await concurrent.Clients.Where(x => x.Id == data.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct); else if (kind == "externalContact") await concurrent.ExternalContacts.Where(x => x.Id == data.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct); else await concurrent.LegalEntities.Where(x => x.Id == data.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct); }
                if (target == "linked-legal") await concurrent.LegalEntities.Where(x => x.Id == data.Source.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ShortName, "Изменено"), ct);
                if (target == "source-owner") await concurrent.Users.Where(x => x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, (int?)null).SetProperty(x => x.Department, "Другой отдел"), ct);
                if (target == "party") await concurrent.ClientCaseRelatedParties.Where(x => x.Id == data.Party).ExecuteUpdateAsync(s => s.SetProperty(x => x.Notes, "Изменено"), ct);
                await tx.CommitAsync(ct); committed = true;
            }
            catch (Exception error) { Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException; if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } p) blocked = p.SqlState; else throw; }
        });
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null; try { result = await RunPartyAsync(request, data, op); } catch (Exception e) { fault = e; }
        Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"kind={kind}; op={op}; target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}; error={fault?.GetType().Name}"); Assert.Null(fault); Assert.True(op == 0 ? result is CreatedResult : result is OkObjectResult);
    }
    [Theory]
    [InlineData("client", 0, "case")]
    [InlineData("client", 0, "source")]
    [InlineData("externalContact", 0, "source")]
    [InlineData("legalEntity", 0, "source")]
    [InlineData("externalContact", 1, "party")]
    [InlineData("externalContact", 1, "permission")]
    [InlineData("client", 0, "owner")]
    public async Task PartyBoundary_PgBusyRowsRejectBeforeMutation(string kind, int op, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await using var seed = Open(); var data = await SeedPartyBoundaryAsync(seed, kind, op); var other = await CreateUserAsync(seed, "party-busy@example.local", "Владелец"); await PreparePartyOwnerAsync(seed, data, other, target == "owner" ? "source-owner" : target); var before = await PartySnapshotAsync(seed); await using var concurrent = Open(); await using var tx = await concurrent.Database.BeginTransactionAsync();
        if (target == "case") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {data.Case} FOR UPDATE");
        else if (target == "party") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseRelatedParties\" WHERE \"Id\" = {data.Party} FOR UPDATE");
        else if (target == "permission") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {data.Source.Actor.RoleId} FOR UPDATE");
        else if (target == "owner") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {other.Id} FOR UPDATE");
        else if (kind == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {data.SourceId} FOR UPDATE");
        else if (kind == "externalContact") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {data.SourceId} FOR UPDATE");
        else await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {data.SourceId} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await RunPartyAsync(request, data, op)); Assert.Equal(before, await PartySnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }
    private static async Task PreparePartyOwnerAsync(PaymentContext db, PartySeed data, User other, string target)
    {
        if (target != "source-owner") return; var department = new DepartmentDictionaryItem { Name = "Отдел стороны дела" }; db.Departments.Add(department); await db.SaveChangesAsync(); await db.Users.Where(x => x.Id == data.Source.Actor.Id || x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, department.Id).SetProperty(x => x.Department, department.Name)); var section = data.Kind == "legalEntity" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients; await db.RolePermissions.Where(x => x.RoleId == data.Source.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Department)); if (data.Kind == "client") await db.Clients.Where(x => x.Id == data.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); else await db.LegalEntities.Where(x => x.Id == data.SourceId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id));
    }
    private sealed class PartyBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { if (!called && data.Context!.ChangeTracker.Entries<ClientCaseRelatedParty>().Any(x => x.State is EntityState.Added or EntityState.Modified)) { called = true; await action(ct); } return result; }
    }
}
