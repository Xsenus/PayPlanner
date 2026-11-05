using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    public static IEnumerable<object[]> CourtLinkOccupiedCases() =>
        from command in new[] { "event-create", "event-update", "deadline-create", "deadline-update" }
        from direction in new[] { "outgoing", "incoming", "both" }
        from postgres in new[] { false, true }
        select new object[] { command, direction, postgres };

    [Theory]
    [MemberData(nameof(CourtLinkOccupiedCases))]
    public async Task CourtLink_HttpRejectsOccupiedTargetAndPreservesCompleteGraph(string command, string direction, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        await OccupyCourtLinkAsync(db, seed, command, direction, "court-event");
        var before = await CourtGraphSnapshotAsync(db);
        using var server = CaseWriteServer(db, seed.Base.Base.User);
        using var http = server.CreateClient();
        http.DefaultRequestHeaders.Add("X-Confirmation-User", seed.Base.Base.User.Id.ToString());
        using var response = await SendCourtGraphHttpAsync(http, seed, command, seed.Base.Base.Cases[0]);
        db.ChangeTracker.Clear();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            command,
            direction,
            postgres,
            status = (int)response.StatusCode,
            deadlines = await db.ClientCaseProceduralDeadlines.AsNoTracking().Where(row => row.ClientCaseId == seed.Base.Base.Cases[0])
                .OrderBy(row => row.Id).Select(row => new { row.Id, row.SourceType, row.SourceId }).ToArrayAsync(),
            events = await db.ClientCaseCourtEvents.AsNoTracking().Where(row => row.ClientCaseId == seed.Base.Base.Cases[0])
                .OrderBy(row => row.Id).Select(row => new { row.Id, row.ProceduralDeadlineId }).ToArrayAsync()
        }));
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static async Task OccupyCourtLinkAsync(PaymentContext db, CourtGraphSeed seed, string command, string direction, string sourceType)
    {
        // The request selects SecondDeadline or SecondEvent. The other record owns it.
        var deadlineOwns = command.StartsWith("event-", StringComparison.Ordinal)
            ? direction is "outgoing" or "both" : direction is "incoming" or "both";
        var eventOwns = command.StartsWith("event-", StringComparison.Ordinal)
            ? direction is "incoming" or "both" : direction is "outgoing" or "both";
        if (deadlineOwns)
            await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.SecondDeadline.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.SourceType, sourceType).SetProperty(row => row.SourceId, seed.SecondEvent.Id));
        if (eventOwns)
            await db.ClientCaseCourtEvents.Where(row => row.Id == seed.SecondEvent.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.ProceduralDeadlineId, seed.SecondDeadline.Id));
        db.ChangeTracker.Clear();
    }

    public static IEnumerable<object[]> CourtLinkLegacyAliases() =>
        from command in new[] { "event-create", "event-update", "deadline-create", "deadline-update" }
        from sourceType in new[] { "courtEvent", " Court_Event ", "\tcourtEvent\u00a0" }
        from postgres in new[] { false, true }
        select new object[] { command, sourceType, postgres };

    [Theory]
    [MemberData(nameof(CourtLinkLegacyAliases))]
    public async Task CourtLink_RecognizesOccupiedLegacySourceAliases(string command, string sourceType, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        await OccupyCourtLinkAsync(db, seed, command, command.StartsWith("event-", StringComparison.Ordinal) ? "outgoing" : "incoming", sourceType);
        var before = await CourtGraphSnapshotAsync(db);
        var result = await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User), seed, command, default);
        Assert.IsType<BadRequestObjectResult>(result);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
    }

    [Theory]
    [InlineData("event-update", false)]
    [InlineData("event-update", true)]
    [InlineData("deadline-update", false)]
    [InlineData("deadline-update", true)]
    public async Task CourtLink_UpdatingOwnPairPreservesReciprocityAndCompletedResults(string command, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        var before = await CourtGraphForeignSnapshotAsync(db, seed);
        var controller = CreateController(db, seed.Base.Base.User);
        if (command == "event-update")
        {
            var request = CourtGraphEventRequest(seed);
            request.ProceduralDeadlineId = seed.Base.OwnDeadline.Id;
            Assert.IsType<OkObjectResult>((await controller.UpdateCourtEvent(seed.Base.Base.Cases[0], seed.Base.Base.Event.Id, request, default)).Result);
        }
        else
        {
            var request = CourtGraphDeadlineRequest(seed);
            request.SourceId = seed.Base.Base.Event.Id;
            Assert.IsType<OkObjectResult>((await controller.UpdateProceduralDeadline(seed.Base.Base.Cases[0], seed.Base.OwnDeadline.Id, request, default)).Result);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(seed.Base.OwnDeadline.Id, await db.ClientCaseCourtEvents.Where(row => row.Id == seed.Base.Base.Event.Id)
            .Select(row => row.ProceduralDeadlineId).SingleAsync());
        Assert.Equal(seed.Base.Base.Event.Id, await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Base.OwnDeadline.Id)
            .Select(row => row.SourceId).SingleAsync());
        Assert.Equal("Сохранённый результат", await db.TaskJournalItems.Where(row => row.Id == seed.DoneTaskId).Select(row => row.Result).SingleAsync());
        Assert.Equal(before, await CourtGraphForeignSnapshotAsync(db, seed));
    }

    public static IEnumerable<object[]> CourtLinkDetachCases() => from sourceType in new[] { "court-event", "courtEvent", " Court_Event " }
                                                                  from postgres in new[] { false, true }
                                                                  select new object[] { sourceType, postgres };

    [Theory]
    [MemberData(nameof(CourtLinkDetachCases))]
    public async Task CourtLink_ChangingSourceTypeReleasesOldEventEvenWithSameNumericSource(string sourceType, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Base.OwnDeadline.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.SourceType, sourceType));
        db.ChangeTracker.Clear();
        var request = CourtGraphDeadlineRequest(seed);
        request.SourceType = "manual";
        request.SourceId = seed.Base.Base.Event.Id;
        Assert.IsType<OkObjectResult>((await CreateController(db, seed.Base.Base.User).UpdateProceduralDeadline(seed.Base.Base.Cases[0], seed.Base.OwnDeadline.Id, request, default)).Result);
        db.ChangeTracker.Clear();
        Assert.Null(await db.ClientCaseCourtEvents.Where(row => row.Id == seed.Base.Base.Event.Id).Select(row => row.ProceduralDeadlineId).SingleAsync());
        var deadline = await db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(row => row.Id == seed.Base.OwnDeadline.Id);
        Assert.Equal("manual", deadline.SourceType);
        Assert.Equal(seed.Base.Base.Event.Id, deadline.SourceId);
        Assert.Equal("Сохранённый результат", await db.TaskJournalItems.Where(row => row.Id == seed.DoneTaskId).Select(row => row.Result).SingleAsync());
    }

    [Theory]
    [InlineData("event-create")]
    [InlineData("event-update")]
    [InlineData("deadline-create")]
    [InlineData("deadline-update")]
    public async Task CourtLink_PgBusyClaimRollsBackAndCommittedClaimIsPreservedOnRetry(string command)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PostgreSqlPaymentContext Open() => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await using var seedDb = Open();
        var seed = await SeedCourtGraphAsync(seedDb);
        var before = await CourtGraphSnapshotAsync(seedDb);
        await using var rival = Open();
        await using var transaction = await rival.Database.BeginTransactionAsync();
        await OccupyCourtLinkAsync(rival, seed, command, "both", "court-event");
        await using var requestDb = Open();
        Assert.IsType<ConflictObjectResult>(await InvokeCourtGraphAsync(CreateController(requestDb, seed.Base.Base.User), seed, command, default));
        requestDb.ChangeTracker.Clear();
        Assert.Null(requestDb.Database.CurrentTransaction);
        Assert.Equal(before, await CourtGraphSnapshotAsync(requestDb));
        await transaction.CommitAsync();
        var occupied = await CourtGraphSnapshotAsync(seedDb);
        Assert.IsType<BadRequestObjectResult>(await InvokeCourtGraphAsync(CreateController(requestDb, seed.Base.Base.User), seed, command, default));
        requestDb.ChangeTracker.Clear();
        Assert.Equal(occupied, await CourtGraphSnapshotAsync(requestDb));
    }
}
