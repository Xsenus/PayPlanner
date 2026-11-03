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
    public static IEnumerable<object[]> PromotionPinCases() => new[] { "actor", "permission", "source", "source-delete", "source-owner", "legal", "legal-permission", "legal-owner", "parent", "parent-owner", "status", "first-permission" }.Select(x => new object[] { x });
    [Theory]
    [MemberData(nameof(PromotionPinCases))]
    public async Task ClientPromotionBoundary_PgPinsApprovalSourceDependenciesAndSelectedStatus(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var other = await CreateUserAsync(seed, "promotion-race@example.local", "Другой владелец"); await PreparePromotionScopeAsync(seed, data, other, target); if (target == "first-permission") await seed.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId).ExecuteDeleteAsync();
        var called = false; var committed = false; string? blocked = null;
        var hook = new PromotionBeforeSave(async ct =>
        {
            called = true; await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            try
            {
                if (target == "actor") await concurrent.Users.Where(x => x.Id == data.Actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false), ct);
                if (target == "permission") await concurrent.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanCreate, false), ct);
                if (target == "source") await concurrent.ExternalContacts.Where(x => x.Id == data.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id), ct);
                if (target == "source-delete") await concurrent.ExternalContacts.Where(x => x.Id == data.Contact).ExecuteUpdateAsync(s => s.SetProperty(x => EF.Property<bool>(x, "IsDeleted"), true), ct);
                if (target == "legal") await concurrent.LegalEntities.Where(x => x.Id == data.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id), ct);
                if (target == "legal-permission") await concurrent.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false), ct);
                if (target == "parent") await concurrent.ExternalContacts.Where(x => x.Id == data.Parent).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id), ct);
                if (target is "source-owner" or "legal-owner" or "parent-owner") await concurrent.Users.Where(x => x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, (int?)null).SetProperty(x => x.Department, "Другой отдел").SetProperty(x => x.IsActive, target != "source-owner"), ct);
                if (target == "status") await concurrent.ClientStatuses.Where(x => x.Id == data.Status).ExecuteUpdateAsync(s => s.SetProperty(x => x.SystemKey, "test.changed.supplier"), ct);
                if (target == "first-permission") { concurrent.RolePermissions.Add(new RolePermission { RoleId = data.Actor.RoleId, Section = RolePermissionCatalog.Clients, CanView = true, CanCreate = false }); await concurrent.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null; try { result = (await ClientContactController(request, data.Actor).PromoteExternalContact(data.Contact, PromotionInput(), default)).Result; } catch (Exception caught) { fault = caught; }
        Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}; error={fault?.GetType().Name}"); Assert.Null(fault); Assert.IsType<OkObjectResult>(result); Assert.Null(request.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData("source")]
    [InlineData("permission")]
    [InlineData("source-owner")]
    [InlineData("legal")]
    [InlineData("legal-permission")]
    [InlineData("legal-owner")]
    [InlineData("parent")]
    [InlineData("parent-owner")]
    [InlineData("status")]
    public async Task ClientPromotionBoundary_PgRefusesBusyDependenciesBeforeCreatingEitherRow(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options); await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var other = await CreateUserAsync(seed, "promotion-busy@example.local", "Другой владелец"); await PreparePromotionScopeAsync(seed, data, other, target); var before = await PromotionSnapshotAsync(seed);
        await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (target is "source" or "parent") { var id = target == "source" ? data.Contact : data.Parent; await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"Id\" = {id} FOR UPDATE"); }
        else if (target is "permission" or "legal-permission") { var section = target == "permission" ? RolePermissionCatalog.Clients : RolePermissionCatalog.LegalEntities; await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {data.Actor.RoleId} AND \"Section\" = {section} FOR UPDATE"); }
        else if (target == "legal") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {data.Legal} FOR UPDATE");
        else if (target == "status") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientStatuses\" WHERE \"Id\" = {data.Status} FOR UPDATE");
        else await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {other.Id} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>((await ClientContactController(request, data.Actor).PromoteExternalContact(data.Contact, PromotionInput(), default)).Result); Assert.Equal(before, await PromotionSnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }
    [Fact]
    public async Task ClientPromotionBoundary_PgConcurrentPromotionKeepsOneClientWithoutOrphan()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options); await using var seed = Open(); var data = await SeedClientPromotionAsync(seed); var count = await seed.Clients.CountAsync(); var called = false; IActionResult? competing = null;
        var hook = new PromotionBeforeSave(async ct => { called = true; await using var concurrent = Open(); competing = (await ClientContactController(concurrent, data.Actor).PromoteExternalContact(data.Contact, PromotionInput(), ct)).Result; });
        await using var request = Open(hook); Assert.IsType<OkObjectResult>((await ClientContactController(request, data.Actor).PromoteExternalContact(data.Contact, PromotionInput(), default)).Result); await using var verify = Open(); var delta = await verify.Clients.CountAsync() - count; Assert.True(called); Assert.True(competing is ConflictObjectResult && delta == 1, $"competing={competing?.GetType().Name}; newClients={delta}"); var id = await verify.ExternalContacts.Where(x => x.Id == data.Contact).Select(x => x.LinkedClientId).SingleAsync(); Assert.True(await verify.Clients.AnyAsync(x => x.Id == id)); await AssertBulkCasesUnchangedAsync(verify, data.Cases);
    }
    private static async Task PreparePromotionScopeAsync(PaymentContext db, PromotionSeed data, User other, string target)
    {
        await db.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId && (x.Section == RolePermissionCatalog.Clients || x.Section == RolePermissionCatalog.LegalEntities)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own));
        if (target is not ("source-owner" or "legal-owner" or "parent-owner")) return;
        var department = new DepartmentDictionaryItem { Name = "Отдел превращения контакта" }; db.Departments.Add(department); await db.SaveChangesAsync(); await db.Users.Where(x => x.Id == data.Actor.Id || x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, department.Id).SetProperty(x => x.Department, department.Name)); var section = target == "legal-owner" ? RolePermissionCatalog.LegalEntities : RolePermissionCatalog.Clients; await db.RolePermissions.Where(x => x.RoleId == data.Actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Department));
        if (target == "legal-owner") await db.LegalEntities.Where(x => x.Id == data.Legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); else { var id = target == "source-owner" ? data.Contact : data.Parent; await db.ExternalContacts.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); }
    }
    private sealed class PromotionBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default) { if (!called && data.Context!.ChangeTracker.Entries<Client>().Any(x => x.State == EntityState.Added)) { called = true; await action(ct); } return result; }
    }
}
