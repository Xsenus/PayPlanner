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
    [InlineData("role")]
    [InlineData("permission")]
    [InlineData("case")]
    [InlineData("client-delete")]
    [InlineData("legal-delete")]
    [InlineData("owner")]
    [InlineData("department")]
    [InlineData("report-user")]
    [InlineData("report-permission")]
    [InlineData("task")]
    [InlineData("new-task")]
    public async Task CaseWriteV1_PgPinsAccessOwnersTasksAndFinalReport(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open(); var (user, ids) = await SeedBulkCasesAsync(seed); var assignee = await CreateUserAsync(seed, "v1-write-race-report@example.local", "Получатель отчёта", DataVisibilityScope.All); var outsider = await CreateUserAsync(seed, "v1-write-race-outsider@example.local", "Другой", DataVisibilityScope.All);
        var nextClient = new Client { Name = "Новый владелец V1", IsActive = true, ResponsibleUserId = user.Id }; var nextLegal = new LegalEntity { ShortName = "Новая организация V1", ResponsibleUserId = user.Id }; seed.Clients.Add(nextClient); seed.LegalEntities.Add(nextLegal); await seed.SaveChangesAsync();
        if (target is "owner" or "department") await seed.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, target == "owner" ? DataVisibilityScope.Own : DataVisibilityScope.Department));
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Синтетический отдел V1 записи" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.Users.Where(row => row.Id == user.Id || row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.Where(row => row.Id == nextClient.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, assignee.Id));
        }
        var close = target is "report-user" or "report-permission" or "task" or "new-task";
        if (close) await seed.SystemSettings.ExecuteUpdateAsync(set => set.SetProperty(row => row.Value, assignee.Id.ToString()));
        var source = await seed.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var input = V1CaseInput(source, close ? ClientCaseStatus.Closed : null); input.Title = "Сохранение V1";
        if (target == "legal-delete") { input.ClientId = null; input.LegalEntityId = nextLegal.Id; }
        if (target is "client-delete" or "owner" or "department") input.ClientId = nextClient.Id;
        var taskId = await seed.TaskJournalItems.Where(row => row.Title == "Открытая задача 1").Select(row => row.Id).SingleAsync(); var reached = false; var committed = false; string? blocked = null;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct); await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); reached = true;
            try
            {
                if (target == "actor") await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                else if (target == "role") await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, outsider.RoleId), ct);
                else if (target == "permission") await other.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
                else if (target == "case") await other.ClientCases.Where(row => row.Id == source.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Параллельное дело"), ct);
                else if (target == "client-delete") await other.Clients.Where(row => row.Id == nextClient.Id).ExecuteDeleteAsync(ct);
                else if (target == "legal-delete") await other.LegalEntities.Where(row => row.Id == nextLegal.Id).ExecuteDeleteAsync(ct);
                else if (target == "owner") await other.Clients.Where(row => row.Id == nextClient.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id), ct);
                else if (target == "department") await other.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                else if (target == "report-user") await other.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                else if (target == "report-permission") await other.RolePermissions.Where(row => row.RoleId == assignee.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false), ct);
                else if (target == "task") await other.TaskJournalItems.Where(row => row.Id == taskId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Result, "Параллельный результат"), ct);
                else { other.TaskJournalItems.Add(new TaskJournalItem { ClientCaseId = source.Id, Title = "Параллельная задача", Status = "new", Result = "", TagsJson = "[]" }); await other.SaveChangesAsync(ct); }
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); var result = await UpdateV1CaseAsync(request, user, source, input, CancellationToken.None);
        Assert.True(hook.Called); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        if (result is ConflictObjectResult) await AssertBulkCasesUnchangedAsync(verify, ids);
        else
        {
            Assert.IsType<OkObjectResult>(result); var actual = await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id); Assert.Equal(input.Title, actual.Title); Assert.Equal(input.Status, actual.Status); Assert.Equal(input.ClientId, actual.ClientId); Assert.Equal(input.LegalEntityId, actual.LegalEntityId); Assert.Equal(source.ResponsibleUserId, actual.ResponsibleUserId); Assert.Equal(source.CourtClaimAmount, actual.CourtClaimAmount); Assert.Equal(source.EventHistory, actual.EventHistory);
            if (!close) await AssertEditLinksHistoryAsync(verify, source, ids);
            else
            {
                var tasks = await verify.TaskJournalItems.AsNoTracking().ToArrayAsync(); Assert.Equal(4, tasks.Length); Assert.Equal("new", Assert.Single(tasks, row => row.ClientCaseId == ids[1]).Status); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Title == "Завершённая задача").Result); Assert.Equal(assignee.Id, Assert.Single(tasks, row => row.IsCaseFinalReport).AssigneeUserId); Assert.DoesNotContain(tasks, row => row.Result == "Параллельный результат");
            }
        }
        Assert.True(await verify.Users.Where(row => row.Id == user.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.RolePermissions.Where(row => row.RoleId == user.RoleId).Select(row => row.CanEdit).SingleAsync()); Assert.True(await verify.Users.Where(row => row.Id == assignee.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.RolePermissions.Where(row => row.RoleId == assignee.RoleId).Select(row => row.CanView).SingleAsync()); Assert.True(await verify.Clients.AnyAsync(row => row.Id == nextClient.Id)); Assert.True(await verify.LegalEntities.AnyAsync(row => row.Id == nextLegal.Id)); Assert.Empty(await verify.Payments.ToArrayAsync());
    }

    [Fact]
    public async Task CaseWriteV1_PgBusyCaseDoesNotChangeItsTasks()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync(); PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (user, ids) = await SeedBulkCasesAsync(seed); var source = await seed.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {source.Id} FOR UPDATE"); await using var request = Open();
        Assert.IsType<ConflictObjectResult>(await UpdateV1CaseAsync(request, user, source, V1CaseInput(source, ClientCaseStatus.Closed), CancellationToken.None)); Assert.Null(request.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(seed, ids);
    }
}
