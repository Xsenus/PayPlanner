using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task CaseDelete_AfterPaymentSqlFailureOrCancellationPreservesWholeCase(bool postgres, bool legacy, bool cancel)
    {
        var fault = new ConfirmationAfterSaveFailure(cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (user, ids) = await SeedCaseDeleteAsync(db); var publisher = new BulkPublicationBoundary(db);
        using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => DeleteCaseAsync(db, user, ids[0], legacy, publisher, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(fault.Called); Assert.Null(db.Database.CurrentTransaction); Assert.Equal(0, publisher.Count); db.ChangeTracker.Clear(); await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseDelete_HardDeleteSaveFailurePreservesPaymentsAndTasks(bool legacy)
    {
        var fault = new CaseParentDeleteFault(); await using var db = await CreateContextAsync(fault, useObjectAuditModel: false);
        var (user, ids) = await SeedCaseDeleteAsync(db); var publisher = new BulkPublicationBoundary(db); db.ChangeTracker.Clear(); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => DeleteCaseAsync(db, user, ids[0], legacy, publisher, CancellationToken.None));
        Assert.Null(db.Database.CurrentTransaction); Assert.Equal(0, publisher.Count); db.ChangeTracker.Clear(); await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseDelete_DoesNotAdoptForeignTransactionOrPendingWork(bool legacy, bool pending)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedCaseDeleteAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); var original = task.Result; if (pending) task.Result = "Чужая несохранённая правка";
        var publisher = new BulkPublicationBoundary(db); var result = await DeleteCaseAsync(db, user, ids[0], legacy, publisher, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result); Assert.Equal(0, publisher.Count); Assert.Same(transaction, db.Database.CurrentTransaction);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal(original, (await db.Entry(task).GetDatabaseValuesAsync())![nameof(TaskJournalItem.Result)]); }
        await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData(true, "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData(false, "inactive", HttpStatusCode.Unauthorized)]
    [InlineData(true, "inactive", HttpStatusCode.Unauthorized)]
    [InlineData(false, "unapproved", HttpStatusCode.Unauthorized)]
    [InlineData(true, "unapproved", HttpStatusCode.Unauthorized)]
    [InlineData(false, "view", HttpStatusCode.Forbidden)]
    [InlineData(true, "view", HttpStatusCode.Forbidden)]
    [InlineData(false, "delete", HttpStatusCode.Forbidden)]
    [InlineData(true, "delete", HttpStatusCode.Forbidden)]
    [InlineData(false, "hidden", HttpStatusCode.NotFound)]
    [InlineData(true, "hidden", HttpStatusCode.NotFound)]
    [InlineData(false, "missing", HttpStatusCode.NotFound)]
    [InlineData(true, "missing", HttpStatusCode.NotFound)]
    public async Task CaseDelete_HttpChecksFreshActorViewDeleteAndVisibility(bool legacy, string mode, HttpStatusCode expected)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedCaseDeleteAsync(db);
        if (mode == "inactive") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (mode == "unapproved") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (mode == "view") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (mode == "delete") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanDelete, false));
        if (mode == "hidden") { await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own)); await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true)); }
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.DeleteAsync($"/api/{(legacy ? "cases" : "v2/cases")}/{(mode == "missing" ? int.MaxValue : ids[0])}");
        Assert.Equal(expected, response.StatusCode); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseDelete_SuccessNeedsDeleteRatherThanEditAndPublishesAfterCommit(bool legacy, bool soft)
    {
        await using var db = await CreateContextAsync(useObjectAuditModel: soft); var (user, ids) = await SeedCaseDeleteAsync(db);
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        var document = new ClientCaseDocument { ClientCaseId = ids[0], DocumentKey = "retained", OriginalFileName = "retained.txt", StoredFileName = "retained.txt", StoragePath = "synthetic/retained.txt", ContentType = "text/plain", SizeBytes = 4 };
        db.ClientCaseDocuments.Add(document); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); var publisher = new BulkPublicationBoundary(db);
        Assert.IsType<NoContentResult>(await DeleteCaseAsync(db, user, ids[0], legacy, publisher, CancellationToken.None));
        Assert.Equal(1, publisher.Count); Assert.Null(db.Database.CurrentTransaction); Assert.False(await db.ClientCases.AnyAsync(row => row.Id == ids[0]));
        Assert.Equal(soft, await db.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == ids[0]));
        var payments = await db.Payments.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(); Assert.Equal(2, payments.Length); Assert.Null(payments[0].ClientCaseId); Assert.Equal(15.25m, payments[0].Amount); Assert.Equal(ids[1], payments[1].ClientCaseId); Assert.Equal(27.50m, payments[1].Amount);
        var tasks = await db.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().ToArrayAsync(); Assert.Equal(3, tasks.Length); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Status == "done").Result); Assert.Equal(ids[1], Assert.Single(tasks, row => row.Title == "Открытая задача 2").ClientCaseId);
        Assert.Equal(soft ? 1 : 0, await db.ClientCaseDocuments.IgnoreQueryFilters().CountAsync());
        Assert.IsType<NotFoundResult>(await DeleteCaseAsync(db, user, ids[0], legacy, publisher, CancellationToken.None)); Assert.Equal(1, publisher.Count);
    }

    [Fact]
    public async Task CaseDelete_SoftV2DoesNotCallProductStorageDeletion()
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedCaseDeleteAsync(db);
        db.ClientCaseDocuments.Add(new ClientCaseDocument { ClientCaseId = ids[0], DocumentKey = "preserved", OriginalFileName = "preserved.txt", StoredFileName = "preserved.txt", StoragePath = "synthetic/preserved.txt", ContentType = "text/plain", SizeBytes = 4 });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear(); var deletion = new RecordingCaseProductDeletion(db) { Status = "unavailable" }; var publisher = new BulkPublicationBoundary(db);
        Assert.IsType<NoContentResult>(await CreateController(db, user, realtimePublisher: publisher, productStorageDeletion: deletion).Delete(ids[0], CancellationToken.None));
        Assert.Equal(0, deletion.Calls); Assert.Equal(1, publisher.Count); Assert.Equal(1, await db.ClientCaseDocuments.IgnoreQueryFilters().CountAsync()); Assert.Empty(await db.StorageObjects.ToArrayAsync()); Assert.Empty(await db.StorageTombstones.ToArrayAsync());
    }

    private static Task<IActionResult> DeleteCaseAsync(PaymentContext db, User user, int id, bool legacy, IRealtimeNotificationPublisher publisher, CancellationToken ct)
    {
        var controller = CreateController(db, user, realtimePublisher: publisher);
        return legacy ? new CasesController(db, publisher) { ControllerContext = controller.ControllerContext }.Delete(id, ct) : controller.Delete(id, ct);
    }

    private static async Task<(User User, int[] Ids)> SeedCaseDeleteAsync(PaymentContext db)
    {
        var result = await SeedBulkCasesAsync(db); var clientId = await db.ClientCases.Where(row => row.Id == result.Ids[0]).Select(row => row.ClientId).SingleAsync();
        db.Payments.AddRange(new Payment { ClientId = clientId, ClientCaseId = result.Ids[0], Type = PaymentType.Income, Status = PaymentStatus.Pending, Amount = 15.25m }, new Payment { ClientId = clientId, ClientCaseId = result.Ids[1], Type = PaymentType.Income, Status = PaymentStatus.Pending, Amount = 27.50m });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear(); return result;
    }

    private static async Task AssertCaseDeleteUnchangedAsync(PaymentContext db, int[] ids)
    {
        var cases = await db.ClientCases.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(); Assert.Equal(ids.Order(), cases.Select(row => row.Id));
        Assert.All(cases, row => { Assert.Equal("Исходная история", row.EventHistory); Assert.Equal(123.45m, row.CourtClaimAmount); Assert.Equal(ClientCaseStatus.Open, row.Status); });
        var payments = await db.Payments.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(); Assert.Equal(2, payments.Length); Assert.Equal(15.25m, payments[0].Amount); Assert.Equal(ids[0], payments[0].ClientCaseId); Assert.Equal(27.50m, payments[1].Amount); Assert.Equal(ids[1], payments[1].ClientCaseId);
        var tasks = await db.TaskJournalItems.AsNoTracking().ToArrayAsync(); Assert.Equal(3, tasks.Length); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Status == "done").Result); Assert.Equal(2, tasks.Count(row => row.Status == "new")); Assert.All(tasks, row => Assert.Contains(row.ClientCaseId!.Value, ids));
    }
}
