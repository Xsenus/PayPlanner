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
    [InlineData("client-permission")]
    [InlineData("client")]
    [InlineData("client-owner")]
    [InlineData("legal-permission")]
    [InlineData("legal")]
    [InlineData("legal-owner")]
    [InlineData("first-permission")]
    public async Task ClientContactBoundary_PgPinsApprovalBothSectionsAndVisibleParents(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, legal) = await SeedClientContactBoundaryAsync(seed); var other = await CreateUserAsync(seed, "contact-race@example.local", "Другой владелец"); await PrepareClientContactScopeAsync(seed, actor, other, client, legal, target);
        if (target == "first-permission") await seed.RolePermissions.Where(x => x.RoleId == actor.RoleId).ExecuteDeleteAsync();
        var called = false; var committed = false; string? blocked = null;
        var hook = new ClientBatchBeforeSave(async ct =>
        {
            called = true; await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            try
            {
                if (target == "actor") await concurrent.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsApproved, false), ct);
                if (target == "client-permission") await concurrent.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanEdit, false), ct);
                if (target == "legal-permission") await concurrent.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == RolePermissionCatalog.LegalEntities).ExecuteUpdateAsync(s => s.SetProperty(x => x.CanView, false), ct);
                if (target == "client") await concurrent.Clients.Where(x => x.Id == client).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id), ct);
                if (target == "legal") await concurrent.LegalEntities.Where(x => x.Id == legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id), ct);
                if (target is "client-owner" or "legal-owner") await concurrent.Users.Where(x => x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, (int?)null).SetProperty(x => x.Department, "Другой отдел"), ct);
                if (target == "first-permission") { concurrent.RolePermissions.Add(new RolePermission { RoleId = actor.RoleId, Section = RolePermissionCatalog.Clients, CanView = true, CanEdit = false }); await concurrent.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); ActionResult<PayPlanner.Api.Models.Responses.ContactDirectoryItemDto>? result = null; Exception? fault = null;
        try { result = await ClientContactController(request, actor).UpdateClientContact(client, ClientContactInput(legal), default); } catch (Exception caught) { fault = caught; }
        Assert.True(called); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.Result?.GetType().Name}; error={fault?.GetType().Name}"); Assert.Null(fault); Assert.IsType<OkObjectResult>(result!.Result); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open(); Assert.Equal("Новое имя", await verify.Clients.Where(x => x.Id == client).Select(x => x.Name).SingleAsync()); Assert.Equal(legal, await verify.Clients.Where(x => x.Id == client).Select(x => x.LegalEntityId).SingleAsync());
    }

    [Theory]
    [InlineData("client")]
    [InlineData("client-permission")]
    [InlineData("legal")]
    [InlineData("legal-permission")]
    [InlineData("client-owner")]
    [InlineData("legal-owner")]
    public async Task ClientContactBoundary_PgRefusesBusyDependenciesWithoutSaving(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, _, client, legal) = await SeedClientContactBoundaryAsync(seed); var other = await CreateUserAsync(seed, "contact-busy@example.local", "Другой владелец"); await PrepareClientContactScopeAsync(seed, actor, other, client, legal, target); var before = await ClientContactSnapshotAsync(seed);
        await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (target == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {client} FOR UPDATE");
        else if (target == "legal") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legal} FOR UPDATE");
        else if (target is "client-permission" or "legal-permission") { var section = target == "client-permission" ? RolePermissionCatalog.Clients : RolePermissionCatalog.LegalEntities; await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"RolePermissions\" WHERE \"RoleId\" = {actor.RoleId} AND \"Section\" = {section} FOR UPDATE"); }
        else await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {other.Id} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>((await ClientContactController(request, actor).UpdateClientContact(client, ClientContactInput(legal), default)).Result); Assert.Equal(before, await ClientContactSnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }

    [Fact]
    public async Task ClientContactBoundary_PgUpdateBlocksParentDeletionUntilCommit()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(seed); var called = false; IActionResult? deletion = null;
        var hook = new ClientBatchBeforeSave(async ct => { called = true; await using var concurrent = Open(); deletion = await ClientsControllerTests.AtomicCreateController(concurrent, actor.Id).Delete(client, ct); });
        await using var request = Open(hook); Assert.IsType<OkObjectResult>((await ClientContactController(request, actor).UpdateClientContact(client, ClientContactInput(), default)).Result); Assert.True(called); Assert.IsType<ConflictObjectResult>(deletion);
        await using var verify = Open(); Assert.True(await verify.Clients.AnyAsync(x => x.Id == client && x.Name == "Новое имя"));
    }

    [Fact]
    public async Task ClientContactBoundary_PgDeletionAlreadyInFlightRejectsUpdateBeforeSql()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, client, _) = await SeedClientContactBoundaryAsync(seed); var called = false; IActionResult? update = null;
        var hook = new ClientBatchBeforeSave(async ct => { called = true; await using var concurrent = Open(); update = (await ClientContactController(concurrent, actor).UpdateClientContact(client, ClientContactInput(), ct)).Result; });
        await using var request = Open(hook); Assert.IsType<NoContentResult>(await ClientsControllerTests.AtomicCreateController(request, actor.Id).Delete(client, default)); Assert.True(called); Assert.IsType<ConflictObjectResult>(update);
        await using var verify = Open(); Assert.False(await verify.Clients.AnyAsync(x => x.Id == client)); Assert.Equal("Прежнее имя", await verify.Clients.IgnoreQueryFilters().Where(x => x.Id == client).Select(x => x.Name).SingleAsync());
    }

    private static async Task PrepareClientContactScopeAsync(PaymentContext db, User actor, User other, int client, int legal, string target)
    {
        await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && (x.Section == RolePermissionCatalog.Clients || x.Section == RolePermissionCatalog.LegalEntities)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Own));
        if (target is not ("client-owner" or "legal-owner")) return;
        var department = new DepartmentDictionaryItem { Name = "Отдел контакта" }; db.Departments.Add(department); await db.SaveChangesAsync(); await db.Users.Where(x => x.Id == actor.Id || x.Id == other.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DepartmentId, department.Id).SetProperty(x => x.Department, department.Name));
        var section = target == "client-owner" ? RolePermissionCatalog.Clients : RolePermissionCatalog.LegalEntities; await db.RolePermissions.Where(x => x.RoleId == actor.RoleId && x.Section == section).ExecuteUpdateAsync(s => s.SetProperty(x => x.ViewScope, DataVisibilityScope.Department));
        if (target == "client-owner") await db.Clients.Where(x => x.Id == client).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id)); else await db.LegalEntities.Where(x => x.Id == legal).ExecuteUpdateAsync(s => s.SetProperty(x => x.ResponsibleUserId, other.Id));
    }
}
