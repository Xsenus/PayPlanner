using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> ComplaintTaskSourceInvalidCases() =>
        from postgres in new[] { false, true }
        from kind in new[] { "instance", "complaint" }
        from tracked in new[] { false, true }
        from side in new[] { "ours", "opponent" }
        select new object[] { postgres, kind, tracked, side };

    [Theory]
    [MemberData(nameof(ComplaintTaskSourceInvalidCases))]
    public async Task ComplaintTaskSource_InvalidParentDoesNotCreateTasks(bool postgres, string kind, bool tracked, string side)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedComplaintTaskSourceAsync(db, side);
        await MakeComplaintTaskParentForeignAsync(db, seed, kind);
        if (!tracked) db.ChangeTracker.Clear();
        var source = await ComplaintTaskSourceRowsAsync(db);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        var tasks = await ComplaintSourceTasks(db, seed).AsNoTracking().ToArrayAsync();
        Assert.True(tasks.Length == 0, $"Derived tasks created from foreign parent={tasks.Length}.");
        Assert.Equal(source, await ComplaintTaskSourceRowsAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> ComplaintTaskSourceHistoryCases() =>
        from postgres in new[] { false, true }
        from kind in new[] { "instance", "complaint" }
        from status in new[] { "new", "done", "cancelled" }
        select new object[] { postgres, kind, status };

    [Theory]
    [MemberData(nameof(ComplaintTaskSourceHistoryCases))]
    public async Task ComplaintTaskSource_InvalidParentPreservesExistingTaskAndHistory(bool postgres, string kind, string status)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        var task = await ComplaintSourceTasks(db, seed).SingleAsync();
        task.Status = status;
        task.Result = "Результат, сохранённый сотрудником";
        task.Title = "Ручное название старой задачи";
        task.DueAt = task.DueAt.AddDays(3);
        await db.SaveChangesAsync();
        await MakeComplaintTaskParentForeignAsync(db, seed, kind);
        var before = await ComplaintTaskHistoryAsync(db, seed);
        var source = await ComplaintTaskSourceRowsAsync(db);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        Assert.Equal(before, await ComplaintTaskHistoryAsync(db, seed));
        Assert.Equal(source, await ComplaintTaskSourceRowsAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, "instance")]
    [InlineData(true, "instance")]
    [InlineData(false, "complaint")]
    [InlineData(true, "complaint")]
    public async Task ComplaintTaskSource_ArchivedForeignParentPreservesOldActiveTask(bool postgres, string kind)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        await MakeComplaintTaskParentForeignAsync(db, seed, kind);
        await db.ClientCaseCourtComplaints.Where(row => row.Id == seed.Complaint.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsArchived, true));
        db.ChangeTracker.Clear();
        var before = await ComplaintTaskHistoryAsync(db, seed);
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        Assert.Equal(before, await ComplaintTaskHistoryAsync(db, seed));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ComplaintTaskSource_CompatiblePendingFieldsAreSaved(bool postgres, bool complaintDate)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        if (complaintDate) seed.Complaint.FiledAt = DateTime.UnixEpoch.AddDays(2);
        else (await db.ClientCases.SingleAsync(row => row.Id == seed.Base.Cases[0])).Title = "Несохранённое название дела";
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        var task = await ComplaintSourceTasks(db, seed).AsNoTracking().SingleAsync();
        if (complaintDate)
        {
            Assert.Equal(DateTime.UnixEpoch.AddDays(12).AddHours(9), task.DueAt);
            Assert.Equal(DateTime.UnixEpoch.AddDays(2), await db.ClientCaseCourtComplaints.AsNoTracking()
                .Where(row => row.Id == seed.Complaint.Id).Select(row => row.FiledAt).SingleAsync());
        }
        else
        {
            Assert.Contains("Несохранённое название дела", task.ObjectName);
            Assert.Equal("Несохранённое название дела", await db.ClientCases.AsNoTracking()
                .Where(row => row.Id == seed.Base.Cases[0]).Select(row => row.Title).SingleAsync());
        }
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComplaintTaskSource_CallerTransactionRemainsOwnedByCaller(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        await using var transaction = await db.Database.BeginTransactionAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Single(await ComplaintSourceTasks(db, seed).ToArrayAsync());
        await transaction.RollbackAsync();
        db.ChangeTracker.Clear();
        Assert.Empty(await ComplaintSourceTasks(db, seed).AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, ClientCaseStatus.Closed)]
    [InlineData(true, ClientCaseStatus.Closed)]
    [InlineData(false, ClientCaseStatus.Lost)]
    [InlineData(true, ClientCaseStatus.Lost)]
    public async Task ComplaintTaskSource_ClosedCaseKeepsExistingTaskClosureRule(bool postgres, ClientCaseStatus status)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        var task = await ComplaintSourceTasks(db, seed).SingleAsync();
        task.Result = "Прежний ручной результат";
        await db.SaveChangesAsync();
        await MakeComplaintTaskParentForeignAsync(db, seed, "instance");
        await db.ClientCases.Where(row => row.Id == seed.Base.Cases[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, status));
        db.ChangeTracker.Clear();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        task = await ComplaintSourceTasks(db, seed).AsNoTracking().SingleAsync();
        Assert.Equal("done", task.Status);
        Assert.Equal("Прежний ручной результат", task.Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComplaintTaskSource_MembershipReadHasConstantQueryCost(bool postgres)
    {
        var recorder = new RecordingCommandInterceptor();
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(database!.ConnectionString).AddInterceptors(recorder).Options)
            : await CreateContextAsync(recorder);
        var seed = await SeedComplaintTaskSourceAsync(db, "ours");
        seed.Complaint.FiledBy = "unknown";
        await db.SaveChangesAsync();
        await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
        var foreignInstance = await db.ClientCaseCourtInstances.AsNoTracking().Where(row => row.ClientCaseId == seed.Base.Cases[1])
            .Select(row => row.Id).SingleAsync();
        async Task AddForeignComplaints(int count)
        {
            for (var index = 0; index < count; index++)
            {
                db.ClientCaseCourtComplaints.Add(new ClientCaseCourtComplaint
                {
                    ClientCaseId = seed.Base.Cases[0],
                    CourtInstanceId = foreignInstance,
                    Title = "Несогласованная апелляция",
                    FiledBy = "ours",
                    FiledAt = DateTime.UnixEpoch,
                    Status = "filed"
                });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        async Task<int> ReadQueryCount()
        {
            recorder.Commands.Clear();
            await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Base.Cases[0], seed.Base.User.Id, seed.Base.User.FullName, default);
            return recorder.Commands.Sum(command => command.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(statement => statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)));
        }
        await AddForeignComplaints(1);
        var one = await ReadQueryCount();
        await AddForeignComplaints(16);
        var seventeen = await ReadQueryCount();
        Assert.True(one > 0);
        Assert.Equal(one, seventeen);
        Assert.Empty(await db.TaskJournalItems.AsNoTracking().Where(row => row.ClientCaseId == seed.Base.Cases[0]
            && row.TagsJson.Contains("case-deadline:appeal-")).ToArrayAsync());
        await AssertCourtRetentionForeignCaseAsync(db, seed.Base);
        Console.WriteLine($"Complaint source SELECT count postgres={postgres}, one={one}, seventeen={seventeen}.");
    }

    private static async Task<CourtWriteSeed> SeedComplaintTaskSourceAsync(PaymentContext db, string side)
    {
        var seed = await SeedCourtWriteAsync(db, "update");
        var complaint = await db.ClientCaseCourtComplaints.SingleAsync(row => row.Id == seed.Complaint.Id);
        complaint.FiledBy = side;
        complaint.FiledAt = DateTime.UnixEpoch;
        complaint.Status = "filed";
        if (side == "opponent") db.ClientCaseCourtEvents.Add(new ClientCaseCourtEvent
        {
            ClientCaseId = seed.Base.Cases[0],
            CourtInstance = "appeal",
            EventType = "hearing",
            Title = "Своя апелляция",
            ScheduledAt = DateTime.UnixEpoch.AddDays(20),
            MovementStatus = "scheduled"
        });
        await db.SaveChangesAsync();
        await db.ClientCases.Include(row => row.CourtComplaints).FirstAsync(row => row.Id == seed.Base.Cases[0]);
        return seed with { Complaint = complaint };
    }

    private static async Task MakeComplaintTaskParentForeignAsync(PaymentContext db, CourtWriteSeed seed, string kind)
    {
        if (kind == "instance")
            await db.ClientCaseCourtInstances.Where(row => row.Id == seed.Instance.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Base.Cases[1]));
        else
        {
            var other = await db.ClientCaseCourtInstances.AsNoTracking().Where(row => row.ClientCaseId == seed.Base.Cases[1])
                .Select(row => row.Id).SingleAsync();
            await db.ClientCaseCourtComplaints.Where(row => row.Id == seed.Complaint.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtInstanceId, other));
        }
    }

    private static IQueryable<TaskJournalItem> ComplaintSourceTasks(PaymentContext db, CourtWriteSeed seed)
    {
        var tag = $"case-deadline:appeal-{(seed.Complaint.FiledBy == "opponent" ? "objection" : "followup")}-{seed.Complaint.Id}";
        return db.TaskJournalItems.IgnoreQueryFilters().Where(row => row.ClientCaseId == seed.Base.Cases[0] && row.TagsJson.Contains(tag));
    }

    private static async Task<string> ComplaintTaskHistoryAsync(PaymentContext db, CourtWriteSeed seed)
    {
        var tasks = ComplaintSourceTasks(db, seed);
        return JsonSerializer.Serialize(new
        {
            Tasks = await tasks.AsNoTracking().OrderBy(row => row.Id).Select(row => new
            {
                row.Id,
                row.ClientCaseId,
                row.ClientId,
                row.LegalEntityId,
                row.Title,
                row.ObjectName,
                row.Description,
                row.Status,
                row.Result,
                row.DueAt,
                row.TagsJson,
                row.CreatedAt,
                row.UpdatedAt,
                row.AssigneeUserId
            }).ToArrayAsync(),
            Comments = await db.TaskJournalComments.AsNoTracking().Where(row => tasks.Any(task => task.Id == row.TaskJournalItemId))
                .OrderBy(row => row.Id).Select(row => new { row.Id, row.TaskJournalItemId, row.Kind, row.Text, row.MetadataJson, row.CreatedAt, row.UpdatedAt }).ToArrayAsync(),
            Participants = await db.TaskJournalParticipants.AsNoTracking().Where(row => tasks.Any(task => task.Id == row.TaskJournalItemId))
                .OrderBy(row => row.Id).Select(row => new
                {
                    row.Id,
                    row.TaskJournalItemId,
                    row.UserId,
                    row.Role,
                    row.DisplayName,
                    row.Email,
                    row.SortOrder,
                    row.ApprovalStatus,
                    row.Resolution,
                    row.RespondedAt,
                    row.CreatedAt,
                    row.UpdatedAt
                }).ToArrayAsync()
        });
    }

    private static async Task<string> ComplaintTaskSourceRowsAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Instances = await db.ClientCaseCourtInstances.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.Instance, row.CourtUrl, row.JudicialUid }).ToArrayAsync(),
        Complaints = await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.CourtInstanceId, row.Title, row.Notes, row.Status, row.FiledBy, row.FiledAt, row.IsArchived, row.ClientCaseDocumentId }).ToArrayAsync()
    });
}
