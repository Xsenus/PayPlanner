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
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class CasesV2ControllerTests
{
    private static readonly string[] CourtGraphCommands = ["deadline-create", "deadline-update", "deadline-close", "event-create", "event-update", "hearing", "party-create", "party-archive"];

    public static IEnumerable<object[]> CourtGraphHttpCases() =>
        from command in CourtGraphCommands
        from mode in new[] { "anonymous", "inactive", "unapproved", "session", "view", "edit", "hidden", "missing" }
        from postgres in mode is "session" or "view" ? new[] { false, true } : new[] { false }
        select new object[] { command, mode, postgres };

    [Theory]
    [MemberData(nameof(CourtGraphHttpCases))]
    public async Task CourtGraph_HttpRequiresCurrentActorAndVisibleEditableCase(string command, string mode, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        var actor = seed.Base.Base.User;
        if (mode == "inactive") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false));
        if (mode == "unapproved") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsApproved, false));
        if (mode == "session") await db.Users.Where(row => row.Id == actor.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.AuthTokenVersion, 1));
        if (mode == "view") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false));
        if (mode == "edit") await db.RolePermissions.Where(row => row.RoleId == actor.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanEdit, false));
        var before = await CourtGraphSnapshotAsync(db);
        using var server = CaseWriteServer(db, actor);
        using var http = server.CreateClient();
        if (mode != "anonymous") http.DefaultRequestHeaders.Add("X-Confirmation-User", actor.Id.ToString());
        var id = mode == "hidden" ? seed.Base.Base.Cases[1] : mode == "missing" ? int.MaxValue : seed.Base.Base.Cases[0];
        using var response = await SendCourtGraphHttpAsync(http, seed, command, id);
        var expected = mode is "view" or "edit" ? HttpStatusCode.Forbidden
            : mode is "hidden" or "missing" ? HttpStatusCode.NotFound : HttpStatusCode.Unauthorized;
        Assert.Equal(expected, response.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
    }

    private static int CourtGraphSaveCount(string command) => command switch
    {
        "deadline-create" or "deadline-update" => 8,
        "deadline-close" or "event-create" => 5,
        "event-update" or "hearing" => 4,
        _ => 2
    };

    public static IEnumerable<object[]> CourtGraphSaveFailures() =>
        from command in CourtGraphCommands
        from save in Enumerable.Range(1, CourtGraphSaveCount(command))
        from postgres in new[] { false, true }
        from cancel in new[] { false, true }
        select new object[] { command, save, postgres, cancel };

    [Theory]
    [MemberData(nameof(CourtGraphSaveFailures))]
    public async Task CourtGraph_FailedSaveOrCancellationRollsBackAllCoreRecords(string command, int save, bool postgres, bool cancel)
    {
        var fault = new CourtWriteAfterSaveFault(save, cancel);
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(fault).Options)
            : await CreateContextAsync(fault);
        var seed = await SeedCourtGraphAsync(db);
        var before = await CourtGraphSnapshotAsync(db);
        var publisher = new BulkPublicationBoundary(db);
        var notifications = NewCourtCoreNotifications(db);
        using var cancellation = new CancellationTokenSource();
        fault.Cancel = cancellation.Cancel;
        fault.Armed = true;
        var controller = CreateController(db, seed.Base.Base.User, realtimePublisher: publisher, notificationService: notifications);
        Task Write() => InvokeCourtGraphAsync(controller, seed, command, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
        else await Assert.ThrowsAsync<CourtWriteInjectedFailure>(Write);
        Assert.True(fault.Called, $"Unreached save {save} for {command}.");
        Assert.Equal(0, publisher.Count);
        Assert.Equal(0, ((CourtCoreNotifications)(object)notifications).Calls);
        Assert.Null(db.Database.CurrentTransaction);
        db.ChangeTracker.Clear();
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
    }

    public static IEnumerable<object[]> CourtGraphSuccessCases() => from command in CourtGraphCommands
                                                                    from postgres in new[] { false, true }
                                                                    select new object[] { command, postgres };

    [Theory]
    [MemberData(nameof(CourtGraphSuccessCases))]
    public async Task CourtGraph_SuccessPreservesBusinessRulesAndCommitsBeforePublication(string command, bool postgres)
    {
        var probe = new CourtGraphSaveProbe();
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).AddInterceptors(probe).Options)
            : await CreateContextAsync(probe);
        var seed = await SeedCourtGraphAsync(db);
        await db.RolePermissions.Where(row => row.RoleId == seed.Base.Base.User.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanCreate, false));
        var foreignBefore = await CourtGraphForeignSnapshotAsync(db, seed);
        var publisher = new BulkPublicationBoundary(db);
        var notifications = NewCourtCoreNotifications(db);
        probe.Armed = true;
        var result = await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User, realtimePublisher: publisher, notificationService: notifications), seed, command, default);
        AssertCourtGraphSuccess(result, command);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(command.StartsWith("party-", StringComparison.Ordinal) ? 0 : 1, publisher.Count);
        Assert.Equal(command.StartsWith("deadline-", StringComparison.Ordinal) || command.StartsWith("event-", StringComparison.Ordinal) ? 1 : 0,
            ((CourtCoreNotifications)(object)notifications).Calls);
        Assert.Equal(foreignBefore, await CourtGraphForeignSnapshotAsync(db, seed));
        Assert.Equal("Сохранённый результат", await db.TaskJournalItems.AsNoTracking().Where(row => row.Id == seed.DoneTaskId).Select(row => row.Result).SingleAsync());
        Assert.Equal(new[] { 15.25m, 27.50m }, await db.Payments.IgnoreQueryFilters().OrderBy(row => row.Id).Select(row => row.Amount).ToArrayAsync());
        Console.WriteLine($"Court graph saves command={command}, postgres={postgres}, count={probe.Saves}.");
    }

    public static IEnumerable<object[]> CourtGraphCallerScopeCases() => from command in CourtGraphCommands
                                                                        from pending in new[] { false, true }
                                                                        select new object[] { command, pending };

    [Theory]
    [MemberData(nameof(CourtGraphCallerScopeCases))]
    public async Task CourtGraph_RejectsUnownedTransactionAndPendingChangesWithoutClearingThem(string command, bool pending)
    {
        await using var db = await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        var before = await CourtGraphSnapshotAsync(db);
        await using var transaction = pending ? null : await db.Database.BeginTransactionAsync();
        var task = await db.TaskJournalItems.SingleAsync(row => row.ClientCaseId == seed.Base.Base.Cases[1]);
        if (pending) task.Result = "Несохранённый результат другого действия";
        var publisher = new BulkPublicationBoundary(db);
        var notifications = NewCourtCoreNotifications(db);
        var result = await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User, realtimePublisher: publisher, notificationService: notifications), seed, command, default);
        Assert.IsType<ConflictObjectResult>(result);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(0, publisher.Count);
        Assert.Equal(0, ((CourtCoreNotifications)(object)notifications).Calls);
        if (pending)
        {
            Assert.Equal("Несохранённый результат другого действия", task.Result);
            Assert.Equal(EntityState.Modified, db.Entry(task).State);
        }
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
    }

    [Theory]
    [InlineData("event-update", false)]
    [InlineData("event-update", true)]
    [InlineData("deadline-update", false)]
    [InlineData("deadline-update", true)]
    public async Task CourtGraph_HistoricalForeignReverseLinkIsPreserved(string command, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        if (command == "event-update")
        {
            await db.ClientCaseCourtEvents.Where(row => row.Id == seed.Base.Base.Event.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ProceduralDeadlineId, seed.Base.OtherDeadline.Id));
            await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Base.OtherDeadline.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.SourceType, "court-event").SetProperty(row => row.SourceId, seed.Base.Base.Event.Id));
        }
        else
        {
            await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Base.OwnDeadline.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.SourceType, "court-event").SetProperty(row => row.SourceId, seed.Base.OtherEvent.Id));
            await db.ClientCaseCourtEvents.Where(row => row.Id == seed.Base.OtherEvent.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ProceduralDeadlineId, seed.Base.OwnDeadline.Id));
        }
        var before = await CourtGraphForeignSnapshotAsync(db, seed);
        AssertCourtGraphSuccess(await InvokeCourtGraphAsync(CreateController(db, seed.Base.Base.User), seed, command, default), command);
        Assert.Equal(before, await CourtGraphForeignSnapshotAsync(db, seed));
    }

    public static IEnumerable<object[]> CourtGraphNotificationCases() =>
        from command in CourtGraphCommands.Where(command => command.StartsWith("deadline-", StringComparison.Ordinal) || command.StartsWith("event-", StringComparison.Ordinal))
        from postgres in new[] { false, true }
        from fail in new[] { false, true }
        select new object[] { command, postgres, fail };

    [Theory]
    [MemberData(nameof(CourtGraphNotificationCases))]
    public async Task CourtGraph_NotificationFailureOccursAfterCommittedCore(string command, bool postgres, bool fail)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        var notifications = NewCourtCoreNotifications(db);
        var probe = (CourtCoreNotifications)(object)notifications;
        probe.Throw = fail;
        var controller = CreateController(db, seed.Base.Base.User, notificationService: notifications);
        if (fail) await Assert.ThrowsAsync<CourtCoreNotificationFault>(() => InvokeCourtGraphAsync(controller, seed, command, default));
        else AssertCourtGraphSuccess(await InvokeCourtGraphAsync(controller, seed, command, default), command);
        Assert.Equal(1, probe.Calls);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.True(await db.TaskJournalItems.AsNoTracking().AnyAsync(row => row.ClientCaseId == seed.Base.Base.Cases[0] && row.TagsJson.Contains("case-deadline")));
        if (command == "deadline-close") Assert.Equal("closed", await db.ClientCaseProceduralDeadlines.AsNoTracking().Where(row => row.Id == seed.Base.OwnDeadline.Id).Select(row => row.Status).SingleAsync());
        if (command == "event-create") Assert.True(await db.ClientCaseCourtEventHistory.AnyAsync(row => row.Summary.Contains("Проверенное событие")));
    }

    public static IEnumerable<object[]> CourtGraphRivals() => new (string Command, string[] Targets)[]
    {
        ("deadline-create", ["task", "document", "source-event"]),
        ("deadline-update", ["deadline", "old-source", "actor"]),
        ("deadline-close", ["deadline", "task", "case"]),
        ("event-create", ["court", "document", "deadline"]),
        ("event-update", ["event", "old-deadline", "view"]),
        ("hearing", ["event", "session", "task"]),
        ("party-create", ["related", "case", "view"]),
        ("party-archive", ["party", "session", "actor"])
    }.SelectMany(item => item.Targets.Select(target => new object[] { item.Command, target }));

    [Theory]
    [MemberData(nameof(CourtGraphRivals))]
    public async Task CourtGraph_PgPreservesConcurrentActorAndReferenceChanges(string command, string target)
    {
        await using var database = await modelTemplate.CreateDatabaseAsync();
        PaymentContext Open(params IInterceptor[] interceptors) => new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(database.ConnectionString).AddInterceptors(interceptors).Options);
        await using var seedDb = Open();
        var seed = await SeedCourtGraphAsync(seedDb);
        var attempted = false;
        var committed = false;
        string? blocked = null;
        string? rivalSnapshot = null;
        var hook = new CourtWriteBeforeSave(async ct =>
        {
            await using var rival = Open();
            await using var transaction = await rival.Database.BeginTransactionAsync(ct);
            await rival.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '300ms'", ct);
            attempted = true;
            try
            {
                await MutateCourtGraphReferenceAsync(rival, seed, command, target, ct);
                await transaction.CommitAsync(ct);
                committed = true;
                rivalSnapshot = await CourtGraphRivalSnapshotAsync(rival, seed, target);
            }
            catch (Exception error) when (TaskSyncPostgresState(error) is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
            {
                blocked = TaskSyncPostgresState(error);
            }
        });
        await using var request = Open(hook);
        hook.Armed = true;
        IActionResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await InvokeCourtGraphAsync(CreateController(request, seed.Base.Base.User), seed, command, default));
        Assert.True(hook.Called && attempted);
        if (error is not null) Assert.True(IsTaskSyncRaceAbort(error), error.ToString());
        await using var verify = Open();
        if (committed)
        {
            if (target == "case")
                Assert.Contains("Подтверждённая параллельная история", await verify.ClientCases.AsNoTracking().Where(row => row.Id == seed.Base.Base.Cases[0]).Select(row => row.EventHistory).SingleAsync());
            else Assert.Equal(rivalSnapshot, await CourtGraphRivalSnapshotAsync(verify, seed, target));
            if (target is not ("task" or "case" or "old-deadline" or "old-source"))
                Assert.True(error is not null || result is ConflictObjectResult or UnauthorizedResult or ForbidResult or NotFoundResult);
        }
        else
        {
            Assert.Contains(blocked, new[] { PostgresErrorCodes.LockNotAvailable, PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.DeadlockDetected });
            if (error is null) AssertCourtGraphSuccess(result!, command);
        }
    }

    private static async Task<string> CourtGraphRivalSnapshotAsync(PaymentContext db, CourtGraphSeed seed, string target) => target switch
    {
        "task" => JsonSerializer.Serialize(await db.TaskJournalItems.AsNoTracking().SingleAsync(row => row.Id == seed.DoneTaskId)),
        "old-deadline" => JsonSerializer.Serialize(await db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(row => row.Id == seed.Base.OwnDeadline.Id)),
        "old-source" => JsonSerializer.Serialize(await db.ClientCaseCourtEvents.AsNoTracking().SingleAsync(row => row.Id == seed.Base.Base.Event.Id)),
        _ => await CourtGraphSnapshotAsync(db)
    };

    private static Task<int> MutateCourtGraphReferenceAsync(PaymentContext db, CourtGraphSeed seed, string command, string target, CancellationToken ct)
    {
        var own = seed.Base.Base.Cases[0];
        var other = seed.Base.Base.Cases[1];
        return target switch
        {
            "session" => db.Users.Where(row => row.Id == seed.Base.Base.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.AuthTokenVersion, 1), ct),
            "actor" => db.Users.Where(row => row.Id == seed.Base.Base.User.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct),
            "view" => db.RolePermissions.Where(row => row.RoleId == seed.Base.Base.User.RoleId).ExecuteUpdateAsync(set => set.SetProperty(row => row.CanView, false), ct),
            "case" => db.ClientCases.Where(row => row.Id == own).ExecuteUpdateAsync(set => set.SetProperty(row => row.EventHistory, "Подтверждённая параллельная история"), ct),
            "task" => db.TaskJournalItems.Where(row => row.Id == seed.DoneTaskId).ExecuteUpdateAsync(set => set.SetProperty(row => row.Result, "Подтверждённый параллельный результат"), ct),
            "document" => db.ClientCaseDocuments.Where(row => row.Id == seed.Base.Base.Document.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, other), ct),
            "court" => db.ClientCaseCourts.Where(row => row.Id == seed.Court.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct),
            "deadline" => db.ClientCaseProceduralDeadlines.Where(row => row.Id == (command == "event-create" ? seed.SecondDeadline.Id : seed.Base.OwnDeadline.Id)).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, other), ct),
            "old-deadline" => db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.Base.OwnDeadline.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, other), ct),
            "related" => db.ClientCaseRelatedParties.Where(row => row.Id == seed.Related.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, other), ct),
            "party" => db.ClientCaseCourtParties.Where(row => row.Id == seed.Party.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, other), ct),
            "source-event" => db.ClientCaseCourtEvents.Where(row => row.Id == seed.SecondEvent.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, other), ct),
            _ => db.ClientCaseCourtEvents.Where(row => row.Id == seed.Base.Base.Event.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.ClientCaseId, other), ct)
        };
    }

    private sealed record CourtGraphSeed(CourtNullableSeed Base, ClientCaseCourt Court, ClientCaseRelatedParty Related,
        ClientCaseCourtParty Party, ClientCaseProceduralDeadline SecondDeadline, ClientCaseCourtEvent SecondEvent, int DoneTaskId);

    private static async Task<CourtGraphSeed> SeedCourtGraphAsync(PaymentContext db)
    {
        var seed = await SeedCourtNullableAsync(db);
        var recipient = await CreateUserAsync(db, "court-graph-recipient@synthetic.local", "Получатель судебных уведомлений");
        await db.ClientCases.Where(row => row.Id == seed.Base.Cases[0]).ExecuteUpdateAsync(set => set.SetProperty(row => row.ResponsibleUserId, recipient.Id));
        var court = new ClientCaseCourt { Name = "Суд для связанных действий", IsActive = true };
        var related = new ClientCaseRelatedParty { ClientCaseId = seed.Base.Cases[0], SourceType = "manual", DisplayName = "Свой участник" };
        var secondDeadline = new ClientCaseProceduralDeadline { ClientCaseId = seed.Base.Cases[0], DeadlineType = "motion", Title = "Второй свой срок", DueAt = DateTime.UnixEpoch.AddDays(9) };
        var secondEvent = new ClientCaseCourtEvent { ClientCaseId = seed.Base.Cases[0], Title = "Второе своё заседание", EventType = "hearing", ScheduledAt = DateTime.UnixEpoch.AddDays(10), MovementStatus = "scheduled" };
        db.ClientCaseCourts.Add(court);
        db.ClientCaseRelatedParties.Add(related);
        db.ClientCaseProceduralDeadlines.Add(secondDeadline);
        db.ClientCaseCourtEvents.Add(secondEvent);
        await db.SaveChangesAsync();
        var party = new ClientCaseCourtParty { ClientCaseId = seed.Base.Cases[0], RelatedPartyId = related.Id, DisplayName = "Сторона своего дела", Side = "claimant" };
        db.ClientCaseCourtParties.Add(party);
        await db.ClientCaseCourtEvents.Where(row => row.Id == seed.Base.Event.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.EventType, "hearing").SetProperty(row => row.MovementStatus, "scheduled"));
        await db.ClientCaseProceduralDeadlines.Where(row => row.Id == seed.OwnDeadline.Id).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.SourceType, "court-event").SetProperty(row => row.SourceId, seed.Base.Event.Id));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new(seed, court, related, party, secondDeadline, secondEvent, seed.OwnDeadline.TaskJournalItemId!.Value);
    }

    private static CasesV2Controller.UpsertClientCaseProceduralDeadlineRequest CourtGraphDeadlineRequest(CourtGraphSeed seed)
        => new()
        {
            DeadlineType = "motion",
            Title = "Проверенный процессуальный срок",
            DueAt = DateTime.UnixEpoch.AddDays(14),
            SourceType = "court-event",
            SourceId = seed.SecondEvent.Id,
            ClientCaseDocumentId = seed.Base.Base.Document.Id
        };

    private static CasesV2Controller.UpsertClientCaseCourtEventRequest CourtGraphEventRequest(CourtGraphSeed seed)
        => new()
        {
            EventType = "hearing",
            Title = "Проверенное событие",
            ScheduledAt = DateTime.UnixEpoch.AddDays(15),
            CourtId = seed.Court.Id,
            ClientCaseDocumentId = seed.Base.Base.Document.Id,
            ProceduralDeadlineId = seed.SecondDeadline.Id
        };

    private static async Task<IActionResult> InvokeCourtGraphAsync(CasesV2Controller controller, CourtGraphSeed seed, string command, CancellationToken ct)
    {
        var id = seed.Base.Base.Cases[0];
        return command switch
        {
            "deadline-create" => (await controller.CreateProceduralDeadline(id, CourtGraphDeadlineRequest(seed), ct)).Result!,
            "deadline-update" => (await controller.UpdateProceduralDeadline(id, seed.Base.OwnDeadline.Id, CourtGraphDeadlineRequest(seed), ct)).Result!,
            "deadline-close" => (await controller.CloseProceduralDeadline(id, seed.Base.OwnDeadline.Id, new() { Reason = "Закрыт сотрудником" }, ct)).Result!,
            "event-create" => (await controller.CreateCourtEvent(id, CourtGraphEventRequest(seed), ct)).Result!,
            "event-update" => (await controller.UpdateCourtEvent(id, seed.Base.Base.Event.Id, CourtGraphEventRequest(seed), ct)).Result!,
            "hearing" => (await controller.RecordHearingOutcome(id, seed.Base.Base.Event.Id, new() { Outcome = "considered", Note = "Сохранённый итог заседания" }, ct)).Result!,
            "party-create" => (await controller.CreateCourtParty(id, new() { RelatedPartyId = seed.Related.Id, DisplayName = "Добавленная сторона" }, ct)).Result!,
            "party-archive" => await controller.ArchiveCourtParty(id, seed.Party.Id, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }

    private static Task<HttpResponseMessage> SendCourtGraphHttpAsync(HttpClient http, CourtGraphSeed seed, string command, int id)
    {
        var path = $"/api/v2/cases/{id}";
        return command switch
        {
            "deadline-create" => http.PostAsJsonAsync(path + "/procedural-deadlines", CourtGraphDeadlineRequest(seed)),
            "deadline-update" => http.PutAsJsonAsync(path + $"/procedural-deadlines/{seed.Base.OwnDeadline.Id}", CourtGraphDeadlineRequest(seed)),
            "deadline-close" => http.PostAsJsonAsync(path + $"/procedural-deadlines/{seed.Base.OwnDeadline.Id}/close", new CasesV2Controller.CloseClientCaseProceduralDeadlineRequest { Reason = "Закрыт сотрудником" }),
            "event-create" => http.PostAsJsonAsync(path + "/court-events", CourtGraphEventRequest(seed)),
            "event-update" => http.PutAsJsonAsync(path + $"/court-events/{seed.Base.Base.Event.Id}", CourtGraphEventRequest(seed)),
            "hearing" => http.PutAsJsonAsync(path + $"/court-events/{seed.Base.Base.Event.Id}/hearing-outcome", new CasesV2Controller.RecordHearingOutcomeRequest { Outcome = "considered" }),
            "party-create" => http.PostAsJsonAsync(path + "/court-parties", new CasesV2Controller.UpsertClientCaseCourtPartyRequest { RelatedPartyId = seed.Related.Id, DisplayName = "Добавленная сторона" }),
            "party-archive" => http.DeleteAsync(path + $"/court-parties/{seed.Party.Id}"),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
    }

    private static void AssertCourtGraphSuccess(IActionResult result, string command)
    {
        if (command.EndsWith("create", StringComparison.Ordinal)) Assert.IsType<CreatedResult>(result);
        else if (command == "party-archive") Assert.IsType<NoContentResult>(result);
        else Assert.IsType<OkObjectResult>(result);
    }

    private static async Task<string> CourtGraphSnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new
    {
        Existing = await CourtWriteSnapshotAsync(db),
        Events = await db.ClientCaseCourtEvents.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Deadlines = await db.ClientCaseProceduralDeadlines.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Parties = await db.ClientCaseCourtParties.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Related = await db.ClientCaseRelatedParties.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Participants = await db.TaskJournalParticipants.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Notifications = await db.UserNotifications.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        Deliveries = await db.NotificationDeliveries.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
    });

    private static async Task<string> CourtGraphForeignSnapshotAsync(PaymentContext db, CourtGraphSeed seed) => JsonSerializer.Serialize(new
    {
        Event = await db.ClientCaseCourtEvents.AsNoTracking().SingleAsync(row => row.Id == seed.Base.OtherEvent.Id),
        Deadline = await db.ClientCaseProceduralDeadlines.AsNoTracking().SingleAsync(row => row.Id == seed.Base.OtherDeadline.Id),
        Tasks = await db.TaskJournalItems.AsNoTracking().Where(row => row.ClientCaseId == seed.Base.Base.Cases[1]).ToArrayAsync(),
        Payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync()
    });

    public static IEnumerable<object[]> CourtGraphForeignLinks() => from kind in new[] { "deadline-task", "deadline-document", "deadline-event", "event-document", "event-deadline", "party-related" }
                                                                    from postgres in new[] { false, true }
                                                                    select new object[] { kind, postgres };

    [Theory]
    [MemberData(nameof(CourtGraphForeignLinks))]
    public async Task CourtGraph_ForeignSelectedLinkIsRejectedWithoutMutation(string kind, bool postgres)
    {
        await using var database = postgres ? await modelTemplate.CreateDatabaseAsync() : null;
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database!.ConnectionString).Options)
            : await CreateContextAsync();
        var seed = await SeedCourtGraphAsync(db);
        var related = new ClientCaseRelatedParty { ClientCaseId = seed.Base.Base.Cases[1], DisplayName = "Чужой участник", SourceType = "manual" };
        db.ClientCaseRelatedParties.Add(related);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await CourtGraphSnapshotAsync(db);
        var controller = CreateController(db, seed.Base.Base.User);
        IActionResult result;
        if (kind.StartsWith("deadline-", StringComparison.Ordinal))
        {
            var request = CourtGraphDeadlineRequest(seed);
            if (kind == "deadline-task") request.TaskJournalItemId = seed.Base.OtherDeadline.TaskJournalItemId;
            if (kind == "deadline-document") request.ClientCaseDocumentId = seed.Base.Base.OtherDocument.Id;
            if (kind == "deadline-event") request.SourceId = seed.Base.OtherEvent.Id;
            result = (await controller.CreateProceduralDeadline(seed.Base.Base.Cases[0], request, default)).Result!;
        }
        else if (kind.StartsWith("event-", StringComparison.Ordinal))
        {
            var request = CourtGraphEventRequest(seed);
            if (kind == "event-document") request.ClientCaseDocumentId = seed.Base.Base.OtherDocument.Id;
            else request.ProceduralDeadlineId = seed.Base.OtherDeadline.Id;
            result = (await controller.CreateCourtEvent(seed.Base.Base.Cases[0], request, default)).Result!;
        }
        else result = (await controller.CreateCourtParty(seed.Base.Base.Cases[0], new() { RelatedPartyId = related.Id }, default)).Result!;
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal(before, await CourtGraphSnapshotAsync(db));
    }

    private sealed class CourtGraphSaveProbe : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int Saves { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Armed) Saves++;
            return ValueTask.FromResult(result);
        }
    }
}
