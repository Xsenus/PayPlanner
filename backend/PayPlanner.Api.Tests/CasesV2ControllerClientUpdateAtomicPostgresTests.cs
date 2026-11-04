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
    public static IEnumerable<object[]> ClientUpdateRaceCases()
    {
        foreach (var route in new[] { 0, 1, 2 })
            foreach (var target in new[] { "actor", "permission", "client" }) yield return new object[] { route, target };
        foreach (var target in new[] { "role", "legal", "status", "assignee", "department", "first-permission" }) yield return new object[] { 2, target };
    }

    [Theory]
    [MemberData(nameof(ClientUpdateRaceCases))]
    public async Task ClientUpdateAtomic_PgPinsFreshAccessTargetAndDependenciesUntilCommit(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, ids, client) = await SeedBirthdayClientAsync(seed);
        var other = await CreateUserAsync(seed, "update-race-other@example.local", "Другой ответственный");
        var legal = new LegalEntity { ShortName = "Организация при изменении", ResponsibleUserId = actor.Id }; var status = new ClientStatusEntity { Name = "Статус при изменении" }; seed.AddRange(legal, status); await seed.SaveChangesAsync();
        await GrantSectionPermissionAsync(seed, actor, RolePermissionCatalog.LegalEntities, viewScope: target == "department" ? DataVisibilityScope.Department : DataVisibilityScope.All);
        if (target == "assignee") await seed.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        if (target == "first-permission") await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Отдел редактирования" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Department));
            await seed.Users.Where(row => row.Id == actor.Id || row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id));
            await seed.LegalEntities.Where(row => row.Id == legal.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id));
        }
        var reached = false; var committed = false; string? blocked = null;
        var hook = new ClientUpdateBeforeSave(async ct =>
        {
            reached = true; await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync(ct);
            await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            try
            {
                if (target == "actor") await concurrent.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false), ct);
                if (target == "permission") await concurrent.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
                if (target == "client") await concurrent.Clients.Where(row => row.Id == client).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id), ct);
                if (target == "role") await concurrent.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, other.RoleId), ct);
                if (target == "legal") await concurrent.LegalEntities.Where(row => row.Id == legal.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id), ct);
                if (target == "status") await concurrent.ClientStatuses.Where(row => row.Id == status.Id).ExecuteDeleteAsync(ct);
                if (target == "assignee") await concurrent.Users.Where(row => row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
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
        var input = AtomicUpdateInput(other.Id); input.LegalEntityId = legal.Id; input.ClientStatusId = status.Id;
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null;
        try { result = await UpdateAtomicClientAsync(request, actor, client, input, route); } catch (Exception caught) { fault = caught; }
        Assert.True(reached); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}; error={fault?.GetType().Name}");
        Assert.Null(fault); Assert.IsType<OkObjectResult>(result); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open(); var saved = await verify.Clients.AsNoTracking().SingleAsync(row => row.Id == client);
        Assert.Equal(input.Name, saved.Name); Assert.Equal(target is "assignee" or "department" ? other.Id : actor.Id, saved.ResponsibleUserId);
        Assert.Equal(legal.Id, saved.LegalEntityId); Assert.Equal(status.Id, saved.ClientStatusId); await AssertBulkCasesUnchangedAsync(verify, ids);
    }

    public static IEnumerable<object[]> ClientUpdateBusyCases()
        => from route in new[] { 0, 1, 2 } from target in new[] { "actor", "client", "legal", "assignee" } select new object[] { route, target };

    [Theory]
    [MemberData(nameof(ClientUpdateBusyCases))]
    public async Task ClientUpdateAtomic_PgRefusesBusyTargetOrDependencyBeforeWriting(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, ids, client) = await SeedBirthdayClientAsync(seed);
        await seed.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        var other = await CreateUserAsync(seed, "update-busy-other@example.local", "Ответственный");
        var legal = new LegalEntity { ShortName = "Занятая организация", ResponsibleUserId = actor.Id }; seed.LegalEntities.Add(legal); await seed.SaveChangesAsync();
        await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (target == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {client} FOR UPDATE");
        else if (target == "legal") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legal.Id} FOR UPDATE");
        else { var user = target == "actor" ? actor.Id : other.Id; await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {user} FOR UPDATE"); }
        var input = AtomicUpdateInput(other.Id); input.LegalEntityId = legal.Id;
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await UpdateAtomicClientAsync(request, actor, client, input, route));
        Assert.Null(request.Database.CurrentTransaction); await AssertAtomicClientOriginalAsync(request, client); await AssertBulkCasesUnchangedAsync(request, ids);
    }

    private sealed class ClientUpdateBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!called && eventData.Context!.ChangeTracker.Entries<Client>().Any(entry => entry.State == EntityState.Modified)) { called = true; await action(cancellationToken); }
            return result;
        }
    }
}
