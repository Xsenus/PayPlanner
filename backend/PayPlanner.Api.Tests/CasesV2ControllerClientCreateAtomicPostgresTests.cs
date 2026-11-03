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
    public static IEnumerable<object[]> ClientCreateLockCases()
    {
        foreach (var route in new[] { 0, 1, 2 })
            foreach (var target in new[] { "actor", "permission", "legal-delete", "assignee" }) yield return new object[] { route, target };
        foreach (var target in new[] { "role", "status-delete", "department" }) yield return new object[] { 2, target };
    }

    [Theory]
    [MemberData(nameof(ClientCreateLockCases))]
    public async Task ClientCreateAtomic_PgPinsAccessLegalStatusAssigneeAndDepartment(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); await GrantSectionPermissionAsync(seed, actor, RolePermissionCatalog.Clients);
        var otherUser = await CreateUserAsync(seed, "client-race-other@example.local", "Другой сотрудник");
        if (target == "assignee") await seed.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        var legal = new LegalEntity { ShortName = "Организация нового клиента", ResponsibleUserId = actor.Id }; var status = new ClientStatusEntity { Name = "Статус нового клиента" }; seed.AddRange(legal, status); await seed.SaveChangesAsync();
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Отдел клиентов" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Department));
            await seed.Users.Where(row => row.Id == actor.Id || row.Id == otherUser.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.LegalEntities.Where(row => row.Id == legal.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, otherUser.Id));
        }
        var reached = false; var mutationCommitted = false; string? blocked = null;
        var hook = new ClientCreateBeforeSave(async ct =>
        {
            reached = true; await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            try
            {
                if (target == "actor") await other.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                if (target == "permission") await other.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false), ct);
                if (target == "role") await other.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, otherUser.RoleId), ct);
                if (target == "legal-delete") await other.LegalEntities.Where(row => row.Id == legal.Id).ExecuteDeleteAsync(ct);
                if (target == "status-delete") await other.ClientStatuses.Where(row => row.Id == status.Id).ExecuteDeleteAsync(ct);
                if (target == "assignee") await other.Users.Where(row => row.Id == otherUser.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                if (target == "department") await other.Users.Where(row => row.Id == otherUser.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                await transaction.CommitAsync(ct); mutationCommitted = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); IActionResult? result = null; Exception? error = null;
        try { result = await CreateAtomicClientAsync(request, actor, new Client { Name = "Клиент под защитой", LegalEntityId = legal.Id, ClientStatusId = status.Id, ResponsibleUserId = otherUser.Id }, route); } catch (Exception caught) { error = caught; }
        Assert.True(reached); Assert.True(!mutationCommitted && blocked == PostgresErrorCodes.LockNotAvailable, $"target={target}; mutationCommitted={mutationCommitted}; blocked={blocked}; result={result?.GetType().Name}; error={error?.GetType().Name}");
        Assert.Null(error); Assert.IsType<CreatedResult>(result); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open(); var saved = await verify.Clients.AsNoTracking().SingleAsync(row => row.Name == "Клиент под защитой");
        Assert.Equal(target == "assignee" ? otherUser.Id : actor.Id, saved.ResponsibleUserId); Assert.Equal(legal.Id, saved.LegalEntityId); Assert.Equal(status.Id, saved.ClientStatusId);
        Assert.True(await verify.Users.Where(row => row.Id == actor.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.Users.Where(row => row.Id == otherUser.Id).Select(row => row.IsActive).SingleAsync());
        Assert.True(await verify.LegalEntities.AnyAsync(row => row.Id == legal.Id)); Assert.True(await verify.ClientStatuses.AnyAsync(row => row.Id == status.Id)); await AssertBulkCasesUnchangedAsync(verify, ids);
    }

    public static IEnumerable<object[]> ClientCreateBusyCases()
        => from route in new[] { 0, 1, 2 } from target in new[] { "actor", "legal", "assignee" } select new object[] { route, target };

    [Theory]
    [MemberData(nameof(ClientCreateBusyCases))]
    public async Task ClientCreateAtomic_PgRefusesBusyDependenciesBeforeInsert(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); await seed.Roles.Where(row => row.Id == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Name, "admin"));
        var assignee = await CreateUserAsync(seed, "client-busy-assignee@example.local", "Ответственный"); var legal = new LegalEntity { ShortName = "Занятая организация", ResponsibleUserId = actor.Id }; seed.LegalEntities.Add(legal); await seed.SaveChangesAsync();
        await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync();
        if (target == "legal") await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"LegalEntities\" WHERE \"Id\" = {legal.Id} FOR UPDATE");
        else { var id = target == "actor" ? actor.Id : assignee.Id; await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {id} FOR UPDATE"); }
        await using var request = Open(); var result = await CreateAtomicClientAsync(request, actor, new Client { Name = "Не создавать при занятых связях", LegalEntityId = legal.Id, ResponsibleUserId = assignee.Id }, route);
        Assert.IsType<ConflictObjectResult>(result); Assert.Equal(1, await request.Clients.CountAsync()); Assert.Null(request.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(request, ids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClientCreateAtomic_PgRejectsPreviouslyTrackedRevokedPermission(int route)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); await GrantSectionPermissionAsync(seed, actor, RolePermissionCatalog.Clients);
        await using var request = Open(); await request.Users.Include(row => row.Role)!.ThenInclude(row => row!.Permissions).SingleAsync(row => row.Id == actor.Id);
        await using (var other = Open()) await other.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false));
        Assert.IsType<ForbidResult>(await CreateAtomicClientAsync(request, actor, new Client { Name = "Отозванное право" }, route));
        Assert.Equal(1, await request.Clients.CountAsync()); Assert.Null(request.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(request, ids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ClientCreateAtomic_LegacyPgPinsFirstPermissionConfiguration(int route)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        var called = false; var committed = false; string? blocked = null;
        var hook = new ClientCreateBeforeSave(async ct =>
        {
            called = true; await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            other.RolePermissions.Add(new RolePermission { RoleId = actor.RoleId, Section = RolePermissionCatalog.Clients, CanView = true, CanCreate = false });
            try { await other.SaveChangesAsync(ct); await transaction.CommitAsync(ct); committed = true; }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); var result = await CreateAtomicClientAsync(request, actor, new Client { Name = "Legacy-создание под защитой" }, route);
        Assert.True(called); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.IsType<CreatedResult>(result); Assert.Null(request.Database.CurrentTransaction);
        Assert.Equal(2, await request.Clients.CountAsync()); Assert.False(await request.RolePermissions.AnyAsync(row => row.RoleId == actor.RoleId)); await AssertBulkCasesUnchangedAsync(request, ids);
    }

    private sealed class ClientCreateBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!called && eventData.Context!.ChangeTracker.Entries<Client>().Any(entry => entry.State == EntityState.Added)) { called = true; await action(cancellationToken); }
            return result;
        }
    }
}
