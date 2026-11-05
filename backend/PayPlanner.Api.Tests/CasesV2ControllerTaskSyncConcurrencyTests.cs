using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> TaskSyncLateRivalCases() =>
        from mode in new[] { "move", "complete", "delete" }
        from transaction in new[] { 0, 1, 2 }
        select new object[] { mode, transaction };

    [Theory]
    [MemberData(nameof(TaskSyncLateRivalCases))]
    public async Task TaskSyncConcurrency_LateRivalKeepsItsCommittedTask(string mode, int transaction)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seedDb = Open();
        var seed = await SeedComplaintTaskSourceAsync(seedDb, "ours");
        await ClientCaseDeadlineTaskSyncService.SyncAsync(seedDb, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        var task = await ComplaintSourceTasks(seedDb, seed).SingleAsync();
        task.Title = "Первоначальная задача";
        task.Result = "Прежний результат";
        await seedDb.SaveChangesAsync();
        await seedDb.ClientCaseCourtComplaints.Where(row => row.Id == seed.Complaint.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.IsArchived, mode == "complete")
            .SetProperty(row => row.FiledAt, DateTime.UnixEpoch.AddDays(2)));
        var operationBefore = await TaskSyncTargetSnapshotAsync(seedDb, task.Id);
        var attempted = false;
        var committed = false;
        string? rivalSnapshot = null;
        string? blocked = null;
        var hook = new CourtWriteBeforeSave(async ct =>
        {
            await using var rival = Open();
            await using var rivalTransaction = await rival.Database.BeginTransactionAsync(ct);
            await rival.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                if (mode == "delete") await rival.TaskJournalItems.Where(row => row.Id == task.Id).ExecuteDeleteAsync(ct);
                else if (mode == "move")
                {
                    var clientId = await rival.ClientCases.Where(row => row.Id == seed.Base.Cases[1]).Select(row => row.ClientId).SingleAsync(ct);
                    await rival.TaskJournalItems.Where(row => row.Id == task.Id).ExecuteUpdateAsync(set => set
                        .SetProperty(row => row.ClientCaseId, seed.Base.Cases[1])
                        .SetProperty(row => row.ClientId, clientId)
                        .SetProperty(row => row.Title, "Задача другого дела")
                        .SetProperty(row => row.Result, "Подтверждённый результат другого дела")
                        .SetProperty(row => row.DueAt, DateTime.UnixEpoch.AddDays(100)), ct);
                }
                else await rival.TaskJournalItems.Where(row => row.Id == task.Id).ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.Status, "done")
                    .SetProperty(row => row.Title, "Завершено сотрудником")
                    .SetProperty(row => row.Result, "Подтверждённый ручной результат")
                    .SetProperty(row => row.DueAt, DateTime.UnixEpoch.AddDays(100)), ct);
                await rivalTransaction.CommitAsync(ct);
                committed = true;
                rivalSnapshot = await TaskSyncTargetSnapshotAsync(rival, task.Id);
            }
            catch (Exception error) when (TaskSyncPostgresState(error) is PostgresErrorCodes.LockNotAvailable
                or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                blocked = TaskSyncPostgresState(error);
            }
        });
        await using var request = Open(hook);
        await using var callerTransaction = transaction == 0 ? null : await request.Database.BeginTransactionAsync(
            transaction == 1 ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        hook.Armed = true;
        var error = await Record.ExceptionAsync(() => ClientCaseDeadlineTaskSyncService.SyncAsync(
            request, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default));
        if (callerTransaction is not null)
        {
            if (error is null) await callerTransaction.CommitAsync();
            else await callerTransaction.RollbackAsync();
        }
        Assert.True(hook.Called && attempted);
        if (error is not null) Assert.True(IsTaskSyncRaceAbort(error), error.ToString());
        await using var verify = Open();
        if (committed)
        {
            Assert.Equal(rivalSnapshot, await TaskSyncTargetSnapshotAsync(verify, task.Id));
        }
        else
        {
            if (error is not null)
            {
                Console.WriteLine($"Service aborted {error.GetType().Name}, state={TaskSyncPostgresState(error)}.");
                Assert.Equal(operationBefore, await TaskSyncTargetSnapshotAsync(verify, task.Id));
            }
            else
            {
                Assert.Contains(blocked, new[] { PostgresErrorCodes.LockNotAvailable, PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.DeadlockDetected });
                Assert.Equal(seed.Base.Cases[0], await verify.TaskJournalItems.Where(row => row.Id == task.Id).Select(row => row.ClientCaseId).SingleAsync());
            }
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("move")]
    [InlineData("case")]
    public async Task TaskSyncConcurrency_PreloadedChangesRequireSafeRefreshAndRetry(string mode)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seedDb = Open();
        var seed = await SeedComplaintTaskSourceAsync(seedDb, "ours");
        await ClientCaseDeadlineTaskSyncService.SyncAsync(seedDb, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        var task = await ComplaintSourceTasks(seedDb, seed).SingleAsync();
        task.Result = "Прежний ручной результат";
        await seedDb.SaveChangesAsync();
        await seedDb.ClientCaseCourtComplaints.Where(row => row.Id == seed.Complaint.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsArchived, mode == "complete"));
        await using var request = Open();
        await request.ClientCases.Include(row => row.CourtComplaints).SingleAsync(row => row.Id == seed.Base.Cases[0]);
        await request.TaskJournalItems.Include(row => row.Participants).SingleAsync(row => row.Id == task.Id);
        await using var rival = Open();
        if (mode == "case") await rival.ClientCases.Where(row => row.Id == seed.Base.Cases[0])
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, ClientCaseStatus.Closed));
        else if (mode == "move") await rival.TaskJournalItems.Where(row => row.Id == task.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Base.Cases[1])
                .SetProperty(row => row.Title, "Перенесённая задача").SetProperty(row => row.Result, "Результат другого дела"));
        else await rival.TaskJournalItems.Where(row => row.Id == task.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.Status, "done").SetProperty(row => row.Result, "Подтверждённый результат сотрудника"));
        var before = await TaskSyncTargetSnapshotAsync(rival, task.Id);
        var error = await Record.ExceptionAsync(() => ClientCaseDeadlineTaskSyncService.SyncAsync(
            request, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default));
        if (error is not null)
        {
            Assert.True(IsTaskSyncRaceAbort(error), error.ToString());
            Assert.Equal(before, await TaskSyncTargetSnapshotAsync(rival, task.Id));
        }
        else if (mode == "case") Assert.Equal("done", await rival.TaskJournalItems.Where(row => row.Id == task.Id).Select(row => row.Status).SingleAsync());
        await using var retry = Open();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(retry, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        if (mode == "case")
        {
            Assert.Equal("done", await rival.TaskJournalItems.Where(row => row.Id == task.Id).Select(row => row.Status).SingleAsync());
            Assert.Equal("Прежний ручной результат", await rival.TaskJournalItems.Where(row => row.Id == task.Id).Select(row => row.Result).SingleAsync());
        }
        else Assert.Equal(before, await TaskSyncTargetSnapshotAsync(rival, task.Id));
    }

    public static IEnumerable<object[]> TaskSyncFailureCases() =>
        from postgres in new[] { false, true }
        from outer in new[] { false, true }
        from cancel in new[] { false, true }
        select new object[] { postgres, outer, cancel };

    [Theory]
    [MemberData(nameof(TaskSyncFailureCases))]
    public async Task TaskSyncConcurrency_FailedSaveKeepsCallerStateAndTransaction(bool postgres, bool outer, bool cancel)
    {
        var fault = new CourtWriteAfterSaveFault(1, cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        await db.ClientCaseCourtComplaints.Where(row => row.Id == seed.Complaint.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsArchived, true));
        db.ChangeTracker.Clear();
        await using var callerTransaction = outer ? await db.Database.BeginTransactionAsync() : null;
        if (outer) await db.TaskJournalItems.Where(row => row.ClientCaseId == seed.Base.Cases[1])
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Result, "Независимая запись вызывающего кода"));
        var before = await CourtWriteSnapshotAsync(db);
        var clientCase = await db.ClientCases.SingleAsync(row => row.Id == seed.Base.Cases[0]);
        clientCase.Title = "Несохранённое намерение вызывающего кода";
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        Task Run() => ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else await Assert.ThrowsAsync<CourtWriteInjectedFailure>(Run);
        Assert.True(fault.Called);
        Assert.Same(callerTransaction, db.Database.CurrentTransaction);
        if (outer) await callerTransaction!.CommitAsync();
        Assert.Equal(before, await CourtWriteSnapshotAsync(db));
        Assert.Equal("Несохранённое намерение вызывающего кода", clientCase.Title);
        Assert.True(db.Entry(clientCase).Property(row => row.Title).IsModified);
        fault.Armed = false;
        var taskId = await ComplaintSourceTasks(db, seed).Select(row => row.Id).SingleAsync();
        var historyCount = await db.TaskJournalComments.CountAsync(row => row.TaskJournalItemId == taskId);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        Assert.Equal("cancelled", await db.TaskJournalItems.AsNoTracking().Where(row => row.Id == taskId).Select(row => row.Status).SingleAsync());
        Assert.Equal(historyCount + 1, await db.TaskJournalComments.CountAsync(row => row.TaskJournalItemId == taskId));
        Assert.Equal("Несохранённое намерение вызывающего кода", await db.ClientCases.AsNoTracking()
            .Where(row => row.Id == seed.Base.Cases[0]).Select(row => row.Title).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskSyncConcurrency_StorePrecisionAcceptsSavedAndPendingFields(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        var clientCase = await db.ClientCases.SingleAsync(row => row.Id == seed.Base.Cases[0]);
        clientCase.FeeAmount = 123.4567m;
        clientCase.CourtDecisionAt = DateTime.UnixEpoch.AddDays(10).AddTicks(17);
        await db.SaveChangesAsync();
        var storedFee = await db.ClientCases.AsNoTracking().Where(row => row.Id == clientCase.Id).Select(row => row.FeeAmount).SingleAsync();
        var feeProperty = db.Model.FindEntityType(typeof(ClientCase))!.FindProperty(nameof(ClientCase.FeeAmount))!;
        Console.WriteLine($"Fee input={clientCase.FeeAmount}, stored={storedFee}, column={feeProperty.GetColumnType()}, precision={feeProperty.GetPrecision()}, scale={feeProperty.GetScale()}.");
        clientCase.Title = "Сохранение после округления базы";
        try { await ClientCaseDeadlineTaskSyncService.SyncAsync(db, clientCase.Id, seed.Base.User.Id, seed.Base.User.FullName, default); }
        catch (DbUpdateConcurrencyException error)
        {
            Console.WriteLine($"Precision check field={error.Data["CaseTaskSyncField"]}.");
            throw;
        }
        Assert.Equal(storedFee, await db.ClientCases.AsNoTracking().Where(row => row.Id == clientCase.Id).Select(row => row.FeeAmount).SingleAsync());
        Assert.Equal(clientCase.Title, await db.ClientCases.AsNoTracking().Where(row => row.Id == clientCase.Id).Select(row => row.Title).SingleAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private static bool IsTaskSyncRaceAbort(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is DbUpdateConcurrencyException || current is PostgresException
                { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.ForeignKeyViolation }) return true;
        return false;
    }

    private static string? TaskSyncPostgresState(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is PostgresException postgres) return postgres.SqlState;
        return null;
    }

    private static async Task<string> TaskSyncTargetSnapshotAsync(PaymentContext db, int taskId) => JsonSerializer.Serialize(new
    {
        Task = await db.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().Where(row => row.Id == taskId)
            .Select(row => new { row.Id, row.ClientCaseId, row.ClientId, row.LegalEntityId, row.Title, row.ObjectName, row.Description, row.Status, row.Result, row.DueAt, row.TagsJson, row.UpdatedAt }).SingleOrDefaultAsync(),
        Comments = await db.TaskJournalComments.IgnoreQueryFilters().AsNoTracking().Where(row => row.TaskJournalItemId == taskId).OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.TaskJournalItemId, row.Kind, row.Text, row.MetadataJson, row.CreatedAt, row.UpdatedAt }).ToArrayAsync(),
        Participants = await db.TaskJournalParticipants.IgnoreQueryFilters().AsNoTracking().Where(row => row.TaskJournalItemId == taskId).OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.TaskJournalItemId, row.UserId, row.Role, row.DisplayName, row.Email, row.ApprovalStatus, row.Resolution }).ToArrayAsync()
    });
}
