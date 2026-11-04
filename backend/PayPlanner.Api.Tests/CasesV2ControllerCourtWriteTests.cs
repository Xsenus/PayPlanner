using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Controllers.V2;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtWriteAdmissionCases() => from command in new[] { "instance", "create", "update", "archive" }
                                                                      from postgres in new[] { false, true }
                                                                      from mode in new[] { "session", "view" }
                                                                      select new object[] { command, postgres, mode };

    [Theory]
    [MemberData(nameof(CourtWriteAdmissionCases))]
    public Task CourtWrite_HttpRejectsRevokedSessionAndMissingView(string command, bool postgres, string mode)
        => AssertCourtWriteHttpRefusalAsync(command, postgres, mode);

    public static IEnumerable<object[]> CourtWriteHttpScopeCases() => from command in new[] { "instance", "create", "update", "archive" }
                                                                      from mode in new[] { "anonymous", "inactive", "unapproved", "edit", "hidden", "missing" }
                                                                      select new object[] { command, mode };

    [Theory]
    [MemberData(nameof(CourtWriteHttpScopeCases))]
    public Task CourtWrite_HttpChecksActorAndScope(string command, string mode)
        => AssertCourtWriteHttpRefusalAsync(command, false, mode);

    private async Task AssertCourtWriteHttpRefusalAsync(string command, bool postgres, string mode)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtWriteAsync(db, command);
        if (mode == "session") await db.Users.Where(row => row.Id == seed.Base.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.AuthTokenVersion, 1));
        if (mode == "inactive") await db.Users.Where(row => row.Id == seed.Base.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (mode == "unapproved") await db.Users.Where(row => row.Id == seed.Base.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (mode == "view") await db.RolePermissions.Where(row => row.RoleId == seed.Base.User.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (mode == "edit") await db.RolePermissions.Where(row => row.RoleId == seed.Base.User.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        var before = await CourtWriteSnapshotAsync(db);
        using var server = CaseWriteServer(db, seed.Base.User);
        using var http = server.CreateClient();
        if (mode != "anonymous") http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Base.User.Id.ToString());
        var id = mode == "hidden" ? seed.Base.Cases[1] : mode == "missing" ? int.MaxValue : seed.Base.Cases[0];
        using var response = await SendCourtWriteHttpAsync(http, seed, command, id);
        var expected = mode is "view" or "edit" ? HttpStatusCode.Forbidden
            : mode is "hidden" or "missing" ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized;
        db.ChangeTracker.Clear();
        var after = await CourtWriteSnapshotAsync(db);
        Assert.True(response.StatusCode == expected && before == after,
            $"Expected{expected}, actual{response.StatusCode}, unchanged={before == after}.");
        Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> CourtWriteFailures() => from command in new[] { "instance", "create", "update", "archive" }
                                                                from save in Enumerable.Range(1, command == "create" ? 3 : command == "instance" ? 1 : 2)
                                                                from postgres in new[] { false, true }
                                                                from cancel in new[] { false, true }
                                                                select new object[] { command, save, postgres, cancel };

    [Theory]
    [MemberData(nameof(CourtWriteFailures))]
    public async Task CourtWrite_AfterActualSaveFailureOrCancellationRollsBackGraph(string command, int save, bool postgres, bool cancel)
    {
        var fault = new CourtWriteAfterSaveFault(save, cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var seed = await SeedCourtWriteAsync(db, command);
        var before = await CourtWriteSnapshotAsync(db);
        var publisher = new BulkPublicationBoundary(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        Task Write() => InvokeCourtWriteAsync(CreateController(db, seed.Base.User, realtimePublisher: publisher), seed, command, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<CourtWriteInjectedFailure>(Write);
        Assert.True(fault.Called);
        Assert.Equal(0, publisher.Count);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        var after = await CourtWriteSnapshotAsync(db);
        Assert.True(before == after, $"Save={save}, transaction={fault.HadTransaction}, graphUnchanged={before == after}.");
    }

    private sealed class CourtWriteInjectedFailure : IOException;

    private sealed class CourtWriteAfterSaveFault(int targetSave, bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public bool HadTransaction { get; private set; }
        public Action? Cancel { get; set; }
        private int saves;

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed && ++saves == targetSave)
            {
                Called = true;
                HadTransaction = eventData.Context!.Database.CurrentTransaction is not null;
                if (cancel)
                {
                    Cancel!();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new CourtWriteInjectedFailure();
            }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData("instance", false)]
    [InlineData("instance", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    public async Task CourtWrite_SuccessCommitsBeforePublicationAndPreservesExistingResults(string command, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtWriteAsync(db, command);
        await db.RolePermissions.Where(row => row.RoleId == seed.Base.User.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false));
        var publisher = new BulkPublicationBoundary(db);
        var result = await InvokeCourtWriteAsync(CreateController(db, seed.Base.User, realtimePublisher: publisher), seed, command, default);
        if (command == "create")
        {
            var created = Assert.IsType<CreatedResult>(result);
            var dto = Assert.IsType<CasesV2Controller.ClientCaseCourtComplaintDto>(created.Value);
            Assert.Equal($"/api/v2/cases/{seed.Base.Cases[0]}/court-instances/first/complaints/{dto.Id}", created.Location);
            Assert.Equal("ours", dto.FiledBy);
        }
        else if (command == "archive") Assert.IsType<NoContentResult>(result);
        else Assert.IsType<OkObjectResult>(result);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(command == "instance" ? 0 : 1, publisher.Count);
        db.ChangeTracker.Clear();
        if (command == "archive") Assert.True(await db.ClientCaseCourtComplaints.Where(row => row.Id == seed.Complaint.Id).Select(row => row.IsArchived).SingleAsync());
        if (command is "create" or "update") Assert.Contains(await db.TaskJournalItems.Where(row => row.ClientCaseId == seed.Base.Cases[0]).ToArrayAsync(),
            row => row.Title == "Отслеживать назначение заседания" && row.Status is not ("done" or "cancelled"));
        await AssertCourtRetentionForeignCaseAsync(db, seed.Base);
    }

    [Theory]
    [InlineData("instance", false)]
    [InlineData("instance", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    public async Task CourtWrite_DoesNotAdoptForeignTransactionOrPendingChanges(string command, bool pending)
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedCourtWriteAsync(db, command);
        var before = await CourtWriteSnapshotAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.Title == "Открытая задача 2");
        var original = task.Result;
        if (pending) task.Result = "Чужая несохранённая правка";
        var publisher = new BulkPublicationBoundary(db);
        var result = await InvokeCourtWriteAsync(CreateController(db, seed.Base.User, realtimePublisher: publisher), seed, command, default);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(0, publisher.Count);
        if (pending)
        {
            Assert.Equal(EntityState.Modified, db.Entry(task).State);
            Assert.Equal(original, (await db.Entry(task).GetDatabaseValuesAsync())![nameof(TaskJournalItem.Result)]);
        }
        Assert.Equal(before, await CourtWriteSnapshotAsync(db));
    }

    [Theory]
    [InlineData("instance", "session")]
    [InlineData("instance", "court")]
    [InlineData("instance", "case")]
    [InlineData("create", "view")]
    [InlineData("create", "document")]
    [InlineData("create", "instance")]
    [InlineData("update", "edit")]
    [InlineData("update", "document")]
    [InlineData("update", "complaint")]
    [InlineData("archive", "actor")]
    [InlineData("archive", "instance")]
    [InlineData("archive", "complaint")]
    [InlineData("create", "task")]
    [InlineData("update", "task")]
    [InlineData("archive", "task")]
    public async Task CourtWrite_PgPinsCurrentActorAndReferencesUntilCommit(string command, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seedDb = Open();
        var seed = await SeedCourtWriteAsync(seedDb, command, existingCreateInstance: true);
        var attempted = false;
        var committed = false;
        string? blocked = null;
        var hook = new CourtWriteBeforeSave(async ct =>
        {
            await using var other = Open();
            await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                if (target == "session") await other.Users.Where(row => row.Id == seed.Base.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.AuthTokenVersion, 1), ct);
                else if (target == "actor") await other.Users.Where(row => row.Id == seed.Base.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                else if (target == "view") await other.RolePermissions.Where(row => row.RoleId == seed.Base.User.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false), ct);
                else if (target == "edit") await other.RolePermissions.Where(row => row.RoleId == seed.Base.User.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false), ct);
                else if (target == "court") await other.ClientCaseCourts.Where(row => row.Id == seed.Court.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct);
                else if (target == "case") await other.ClientCases.Where(row => row.Id == seed.Base.Cases[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.EventHistory, "Параллельная история"), ct);
                else if (target == "document") await other.ClientCaseDocuments.Where(row => row.Id == seed.Base.Document.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Base.Cases[1]), ct);
                else if (target == "instance") await other.ClientCaseCourtInstances.Where(row => row.Id == seed.Instance.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Base.Cases[1]), ct);
                else if (target == "task") await other.TaskJournalItems.Where(row => row.ClientCaseId == seed.Base.Cases[0] && row.Status == "done").ExecuteUpdateAsync(set => set.SetProperty(row => row.Result, "Параллельный результат"), ct);
                else await other.ClientCaseCourtComplaints.Where(row => row.Id == seed.Complaint.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Base.Cases[1]), ct);
                await transaction.CommitAsync(ct);
                committed = true;
            }
            catch (Exception error)
            {
                Exception? current = error;
                while (current is not null && current is not PostgresException) current = current.InnerException;
                if (current is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } conflict) blocked = conflict.SqlState;
                else throw;
            }
        });
        await using var request = Open(hook);
        var publisher = new BulkPublicationBoundary(request);
        hook.Armed = true;
        var result = await InvokeCourtWriteAsync(CreateController(request, seed.Base.User, realtimePublisher: publisher), seed, command, default);
        Assert.True(hook.Called);
        Assert.True(attempted);
        Assert.False(committed);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked);
        Assert.IsAssignableFrom<ActionResult>(result);
        Assert.True(result is CreatedResult or OkObjectResult or NoContentResult);
        Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        Assert.Equal("Сохранённый результат", await verify.TaskJournalItems.Where(row => row.Status == "done" && row.Title != "Отслеживать назначение заседания").Select(row => row.Result).SingleAsync());
    }

    private sealed class CourtWriteBeforeSave(Func<CancellationToken, Task> beforeSave) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && !Called)
            {
                Called = true;
                await beforeSave(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("instance", "court")]
    [InlineData("create", "document")]
    [InlineData("update", "complaint")]
    [InlineData("archive", "instance")]
    public async Task CourtWrite_PgBusyReferenceRejectsBeforeAnyWrite(string command, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).Options);
        await using var seedDb = Open();
        var seed = await SeedCourtWriteAsync(seedDb, command, existingCreateInstance: true);
        var before = await CourtWriteSnapshotAsync(seedDb);
        await using var other = Open();
        await using var transaction = await other.Database.BeginTransactionAsync();
        if (target == "court")
            await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourts\" WHERE \"Id\" = {seed.Court.Id} FOR UPDATE");
        else if (target == "document")
            await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseDocuments\" WHERE \"Id\" = {seed.Base.Document.Id} FOR UPDATE");
        else if (target == "complaint")
            await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtComplaints\" WHERE \"Id\" = {seed.Complaint.Id} FOR UPDATE");
        else
            await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtInstances\" WHERE \"Id\" = {seed.Instance.Id} FOR UPDATE");
        await using var request = Open();
        var publisher = new BulkPublicationBoundary(request);
        var result = await InvokeCourtWriteAsync(CreateController(request, seed.Base.User, realtimePublisher: publisher), seed, command, default);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Null(request.Database.CurrentTransaction);
        Assert.Equal(0, publisher.Count);
        await transaction.RollbackAsync();
        Assert.Equal(before, await CourtWriteSnapshotAsync(seedDb));
    }

    [Theory]
    [InlineData("instance", false)]
    [InlineData("instance", true)]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task CourtWrite_RejectsInactiveCourtOrForeignDocumentBeforeWrites(string command, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtWriteAsync(db, command);
        if (command == "instance") await db.ClientCaseCourts.Where(row => row.Id == seed.Court.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        var before = await CourtWriteSnapshotAsync(db);
        var result = await InvokeCourtWriteAsync(CreateController(db, seed.Base.User), seed, command, default,
            documentId: command == "instance" ? null : seed.Base.OtherDocument.Id);
        Assert.IsType<BadRequestObjectResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtWriteSnapshotAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed record CourtWriteSeed(CourtRetentionSeed Base, ClientCaseCourt Court, ClientCaseCourtInstance Instance, ClientCaseCourtComplaint Complaint);

    private static async Task<CourtWriteSeed> SeedCourtWriteAsync(PaymentContext db, string command, bool existingCreateInstance = false)
    {
        var seed = await SeedCourtRetentionAsync(db);
        var court = new ClientCaseCourt { Name = "Суд для записи", IsActive = true };
        db.ClientCaseCourts.Add(court);
        var instance = new ClientCaseCourtInstance { ClientCaseId = seed.Cases[0], Instance = "first", CourtName = "Свой суд" };
        var otherInstance = new ClientCaseCourtInstance { ClientCaseId = seed.Cases[1], Instance = "cassation", CourtName = "Другой суд" };
        db.ClientCaseCourtInstances.Add(otherInstance);
        if (command != "create" || existingCreateInstance) db.ClientCaseCourtInstances.Add(instance);
        await db.SaveChangesAsync();
        var complaint = new ClientCaseCourtComplaint
        { ClientCaseId = seed.Cases[0], CourtInstanceId = instance.Id, ClientCaseDocumentId = seed.Document.Id, Title = "Исходная жалоба", FiledBy = command == "archive" ? "ours" : "unknown", FiledAt = command == "archive" ? DateTime.UnixEpoch : null, Status = command == "archive" ? "filed" : "draft" };
        if (command != "create") db.ClientCaseCourtComplaints.Add(complaint);
        db.ClientCaseCourtComplaints.Add(new ClientCaseCourtComplaint
        { ClientCaseId = seed.Cases[1], CourtInstanceId = otherInstance.Id, Title = "Чужая жалоба", Notes = "private-court-write" });
        await db.SaveChangesAsync();
        if (command == "archive") await ClientCaseDeadlineTaskSyncService.SyncAsync(db, seed.Cases[0], seed.User.Id, seed.User.FullName, default);
        db.ChangeTracker.Clear();
        return new(seed, court, instance, complaint);
    }

    private static CasesV2Controller.UpsertClientCaseCourtComplaintRequest CourtWriteComplaintInput(CourtWriteSeed seed, int? documentId = null) => new()
    {
        Title = "Сохранённая жалоба",
        ComplaintType = "appealComplaint",
        TargetInstance = "appeal",
        FiledBy = "ours",
        Status = "filed",
        FiledAt = DateTime.UnixEpoch,
        ClientCaseDocumentId = documentId ?? seed.Base.Document.Id,
        Notes = "Сохранить вместе с задачами"
    };

    private static async Task<IActionResult> InvokeCourtWriteAsync(CasesV2Controller controller, CourtWriteSeed seed, string command, CancellationToken ct, int? documentId = null)
    {
        if (command == "instance") return (await controller.UpsertCourtInstance(seed.Base.Cases[0], "first",
            new CasesV2Controller.UpsertClientCaseCourtInstanceRequest { CourtId = seed.Court.Id, Notes = "Изменённая инстанция" }, ct)).Result!;
        if (command == "create") return (await controller.CreateCourtComplaint(seed.Base.Cases[0], "first", CourtWriteComplaintInput(seed, documentId), ct)).Result!;
        if (command == "update") return (await controller.UpdateCourtComplaint(seed.Base.Cases[0], "first", seed.Complaint.Id, CourtWriteComplaintInput(seed, documentId), ct)).Result!;
        return await controller.ArchiveCourtComplaint(seed.Base.Cases[0], "first", seed.Complaint.Id, ct);
    }

    private static Task<HttpResponseMessage> SendCourtWriteHttpAsync(HttpClient http, CourtWriteSeed seed, string command, int caseId)
    {
        var path = $"/api/v2/cases/{caseId}/court-instances/first";
        if (command == "instance") return http.PutAsJsonAsync(path, new CasesV2Controller.UpsertClientCaseCourtInstanceRequest { CourtId = seed.Court.Id, Notes = "Изменённая инстанция" });
        if (command == "create") return http.PostAsJsonAsync(path + "/complaints", CourtWriteComplaintInput(seed));
        if (command == "update") return http.PutAsJsonAsync(path + $"/complaints/{seed.Complaint.Id}", CourtWriteComplaintInput(seed));
        return http.DeleteAsync(path + $"/complaints/{seed.Complaint.Id}");
    }

    private static async Task<string> CourtWriteSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Existing = await CourtNullableSnapshotAsync(db),
        Cases = await db.ClientCases.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.CourtName, row.CourtUrl, row.JudicialUid, row.EventHistory }).ToArrayAsync(),
        Instances = await db.ClientCaseCourtInstances.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new
        { row.Id, row.ClientCaseId, row.Instance, row.CourtId, row.CourtName, row.CourtCaseNumber, row.JudicialUid, row.CourtUrl, row.JudgeName, row.Courtroom, row.SecretaryName, row.Notes, row.CreatedAt, row.CreatedByUserId, row.UpdatedAt, row.UpdatedByUserId }).ToArrayAsync(),
        Complaints = await db.ClientCaseCourtComplaints.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new
        { row.Id, row.ClientCaseId, row.CourtInstanceId, row.ComplaintType, row.Title, row.TargetInstance, row.FiledBy, row.FiledAt, row.Status, row.ClientCaseDocumentId, row.Notes, row.IsArchived, row.CreatedAt, row.UpdatedAt, row.CreatedByUserId, row.UpdatedByUserId }).ToArrayAsync(),
        Tasks = await db.TaskJournalItems.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new
        { row.Id, row.ClientCaseId, row.Title, row.Description, row.Status, row.Result, row.TagsJson, row.DueAt, row.AssigneeUserId, row.CreatedAt, row.UpdatedAt }).ToArrayAsync(),
        Comments = await db.TaskJournalComments.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).Select(row => new
        { row.Id, row.TaskJournalItemId, row.AuthorUserId, row.AuthorName, row.Kind, row.Text, row.CreatedAt }).ToArrayAsync(),
        AuditCount = await db.ObjectAuditEvents.CountAsync()
    });
}
