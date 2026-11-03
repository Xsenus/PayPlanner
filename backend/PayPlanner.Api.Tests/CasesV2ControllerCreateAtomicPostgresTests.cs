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
    public static IEnumerable<object[]> CreateAtomicLocks()
    {
        foreach (var route in new[] { 0, 1, 2 })
            foreach (var target in new[] { "actor", "client-delete", "assignee" }) yield return new object[] { route, target };
        foreach (var target in new[] { "role", "permission", "legal-delete", "department", "finance" }) yield return new object[] { 2, target };
    }

    [Theory]
    [MemberData(nameof(CreateAtomicLocks))]
    public async Task CaseCreateAtomic_PgPinsFreshAccessOwnersAndResponsible(int route, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open(params IInterceptor[] hooks) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(hooks).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); var assignee = await CreateUserAsync(seed, "create-race-assignee@example.local", "Ответственный", DataVisibilityScope.All); await GrantSectionPermissionAsync(seed, actor, RolePermissionCatalog.Payments);
        var owner = new Client { Name = "Новый владелец", IsActive = true, ResponsibleUserId = actor.Id }; var legal = new LegalEntity { ShortName = "Организация нового дела", ResponsibleUserId = actor.Id }; seed.Clients.Add(owner); seed.LegalEntities.Add(legal); await seed.SaveChangesAsync();
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Отдел создания" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Department));
            await seed.Users.Where(row => row.Id == actor.Id || row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.Where(row => row.Id == owner.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, assignee.Id));
        }
        var input = new ClientCase { ClientId = target == "legal-delete" ? null : owner.Id, LegalEntityId = target == "legal-delete" ? legal.Id : null, ResponsibleUserId = assignee.Id, Title = "Новое дело под защитой", FeeAmount = 817.77m };
        var reached = false; var committed = false; string? blocked = null;
        var hook = new CreateBeforeSave(async ct =>
        {
            await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct); await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); reached = true;
            try
            {
                if (target == "actor") await other.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                if (target == "role") await other.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, assignee.RoleId), ct);
                if (target == "permission") await other.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false), ct);
                if (target == "finance") await other.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Payments).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false), ct);
                if (target == "client-delete") await other.Clients.Where(row => row.Id == owner.Id).ExecuteDeleteAsync(ct);
                if (target == "legal-delete") await other.LegalEntities.Where(row => row.Id == legal.Id).ExecuteDeleteAsync(ct);
                if (target == "assignee") await other.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                if (target == "department") await other.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); var publisher = new BulkPublicationBoundary(request); var result = await CreateAtomicCaseAsync(request, actor, input, route, publisher);
        Assert.True(hook.Called); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.Null(request.Database.CurrentTransaction); await using var verify = Open();
        if (result is ConflictObjectResult) { Assert.Equal(0, publisher.Count); await AssertBulkCasesUnchangedAsync(verify, ids); }
        else
        {
            Assert.IsType<CreatedResult>(result); var saved = await verify.ClientCases.AsNoTracking().SingleAsync(row => !ids.Contains(row.Id)); Assert.Equal(input.Title, saved.Title); Assert.Equal(input.ClientId, saved.ClientId); Assert.Equal(input.LegalEntityId, saved.LegalEntityId); Assert.Equal(assignee.Id, saved.ResponsibleUserId); Assert.Equal(817.77m, saved.FeeAmount); Assert.Equal(route == 1 ? 0 : 1, publisher.Count);
            Assert.Equal(3, await verify.TaskJournalItems.CountAsync()); Assert.Equal("Сохранённый результат", (await verify.TaskJournalItems.SingleAsync(row => row.Status == "done")).Result); Assert.All(await verify.ClientCases.AsNoTracking().Where(row => ids.Contains(row.Id)).ToArrayAsync(), row => { Assert.Equal("Исходная история", row.EventHistory); Assert.Equal(123.45m, row.CourtClaimAmount); });
        }
        Assert.True(await verify.Users.Where(row => row.Id == actor.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.Users.Where(row => row.Id == assignee.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.Clients.AnyAsync(row => row.Id == owner.Id)); Assert.True(await verify.LegalEntities.AnyAsync(row => row.Id == legal.Id)); Assert.Empty(await verify.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateAtomic_PgRefusesBusyOwnerBeforeInserting(int route)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); var owner = await seed.Clients.Select(row => row.Id).SingleAsync(); await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(); await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Clients\" WHERE \"Id\" = {owner} FOR UPDATE");
        await using var request = Open(); var publisher = new BulkPublicationBoundary(request); Assert.IsType<ConflictObjectResult>(await CreateAtomicCaseAsync(request, actor, new ClientCase { ClientId = owner, Title = "Занятый владелец" }, route, publisher));
        Assert.Equal(0, publisher.Count); Assert.Null(request.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(seed, ids);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CaseCreateAtomic_PgDoesNotTrustTrackedPermissionAfterItWasRevoked(int route)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (actor, ids) = await SeedBulkCasesAsync(seed); var owner = await seed.Clients.Select(row => row.Id).SingleAsync(); await using var request = Open(); await request.Users.Include(row => row.Role)!.ThenInclude(row => row!.Permissions).SingleAsync(row => row.Id == actor.Id);
        await using (var other = Open()) await other.RolePermissions.Where(row => row.RoleId == actor.RoleId && row.Section == RolePermissionCatalog.Cases).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false));
        var publisher = new BulkPublicationBoundary(request); Assert.IsType<ForbidResult>(await CreateAtomicCaseAsync(request, actor, new ClientCase { ClientId = owner, Title = "Отозванное право" }, route, publisher)); Assert.Equal(0, publisher.Count); await AssertBulkCasesUnchangedAsync(seed, ids);
    }

    private sealed class CreateBeforeSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        public bool Called { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Called && eventData.Context!.ChangeTracker.Entries<ClientCase>().Any(entry => entry.State == EntityState.Added)) { Called = true; await action(cancellationToken); }
            return result;
        }
    }
}
