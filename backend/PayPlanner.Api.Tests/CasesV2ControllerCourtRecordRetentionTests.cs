using System.Net;
using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtRetentionDeleteCases() => from route in new[] { 0, 1, 2 }
                                                                       from provider in new[] { 0, 1, 2, 3 }
                                                                       from kind in new[] { "history", "materials" }
                                                                       select new object[] { route, provider, kind };

    [Theory]
    [MemberData(nameof(CourtRetentionDeleteCases))]
    public async Task CourtRetention_HttpAppliesModePreservingForeignDependents(int route, int provider, string kind)
    {
        await using var database = provider >= 2 ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = provider >= 2
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options, useObjectAuditModel: provider == 2)
            : await CreateContextAsync(useObjectAuditModel: provider == 1);
        var seed = await SeedCourtRetentionAsync(db);
        await AddForeignCourtRetentionAsync(db, seed, kind);
        db.ChangeTracker.Clear();
        var before = await CourtRetentionSnapshotAsync(db);
        var foreignBefore = await CourtRetentionForeignSnapshotAsync(db, seed);
        using var server = CaseWriteServer(db, seed.User);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.User.Id.ToString());
        using var response = await http.DeleteAsync($"/api/{(route == 0 ? "cases" : $"v{route}/cases")}/{seed.Cases[0]}");
        var body = await response.Content.ReadAsStringAsync();
        db.ChangeTracker.Clear();
        var raw = kind == "history"
            ? await db.ClientCaseCourtEventHistory.IgnoreQueryFilters().AnyAsync(row => row.ClientCaseId == seed.Cases[1] && row.ClientCaseCourtEventId == seed.Event.Id)
            : await db.ClientCaseHearingPreparationDocuments.IgnoreQueryFilters().AnyAsync(row => row.ClientCaseHearingPreparationId == seed.OtherPreparation.Id && row.ClientCaseDocumentId == seed.Document.Id);
        var visible = kind == "history"
            ? await db.ClientCaseCourtEventHistory.AnyAsync(row => row.ClientCaseId == seed.Cases[1] && row.ClientCaseCourtEventId == seed.Event.Id)
            : await db.ClientCaseHearingPreparationDocuments.AnyAsync(row => row.ClientCaseHearingPreparationId == seed.OtherPreparation.Id && row.ClientCaseDocumentId == seed.Document.Id);
        var after = await CourtRetentionSnapshotAsync(db);
        if (kind == "history" && provider is 1 or 2)
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.True(raw);
            Assert.True(visible);
            Assert.False(await db.ClientCases.AnyAsync(row => row.Id == seed.Cases[0]));
            Assert.True(await db.ClientCaseCourtEvents.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Event.Id));
            Assert.Equal(foreignBefore, await CourtRetentionForeignSnapshotAsync(db, seed));
            await AssertCourtRetentionForeignCaseAsync(db, seed);
            Assert.Null(db.Database.CurrentTransaction);
            return;
        }
        Assert.True(response.StatusCode == HttpStatusCode.Conflict,
            $"Expected409, actual{response.StatusCode}, foreign raw={raw}, visible={visible}, unchanged={before == after}.");
        Assert.DoesNotContain("private-retained-history", body);
        Assert.DoesNotContain("Другой клиент", body);
        Assert.True(raw);
        Assert.True(visible);
        Assert.Equal(before, after);
        Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> CourtRetentionHiddenCases() => from route in new[] { 0, 1, 2 }
                                                                       from postgres in new[] { false, true }
                                                                       from kind in new[] { "history", "materials" }
                                                                       select new object[] { route, postgres, kind };

    [Theory]
    [MemberData(nameof(CourtRetentionHiddenCases))]
    public async Task CourtRetention_PreservesAlreadyHiddenForeignHistory(int route, bool postgres, string kind)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtRetentionAsync(db);
        await AddForeignCourtRetentionAsync(db, seed, kind);
        await db.ClientCases.Where(row => row.Id == seed.Cases[1])
            .ExecuteUpdateAsync(set => set.SetProperty(row => EF.Property<bool>(row, "IsDeleted"), true));
        db.ChangeTracker.Clear();
        var before = await CourtRetentionSnapshotAsync(db);
        var foreignBefore = await CourtRetentionForeignSnapshotAsync(db, seed);
        var result = await DeleteCourtOwnershipCaseAsync(db, seed.User, seed.Cases[0], route);
        db.ChangeTracker.Clear();
        Assert.Null(db.Database.CurrentTransaction);
        if (kind == "history")
        {
            Assert.IsType<NoContentResult>(result);
            Assert.False(await db.ClientCases.AnyAsync(row => row.Id == seed.Cases[0]));
            Assert.True(await db.ClientCaseCourtEvents.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Event.Id));
            Assert.True(await db.ClientCases.IgnoreQueryFilters().Where(row => row.Id == seed.Cases[1]).Select(row => EF.Property<bool>(row, "IsDeleted")).SingleAsync());
            Assert.Equal(foreignBefore, await CourtRetentionForeignSnapshotAsync(db, seed));
        }
        else
        {
            Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal(before, await CourtRetentionSnapshotAsync(db));
        }
    }

    [Theory]
    [InlineData("history-new")]
    [InlineData("history-case")]
    [InlineData("materials-new")]
    [InlineData("materials-link")]
    public async Task CourtRetention_PgExcludesForeignWritesBeforeSnapshotChecks(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options, useObjectAuditModel: target.StartsWith("materials", StringComparison.Ordinal));
        await using var seedDb = Open();
        var seed = await SeedCourtRetentionAsync(seedDb);
        var attempted = false;
        var committed = false;
        string? blocked = null;
        var hook = new CourtDeleteBeforeInstanceLock(async ct =>
        {
            await using var other = Open();
            await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                if (target == "history-new") other.ClientCaseCourtEventHistory.Add(new ClientCaseCourtEventHistory
                { ClientCaseId = seed.Cases[1], ClientCaseCourtEventId = seed.Event.Id, Action = "created", Summary = "Параллельная чужая история" });
                else if (target == "history-case") await other.ClientCaseCourtEventHistory.Where(row => row.Id == seed.History.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[1]), ct);
                else if (target == "materials-new") other.ClientCaseHearingPreparationDocuments.Add(new ClientCaseHearingPreparationDocument
                { ClientCaseHearingPreparationId = seed.OtherPreparation.Id, ClientCaseDocumentId = seed.Document.Id });
                else await other.ClientCaseHearingPreparationDocuments.Where(row => row.ClientCaseHearingPreparationId == seed.OtherPreparation.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseDocumentId, seed.Document.Id), ct);
                await other.SaveChangesAsync(ct);
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
        var result = await DeleteCourtOwnershipCaseAsync(request, seed.User, seed.Cases[0], 2);
        Assert.True(hook.Called);
        Assert.True(attempted);
        Assert.False(committed);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked);
        Assert.IsType<NoContentResult>(result);
        await using var verify = Open();
        await AssertCourtRetentionForeignCaseAsync(verify, seed);
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("event")]
    [InlineData("document")]
    public async Task CourtRetention_PgExcludesIncomingParentsBeforeCaseLock(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options, useObjectAuditModel: target != "event");
        await using var seedDb = Open();
        var seed = await SeedCourtRetentionAsync(seedDb);
        var instance = new ClientCaseCourtInstance { ClientCaseId = seed.Cases[1], Instance = "first" };
        var courtEvent = new ClientCaseCourtEvent { ClientCaseId = seed.Cases[1], Title = "Другое заседание", ScheduledAt = DateTime.UnixEpoch };
        seedDb.ClientCaseCourtInstances.Add(instance);
        seedDb.ClientCaseCourtEvents.Add(courtEvent);
        await seedDb.SaveChangesAsync();
        var complaint = new ClientCaseCourtComplaint { ClientCaseId = seed.Cases[1], CourtInstanceId = instance.Id, Title = "Другая жалоба" };
        var history = new ClientCaseCourtEventHistory
        { ClientCaseId = seed.Cases[1], ClientCaseCourtEventId = courtEvent.Id, Action = "created", Summary = "Другая история" };
        seedDb.ClientCaseCourtComplaints.Add(complaint);
        seedDb.ClientCaseCourtEventHistory.Add(history);
        await seedDb.SaveChangesAsync();
        var attempted = false;
        var committed = false;
        string? blocked = null;
        var hook = new CourtRetentionBeforeRoleLock(async ct =>
        {
            await using var other = Open();
            await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                if (target == "instance") await other.ClientCaseCourtInstances.Where(row => row.Id == instance.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[0]), ct);
                else if (target == "event") await other.ClientCaseCourtEvents.Where(row => row.Id == courtEvent.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[0]), ct);
                else await other.ClientCaseDocuments.Where(row => row.Id == seed.OtherDocument.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[0]), ct);
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
        var result = await DeleteCourtOwnershipCaseAsync(request, seed.User, seed.Cases[0], 2);
        Assert.True(hook.Called);
        Assert.True(attempted);
        Assert.False(committed);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked);
        Assert.IsType<NoContentResult>(result);
        await using var verify = Open();
        Assert.Equal(seed.Cases[1], await verify.ClientCaseCourtInstances.IgnoreQueryFilters().Where(row => row.Id == instance.Id).Select(row => row.ClientCaseId).SingleAsync());
        Assert.Equal(seed.Cases[1], await verify.ClientCaseCourtEvents.IgnoreQueryFilters().Where(row => row.Id == courtEvent.Id).Select(row => row.ClientCaseId).SingleAsync());
        Assert.True(await verify.ClientCaseCourtComplaints.AnyAsync(row => row.Id == complaint.Id));
        Assert.True(await verify.ClientCaseCourtEventHistory.AnyAsync(row => row.Id == history.Id));
        await AssertCourtRetentionForeignCaseAsync(verify, seed);
    }

    [Fact]
    public async Task CourtRetention_PgCommittedPreparationMoveRejectsDeletionWithoutUndoingMove()
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options, useObjectAuditModel: false);
        await using var seedDb = Open();
        var seed = await SeedCourtRetentionAsync(seedDb);
        var committed = false;
        var hook = new CourtRetentionBeforeRoleLock(async ct =>
        {
            await using var other = Open();
            await other.ClientCaseHearingPreparations.Where(row => row.Id == seed.Preparation.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[1]), ct);
            committed = true;
        });
        await using var request = Open(hook);
        var result = await DeleteCourtOwnershipCaseAsync(request, seed.User, seed.Cases[0], 2);
        Assert.True(hook.Called);
        Assert.True(committed);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Null(request.Database.CurrentTransaction);
        await using var verify = Open();
        Assert.True(await verify.ClientCases.AnyAsync(row => row.Id == seed.Cases[0]));
        Assert.True(await verify.ClientCaseDocuments.AnyAsync(row => row.Id == seed.Document.Id));
        Assert.Equal(seed.Cases[1], await verify.ClientCaseHearingPreparations.Where(row => row.Id == seed.Preparation.Id).Select(row => row.ClientCaseId).SingleAsync());
        Assert.True(await verify.ClientCaseHearingPreparationDocuments.AnyAsync(row => row.ClientCaseHearingPreparationId == seed.Preparation.Id && row.ClientCaseDocumentId == seed.Document.Id));
        Assert.True(await verify.Payments.AnyAsync(row => row.ClientCaseId == seed.Cases[0]));
        await AssertCourtRetentionForeignCaseAsync(verify, seed);
    }

    private sealed class CourtRetentionBeforeRoleLock(Func<CancellationToken, Task> action) : DbCommandInterceptor
    {
        public bool Called { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Called && command.CommandText.Contains("SELECT \"Id\" FROM \"Roles\" WHERE", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR SHARE NOWAIT", StringComparison.Ordinal))
            {
                Called = true;
                await action(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData("event")]
    [InlineData("document")]
    [InlineData("preparation")]
    public async Task CourtRetention_PgPinsParentOwnershipUntilDeleteCompletes(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options, useObjectAuditModel: target != "event");
        await using var seedDb = Open();
        var seed = await SeedCourtRetentionAsync(seedDb);
        var attempted = false;
        var committed = false;
        string? blocked = null;
        var hook = new CaseDeleteBeforePaymentSave(async ct =>
        {
            await using var other = Open();
            await using var transaction = await other.Database.BeginTransactionAsync(ct);
            await other.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                if (target == "event") await other.ClientCaseCourtEvents.Where(row => row.Id == seed.Event.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[1]), ct);
                else if (target == "document") await other.ClientCaseDocuments.Where(row => row.Id == seed.Document.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[1]), ct);
                else await other.ClientCaseHearingPreparations.Where(row => row.Id == seed.Preparation.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, seed.Cases[1]), ct);
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
        var result = await DeleteCourtOwnershipCaseAsync(request, seed.User, seed.Cases[0], 2);
        Assert.True(hook.Called);
        Assert.True(attempted);
        Assert.False(committed);
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked);
        Assert.IsType<NoContentResult>(result);
        await using var verify = Open();
        if (target == "event")
        {
            Assert.False(await verify.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Cases[0]));
            Assert.False(await verify.ClientCaseCourtEvents.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Event.Id));
            Assert.False(await verify.ClientCaseDocuments.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Document.Id));
            Assert.False(await verify.ClientCaseHearingPreparations.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Preparation.Id));
        }
        else
        {
            Assert.Equal(seed.Cases[0], await verify.ClientCaseCourtEvents.IgnoreQueryFilters().Where(row => row.Id == seed.Event.Id).Select(row => row.ClientCaseId).SingleAsync());
            Assert.Equal(seed.Cases[0], await verify.ClientCaseDocuments.IgnoreQueryFilters().Where(row => row.Id == seed.Document.Id).Select(row => row.ClientCaseId).SingleAsync());
            Assert.Equal(seed.Cases[0], await verify.ClientCaseHearingPreparations.IgnoreQueryFilters().Where(row => row.Id == seed.Preparation.Id).Select(row => row.ClientCaseId).SingleAsync());
        }
        await AssertCourtRetentionForeignCaseAsync(verify, seed);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("history")]
    [InlineData("document")]
    [InlineData("preparation")]
    [InlineData("link")]
    public async Task CourtRetention_PgBusyRowsStopBeforePaymentDetach(string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options,
            useObjectAuditModel: target is "document" or "preparation" or "link");
        await using var seedDb = Open();
        var seed = await SeedCourtRetentionAsync(seedDb);
        var before = await CourtRetentionSnapshotAsync(seedDb);
        await using var other = Open();
        await using var transaction = await other.Database.BeginTransactionAsync();
        if (target == "event") await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtEvents\" WHERE \"Id\" = {seed.Event.Id} FOR UPDATE");
        else if (target == "history") await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseCourtEventHistory\" WHERE \"Id\" = {seed.History.Id} FOR UPDATE");
        else if (target == "document") await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseDocuments\" WHERE \"Id\" = {seed.Document.Id} FOR UPDATE");
        else if (target == "preparation") await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"ClientCaseHearingPreparations\" WHERE \"Id\" = {seed.Preparation.Id} FOR UPDATE");
        else await other.Database.ExecuteSqlInterpolatedAsync($"SELECT \"ClientCaseDocumentId\" FROM \"ClientCaseHearingPreparationDocuments\" WHERE \"ClientCaseDocumentId\" = {seed.Document.Id} FOR UPDATE");
        await using var request = Open();
        Assert.IsType<ConflictObjectResult>(await DeleteCourtOwnershipCaseAsync(request, seed.User, seed.Cases[0], 2));
        Assert.Null(request.Database.CurrentTransaction);
        Assert.Equal(before, await CourtRetentionSnapshotAsync(seedDb));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    public async Task CourtRetention_ValidLinksRetainOrdinaryDeletion(int route, int provider)
    {
        await using var database = provider >= 2 ? await modelTemplate.CreateDatabaseAsync() : null;
        await using var db = provider >= 2
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options, useObjectAuditModel: provider == 2)
            : await CreateContextAsync(useObjectAuditModel: provider == 1);
        var seed = await SeedCourtRetentionAsync(db);
        Assert.IsType<NoContentResult>(await DeleteCourtOwnershipCaseAsync(db, seed.User, seed.Cases[0], route));
        db.ChangeTracker.Clear();
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(await db.ClientCases.AnyAsync(row => row.Id == seed.Cases[0]));
        Assert.Equal(provider is 1 or 2, await db.ClientCases.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.Cases[0]));
        Assert.Equal(provider is 1 or 2, await db.ClientCaseCourtEventHistory.IgnoreQueryFilters().AnyAsync(row => row.Id == seed.History.Id));
        Assert.Equal(provider is 1 or 2, await db.ClientCaseHearingPreparationDocuments.IgnoreQueryFilters()
            .AnyAsync(row => row.ClientCaseHearingPreparationId == seed.Preparation.Id));
        await AssertCourtRetentionForeignCaseAsync(db, seed);
    }

    [Theory]
    [InlineData("history")]
    [InlineData("materials")]
    public async Task CourtRetention_HardV2RefusalPrecedesStorageAndPublication(string kind)
    {
        await using var db = await CreateContextAsync(useObjectAuditModel: false);
        var seed = await SeedCourtRetentionAsync(db);
        await AddForeignCourtRetentionAsync(db, seed, kind);
        db.ChangeTracker.Clear();
        var before = await CourtRetentionSnapshotAsync(db);
        var storage = new RecordingCaseProductDeletion(db) { Status = "unavailable" };
        var publisher = new BulkPublicationBoundary(db);
        var controller = CreateController(db, seed.User, realtimePublisher: publisher, productStorageDeletion: storage);
        Assert.IsType<ConflictObjectResult>(await controller.Delete(seed.Cases[0], default));
        Assert.Equal(0, storage.Calls);
        Assert.Equal(0, publisher.Count);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtRetentionSnapshotAsync(db));
    }

    private sealed record CourtRetentionSeed(User User, int[] Cases, ClientCaseCourtEvent Event, ClientCaseCourtEventHistory History,
        ClientCaseDocument Document, ClientCaseDocument OtherDocument, ClientCaseHearingPreparation Preparation, ClientCaseHearingPreparation OtherPreparation);

    private static async Task<CourtRetentionSeed> SeedCourtRetentionAsync(PaymentContext db)
    {
        var (user, ids) = await SeedCaseDeleteAsync(db);
        var other = await CreateUserAsync(db, "court-retention-other@synthetic.local", "Другой сотрудник");
        var client = new Client { Name = "Другой клиент", IsActive = true, ResponsibleUserId = other.Id };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        await db.ClientCases.Where(row => row.Id == ids[1]).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.ClientId, client.Id).SetProperty(row => row.ResponsibleUserId, other.Id));
        await db.Payments.Where(row => row.ClientCaseId == ids[1]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientId, client.Id));
        await db.RolePermissions.Where(row => row.RoleId == user.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.ViewScope, DataVisibilityScope.Own));
        var courtEvent = new ClientCaseCourtEvent { ClientCaseId = ids[0], Title = "Своё заседание", ScheduledAt = DateTime.UnixEpoch, CourtInstance = "first" };
        var document = new ClientCaseDocument
        { ClientCaseId = ids[0], DocumentKey = "retention-own", OriginalFileName = "own.txt", StoredFileName = "own.txt", StoragePath = "synthetic/own.txt", ContentType = "text/plain", SizeBytes = 4 };
        var otherDocument = new ClientCaseDocument
        { ClientCaseId = ids[1], DocumentKey = "retention-other", OriginalFileName = "other.txt", StoredFileName = "other.txt", StoragePath = "synthetic/other.txt", ContentType = "text/plain", SizeBytes = 5 };
        var preparation = new ClientCaseHearingPreparation { ClientCaseId = ids[0], HearingAt = DateTime.UnixEpoch };
        var otherPreparation = new ClientCaseHearingPreparation { ClientCaseId = ids[1], HearingAt = DateTime.UnixEpoch.AddDays(1), MaterialsCheckedAt = DateTime.UnixEpoch, MaterialsCheckedByName = "Сохранить проверку" };
        db.ClientCaseCourtEvents.Add(courtEvent);
        db.ClientCaseDocuments.AddRange(document, otherDocument);
        db.ClientCaseHearingPreparations.AddRange(preparation, otherPreparation);
        await db.SaveChangesAsync();
        var history = new ClientCaseCourtEventHistory
        { ClientCaseId = ids[0], ClientCaseCourtEventId = courtEvent.Id, Action = "created", Summary = "Своя история", OccurredAt = DateTime.UnixEpoch };
        db.ClientCaseCourtEventHistory.Add(history);
        db.ClientCaseHearingPreparationDocuments.AddRange(
            new ClientCaseHearingPreparationDocument { ClientCaseHearingPreparationId = preparation.Id, ClientCaseDocumentId = document.Id },
            new ClientCaseHearingPreparationDocument { ClientCaseHearingPreparationId = otherPreparation.Id, ClientCaseDocumentId = otherDocument.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(user, ids, courtEvent, history, document, otherDocument, preparation, otherPreparation);
    }

    private static async Task AddForeignCourtRetentionAsync(PaymentContext db, CourtRetentionSeed seed, string kind)
    {
        if (kind == "history") db.ClientCaseCourtEventHistory.Add(new ClientCaseCourtEventHistory
        { ClientCaseId = seed.Cases[1], ClientCaseCourtEventId = seed.Event.Id, Action = "created", Summary = "private-retained-history" });
        else db.ClientCaseHearingPreparationDocuments.Add(new ClientCaseHearingPreparationDocument
        { ClientCaseHearingPreparationId = seed.OtherPreparation.Id, ClientCaseDocumentId = seed.Document.Id });
        await db.SaveChangesAsync();
    }

    private static async Task AssertCourtRetentionForeignCaseAsync(PaymentContext db, CourtRetentionSeed seed)
    {
        Assert.True(await db.ClientCases.AnyAsync(row => row.Id == seed.Cases[1]));
        var document = await db.ClientCaseDocuments.SingleAsync(row => row.Id == seed.OtherDocument.Id);
        Assert.Equal(seed.Cases[1], document.ClientCaseId);
        Assert.Equal("synthetic/other.txt", document.StoragePath);
        Assert.Equal(5, document.SizeBytes);
        var preparation = await db.ClientCaseHearingPreparations.SingleAsync(row => row.Id == seed.OtherPreparation.Id);
        Assert.Equal(seed.Cases[1], preparation.ClientCaseId);
        Assert.Equal(DateTime.UnixEpoch, preparation.MaterialsCheckedAt);
        Assert.Equal("Сохранить проверку", preparation.MaterialsCheckedByName);
        Assert.Equal(new[] { seed.OtherDocument.Id }, await db.ClientCaseHearingPreparationDocuments
            .Where(row => row.ClientCaseHearingPreparationId == preparation.Id).Select(row => row.ClientCaseDocumentId).ToArrayAsync());
        Assert.Equal(new[] { 15.25m, 27.50m }, await db.Payments.IgnoreQueryFilters().OrderBy(row => row.Id).Select(row => row.Amount).ToArrayAsync());
        Assert.Equal(seed.Cases[1], await db.Payments.IgnoreQueryFilters().OrderBy(row => row.Id).Select(row => row.ClientCaseId).LastAsync());
        Assert.Equal("Сохранённый результат", await db.TaskJournalItems.IgnoreQueryFilters().Where(row => row.Status == "done").Select(row => row.Result).SingleAsync());
    }

    private static async Task<string> CourtRetentionForeignSnapshotAsync(PaymentContext db, CourtRetentionSeed seed) => JsonSerializer.Serialize(new
    {
        Case = await db.ClientCases.IgnoreQueryFilters().AsNoTracking().Where(row => row.Id == seed.Cases[1])
            .Select(row => new { row.Id, row.ClientId, row.Title, row.Status, row.EventHistory }).SingleAsync(),
        Documents = await db.ClientCaseDocuments.IgnoreQueryFilters().AsNoTracking().Where(row => row.ClientCaseId == seed.Cases[1]).OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.DocumentKey, row.Version, row.StoragePath, row.SizeBytes }).ToArrayAsync(),
        History = await db.ClientCaseCourtEventHistory.IgnoreQueryFilters().AsNoTracking().Where(row => row.ClientCaseId == seed.Cases[1]).OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.ClientCaseCourtEventId, row.Action, row.Summary, row.OccurredAt, row.ActorUserId, row.ActorUserName }).ToArrayAsync(),
        Preparation = await db.ClientCaseHearingPreparations.IgnoreQueryFilters().AsNoTracking().Where(row => row.Id == seed.OtherPreparation.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.HearingAt, row.MaterialsCheckedAt, row.MaterialsCheckedByName, row.UpdatedAt }).SingleAsync(),
        Links = await db.ClientCaseHearingPreparationDocuments.IgnoreQueryFilters().AsNoTracking().Where(row => row.ClientCaseHearingPreparationId == seed.OtherPreparation.Id)
            .OrderBy(row => row.ClientCaseDocumentId).Select(row => new { row.ClientCaseHearingPreparationId, row.ClientCaseDocumentId }).ToArrayAsync()
    });

    private static async Task<string> CourtRetentionSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Existing = await CourtDeleteOwnershipSnapshotAsync(db),
        Events = await db.ClientCaseCourtEvents.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.Title, row.ScheduledAt, row.SourceType }).ToArrayAsync(),
        History = await db.ClientCaseCourtEventHistory.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.ClientCaseCourtEventId, row.Action, row.Summary, row.OccurredAt, row.ActorUserId, row.ActorUserName }).ToArrayAsync(),
        VisibleHistory = await db.ClientCaseCourtEventHistory.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(),
        Documents = await db.ClientCaseDocuments.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.DocumentKey, row.Version, row.OriginalFileName, row.StoragePath, row.SizeBytes }).ToArrayAsync(),
        Preparations = await db.ClientCaseHearingPreparations.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.ClientCaseId, row.HearingAt, row.ContractCheckedAt, row.AuthorityCheckedAt, row.MaterialsCheckedAt, row.MaterialsCheckedByName, row.UpdatedAt }).ToArrayAsync(),
        Links = await db.ClientCaseHearingPreparationDocuments.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(row => row.ClientCaseHearingPreparationId).ThenBy(row => row.ClientCaseDocumentId)
            .Select(row => new { row.ClientCaseHearingPreparationId, row.ClientCaseDocumentId }).ToArrayAsync(),
        VisibleLinks = await db.ClientCaseHearingPreparationDocuments.OrderBy(row => row.ClientCaseHearingPreparationId).ThenBy(row => row.ClientCaseDocumentId)
            .Select(row => new { row.ClientCaseHearingPreparationId, row.ClientCaseDocumentId }).ToArrayAsync()
    });
}
