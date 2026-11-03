using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseBulk_PriorityFailureAfterSqlRollsBackWholeBatch(bool postgres, bool cancel)
    {
        var failure = new ConfirmationAfterSaveFailure(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(failure).Options) : await CreateContextAsync(failure);
        var (user, ids) = await SeedBulkCasesAsync(db); var publisher = new RecordingRealtimeNotificationPublisher();
        var controller = CreateController(db, user, realtimePublisher: publisher);
        using var cancellation = new CancellationTokenSource(); failure.Armed = true; failure.Cancel = cancellation.Cancel;
        Task Write() => controller.BulkUpdate(new() { Ids = ids, Priority = "urgent" }, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(failure.Called); Assert.Equal(0, publisher.TaskJournalChangedCount); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    [InlineData("unapproved", HttpStatusCode.Unauthorized)]
    [InlineData("permission", HttpStatusCode.Forbidden)]
    [InlineData("view", HttpStatusCode.Forbidden)]
    [InlineData("hidden", HttpStatusCode.NotFound)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    public async Task CaseBulk_HttpChecksFreshActorPermissionAndVisibility(string mode, HttpStatusCode expected)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db);
        if (mode == "inactive") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (mode == "unapproved") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (mode == "permission") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        if (mode == "view") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (mode == "hidden") { await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own)); await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true)); }
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync("/api/v2/cases/bulk-update", new CasesV2Controller.BulkUpdateCasesRequest { Ids = mode == "missing" ? [int.MaxValue] : ids, Priority = "urgent" });
        Assert.Equal(expected, response.StatusCode); Assert.Null(db.Database.CurrentTransaction); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CaseBulk_AtMost500DistinctIdsAndNoopHaveExactCountsAndNoPublication()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var publisher = new BulkPublicationBoundary(db);
        var controller = CreateController(db, user, realtimePublisher: publisher);
        var result = Assert.IsType<OkObjectResult>(await controller.BulkUpdate(new() { Ids = Enumerable.Range(1, 500).Concat(ids).Concat([0, -1]).ToArray(), Priority = "normal" }, CancellationToken.None));
        Assert.Equal(2, GetAnonymousInt(result.Value, "updated")); Assert.Equal(500, GetAnonymousInt(result.Value, "requested")); Assert.Equal(0, publisher.Count); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Fact]
    public async Task CaseBulk_PublishesOnceOnlyAfterItsTransactionHasCommitted()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db); var publisher = new BulkPublicationBoundary(db);
        var result = await CreateController(db, user, realtimePublisher: publisher).BulkUpdate(new() { Ids = ids, Priority = "urgent" }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result); Assert.Equal(1, publisher.Count); Assert.All(await db.ClientCases.AsNoTracking().ToArrayAsync(), item => Assert.Equal("urgent", item.Priority)); Assert.Equal(3, await db.TaskJournalItems.CountAsync()); Assert.Empty(await db.Payments.ToArrayAsync());
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseBulkBaseline_ArchiveFailureAfterSqlRollsBackCasesTasksAndReports(bool postgres, bool cancel)
    {
        var failure = new ConfirmationAfterSaveFailure(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(failure).Options) : await CreateContextAsync(failure);
        var (user, ids) = await SeedBulkCasesAsync(db); var publisher = new RecordingRealtimeNotificationPublisher();
        var controller = CreateController(db, user, realtimePublisher: publisher);
        using var cancellation = new CancellationTokenSource(); failure.Armed = true; failure.Cancel = cancellation.Cancel;
        Task Write() => controller.BulkUpdate(new() { Ids = ids, Status = ClientCaseStatus.Closed }, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(failure.Called); Assert.Equal(0, publisher.TaskJournalChangedCount); Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseBulkBaseline_RejectsInvalidStatusAndOversizedSelection(bool oversized)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db);
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync("/api/v2/cases/bulk-update", new CasesV2Controller.BulkUpdateCasesRequest { Ids = oversized ? Enumerable.Range(1, 501).ToArray() : ids, Status = oversized ? ClientCaseStatus.OnHold : (ClientCaseStatus)999 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); db.ChangeTracker.Clear(); await AssertBulkCasesUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseBulkBaseline_DoesNotAdoptForeignTransactionOrPendingWork(bool pending)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedBulkCasesAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); var original = task.Result;
        if (pending) task.Result = "Несохранённый чужой результат";
        var controller = CreateController(db, user);
        var result = await controller.BulkUpdate(new() { Ids = ids, Priority = "urgent" }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal(original, (await db.Entry(task).GetDatabaseValuesAsync())![nameof(TaskJournalItem.Result)]); }
        Assert.All(await db.ClientCases.AsNoTracking().ToArrayAsync(), item => Assert.NotEqual("urgent", item.Priority));
    }

    private static async Task<(User User, int[] Ids)> SeedBulkCasesAsync(PaymentContext db)
    {
        var user = await CreateUserAsync(db, "bulk-atomic@example.local", "Юрист", DataVisibilityScope.All);
        var client = new Client { Name = "Синтетический клиент", IsActive = true, ResponsibleUserId = user.Id };
        var first = new ClientCase { Client = client, ResponsibleUserId = user.Id, Title = "Судебное дело", CourtName = "Суд", Status = ClientCaseStatus.Open, EventHistory = "Исходная история", CourtClaimAmount = 123.45m };
        var second = new ClientCase { Client = client, ResponsibleUserId = user.Id, Title = "Обычное дело", Status = ClientCaseStatus.Open, EventHistory = "Исходная история", CourtClaimAmount = 123.45m };
        db.ClientCases.AddRange(first, second); db.SystemSettings.Add(new SystemSetting { Key = ClientCaseFinalReportTaskService.AssigneeUserIdSettingKey, Value = user.Id.ToString() }); await db.SaveChangesAsync();
        db.TaskJournalItems.AddRange(new TaskJournalItem { ClientCaseId = first.Id, Title = "Открытая задача 1", Status = "new", Result = "", TagsJson = "[]" }, new TaskJournalItem { ClientCaseId = second.Id, Title = "Открытая задача 2", Status = "new", Result = "", TagsJson = "[]" }, new TaskJournalItem { ClientCaseId = first.Id, Title = "Завершённая задача", Status = "done", Result = "Сохранённый результат", TagsJson = "[]" });
        await db.SaveChangesAsync(); return (user, [first.Id, second.Id]);
    }

    private static async Task AssertBulkCasesUnchangedAsync(PaymentContext db, int[] ids)
    {
        var cases = await db.ClientCases.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(); Assert.Equal(ids.Order(), cases.Select(row => row.Id));
        Assert.All(cases, item => { Assert.Equal(ClientCaseStatus.Open, item.Status); Assert.Equal("Исходная история", item.EventHistory); Assert.Equal(123.45m, item.CourtClaimAmount); });
        Assert.All(cases, item => Assert.NotEqual("urgent", item.Priority));
        var tasks = await db.TaskJournalItems.AsNoTracking().ToArrayAsync(); Assert.Equal(3, tasks.Length); Assert.Equal(2, tasks.Count(row => row.Status == "new")); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Status == "done").Result);
        Assert.All(tasks, task => Assert.Contains(task.ClientCaseId!.Value, ids)); Assert.Empty(await db.TaskJournalComments.ToArrayAsync()); Assert.Empty(await db.Payments.ToArrayAsync());
    }

    private sealed class BulkPublicationBoundary(PaymentContext db) : IRealtimeNotificationPublisher
    {
        public int Count { get; private set; }
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PublishTaskJournalChangedAsync(CancellationToken cancellationToken = default) { Assert.Null(db.Database.CurrentTransaction); Count++; return Task.CompletedTask; }
    }
}
