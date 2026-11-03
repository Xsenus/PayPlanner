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
    public static IEnumerable<object[]> CaseDeleteRaces() => from legacy in new[] { false, true }
                                                             from target in new[] { "actor", "role", "permission", "case", "owner", "department", "payment", "task", "link" }
                                                             select new object[] { legacy, target };

    [Theory]
    [MemberData(nameof(CaseDeleteRaces))]
    public async Task CaseDelete_PgPinsAccessCasePaymentLinksAndTaskResults(bool legacy, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open(); var (user, ids) = await SeedCaseDeleteAsync(seed); var outsider = await CreateUserAsync(seed, "delete-competitor@example.local", "Другой", DataVisibilityScope.All);
        if (target is "owner" or "department") await seed.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, target == "owner" ? DataVisibilityScope.Own : DataVisibilityScope.Department));
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Синтетический отдел удаления" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.Users.Where(row => row.Id == user.Id || row.Id == outsider.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id)); await seed.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id));
        }
        var source = await seed.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var paymentId = await seed.Payments.Where(row => row.ClientCaseId == source.Id).Select(row => row.Id).SingleAsync(); var taskId = await seed.TaskJournalItems.Where(row => row.Status == "done").Select(row => row.Id).SingleAsync();
        var reached = false; var committed = false; string? blocked = null;
        var hook = new CaseDeleteBeforePaymentSave(async ct =>
        {
            await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct); await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); reached = true;
            try
            {
                if (target == "actor") await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                else if (target == "role") await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, outsider.RoleId), ct);
                else if (target == "permission") await other.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanDelete, false), ct);
                else if (target == "case") await other.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Параллельное дело"), ct);
                else if (target == "owner") await other.Clients.Where(row => row.Id == source.ClientId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id), ct);
                else if (target == "department") await other.Users.Where(row => row.Id == outsider.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                else if (target == "payment") await other.Payments.Where(row => row.Id == paymentId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, ids[1]), ct);
                else if (target == "task") await other.TaskJournalItems.Where(row => row.Id == taskId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Result, "Параллельный результат"), ct);
                else { other.Payments.Add(new Payment { ClientId = source.ClientId, ClientCaseId = source.Id, Type = PaymentType.Income, Status = PaymentStatus.Pending, Amount = 99.99m }); await other.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); var publisher = new BulkPublicationBoundary(request); var result = await DeleteCaseAsync(request, user, source.Id, legacy, publisher, CancellationToken.None);
        Assert.True(hook.Called); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        if (result is ConflictObjectResult) { Assert.Equal(0, publisher.Count); await AssertCaseDeleteUnchangedAsync(verify, ids); }
        else
        {
            Assert.IsType<NoContentResult>(result); Assert.Equal(1, publisher.Count); Assert.False(await verify.ClientCases.AnyAsync(row => row.Id == source.Id)); Assert.True(await verify.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == source.Id));
            var payments = await verify.Payments.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(); Assert.Equal(2, payments.Length); Assert.Null(payments[0].ClientCaseId); Assert.Equal(15.25m, payments[0].Amount); Assert.Equal(ids[1], payments[1].ClientCaseId); Assert.Equal(27.50m, payments[1].Amount);
            var tasks = await verify.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().ToArrayAsync(); Assert.Equal(3, tasks.Length); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Status == "done").Result); Assert.Equal(ids[1], Assert.Single(tasks, row => row.Title == "Открытая задача 2").ClientCaseId);
        }
        Assert.True(await verify.Users.Where(row => row.Id == user.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases).Select(row => row.CanDelete).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseDelete_PgBusyCaseReturnsConflictWithoutDetachingPayments(bool legacy)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (user, ids) = await SeedCaseDeleteAsync(seed); await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {ids[0]} FOR UPDATE");
        await using var request = Open(); var publisher = new BulkPublicationBoundary(request); Assert.IsType<ConflictObjectResult>(await DeleteCaseAsync(request, user, ids[0], legacy, publisher, CancellationToken.None));
        Assert.Null(request.Database.CurrentTransaction); Assert.Equal(0, publisher.Count); await AssertCaseDeleteUnchangedAsync(seed, ids);
    }

    private sealed class CaseDeleteBeforePaymentSave(Func<CancellationToken, Task> action) : SaveChangesInterceptor
    {
        public bool Called { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Called && eventData.Context!.ChangeTracker.Entries<Payment>().Any(entry => entry.State == EntityState.Modified && entry.Property(row => row.ClientCaseId).IsModified && entry.Entity.ClientCaseId is null))
            { Called = true; Assert.NotNull(eventData.Context.Database.CurrentTransaction); await action(cancellationToken); }
            return result;
        }
    }
}
