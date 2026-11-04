using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    // Providers 0/1 use SQLite hard/soft deletion, 2/3 use PostgreSQL soft/hard deletion.
    public static IEnumerable<object[]> CourtNullableDeleteCases() => from route in new[] { 0, 1, 2 }
                                                                      from provider in new[] { 0, 1, 2, 3 }
                                                                      from kind in new[] { "complaint-document", "event-document", "deadline-document", "event-deadline" }
                                                                      select new object[] { route, provider, kind };

    [Theory]
    [MemberData(nameof(CourtNullableDeleteCases))]
    public async Task CourtNullable_HttpPreservesForeignReferenceForEachMode(int route, int provider, string kind)
    {
        await using var database = provider >= 2 ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = provider >= 2
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options, useObjectAuditModel: provider == 2)
            : await CreateContextAsync(useObjectAuditModel: provider == 1);
        var seed = await SeedCourtNullableAsync(db);
        await SetForeignNullableReferenceAsync(db, seed, kind);
        var before = await CourtNullableSnapshotAsync(db);
        var foreignBefore = await CourtNullableForeignSnapshotAsync(db, seed);
        var expectedTarget = await CourtNullableTargetAsync(db, seed, kind);
        using var server = CaseWriteServer(db, seed.Base.User);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Base.User.Id.ToString());
        using var response = await http.DeleteAsync($"/api/{(route == 0 ? "cases" : $"v{route}/cases")}/{seed.Base.Cases[0]}");
        var body = await response.Content.ReadAsStringAsync();
        db.ChangeTracker.Clear();
        var actualTarget = await CourtNullableTargetAsync(db, seed, kind);
        var foreignAfter = await CourtNullableForeignSnapshotAsync(db, seed);
        if (provider is 1 or 2)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(expectedTarget, actualTarget);
            Assert.Equal(foreignBefore, foreignAfter);
            Assert.False(await db.ClientCases.AnyAsync(row => row.Id == seed.Base.Cases[0]));
            Assert.True(await db.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Base.Cases[0]));
        }
        else
        {
            Assert.True(response.StatusCode == HttpStatusCode.Conflict,
                $"Expected409, actual{response.StatusCode}, originalTarget={expectedTarget}, currentTarget={actualTarget}, foreignUnchanged={foreignBefore == foreignAfter}.");
            Assert.Equal(expectedTarget, actualTarget);
            Assert.Equal(before, await CourtNullableSnapshotAsync(db));
        }
        Assert.DoesNotContain("private-nullable-reference", body);
        Assert.DoesNotContain("Другой клиент", body);
        Assert.Null(db.Database.CurrentTransaction);
        await AssertCourtRetentionForeignCaseAsync(db, seed.Base);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task CourtNullable_ValidHardDeletionPreservesOtherCase(int route, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options, useObjectAuditModel: false)
            : await CreateContextAsync(useObjectAuditModel: false);
        var seed = await SeedCourtNullableAsync(db);
        var before = await CourtNullableForeignSnapshotAsync(db, seed);
        Assert.IsType<NoContentResult>(await DeleteCourtOwnershipCaseAsync(db, seed.Base.User, seed.Base.Cases[0], route));
        db.ChangeTracker.Clear();
        Assert.False(await db.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Base.Cases[0]));
        Assert.False(await db.ClientCaseProceduralDeadlines.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.OwnDeadline.Id));
        Assert.Equal(before, await CourtNullableForeignSnapshotAsync(db, seed));
        Assert.Null(db.Database.CurrentTransaction);
        await AssertCourtRetentionForeignCaseAsync(db, seed.Base);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtNullable_DeletingOwnedOutboundReferencesRetainsForeignParents(bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options, useObjectAuditModel: false)
            : await CreateContextAsync(useObjectAuditModel: false);
        var seed = await SeedCourtNullableAsync(db);
        await db.ClientCaseCourtComplaints.Where(row => row.Id == seed.OwnComplaint.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, seed.Base.OtherDocument.Id));
        await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.OwnDeadline.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, seed.Base.OtherDocument.Id));
        await db.ClientCaseCourtEvents.Where(row => row.Id == seed.Base.Event.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.ClientCaseDocumentId, seed.Base.OtherDocument.Id).SetProperty(row => row.ProceduralDeadlineId, seed.OtherDeadline.Id));
        var before = await CourtNullableForeignSnapshotAsync(db, seed);
        Assert.IsType<NoContentResult>(await DeleteCourtOwnershipCaseAsync(db, seed.Base.User, seed.Base.Cases[0], 2));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtNullableForeignSnapshotAsync(db, seed));
        Assert.False(await db.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Base.Cases[0]));
        Assert.Null(db.Database.CurrentTransaction);
        await AssertCourtRetentionForeignCaseAsync(db, seed.Base);
    }

    public static IEnumerable<object[]> CourtNullableHistoricalCases() => from postgres in new[] { false, true }
                                                                          from kind in new[] { "complaint-document", "event-document", "deadline-document", "event-deadline" }
                                                                          select new object[] { postgres, kind };

    [Theory]
    [MemberData(nameof(CourtNullableHistoricalCases))]
    public async Task CourtNullable_HistoricalReferencesStopHardDeletion(bool postgres, string kind)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options, useObjectAuditModel: false)
            : await CreateContextAsync(useObjectAuditModel: false);
        var seed = await SeedCourtNullableAsync(db);
        await SetForeignNullableReferenceAsync(db, seed, kind);
        if (kind == "complaint-document") await db.ClientCaseCourtComplaints.Where(row => row.Id == seed.OtherComplaint.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsArchived, true));
        else if (kind == "event-document") await db.ClientCaseCourtEvents.Where(row => row.Id == seed.OtherEvent.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.MovementStatus, "completed"));
        else await db.ClientCaseProceduralDeadlines.Where(row => row.Id == (kind == "event-deadline" ? seed.OwnDeadline.Id : seed.OtherDeadline.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "closed"));
        var before = await CourtNullableSnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await DeleteCourtOwnershipCaseAsync(db, seed.Base.User, seed.Base.Cases[0], 2));
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtNullableSnapshotAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("complaint-document")]
    [InlineData("event-document")]
    [InlineData("deadline-document")]
    [InlineData("event-deadline")]
    public async Task CourtNullable_RefusalPrecedesStorageAndRealtime(string kind)
    {
        await using var db = await CreateContextAsync(useObjectAuditModel: false);
        var seed = await SeedCourtNullableAsync(db);
        await SetForeignNullableReferenceAsync(db, seed, kind);
        var before = await CourtNullableSnapshotAsync(db);
        var storage = new RecordingCaseProductDeletion(db) { Status = "unavailable" };
        var publisher = new BulkPublicationBoundary(db);
        var controller = CreateController(db, seed.Base.User, realtimePublisher: publisher, productStorageDeletion: storage);
        Assert.IsType<ConflictObjectResult>(await controller.Delete(seed.Base.Cases[0], default));
        Assert.Equal(0, storage.Calls);
        Assert.Equal(0, publisher.Count);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtNullableSnapshotAsync(db));
    }

    [Theory]
    [InlineData("deadline-document")]
    [InlineData("deadline-insert")]
    [InlineData("deadline-parent")]
    public async Task CourtNullable_PgPreservesCommittedDeadlineChangesOnDeleteConflict(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options, useObjectAuditModel: false);
        await using var seedDb = Open();
        var seed = await SeedCourtNullableAsync(seedDb);
        var attempted = false;
        var committed = false;
        string? committedState = null;
        var hook = new CourtRetentionBeforeRoleLock(async ct =>
        {
            await using var other = Open();
            await using var transaction = await other.Database.BeginTransactionAsync(ct);
            attempted = true;
            if (target == "deadline-document") await other.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.OtherDeadline.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, seed.Base.Document.Id), ct);
            else if (target == "deadline-parent") await other.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.OtherDeadline.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Base.Cases[0]), ct);
            else other.ClientCaseProceduralDeadlines.Add(new ClientCaseProceduralDeadline
            { ClientCaseId = seed.Base.Cases[1], ClientCaseDocumentId = seed.Base.Document.Id, DeadlineType = "appeal", Title = "Параллельный чужой срок", DueAt = DateTime.UnixEpoch });
            await other.SaveChangesAsync(ct);
            committedState = await CourtNullableSnapshotAsync(other);
            await transaction.CommitAsync(ct);
            committed = true;
        });
        await using var request = Open(hook);
        var storage = new RecordingCaseProductDeletion(request) { RecordWrites = true };
        var publisher = new BulkPublicationBoundary(request);
        var result = await CreateController(request, seed.Base.User, realtimePublisher: publisher, productStorageDeletion: storage)
            .Delete(seed.Base.Cases[0], default);
        Assert.True(hook.Called);
        Assert.True(attempted);
        Assert.True(committed);
        Assert.NotNull(committedState);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(1, storage.Calls);
        Assert.Equal(0, publisher.Count);
        Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        Assert.True(await verify.ClientCases.AnyAsync(row => row.Id == seed.Base.Cases[0]));
        Assert.Equal(committedState, await CourtNullableSnapshotAsync(verify));
        Assert.Empty(await verify.StorageObjects.IgnoreQueryFilters().ToArrayAsync());
        Assert.Empty(await verify.StorageTombstones.IgnoreQueryFilters().ToArrayAsync());
        await AssertCourtRetentionForeignCaseAsync(verify, seed.Base);
    }

    [Fact]
    public async Task CourtNullable_PgBusyOwnedDeadlineStopsBeforeWrites()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options, useObjectAuditModel: false);
        await using var seedDb = Open();
        var seed = await SeedCourtNullableAsync(seedDb);
        var before = await CourtNullableSnapshotAsync(seedDb);
        await using var other = Open();
        await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseProceduralDeadlines\" WHERE \"Id\" = {seed.OwnDeadline.Id} FOR UPDATE");
        await using var request = Open();
        Assert.IsType<ConflictObjectResult>(await DeleteCourtOwnershipCaseAsync(request, seed.Base.User, seed.Base.Cases[0], 2));
        Assert.Null(request.Database.CurrentTransaction);
        Assert.Equal(before, await CourtNullableSnapshotAsync(seedDb));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtNullable_PgUnrelatedDeadlineWriterAllowsDeletion(bool hard)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options, useObjectAuditModel: !hard);
        await using var seedDb = Open();
        var seed = await SeedCourtNullableAsync(seedDb);
        var foreignBefore = await CourtNullableForeignSnapshotAsync(seedDb, seed);
        await using var other = Open();
        await using var transaction = await other.Database.BeginTransactionAsync();
        await other.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.OtherDeadline.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Чужая незавершённая правка"));
        await using var request = Open();
        var result = await DeleteCourtOwnershipCaseAsync(request, seed.Base.User, seed.Base.Cases[0], 2);
        Assert.Null(request.Database.CurrentTransaction);
        await transaction.RollbackAsync();
        await using var verify = Open();
        Assert.IsType<NoContentResult>(result);
        Assert.False(await verify.ClientCases.AnyAsync(row => row.Id == seed.Base.Cases[0]));
        Assert.Equal(!hard, await verify.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Base.Cases[0]));
        Assert.Equal(foreignBefore, await CourtNullableForeignSnapshotAsync(verify, seed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtNullable_PgHardFailureOrCancellationRollsBackActualPaymentSql(bool cancel)
    {
        var fault = new ConfirmationAfterSaveFailure(cancel);
        await using var database = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(fault).Options, useObjectAuditModel: false);
        var seed = await SeedCourtNullableAsync(db);
        var before = await CourtNullableSnapshotAsync(db);
        var publisher = new BulkPublicationBoundary(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        Task Write() => CreateController(db, seed.Base.User, realtimePublisher: publisher).Delete(seed.Base.Cases[0], cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<ConfirmationSaveFailureException>(Write);
        Assert.True(fault.Called);
        Assert.Equal(0, publisher.Count);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtNullableSnapshotAsync(db));
    }

    private sealed record CourtNullableSeed(CourtRetentionSeed Base, ClientCaseCourtComplaint OwnComplaint,
        ClientCaseCourtComplaint OtherComplaint, ClientCaseProceduralDeadline OwnDeadline,
        ClientCaseProceduralDeadline OtherDeadline, ClientCaseCourtEvent OtherEvent);

    private static async Task<CourtNullableSeed> SeedCourtNullableAsync(PaymentContext db)
    {
        var seed = await SeedCourtRetentionAsync(db);
        var ownInstance = new ClientCaseCourtInstance { ClientCaseId = seed.Cases[0], Instance = "first" };
        var otherInstance = new ClientCaseCourtInstance { ClientCaseId = seed.Cases[1], Instance = "first" };
        var ownTask = await db.TaskJournalItems.Where(row => row.ClientCaseId == seed.Cases[0] && row.Status == "done").Select(row => row.Id).SingleAsync();
        var otherTask = await db.TaskJournalItems.Where(row => row.ClientCaseId == seed.Cases[1]).Select(row => row.Id).SingleAsync();
        var ownDeadline = new ClientCaseProceduralDeadline
        { ClientCaseId = seed.Cases[0], ClientCaseDocumentId = seed.Document.Id, TaskJournalItemId = ownTask, DeadlineType = "appeal", Title = "Свой срок", DueAt = DateTime.UnixEpoch.AddDays(7) };
        var otherDeadline = new ClientCaseProceduralDeadline
        { ClientCaseId = seed.Cases[1], ClientCaseDocumentId = seed.OtherDocument.Id, TaskJournalItemId = otherTask, DeadlineType = "appeal", Title = "Другой срок", Notes = "private-nullable-reference", DueAt = DateTime.UnixEpoch.AddDays(8) };
        db.ClientCaseCourtInstances.AddRange(ownInstance, otherInstance);
        db.ClientCaseProceduralDeadlines.AddRange(ownDeadline, otherDeadline);
        await db.SaveChangesAsync();
        var ownComplaint = new ClientCaseCourtComplaint
        { ClientCaseId = seed.Cases[0], CourtInstanceId = ownInstance.Id, ClientCaseDocumentId = seed.Document.Id, Title = "Своя жалоба" };
        var otherComplaint = new ClientCaseCourtComplaint
        { ClientCaseId = seed.Cases[1], CourtInstanceId = otherInstance.Id, ClientCaseDocumentId = seed.OtherDocument.Id, Title = "Другая жалоба", Notes = "private-nullable-reference" };
        var otherEvent = new ClientCaseCourtEvent
        { ClientCaseId = seed.Cases[1], ClientCaseDocumentId = seed.OtherDocument.Id, ProceduralDeadlineId = otherDeadline.Id, Title = "Другое событие", ScheduledAt = DateTime.UnixEpoch, ExternalKey = "nullable-other", Description = "private-nullable-reference" };
        db.ClientCaseCourtComplaints.AddRange(ownComplaint, otherComplaint);
        db.ClientCaseCourtEvents.Add(otherEvent);
        await db.ClientCaseCourtEvents.Where(row => row.Id == seed.Event.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.ClientCaseDocumentId, seed.Document.Id).SetProperty(row => row.ProceduralDeadlineId, ownDeadline.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(seed, ownComplaint, otherComplaint, ownDeadline, otherDeadline, otherEvent);
    }

    private static async Task SetForeignNullableReferenceAsync(PaymentContext db, CourtNullableSeed seed, string kind)
    {
        var changed = kind switch
        {
            "complaint-document" => await db.ClientCaseCourtComplaints.Where(row => row.Id == seed.OtherComplaint.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, seed.Base.Document.Id)),
            "event-document" => await db.ClientCaseCourtEvents.Where(row => row.Id == seed.OtherEvent.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, seed.Base.Document.Id)),
            "deadline-document" => await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.OtherDeadline.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, seed.Base.Document.Id)),
            "event-deadline" => await db.ClientCaseCourtEvents.Where(row => row.Id == seed.OtherEvent.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ProceduralDeadlineId, seed.OwnDeadline.Id)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.Equal(1, changed);
    }

    private static Task<int?> CourtNullableTargetAsync(PaymentContext db, CourtNullableSeed seed, string kind) => kind switch
    {
        "complaint-document" => db.ClientCaseCourtComplaints.IgnoreQueryFilters().Where(row => row.Id == seed.OtherComplaint.Id).Select(row => row.ClientCaseDocumentId).SingleAsync(),
        "event-document" => db.ClientCaseCourtEvents.IgnoreQueryFilters().Where(row => row.Id == seed.OtherEvent.Id).Select(row => row.ClientCaseDocumentId).SingleAsync(),
        "deadline-document" => db.ClientCaseProceduralDeadlines.IgnoreQueryFilters().Where(row => row.Id == seed.OtherDeadline.Id).Select(row => row.ClientCaseDocumentId).SingleAsync(),
        "event-deadline" => db.ClientCaseCourtEvents.IgnoreQueryFilters().Where(row => row.Id == seed.OtherEvent.Id).Select(row => row.ProceduralDeadlineId).SingleAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static async Task<string> CourtNullableForeignSnapshotAsync(PaymentContext db, CourtNullableSeed seed) => JsonSerializer.Serialize(new
    {
        Existing = await CourtRetentionForeignSnapshotAsync(db, seed.Base),
        Complaint = await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AsNoTracking().Where(row => row.Id == seed.OtherComplaint.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.CourtInstanceId, row.ClientCaseDocumentId, row.Title, row.Notes, row.Status, row.IsArchived }).SingleAsync(),
        Event = await db.ClientCaseCourtEvents.IgnoreQueryFilters().AsNoTracking().Where(row => row.Id == seed.OtherEvent.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.ClientCaseDocumentId, row.ProceduralDeadlineId, row.Title, row.Description, row.MovementStatus, row.ScheduledAt, row.SourceType }).SingleAsync(),
        Deadline = await db.ClientCaseProceduralDeadlines.IgnoreQueryFilters().AsNoTracking().Where(row => row.Id == seed.OtherDeadline.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.ClientCaseDocumentId, row.TaskJournalItemId, row.Title, row.Description, row.Notes, row.DueAt, row.AllDay, row.Status, row.SourceType, row.SourceDocumentKey, row.ClosedAt }).SingleAsync(),
        ComplaintVisible = await db.ClientCaseCourtComplaints.AnyAsync(row => row.Id == seed.OtherComplaint.Id),
        EventVisible = await db.ClientCaseCourtEvents.AnyAsync(row => row.Id == seed.OtherEvent.Id),
        DeadlineVisible = await db.ClientCaseProceduralDeadlines.AnyAsync(row => row.Id == seed.OtherDeadline.Id)
    });

    private static async Task<string> CourtNullableSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Existing = await CourtRetentionSnapshotAsync(db),
        Complaints = await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.CourtInstanceId, row.ClientCaseDocumentId, row.Title, row.Status, row.IsArchived }).ToArrayAsync(),
        Events = await db.ClientCaseCourtEvents.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.ClientCaseDocumentId, row.ProceduralDeadlineId, row.Title, row.MovementStatus }).ToArrayAsync(),
        Deadlines = await db.ClientCaseProceduralDeadlines.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.ClientCaseDocumentId, row.TaskJournalItemId, row.Title, row.DeadlineType, row.DueAt, row.Status, row.Notes, row.ClosedAt }).ToArrayAsync()
    });
}
