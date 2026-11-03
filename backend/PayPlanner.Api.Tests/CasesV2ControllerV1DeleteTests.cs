using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers.V1;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static Task<IActionResult> DeleteV1CaseAsync(PaymentContext db, User user, int id, CancellationToken ct)
        => new CasesV1Controller(db) { ControllerContext = CreateController(db, user).ControllerContext }.Delete(id, ct);

    [Fact]
    public async Task CaseDeleteV1_HardDeleteFailureRetainsPaymentLinks()
    {
        var fault = new CaseParentDeleteFault(); await using var db = await CreateContextAsync(fault, useObjectAuditModel: false);
        var (user, ids) = await SeedCaseDeleteAsync(db); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => DeleteV1CaseAsync(db, user, ids[0], CancellationToken.None));
        db.ChangeTracker.Clear(); var cases = await db.ClientCases.CountAsync(); var linked = await db.Payments.CountAsync(row => row.ClientCaseId == ids[0]);
        Assert.True(cases == ids.Length && linked == 1, $"cases={cases}; sourceLinkedPayments={linked}");
        Assert.Null(db.Database.CurrentTransaction); await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CaseDeleteV1_AfterPaymentSqlFailureOrCancellationRollsBack(bool postgres, bool cancel)
    {
        var fault = new ConfirmationAfterSaveFailure(cancel); await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options) : await CreateContextAsync(fault);
        var (user, ids) = await SeedCaseDeleteAsync(db); using var cancellation = new CancellationTokenSource(); fault.Cancel = cancellation.Cancel; fault.Armed = true;
        Task Write() => DeleteV1CaseAsync(db, user, ids[0], cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write); else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(fault.Called); Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseDeleteV1_DoesNotAdoptForeignTransactionOrPendingWork(bool pending)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedCaseDeleteAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Status == "done"); var original = task.Result; if (pending) task.Result = "Чужая несохранённая правка V1";
        Assert.IsType<ConflictObjectResult>(await DeleteV1CaseAsync(db, user, ids[0], CancellationToken.None)); Assert.Same(transaction, db.Database.CurrentTransaction);
        if (pending) { Assert.Equal(EntityState.Modified, db.Entry(task).State); Assert.Equal(original, (await db.Entry(task).GetDatabaseValuesAsync())![nameof(TaskJournalItem.Result)]); }
        await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Fact]
    public Task CaseDeleteV1_HttpCannotDeleteWhenViewWasRevoked()
        => AssertV1DeleteHttpRefusalAsync("view", HttpStatusCode.Forbidden);

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("inactive", HttpStatusCode.Unauthorized)]
    [InlineData("unapproved", HttpStatusCode.Unauthorized)]
    [InlineData("delete", HttpStatusCode.Forbidden)]
    [InlineData("hidden", HttpStatusCode.NotFound)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    public Task CaseDeleteV1_HttpChecksFreshActorDeleteAndVisibility(string mode, HttpStatusCode expected)
        => AssertV1DeleteHttpRefusalAsync(mode, expected);

    private async Task AssertV1DeleteHttpRefusalAsync(string mode, HttpStatusCode expected)
    {
        await using var db = await CreateContextAsync(); var (user, ids) = await SeedCaseDeleteAsync(db);
        if (mode == "inactive") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (mode == "unapproved") await db.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (mode == "view") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (mode == "delete") await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanDelete, false));
        if (mode == "hidden") { await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own)); await db.ClientCases.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true)); }
        using var server = CaseWriteServer(db, user); using var client = server.CreateClient(); if (mode != "anonymous") client.DefaultRequestHeaders.Add("X-Confirmation-User", user.Id.ToString());
        using var response = await client.DeleteAsync($"/api/v1/cases/{(mode == "missing" ? int.MaxValue : ids[0])}");
        var cases = await db.ClientCases.CountAsync(); var linked = await db.Payments.CountAsync(row => row.ClientCaseId == ids[0]);
        Assert.True(response.StatusCode == expected && cases == ids.Length && linked == 1, $"HTTP {(int)response.StatusCode}; cases={cases}; sourceLinkedPayments={linked}");
        Assert.Null(db.Database.CurrentTransaction); db.ChangeTracker.Clear(); await AssertCaseDeleteUnchangedAsync(db, ids);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CaseDeleteV1_SuccessKeepsUnrelatedPaymentsResultsAndRetention(bool postgres, bool soft)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options) : await CreateContextAsync(useObjectAuditModel: soft);
        var (user, ids) = await SeedCaseDeleteAsync(db); await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        db.ClientCaseDocuments.Add(new ClientCaseDocument { ClientCaseId = ids[0], DocumentKey = "retained-v1", OriginalFileName = "retained.txt", StoredFileName = "retained.txt", StoragePath = "synthetic/retained.txt", ContentType = "text/plain", SizeBytes = 4 }); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.IsType<NoContentResult>(await DeleteV1CaseAsync(db, user, ids[0], CancellationToken.None)); Assert.Null(db.Database.CurrentTransaction); Assert.False(await db.ClientCases.AnyAsync(row => row.Id == ids[0]));
        Assert.Equal(soft, await db.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == ids[0]));
        var payments = await db.Payments.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(); Assert.Equal(2, payments.Length); Assert.Null(payments[0].ClientCaseId); Assert.Equal(15.25m, payments[0].Amount); Assert.Equal(ids[1], payments[1].ClientCaseId); Assert.Equal(27.50m, payments[1].Amount);
        var tasks = await db.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().ToArrayAsync(); Assert.Equal(3, tasks.Length); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Status == "done").Result); Assert.Equal(ids[1], Assert.Single(tasks, row => row.Title == "Открытая задача 2").ClientCaseId);
        Assert.Equal(soft ? 1 : 0, await db.ClientCaseDocuments.IgnoreQueryFilters().CountAsync()); Assert.IsType<NotFoundResult>(await DeleteV1CaseAsync(db, user, ids[0], CancellationToken.None));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("role")]
    [InlineData("permission")]
    [InlineData("case")]
    [InlineData("owner")]
    [InlineData("department")]
    [InlineData("payment")]
    [InlineData("task")]
    [InlineData("link")]
    public async Task CaseDeleteV1_PgPinsAccessCasePaymentLinksAndTaskResults(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seed = Open(); var (user, ids) = await SeedCaseDeleteAsync(seed); var outsider = await CreateUserAsync(seed, "v1-delete-competitor@example.local", "Другой", DataVisibilityScope.All);
        if (target is "owner" or "department") await seed.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, target == "owner" ? DataVisibilityScope.Own : DataVisibilityScope.Department));
        if (target == "department")
        {
            var department = new DepartmentDictionaryItem { Name = "Синтетический отдел V1 удаления" }; seed.Departments.Add(department); await seed.SaveChangesAsync();
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
        await using var request = Open(hook); var result = await DeleteV1CaseAsync(request, user, source.Id, CancellationToken.None);
        Assert.True(hook.Called); Assert.True(reached); Assert.False(committed); Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked); Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        if (result is ConflictObjectResult) await AssertCaseDeleteUnchangedAsync(verify, ids);
        else
        {
            Assert.IsType<NoContentResult>(result); Assert.False(await verify.ClientCases.AnyAsync(row => row.Id == source.Id)); Assert.True(await verify.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == source.Id));
            var payments = await verify.Payments.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(); Assert.Equal(2, payments.Length); Assert.Null(payments[0].ClientCaseId); Assert.Equal(15.25m, payments[0].Amount); Assert.Equal(ids[1], payments[1].ClientCaseId); Assert.Equal(27.50m, payments[1].Amount);
            var tasks = await verify.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().ToArrayAsync(); Assert.Equal(3, tasks.Length); Assert.Equal("Сохранённый результат", Assert.Single(tasks, row => row.Status == "done").Result); Assert.Equal(ids[1], Assert.Single(tasks, row => row.Title == "Открытая задача 2").ClientCaseId);
        }
        Assert.True(await verify.Users.Where(row => row.Id == user.Id).Select(row => row.IsActive).SingleAsync()); Assert.True(await verify.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases).Select(row => row.CanDelete).SingleAsync());
    }

    [Fact]
    public async Task CaseDeleteV1_PgBusyCaseDoesNotDetachPayments()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seed = Open(); var (user, ids) = await SeedCaseDeleteAsync(seed); await using var other = Open(); await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCases\" WHERE \"Id\" = {ids[0]} FOR UPDATE");
        await using var request = Open(); Assert.IsType<ConflictObjectResult>(await DeleteV1CaseAsync(request, user, ids[0], CancellationToken.None));
        Assert.Null(request.Database.CurrentTransaction); await AssertCaseDeleteUnchangedAsync(seed, ids);
    }
}
