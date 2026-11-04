using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    [Theory]
    [InlineData("before", "unapproved", 401)]
    [InlineData("after", "unapproved", 401)]
    [InlineData("before", "inactive", 401)]
    [InlineData("after", "inactive", 401)]
    [InlineData("before", "session", 401)]
    [InlineData("after", "session", 401)]
    [InlineData("before", "permission", 403)]
    [InlineData("after", "permission", 403)]
    [InlineData("before", "view", 403)]
    [InlineData("after", "view", 403)]
    [InlineData("before", "role", 403)]
    [InlineData("after", "role", 403)]
    [InlineData("before", "hidden", 404)]
    [InlineData("after", "hidden", 404)]
    [InlineData("before", "owner", 404)]
    [InlineData("after", "owner", 404)]
    [InlineData("before", "binding", 409)]
    [InlineData("after", "binding", 409)]
    [InlineData("before", "outcome", 409)]
    [InlineData("after", "outcome", 409)]
    [InlineData("before", "court-field", 409)]
    [InlineData("after", "court-field", 409)]
    public async Task CourtFileHttp_StaleAdmissionCannotPublishDocument(string phase, string mode, int expected)
    {
        await using var db = await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        async Task ChangeAsync()
        {
            var query = db.ClientCases.Where(row => row.Id == clientCase.Id);
            if (mode == "binding") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/new"));
            else if (mode == "outcome")
            {
                var now = DateTime.UtcNow;
                await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtSyncLastAttemptAt, now).SetProperty(row => row.CourtSyncLastSuccessAt, now));
            }
            else if (mode == "court-field") await query.ExecuteUpdateAsync(set => set.SetProperty(row => row.JudgeName, "Ручная правка"));
            else await ChangeCourtSearchAccessAsync(db, user, clientCase.Id, mode);
            before = await CourtCoreStateAsync(db);
        }
        var storage = new CourtPublicationStorage { AfterSave = phase == "after" ? ChangeAsync : null };
        var realtime = new CourtFinalRealtime(phase == "before" ? ChangeAsync : () => Task.CompletedTask);
        using var server = CourtCoreServer(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage, NewCourtCoreNotifications(db), realtime);
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Court-Search-User", user.Id.ToString());
        using var response = await client.PostAsJsonAsync($"/api/v2/cases/{clientCase.Id}/court-sync/run", new { });
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.NotNull(before);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(phase == "before" ? 0 : 1, storage.Saves);
        Assert.False(storage.HasFile);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtFile_ConcurrentHistoryDuringFileWriteIsMerged(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtPublicationStorage
        {
            AfterSave = () => db.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set =>
                set.SetProperty(row => row.EventHistory, "История во время записи файла\n").SetProperty(row => row.Title, "Независимое название")),
        };
        var result = await CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage,
            NewCourtCoreNotifications(db), new CourtCoreRealtime(db)).RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        var current = await db.ClientCases.AsNoTracking().SingleAsync();
        Assert.Equal("Независимое название", current.Title);
        var lines = current.EventHistory.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("История во время записи файла", lines[0]);
        Assert.Equal(3, lines.Length);
        Assert.Contains("Сводка судебного события", current.EventHistory);
        Assert.Contains("Судебный источник", current.EventHistory);
        Assert.NotNull(current.CourtSyncLastSuccessAt);
        Assert.Single(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, storage.Saves);
        Assert.True(storage.HasFile);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    public async Task CourtFile_FailedRecordWriteRollsBackGraphAndRemovesFile(bool postgres, int ordinal, bool cancel)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var fault = new CourtCoreAfterSql(ordinal, cancel);
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        string? before = null;
        var storage = new CourtPublicationStorage
        {
            AfterSave = async () =>
            {
                before = await CourtCoreStateAsync(db);
                fault.Armed = true;
            },
        };
        var controller = CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage,
            NewCourtCoreNotifications(db), new CourtCoreRealtime(db));
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        else await Assert.ThrowsAsync<CourtCoreSqlFault>(() => controller.RunCourtSync(clientCase.Id, null, cancellation.Token));
        Assert.True(fault.Called);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, storage.Saves);
        Assert.False(storage.HasFile);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData("unapproved", 401)]
    [InlineData("binding", 409)]
    [InlineData("actor-busy", 409)]
    [InlineData("case-busy", 409)]
    public async Task CourtFile_ExistingPostgresProtectsAdmissionAfterFileWrite(string mode, int expected)
    {
        await using var lease = await modelTemplate.CreateDatabaseAsync();
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        await using var other = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        string? before = null;
        var storage = new CourtPublicationStorage
        {
            AfterSave = async () =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                if (mode == "unapproved") await ChangeCourtSearchAccessAsync(other, user, clientCase.Id, mode);
                else if (mode == "binding") await other.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.CourtUrl, "https://court.test/new"));
                else
                {
                    await other.Database.BeginTransactionAsync();
                    if (mode == "actor-busy") await other.Users.Where(row => row.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.FullName, "Занят"));
                    else await other.ClientCases.Where(row => row.Id == clientCase.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, "Занято"));
                }
                before = await CourtCoreStateAsync(db);
            },
        };
        var result = await CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage,
            NewCourtCoreNotifications(db), new CourtCoreRealtime(db)).RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.Equal(expected, Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>(result.Result).StatusCode);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Empty(await db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal(1, storage.Saves);
        Assert.False(storage.HasFile);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtFile_LostCommitAcknowledgementKeepsPublishedBytesAndRetryDoesNotDuplicate(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        var fault = new CourtFileCommitFault();
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtPublicationStorage { AfterSave = () => { fault.Armed = true; return Task.CompletedTask; } };
        var provider = new CourtCoreProvider(() => Task.CompletedTask);
        var controller = CourtCoreController(db, user, provider, storage, NewCourtCoreNotifications(db), new CourtCoreRealtime(db));
        await Assert.ThrowsAsync<IOException>(() => controller.RunCourtSync(clientCase.Id, null, CancellationToken.None));
        Assert.True(fault.Called);
        var accepted = await db.ClientCaseDocuments.AsNoTracking().SingleAsync();
        Assert.True(storage.HasFile);
        Assert.Null((await db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        Assert.Null(db.Database.CurrentTransaction);
        var retry = await CourtCoreController(db, user, provider, storage, NewCourtCoreNotifications(db), new CourtCoreRealtime(db))
            .RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(retry.Result);
        Assert.Equal(accepted.Id, (await db.ClientCaseDocuments.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, storage.Saves);
        Assert.True(storage.HasFile);
        Assert.NotNull((await db.ClientCases.AsNoTracking().SingleAsync()).CourtSyncLastSuccessAt);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtFile_CompetingVersionDuringFileWriteIsPreserved(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtPublicationStorage();
        string? before = null;
        storage.AfterSave = async () =>
        {
            var name = storage.StoredPath[(storage.StoredPath.LastIndexOf('/') + 1)..];
            var key = name[..name.LastIndexOf("-v1-", StringComparison.Ordinal)];
            db.ClientCaseDocuments.Add(new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = key,
                Version = 1,
                OriginalFileName = "Ручной документ.html",
                StoragePath = "other/document.html",
                StoredFileName = "document.html",
                Status = "received",
                DocumentType = "manual",
            });
            await db.SaveChangesAsync();
            before = await CourtCoreStateAsync(db);
        };
        var result = await CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage,
            NewCourtCoreNotifications(db), new CourtCoreRealtime(db)).RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal("Ручной документ.html", (await db.ClientCaseDocuments.AsNoTracking().SingleAsync()).OriginalFileName);
        Assert.Equal(1, storage.Saves);
        Assert.False(storage.HasFile);
        Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CourtFile_CompetingVersionBeforeFileWriteDoesNotCreateAnotherVersion(bool postgres)
    {
        await using var lease = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease!.ConnectionString).Options)
            : await CreateContextAsync();
        var (user, clientCase) = await SeedCourtSearchAsync(db);
        var storage = new CourtPublicationStorage();
        string? before = null;
        var realtime = new CourtFinalRealtime(async () =>
        {
            var key = await db.ClientCaseProceduralDeadlines.Where(row => row.SourceType == "sudrf" && row.SourceDocumentKey != null && row.SourceDocumentKey != "")
                .Select(row => row.SourceDocumentKey).Distinct().SingleAsync();
            db.ClientCaseDocuments.Add(new ClientCaseDocument
            {
                ClientCaseId = clientCase.Id,
                DocumentKey = key!,
                Version = 1,
                OriginalFileName = "Параллельная сводка.html",
                StoragePath = "other/summary.html",
                StoredFileName = "summary.html",
                Status = "received",
                DocumentType = "manual",
            });
            await db.SaveChangesAsync();
            before = await CourtCoreStateAsync(db);
        });
        var result = await CourtCoreController(db, user, new CourtCoreProvider(() => Task.CompletedTask), storage,
            NewCourtCoreNotifications(db), realtime).RunCourtSync(clientCase.Id, null, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.NotNull(before);
        Assert.Equal(before, await CourtCoreStateAsync(db));
        Assert.Equal("Параллельная сводка.html", (await db.ClientCaseDocuments.AsNoTracking().SingleAsync()).OriginalFileName);
        Assert.Equal(0, storage.Saves);
        Assert.False(storage.HasFile);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class CourtFileCommitFault : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Called { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (!Armed) return Task.CompletedTask;
            Armed = false;
            Called = true;
            throw new IOException("Synthetic lost court document COMMIT acknowledgement.");
        }
    }

    private sealed class CourtPublicationStorage : ICaseDocumentStorage
    {
        private readonly CourtCoreStorage inner = new();
        private string? path;
        public Func<Task>? AfterSave { get; set; }
        public string StoredPath => path!;
        public int Saves => inner.Saves;
        public bool HasFile => path is not null && inner.Exists(path);
        public string BuildRelativePath(int caseId, string fileName) => inner.BuildRelativePath(caseId, fileName);
        public string ResolvePath(string relativePath) => inner.ResolvePath(relativePath);
        public bool Exists(string relativePath) => inner.Exists(relativePath);
        public void DeleteIfExists(string relativePath) => inner.DeleteIfExists(relativePath);
        public Task SaveAsync(string relativePath, Stream stream, CancellationToken ct) => SaveAsync(relativePath, [], ct);
        public async Task SaveAsync(string relativePath, byte[] bytes, CancellationToken ct)
        {
            path = relativePath;
            await inner.SaveAsync(relativePath, bytes, ct);
            if (AfterSave is not null) await AfterSave();
        }
    }
}
