using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Requests;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> ClientBatchRaceCases()
    {
        foreach (var operation in new[] { 0, 1, 2, 3, 4 })
            foreach (var target in new[] { "actor", "permission", "client" }) yield return new object[] { operation, target };
        yield return new object[] { 4, "assignee" };
        yield return new object[] { 0, "profile" };
        yield return new object[] { 0, "new-payment" };
    }

    [Theory]
    [MemberData(nameof(ClientBatchRaceCases))]
    public async Task ClientBatch_PgPinsAccessTargetsOwnersAndProfiles(int operation, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, first, second) = await SeedClientBatchAsync(seed); var other = await CreateUserAsync(seed, "batch-race@example.local", "Другой ответственный");
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Отдел операций" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Department));
            await seed.Users.Where(row => row.Id == actor.Id || row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.Where(row => row.Id == first || row.Id == second).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id));
        }
        var reached = false; var committed = false; string? blocked = null;
        var hook = new ClientBatchBeforeSave(async ct =>
        {
            reached = true; await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            try
            {
                if (target == "actor") await concurrent.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false), ct);
                if (target == "permission") await concurrent.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Clients).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanDelete, false).SetProperty(row => row.CanEdit, false), ct);
                if (target == "client") await concurrent.Clients.Where(row => row.Id == first).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, other.Id), ct);
                if (target == "assignee") await concurrent.Users.Where(row => row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                if (target == "department") await concurrent.Users.Where(row => row.Id == other.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                if (target == "profile") await concurrent.ClientPassports.Where(row => row.ClientId == first).ExecuteUpdateAsync(set => set.SetProperty(row => row.Number, "CHANGED"), ct);
                if (target == "new-payment") { concurrent.Payments.Add(new Payment { ClientId = first, Amount = 123.45m, Date = DateTime.UtcNow, PlannedDate = DateTime.UtcNow }); await concurrent.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); IActionResult? result = null; Exception? fault = null;
        try { result = await WriteClientBatchAsync(request, actor, [first, second], operation, target == "department" ? actor.Id : other.Id); } catch (Exception caught) { fault = caught; }
        Assert.True(reached); Assert.True(!committed && blocked == PostgresErrorCodes.LockNotAvailable, $"operation={operation}; target={target}; concurrentCommitted={committed}; blocked={blocked}; result={result?.GetType().Name}; error={fault?.GetType().Name}");
        Assert.Null(fault); if (operation < 3) Assert.IsType<NoContentResult>(result); else Assert.IsType<OkObjectResult>(result); Assert.Null(request.Database.CurrentTransaction);
        if (target == "department")
        {
            await using var verify = Open(); Assert.All(await verify.Clients.Where(row => row.Id == first || row.Id == second).ToArrayAsync(), row => Assert.Equal(actor.Id, row.ResponsibleUserId));
        }
    }

    [Fact]
    public Task ClientBatch_PgPinsDepartmentOwnerDuringActualReassignment()
        => ClientBatch_PgPinsAccessTargetsOwnersAndProfiles(4, "department");

    public static IEnumerable<object[]> ClientBatchBusyCases()
    {
        foreach (var operation in new[] { 0, 1, 2, 3, 4 })
            foreach (var target in new[] { "actor", "client" }) yield return new object[] { operation, target };
        yield return new object[] { 4, "assignee" };
        foreach (var target in new[] { "address", "passport", "contact" }) yield return new object[] { 0, target };
    }

    [Theory]
    [MemberData(nameof(ClientBatchBusyCases))]
    public async Task ClientBatch_PgRefusesBusyRowsWithoutAnyMutation(int operation, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, _, first, second) = await SeedClientBatchAsync(seed); var other = await CreateUserAsync(seed, "batch-busy@example.local", "Ответственный"); var before = await ClientBatchSnapshotAsync(seed);
        await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync();
        if (target == "client") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {first} FOR UPDATE");
        else if (target == "address") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"AddressRecords\" WHERE \"ClientId\" = {first} FOR UPDATE");
        else if (target == "passport") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientPassports\" WHERE \"ClientId\" = {first} FOR UPDATE");
        else if (target == "contact") await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ExternalContacts\" WHERE \"LinkedClientId\" = {first} FOR UPDATE");
        else { var id = target == "actor" ? actor.Id : other.Id; await concurrent.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Users\" WHERE \"Id\" = {id} FOR UPDATE"); }
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await WriteClientBatchAsync(request, actor, [first, second], operation, other.Id)); Assert.Equal(before, await ClientBatchSnapshotAsync(request)); Assert.Null(request.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientBatch_PgRequestCreationAndDeletionCannotLeaveOrphanLogicalLink(bool createFirst)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, first, _) = await SeedClientBatchAsync(seed); await GrantSectionPermissionAsync(seed, actor, RolePermissionCatalog.Requests); await new SalesRequestService(seed).EnsureDefaultsAsync(default);
        var called = false; var conflict = false;
        var hook = new ClientBatchBeforeSave(async ct =>
        {
            called = true; await using var concurrent = Open();
            if (createFirst) conflict = await WriteClientBatchAsync(concurrent, actor, [first], 0, ct: ct) is ConflictObjectResult;
            else { var result = await new SalesRequestService(concurrent).CreateAsync(new SalesRequestCreateInput("Call", first, null, null, null, null, null, "Синтетическая заявка", null, null, null), actor.Id, ct); conflict = !result.Ok && result.Status == 409; }
        }, requests: createFirst);
        await using var request = Open(hook);
        if (createFirst) Assert.True((await new SalesRequestService(request).CreateAsync(new SalesRequestCreateInput("Call", first, null, null, null, null, null, "Синтетическая заявка", null, null, null), actor.Id, default)).Ok);
        else Assert.IsType<NoContentResult>(await WriteClientBatchAsync(request, actor, [first], 0));
        await using var verify = Open(); var requests = await verify.SalesRequests.CountAsync(row => row.ClientId == first); var clients = await verify.Clients.CountAsync(row => row.Id == first);
        Assert.True(called); Assert.True(conflict, $"createFirst={createFirst}; competing action succeeded; activeClients={clients}; logicalRequests={requests}");
        Assert.Equal(createFirst ? 1 : 0, requests); Assert.Equal(createFirst ? 1 : 0, clients);
    }

    private sealed class ClientBatchBeforeSave(Func<CancellationToken, Task> action, bool requests = false) : SaveChangesInterceptor
    {
        private bool called;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context!; var relevant = requests ? db.ChangeTracker.Entries<SalesRequest>().Any(entry => entry.State == EntityState.Added) : db.ChangeTracker.Entries<Client>().Any(entry => entry.State is EntityState.Modified or EntityState.Deleted);
            if (!called && relevant) { called = true; await action(cancellationToken); }
            return result;
        }
    }

    [Fact]
    public async Task ClientBatch_LegacyPgPinsFirstClientPermission()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, _, first, second) = await SeedClientBatchAsync(seed); var other = await CreateUserAsync(seed, "batch-legacy@example.local", "Новый ответственный"); await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteDeleteAsync();
        var reached = false; var committed = false; string? blocked = null;
        var hook = new ClientBatchBeforeSave(async ct =>
        {
            reached = true; await using var concurrent = Open(); await using var transaction = await concurrent.Database.BeginTransactionAsync(ct); await concurrent.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            concurrent.RolePermissions.Add(new RolePermission { RoleId = actor.RoleId, Section = RolePermissionCatalog.Clients, CanView = true, CanEdit = false });
            try { await concurrent.SaveChangesAsync(ct); await transaction.CommitAsync(ct); committed = true; }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); Assert.IsType<OkObjectResult>(await WriteClientBatchAsync(request, actor, [first, second], 4, other.Id)); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked);
        await using var verify = Open(); Assert.All(await verify.Clients.Where(row => row.Id == first || row.Id == second).ToArrayAsync(), row => Assert.Equal(other.Id, row.ResponsibleUserId)); Assert.False(await verify.RolePermissions.AnyAsync(row => row.RoleId == actor.RoleId));
    }
}
