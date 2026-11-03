using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> EditLinkRaceCases() => from legacy in new[] { false, true } from target in new[] { "client-delete", "legal-delete", "assignee", "owner", "department", "report", "task" } select new object[] { legacy, target };

    [Theory]
    [MemberData(nameof(EditLinkRaceCases))]
    public async Task CaseEditLinks_PgPinsNewLinksAndRelatedTaskResults(bool legacy, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open(); var (user, ids) = await SeedBulkCasesAsync(seed); var assignee = await CreateUserAsync(seed, "edit-race-target@example.local", "Новый ответственный", DataVisibilityScope.All); var outsider = await CreateUserAsync(seed, "edit-race-outsider@example.local", "Другой", DataVisibilityScope.All);
        var nextClient = new Client { Name = "Новый владелец", IsActive = true, ResponsibleUserId = user.Id }; var nextLegal = new LegalEntity { ShortName = "Новая компания", ResponsibleUserId = user.Id }; seed.Clients.Add(nextClient); seed.LegalEntities.Add(nextLegal); await seed.SaveChangesAsync();
        if (target is "owner" or "department") await seed.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, target == "owner" ? DataVisibilityScope.Own : DataVisibilityScope.Department));
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Синтетический отдел" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
            await seed.Users.Where(row => row.Id == user.Id || row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, department.Id).SetProperty(row => row.Department, department.Name));
            await seed.Clients.Where(row => row.Id == nextClient.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, assignee.Id));
        }
        if (target is "report" or "task") await seed.SystemSettings.ExecuteUpdateAsync(set => set.SetProperty(row => row.Value, assignee.Id.ToString()));
        var source = await seed.ClientCases.AsNoTracking().SingleAsync(row => row.Id == ids[0]); var model = EditRequest(source); model.Title = "Обычная правка";
        if (target == "legal-delete") { model.ClientId = null; model.LegalEntityId = nextLegal.Id; }
        if (target is "client-delete" or "owner" or "department") model.ClientId = nextClient.Id;
        if (target == "assignee") model.ResponsibleUserId = assignee.Id;
        var close = target is "task" or "report"; if (close) model.Status = ClientCaseStatus.Closed;
        var taskId = await seed.TaskJournalItems.Where(row => row.Status == "new" && row.ClientCaseId == source.Id).Select(row => row.Id).SingleAsync();
        var reached = false; var committed = false; string? blocked = null;
        var hook = new ConfirmationBeforeSave(async ct =>
        {
            await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync(ct); await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct); reached = true;
            try
            {
                if (target == "client-delete") await other.Clients.Where(row => row.Id == nextClient.Id).ExecuteDeleteAsync(ct);
                else if (target == "legal-delete") await other.LegalEntities.Where(row => row.Id == nextLegal.Id).ExecuteDeleteAsync(ct);
                else if (target == "owner") await other.Clients.Where(row => row.Id == nextClient.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, outsider.Id), ct);
                else if (target == "department") await other.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.DepartmentId, (int?)null).SetProperty(row => row.Department, "Другой отдел"), ct);
                else if (target is "assignee" or "report") await other.Users.Where(row => row.Id == assignee.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                else await other.TaskJournalItems.Where(row => row.Id == taskId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Result, "Параллельный результат"), ct);
                await transaction.CommitAsync(ct); committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error; while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState; else throw;
            }
        });
        await using var request = Open(hook); var publisher = new BulkPublicationBoundary(request);
        var v2 = CreateController(request, user, realtimePublisher: publisher);
        IActionResult result = legacy ? await new CasesController(request, publisher) { ControllerContext = v2.ControllerContext }.UpdateWithSource(source.Id, model, CancellationToken.None) : await v2.UpdateWithSource(source.Id, model, CancellationToken.None);
        Assert.True(hook.Called); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        if (result is ConflictObjectResult) { Assert.Equal(0, publisher.Count); await AssertBulkCasesUnchangedAsync(verify, ids); }
        else
        {
            Assert.IsType<OkObjectResult>(result); Assert.Equal(1, publisher.Count); var actual = await verify.ClientCases.AsNoTracking().SingleAsync(row => row.Id == source.Id);
            Assert.Equal(model.ClientId, actual.ClientId); Assert.Equal(model.LegalEntityId, actual.LegalEntityId); Assert.Equal(model.ResponsibleUserId, actual.ResponsibleUserId); Assert.Equal(model.Status, actual.Status); Assert.Equal(123.45m, actual.CourtClaimAmount);
            if (!close) await AssertEditLinksHistoryAsync(verify, source, ids);
            else
            {
                Assert.Equal("Сохранённый результат", (await verify.TaskJournalItems.AsNoTracking().SingleAsync(row => row.Title == "Завершённая задача")).Result);
                Assert.Equal("new", (await verify.TaskJournalItems.AsNoTracking().SingleAsync(row => row.ClientCaseId == ids[1])).Status);
                Assert.DoesNotContain(await verify.TaskJournalItems.AsNoTracking().ToArrayAsync(), row => row.Result == "Параллельный результат");
            }
        }
        Assert.True(await verify.Clients.AnyAsync(row => row.Id == nextClient.Id)); Assert.True(await verify.LegalEntities.AnyAsync(row => row.Id == nextLegal.Id)); Assert.True(await verify.Users.Where(row => row.Id == assignee.Id).Select(row => row.IsActive).SingleAsync()); Assert.Empty(await verify.Payments.ToArrayAsync());
    }
}
