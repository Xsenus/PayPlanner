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
    [InlineData("permission")]
    [InlineData("actor")]
    [InlineData("case")]
    [InlineData("owner")]
    [InlineData("department")]
    [InlineData("responsible")]
    [InlineData("task")]
    [InlineData("report")]
    public async Task CaseBulk_PgLocksFreshAccessAndTargetsBeforeWriting(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open(); var (user, ids) = await SeedBulkCasesAsync(seed);
        var outsider = await CreateUserAsync(seed, "bulk-outsider@example.local", "Другой", DataVisibilityScope.All);
        var assignee = await CreateUserAsync(seed, "bulk-assignee@example.local", "Новый ответственный", DataVisibilityScope.All);
        var clientId = await seed.ClientCases.Where(row => row.Id == ids[0]).Select(row => row.ClientId!.Value).SingleAsync();
        var taskId = await seed.TaskJournalItems.Where(row => row.Status == "new").OrderBy(row => row.Id).Select(row => row.Id).FirstAsync();
        if (target is "owner" or "department")
        {
            await seed.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, target == "department" ? DataVisibilityScope.Department : DataVisibilityScope.Own));
            await seed.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, (int?)null));
        }
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Синтетический отдел" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.Users.Where(row => row.Id == user.Id || row.Id == outsider.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.Where(row => row.Id == clientId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id));
        }
        if (target == "report") await seed.SystemSettings.ExecuteUpdateAsync(set => set.SetProperty(row => row.Value, assignee.Id.ToString()));
        var competingCommitted = false; string? blockedState = null; string phase = "before-hook"; string? competingError = null;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            phase = "open-context";
            try
            {
                await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct);
                phase = "set-timeout";
                await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
                phase = "competing-write";
                try
                {
                    if (target == "permission") await other.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
                    else if (target == "actor") await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                    else if (target == "case") await other.ClientCases.Where(row => row.Id == ids[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.Priority, "low"), ct);
                    else if (target == "owner") await other.Clients.Where(row => row.Id == clientId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id), ct);
                    else if (target == "department") await other.Users.Where(row => row.Id == outsider.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                    else if (target is "responsible" or "report") await other.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                    else await other.TaskJournalItems.Where(row => row.Id == taskId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Result, "Конкурирующий результат"), ct);
                    await transaction.CommitAsync(ct); competingCommitted = true;
                }
                catch (Exception error)
                {
                    Exception? current = error;
                    while (current is not null && current is not PostgresException) current = current.InnerException;
                    if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } locked) blockedState = locked.SqlState;
                    else throw;
                }
            }
            catch (Exception error) { competingError = error.GetType().Name + ":" + (error is PostgresException pg ? pg.SqlState : error.InnerException?.GetType().Name); throw; }
        });
        await using var request = Open(hook); var publisher = new BulkPublicationBoundary(request); var archive = target is "task" or "report";
        var result = await CreateController(request, user, realtimePublisher: publisher).BulkUpdate(new()
        {
            Ids = ids.Reverse().ToArray(),
            Priority = archive ? null : "urgent",
            Status = archive ? ClientCaseStatus.Closed : null,
            ResponsibleUserId = target == "responsible" ? assignee.Id : null,
        }, CancellationToken.None);
        Assert.True(hook.Called); Assert.False(competingCommitted); Assert.True(blockedState == PostgresErrorCodes.LockNotAvailable, $"phase={phase}; error={competingError}; result={result.GetType().Name}; blocked={blockedState}"); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        if (result is ConflictObjectResult) { Assert.Equal(0, publisher.Count); await AssertBulkCasesUnchangedAsync(verify, ids); }
        else
        {
            Assert.IsType<OkObjectResult>(result); Assert.Equal(1, publisher.Count);
            var cases = await verify.ClientCases.AsNoTracking().ToArrayAsync(); Assert.All(cases, row => { Assert.Equal(archive ? ClientCaseStatus.Closed : ClientCaseStatus.Open, row.Status); Assert.Equal(123.45m, row.CourtClaimAmount); if (!archive) Assert.Equal("urgent", row.Priority); });
            Assert.Equal("Сохранённый результат", (await verify.TaskJournalItems.AsNoTracking().SingleAsync(row => row.Title == "Завершённая задача")).Result);
            Assert.DoesNotContain(await verify.TaskJournalItems.AsNoTracking().ToArrayAsync(), row => row.Result == "Конкурирующий результат");
        }
        Assert.True(await verify.Users.Where(row => row.Id == user.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.Users.Where(row => row.Id == assignee.Id).Select(row => row.IsActive).SingleAsync());
        Assert.Empty(await verify.Payments.ToArrayAsync());
    }
}
