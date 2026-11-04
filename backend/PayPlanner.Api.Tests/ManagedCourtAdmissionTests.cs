using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    public static IEnumerable<object[]> CourtManagedStaleCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var point in new[] { "source", "put" })
                foreach (var (mode, status) in new[] { ("session", 401), ("view", 403), ("outcome", 409), ("court-field", 409) })
                    yield return [postgres, point, mode, status];
    }

    public static IEnumerable<object[]> CourtManagedAccessCases()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var point in new[] { "source", "put" })
                foreach (var (mode, status) in new[]
                {
                    ("unapproved", 401), ("inactive", 401), ("permission", 403),
                    ("hidden", 404), ("owner", 404), ("binding", 409),
                })
                    yield return [postgres, point, mode, status];
    }

    [Theory]
    [MemberData(nameof(CourtManagedStaleCases))]
    public Task CourtManaged_StaleAdmissionCannotCreateSourceOrDocument(bool postgres, string point, string mode, int expected)
        => AssertCourtManagedAdmissionAsync(postgres, point, mode, expected);

    [Theory]
    [MemberData(nameof(CourtManagedAccessCases))]
    public Task CourtManaged_CurrentAccessIsRequired(bool postgres, string point, string mode, int expected)
        => AssertCourtManagedAdmissionAsync(postgres, point, mode, expected);

    [Theory]
    [InlineData(false, "session", 401)]
    [InlineData(true, "session", 401)]
    [InlineData(false, "view", 403)]
    [InlineData(true, "view", 403)]
    [InlineData(false, "outcome", 409)]
    [InlineData(true, "outcome", 409)]
    [InlineData(false, "court-field", 409)]
    [InlineData(true, "court-field", 409)]
    public Task CourtManaged_ResumeReadCannotPublishForStaleRequest(bool postgres, string mode, int expected)
        => AssertCourtManagedAdmissionAsync(postgres, "resume", mode, expected);

    [Theory]
    [InlineData(false, "deadline-link")]
    [InlineData(true, "deadline-link")]
    [InlineData(false, "deadline-source")]
    [InlineData(true, "deadline-source")]
    [InlineData(false, "deadline-status")]
    [InlineData(true, "deadline-status")]
    public Task CourtManaged_ConcurrentControlChangeIsPreserved(bool postgres, string mode)
        => AssertCourtManagedAdmissionAsync(postgres, "put", mode, 409);

    [Theory]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "put")]
    [InlineData(true, "put")]
    public Task CourtManaged_ChangedRoleCannotContinue(bool postgres, string point)
        => AssertCourtManagedAdmissionAsync(postgres, point, "role", 403);

    private async Task AssertCourtManagedAdmissionAsync(bool postgres, string point, string mode, int expected)
    {
        await using var f = await CreateFixtureAsync(postgres, point == "resume" ? 2 : 1);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        if (point == "resume")
        {
            Assert.Equal(1, (await GenerateCourtAsync(f, source)).PendingCourtActDocuments);
            await f.CopyPendingAsync();
            f.Db.ChangeTracker.Clear();
        }
        var called = false;
        string? before = null;
        async Task ChangeAsync()
        {
            if (called) return;
            called = true;
            Assert.Null(f.Db.Database.CurrentTransaction);
            await using var observer = f.Observer();
            Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
            await ChangeCourtManagedAdmissionAsync(observer, f.User, f.Case, mode);
            before = await CourtManagedStateAsync(observer);
        }
        if (point == "put") f.Local.BeforePutAsync = _ => ChangeAsync();
        if (point == "resume") f.Local.BeforeReadAsync = _ => ChangeAsync();
        var realtime = new CourtManagedAdmissionRealtime(point == "source" ? ChangeAsync : () => Task.CompletedTask);
        var result = await f.Controller(source, realtime: realtime).RunCourtSync(f.Case.Id, null, CancellationToken.None);
        Assert.Equal(expected, CourtManagedStatus(result.Result));
        Assert.True(called);
        Assert.NotNull(before);
        await using var proof = f.Observer();
        Assert.Equal(before, await CourtManagedStateAsync(proof));
        Assert.Empty(await proof.ClientCaseDocuments.Where(row => row.SourceType == "sudrf").ToArrayAsync());
        Assert.Equal(mode == "deadline-link" ? 1 : 0, await proof.ClientCaseDocuments.CountAsync());
        if (point == "source")
        {
            Assert.Equal(0, f.Local.PutCount);
            Assert.Empty(await proof.CourtDocumentSourceScopes.ToArrayAsync());
        }
        else Assert.Single(await proof.CourtDocumentSourceAttempts.ToArrayAsync());
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtManaged_QueueCannotPublishAfterOriginalAuthorLosesView(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 2, autoPublication: true);
        Assert.Equal(1, (await GenerateCourtAsync(f, CourtDeadlineSource(f.Case.CourtUrl))).PendingCourtActDocuments);
        await f.CopyPendingAsync();
        await using var observer = f.Observer();
        await ChangeCourtManagedAdmissionAsync(observer, f.User, f.Case, "view");
        var before = await CourtManagedStateAsync(observer);
        var result = await f.RunCourtQueueAsync();
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(before, await CourtManagedStateAsync(observer));
        Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtManaged_SourceSqlFailureRollsBackOnlySourceCapture(bool postgres, bool cancel)
    {
        var fault = new CourtManagedSourceFault(cancel);
        await using var f = await CreateFixtureAsync(postgres, 1, false, fault);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        string? before = null;
        var realtime = new CourtManagedAdmissionRealtime(async () =>
        {
            await using var observer = f.Observer();
            before = await CourtManagedStateAsync(observer);
            fault.Armed = true;
        });
        var controller = f.Controller(CourtDeadlineSource(f.Case.CourtUrl), realtime: realtime);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RunCourtSync(f.Case.Id, null, cancellation.Token));
        else await Assert.ThrowsAsync<CourtManagedSqlFault>(() => controller.RunCourtSync(f.Case.Id, null, cancellation.Token));
        Assert.True(fault.Called);
        await using var proof = f.Observer();
        Assert.Equal(before, await CourtManagedStateAsync(proof));
        Assert.Equal(0, f.Local.PutCount);
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, "source")]
    [InlineData(true, "source")]
    [InlineData(false, "document")]
    [InlineData(true, "document")]
    public async Task CourtManaged_LostCommitAcknowledgementRetainsFrozenAttemptAndPublishedBytes(bool postgres, string point)
    {
        var fault = new CourtManagedCommitFault(point);
        await using var f = await CreateFixtureAsync(postgres, 1, false, fault);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        var realtime = new CourtManagedAdmissionRealtime(() => { fault.Armed = true; return Task.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => f.Controller(source, realtime: realtime).RunCourtSync(f.Case.Id, null, CancellationToken.None));
        Assert.True(fault.Called);
        await using var proof = f.Observer();
        var attempt = await proof.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var bytes = (await proof.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes;
        Assert.Equal(point == "document" ? 1 : 0, await proof.ClientCaseDocuments.CountAsync());
        var retry = await GenerateCourtAsync(f, source);
        Assert.Equal("external-sync-completed", retry.Status);
        Assert.Equal(attempt.Id, (await proof.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(attempt.FrozenSnapshotJson, (await proof.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync()).FrozenSnapshotJson);
        Assert.Equal(bytes, (await proof.CourtDocumentSourceContents.AsNoTracking().SingleAsync()).Bytes);
        Assert.Single(await proof.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, f.Local.PutCount);
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("source", "actor")]
    [InlineData("source", "case")]
    [InlineData("put", "actor")]
    [InlineData("put", "case")]
    public async Task CourtManaged_ExistingPostgresBusyAdmissionRejectsWithoutPartialGraph(string point, string target)
    {
        await using var f = await CreateFixtureAsync(true, 1);
        await using var blocker = f.Observer();
        string? before = null;
        async Task BlockAsync()
        {
            Assert.Null(f.Db.Database.CurrentTransaction);
            await blocker.Database.BeginTransactionAsync();
            if (target == "actor") await blocker.Users.Where(row => row.Id == f.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, "Занят"));
            else await blocker.ClientCases.Where(row => row.Id == f.Case.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Занято"));
            before = await CourtManagedStateAsync(f.Db);
        }
        if (point == "put") f.Local.BeforePutAsync = _ => BlockAsync();
        var realtime = new CourtManagedAdmissionRealtime(point == "source" ? BlockAsync : () => Task.CompletedTask);
        var result = await f.Controller(CourtDeadlineSource(f.Case.CourtUrl), realtime: realtime).RunCourtSync(f.Case.Id, null, CancellationToken.None);
        Assert.Equal(409, CourtManagedStatus(result.Result));
        Assert.Equal(before, await CourtManagedStateAsync(f.Db));
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtManaged_CurrentNonzeroSessionCanPublish(bool postgres)
    {
        await using var f = await CreateFixtureAsync(postgres, 1);
        await using var observer = f.Observer();
        await observer.Users.Where(row => row.Id == f.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.AuthTokenVersion, 7));
        var controller = f.Controller(CourtDeadlineSource(f.Case.CourtUrl));
        ((ClaimsIdentity)controller.User.Identity!).AddClaim(AuthSessionClaims.CreateTokenVersionClaim(7));
        var result = await controller.RunCourtSync(f.Case.Id, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.NotNull((await observer.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CourtManaged_CurrentEditorPreservesOriginalAuthorAndRequiresTheirAccess(bool postgres, bool revokeOriginal)
    {
        await using var f = await CreateFixtureAsync(postgres, 2);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        Assert.Equal(1, (await GenerateCourtAsync(f, source)).PendingCourtActDocuments);
        await f.CopyPendingAsync();
        await using var observer = f.Observer();
        var original = await observer.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        var editor = await NewCourtManagedEditorAsync(observer);
        var called = false;
        if (revokeOriginal) f.Local.BeforeReadAsync = async _ =>
        {
            if (called) return;
            called = true;
            await ChangeCourtManagedAdmissionAsync(observer, f.User, f.Case, "view");
        };
        var controller = f.Controller(source);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, editor.Id.ToString()), new Claim(ClaimTypes.Name, editor.FullName)], "court-current-editor"));
        Assert.IsType<OkObjectResult>((await controller.RunCourtSync(f.Case.Id, null, CancellationToken.None)).Result);
        var retained = await observer.CourtDocumentSourceAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, retained.Id);
        Assert.Equal(original.FrozenSnapshotJson, retained.FrozenSnapshotJson);
        if (revokeOriginal)
        {
            Assert.True(called);
            Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
            Assert.Null((await observer.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        }
        else
        {
            var document = await observer.ClientCaseDocuments.AsNoTracking().SingleAsync();
            Assert.Equal(f.User.Id, document.UploadedByUserId);
            Assert.Equal(f.User.FullName, document.UploadedByName);
            Assert.Equal(document.Id, (await observer.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        }
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task CourtManaged_ExistingPostgresPinsOriginalAuthorDuringAnotherEditorsPublication()
    {
        await using var f = await CreateFixtureAsync(true, 2);
        var source = CourtDeadlineSource(f.Case.CourtUrl);
        await GenerateCourtAsync(f, source);
        await f.CopyPendingAsync();
        await using var observer = f.Observer();
        var editor = await NewCourtManagedEditorAsync(observer);
        await using var blocker = f.Observer();
        var called = false;
        f.Local.BeforeReadAsync = async _ =>
        {
            if (called) return;
            called = true;
            await blocker.Database.BeginTransactionAsync();
            await blocker.Users.Where(row => row.Id == f.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, "Занят"));
        };
        var controller = f.Controller(source);
        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, editor.Id.ToString()), new Claim(ClaimTypes.Name, editor.FullName)], "court-current-editor"));
        var result = await controller.RunCourtSync(f.Case.Id, null, CancellationToken.None);
        Assert.True(called);
        Assert.Equal(409, CourtManagedStatus(result.Result));
        Assert.Empty(await observer.ClientCaseDocuments.ToArrayAsync());
        Assert.Null((await observer.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync()).ClientCaseDocumentId);
        Assert.Null(f.Db.Database.CurrentTransaction);
    }

    private static async Task<User> NewCourtManagedEditorAsync(PaymentContext db)
    {
        var editor = new User
        {
            Email = "current-editor@example.local",
            FullName = "Текущий редактор",
            PasswordHash = "synthetic",
            IsActive = true,
            IsApproved = true,
            Role = new Role
            {
                Name = "admin",
                Permissions = new[] { RolePermissionCatalog.Cases, RolePermissionCatalog.Tasks }.Select(section => new RolePermission
                { Section = section, CanView = true, CanCreate = true, CanEdit = true, CanDelete = true, ViewScope = DataVisibilityScope.All }).ToList(),
            },
        };
        db.Users.Add(editor);
        await db.SaveChangesAsync();
        return editor;
    }

    private static int? CourtManagedStatus(ActionResult? result) => result switch
    {
        ForbidResult => 403,
        Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult status => status.StatusCode,
        _ => null,
    };

    private static async Task ChangeCourtManagedAdmissionAsync(PaymentContext db, User user, ClientCase clientCase, string mode)
    {
        var users = db.Users.Where(row => row.Id == user.Id);
        var cases = db.ClientCases.Where(row => row.Id == clientCase.Id);
        var permission = db.RolePermissions.Where(row => row.RoleId == user.RoleId && row.Section == RolePermissionCatalog.Cases);
        if (mode == "session") await users.ExecuteUpdateAsync(set => set.SetProperty(row => row.AuthTokenVersion, 1));
        else if (mode == "unapproved") await users.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        else if (mode == "inactive") await users.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        else if (mode == "permission") await permission.ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        else if (mode == "view") await permission.ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        else if (mode == "role")
        {
            var revoked = new Role { Name = "court-no-access-" + Guid.NewGuid().ToString("N") };
            db.Roles.Add(revoked);
            await db.SaveChangesAsync();
            await users.ExecuteUpdateAsync(set => set.SetProperty(row => row.RoleId, revoked.Id));
        }
        else if (mode == "hidden") await cases.ExecuteUpdateAsync(set => set.SetProperty(row => row.IsHiddenFromExecutor, true));
        else if (mode == "owner")
        {
            await cases.ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, (int?)null));
            await db.Clients.Where(row => row.Id == clientCase.ClientId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, (int?)null));
        }
        else if (mode == "binding") await cases.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://new.test/source"));
        else if (mode == "outcome")
        {
            var now = DateTime.UtcNow;
            await cases.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtSyncLastSuccessAt, now).SetProperty(row => row.CourtSyncLastAttemptAt, now));
        }
        else if (mode == "court-field") await cases.ExecuteUpdateAsync(set => set.SetProperty(row => row.JudgeName, "Ручная правка"));
        else if (mode == "deadline-status") await db.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.Status, "archived"));
        else if (mode == "deadline-source") await db.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceUrl, "https://new.test/manual"));
        else
        {
            Assert.Equal("deadline-link", mode);
            var document = new ClientCaseDocument { ClientCaseId = clientCase.Id, DocumentKey = "manual-keep", DocumentType = "manual", Status = "received", OriginalFileName = "Ручной.html" };
            db.ClientCaseDocuments.Add(document);
            await db.SaveChangesAsync();
            await db.ClientCaseProceduralDeadlines.ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, (int?)document.Id));
        }
    }

    private static async Task<string> CourtManagedStateAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Cases = await db.ClientCases.AsNoTracking().Select(row => new
        {
            row.Id,
            row.Title,
            row.EventHistory,
            row.CourtName,
            row.CourtCaseNumber,
            row.CourtUrl,
            row.JudicialUid,
            row.CourtSyncLastAttemptAt,
            row.CourtSyncLastSuccessAt,
            row.CourtSyncFailureStartedAt,
            row.CourtSyncLastError,
            row.JudgeName,
            row.Courtroom,
            row.CourtDecisionAt,
            row.NextHearingAt,
            row.AppealDeadlineAt,
            row.CassationDeadlineAt,
            row.CourtClaimAmount,
            row.IsHiddenFromExecutor,
            row.ResponsibleUserId,
        }).ToArrayAsync(),
        Deadlines = await db.ClientCaseProceduralDeadlines.AsNoTracking().OrderBy(row => row.Id).Select(row => new
        { row.Id, row.ClientCaseId, row.Status, row.SourceType, row.SourceUrl, row.SourceDocumentKey, row.ClientCaseDocumentId, row.DueAt, row.Notes }).ToArrayAsync(),
        Tasks = await db.TaskJournalItems.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientCaseId, row.Status, row.TagsJson }).ToArrayAsync(),
        Documents = await db.ClientCaseDocuments.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.ClientCaseId, row.DocumentKey, row.Version, row.Status, row.SourceType }).ToArrayAsync(),
        Audits = await db.ObjectAuditEvents.AsNoTracking().Where(row => row.ObjectType == "client-case" || row.ObjectType == "client-case-document").OrderBy(row => row.Id).Select(row => new { row.Id, row.ObjectType, row.ObjectId, row.Description }).ToArrayAsync(),
        Scopes = await db.CourtDocumentSourceScopes.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Version, row.LastAcceptedAt, row.InputSha256 }).ToArrayAsync(),
        Sources = await db.CourtDocumentSources.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.IsActive, row.AcceptedScopeVersion }).ToArrayAsync(),
        Attempts = await db.CourtDocumentSourceAttempts.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.FrozenSnapshotJson, row.RequestSha256 }).ToArrayAsync(),
        Contents = await db.CourtDocumentSourceContents.AsNoTracking().OrderBy(row => row.AttemptId).Select(row => new { row.AttemptId, row.Bytes, row.Sha256 }).ToArrayAsync(),
    });

    private sealed class CourtManagedAdmissionRealtime(Func<Task> callback) : IRealtimeNotificationPublisher
    {
        private bool called;
        public Task PublishUserNotificationAsync(UserNotification notification, bool toastEnabled = true, CancellationToken ct = default) => Task.CompletedTask;
        public async Task PublishTaskJournalChangedAsync(CancellationToken ct = default)
        {
            if (called) return;
            called = true;
            await callback();
        }
    }

    private sealed class CourtManagedSqlFault : Exception;
    private sealed class CourtManagedSourceFault(bool cancel) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public Action Cancel { get; set; } = null!;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            if (!Armed || !data.Context!.ChangeTracker.Entries<CourtDocumentSourceAttempt>().Any()) return ValueTask.FromResult(result);
            Called = true;
            if (cancel) { Cancel(); ct.ThrowIfCancellationRequested(); }
            throw new CourtManagedSqlFault();
        }
    }

    private sealed class CourtManagedCommitFault(string point) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (!Armed || !(point == "source" ? data.Context!.ChangeTracker.Entries<CourtDocumentSourceAttempt>().Any()
                    : data.Context!.ChangeTracker.Entries<ClientCaseDocument>().Any())) return Task.CompletedTask;
            Armed = false;
            Called = true;
            throw new IOException("Synthetic court COMMIT acknowledgement loss.");
        }
    }
}
