using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Payments;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentInputStateTests
{
    public static IEnumerable<object[]> BaselineCases()
    {
        foreach (var api in new[] { 0, 1, 2, 3 })
            foreach (var input in new[] { "marker", "refunded" }) yield return [api, input];
    }

    [Theory]
    [MemberData(nameof(BaselineCases))]
    public async Task ForgedCreationStateIsRejectedBeforeAnyWrite(int api, string input)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var create = await RouteAsync(db, fixture, api); var model = PaymentFor(fixture);
        if (input == "marker") model.SystemNotes = PaymentRemainderTransferRules.BuildMarker(fixture.PaymentId, 100m, 40m, 60m, model.Date);
        else model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Refunded;
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await create(model));
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
        await AssertNoAttemptCommitAsync(db, api);
    }

    public static IEnumerable<object[]> MarkerCases()
    {
        foreach (var api in new[] { 0, 1, 2, 3 })
            foreach (var marker in new[] { "[pp:installment-payment]", "[pp:partial-remainder-source]", "[pp:invoice-items]", "[pp:spreadsheet-import]", "[PayPlanner:TaskContractorPayment]:1", "[PayPlanner:ExpenseInvoiceCaseBackfill]", "[PP:future]", "[payplanner:future]", "[pp:" }) yield return [api, marker];
    }

    [Theory]
    [MemberData(nameof(MarkerCases))]
    public async Task NewPaymentCannotSupplyReservedNoteNamespaces(int api, string marker)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var create = await RouteAsync(db, fixture, api); var model = PaymentFor(fixture);
        model.SystemNotes = $"Human preface\r\n\uFEFF  {marker} {{\"SourcePaymentId\":{fixture.PaymentId},\"ToPay\":999}}\r\nTail";
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await create(model)); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
        await AssertNoAttemptCommitAsync(db, api);
    }

    [Theory]
    [InlineData(0, PaymentAdvanceReviewStatus.Ignored)]
    [InlineData(0, PaymentAdvanceReviewStatus.Allocated)]
    [InlineData(1, PaymentAdvanceReviewStatus.Ignored)]
    [InlineData(1, PaymentAdvanceReviewStatus.Allocated)]
    [InlineData(2, PaymentAdvanceReviewStatus.Ignored)]
    [InlineData(2, PaymentAdvanceReviewStatus.Allocated)]
    [InlineData(3, PaymentAdvanceReviewStatus.Ignored)]
    [InlineData(3, PaymentAdvanceReviewStatus.Allocated)]
    public async Task NewPaymentDoesNotClaimAnExistingReviewOutcome(int api, PaymentAdvanceReviewStatus status)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var create = await RouteAsync(db, fixture, api); var model = PaidAdvance(fixture); model.AdvanceReviewStatus = status;
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await create(model)); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
        await AssertNoAttemptCommitAsync(db, api);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task InitialAdvanceConfirmationUsesCreatePermissionAndCreatesRealCredit(int api, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        await AssertAdvanceAsync(db, fixture, api);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PendingAdvanceAndHumanNotesStayAvailable(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var create = await RouteAsync(db, fixture, api); var model = PaidAdvance(fixture); model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
        const string note = "Частичная оплата: 40 / 100 • Остаток 60 → 02.11.2026\nHuman pp: text without bracket";
        model.SystemNotes = note; model.RescheduleCount = 3;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Contains(note, stored.SystemNotes); Assert.Equal(3, stored.RescheduleCount);
        Assert.Equal(PaymentAdvanceReviewStatus.RequiresDecision, stored.AdvanceReviewStatus); Assert.Null(stored.AdvanceReviewedAt);
        Assert.DoesNotContain(await db.ClientBalanceTransactions.ToArrayAsync(), t => t.PaymentId == saved.Id);
    }

    public static IEnumerable<object[]> Denials()
    {
        foreach (var api in new[] { 0, 1, 2, 3 })
            foreach (var denial in new[] { "create", "inactive", "unapproved" }) yield return [api, denial];
    }

    [Theory]
    [MemberData(nameof(Denials))]
    public async Task ActualActorAndCreatePermissionPrecedeTheInputError(int api, string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var create = await RouteAsync(db, fixture, api);
        if (denial == "create") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanCreate = false;
        else if (denial == "inactive") (await db.Users.SingleAsync()).IsActive = false;
        else (await db.Users.SingleAsync()).IsApproved = false;
        await db.SaveChangesAsync(); var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        var model = PaymentFor(fixture); model.SystemNotes = "[pp:installment-payment] {}";
        var response = await create(model);
        if (api == 3 || denial == "create") Assert.IsType<ForbidResult>(response); else Assert.IsType<UnauthorizedResult>(response);
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); await AssertNoAttemptCommitAsync(db, api);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UpdateCannotIntroduceNewStructuralNotes(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.SystemNotes = "[pp:installment-payment] {\"ToPay\":999}";
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await UpdateAsync(db, fixture, api, model));
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task ExistingImportNotesCanBeEchoedButNotForged(int api, bool change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        await AssertHistoricalNotesAsync(db, fixture, api, change);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WireRejectsForgedServerNotes(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        using var server = PaymentCreateGraphTests.Server(db, fixture); using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fixture-User", fixture.ActorId.ToString());
        var model = PaymentFor(fixture); model.SystemNotes = "[pp:invoice-items] []";
        using var response = await client.PostAsJsonAsync(api == 0 ? "/api/payments" : $"/api/v{api}/payments", model);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(1, await db.Payments.CountAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AdvanceCreditRollsBackIfTheSecondSaveFails(int api)
    {
        var fault = new PaymentCreateSellerSelectionTests.FailSecondSave();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); var fixture = await SeedAsync(db, false);
        await AssertRollbackAsync(db, fixture, api, fault);
    }

    [Fact]
    public async Task CommittedHistoricalCommandCanStillReplayWithoutCreatingOrRewriting()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        await AssertHistoricalReplayAsync(db, fixture);
    }

    internal static Payment PaidAdvance(Fixture fixture)
    {
        var model = PaymentFor(fixture); model.PaidAmount = 100m; model.IsPaid = true; model.Status = PaymentStatus.Completed;
        model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance; return model;
    }

    internal static async Task<Func<Payment, Task<IActionResult>>> RouteAsync(PaymentContext db, Fixture fixture, int api)
    {
        if (api != 3) return model => PaymentCreateGraphTests.CreateAsync(db, fixture, api, model);
        var controller = PaymentCreateFinancialBoundaryTests.Controller(db, fixture); var id = await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller);
        return model => controller.CommitCreateAttempt(id, Command(model), default);
    }

    internal static JsonElement Command(Payment model)
    {
        var node = JsonNode.Parse(PaymentCreateFinancialBoundaryTests.Command(model).GetRawText())!.AsObject();
        node["advanceReviewStatus"] = model.AdvanceReviewStatus.ToString(); node["advanceReviewReason"] = model.AdvanceReviewReason;
        return JsonSerializer.SerializeToElement(node);
    }

    internal static Task<IActionResult> UpdateAsync(PaymentContext db, Fixture fixture, int api, Payment model)
        => api == 0 ? BalanceApplicationPaymentWriterTests.Controller(db, fixture).Update(fixture.PaymentId, model, default)
            : VersionedPaymentWriterTests.WriteAsync(db, fixture, api, "update", model);

    private static async Task AssertNoAttemptCommitAsync(PaymentContext db, int api)
    {
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
        if (api == 3) { var attempt = await db.PaymentCreateAttempts.AsNoTracking().SingleAsync(); Assert.Equal(PaymentCreateAttempt.Draft, attempt.State); Assert.Null(attempt.CanonicalPaymentId); }
    }

    internal static async Task AssertAdvanceAsync(PaymentContext db, Fixture fixture, int api)
    {
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false; await db.SaveChangesAsync();
        var create = await RouteAsync(db, fixture, api); var model = PaidAdvance(fixture); var before = DateTime.UtcNow;
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(model)).Value);
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, stored.AdvanceReviewStatus); Assert.Equal(fixture.ActorId, stored.AdvanceReviewedByUserId);
        Assert.InRange(stored.AdvanceReviewedAt!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        var credit = Assert.Single(await db.ClientBalanceTransactions.Where(t => t.PaymentId == saved.Id).ToArrayAsync());
        Assert.Equal(ClientBalanceTransactionType.UnallocatedAdvance, credit.Type); Assert.Equal(ClientBalanceDirection.Credit, credit.Direction); Assert.Equal(100m, credit.Amount);
        Assert.Equal(model.ClientId, credit.ClientId); Assert.Equal(model.LegalEntityId, credit.LegalEntityId);
        if (api == 3)
        {
            Assert.Equal(saved.Id, Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await create(model)).Value).Id);
            Assert.Single(await db.ClientBalanceTransactions.Where(t => t.PaymentId == saved.Id).ToArrayAsync());
        }
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        Assert.IsType<ForbidResult>(await controller.IgnoreAdvance(saved.Id, new() { Reason = "Not allowed after creation" }, default));
        Assert.Equal(100m, (await db.ClientBalanceTransactions.SingleAsync(t => t.PaymentId == saved.Id)).Amount);
    }

    internal static async Task AssertHistoricalNotesAsync(PaymentContext db, Fixture fixture, int api, bool change)
    {
        const string marker = "[pp:spreadsheet-import] trusted-source";
        var row = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); row.SystemNotes = marker; await db.SaveChangesAsync();
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture);
        model.SystemNotes = change ? marker.Replace("trusted-source", "changed-source") : marker + "\nOrdinary human note"; model.Notes = "Permitted note";
        var before = await PaymentCreateGraphTests.SnapshotAsync(db); var result = await UpdateAsync(db, fixture, api, model);
        if (change) { Assert.IsType<BadRequestObjectResult>(result); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); }
        else
        {
            Assert.IsType<OkObjectResult>(result); var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
            Assert.Contains(marker, stored.SystemNotes); Assert.Equal(model.Notes, stored.Notes);
            if (api != 1) Assert.Contains("Ordinary human note", stored.SystemNotes);
        }
    }

    internal static async Task AssertRollbackAsync(PaymentContext db, Fixture fixture, int api, PaymentCreateSellerSelectionTests.FailSecondSave fault)
    {
        var create = await RouteAsync(db, fixture, api); var before = await PaymentCreateGraphTests.SnapshotAsync(db); fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => create(PaidAdvance(fixture))); fault.Enabled = false;
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); await AssertNoAttemptCommitAsync(db, api);
    }

    internal static async Task AssertHistoricalReplayAsync(PaymentContext db, Fixture fixture)
    {
        var controller = PaymentCreateFinancialBoundaryTests.Controller(db, fixture); var id = await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller);
        var model = PaymentFor(fixture); var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await controller.CommitCreateAttempt(id, Command(model), default)).Value);
        // An already committed older command is a receipt, never a new admission.
        model.SystemNotes = "[pp:spreadsheet-import] historical"; var body = Command(model);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }; options.Converters.Add(new JsonStringEnumConverter());
        Assert.True(PaymentCreateCommand.TryRead(body, options, out _, out var identity, out _));
        (await db.PaymentCreateAttempts.SingleAsync()).PayloadIdentity = identity;
        (await db.Payments.SingleAsync(p => p.Id == saved.Id)).SystemNotes = model.SystemNotes; await db.SaveChangesAsync();
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        var replay = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.CommitCreateAttempt(id, body, default)).Value);
        Assert.Equal(saved.Id, replay.Id); Assert.Equal(model.SystemNotes, replay.SystemNotes);
        Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db)); Assert.Equal(2, await db.Payments.CountAsync());
    }
}

public sealed class PaymentInputStatePostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> InvalidCases()
    {
        foreach (var api in new[] { 0, 1, 2, 3 })
            foreach (var invalid in new[] { "marker", "refunded", "ignored" }) yield return [api, invalid];
    }
    [Theory]
    [MemberData(nameof(InvalidCases))]
    public async Task NativeRejectsFabricatedStateWithoutChanges(int api, string invalid)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); var create = await PaymentInputStateTests.RouteAsync(db, fixture, api);
        var model = PaymentInputStateTests.PaidAdvance(fixture);
        if (invalid == "marker") model.SystemNotes = "[pp:partial-remainder-source] {\"SourcePaymentId\":1}";
        else model.AdvanceReviewStatus = invalid == "refunded" ? PaymentAdvanceReviewStatus.Refunded : PaymentAdvanceReviewStatus.Ignored;
        var before = await PaymentCreateGraphTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await create(model)); Assert.Equal(before, await PaymentCreateGraphTests.SnapshotAsync(db));
        Assert.Null(db.Database.CurrentTransaction);
        if (api == 3) Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.SingleAsync()).State);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task NativeAcceptedAdvanceHasOneCreditAndRealReviewer(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legal); await PaymentInputStateTests.AssertAdvanceAsync(db, fixture, api);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task NativeHistoricalImportNotesStayImmutable(int api, bool change)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); await PaymentInputStateTests.AssertHistoricalNotesAsync(db, fixture, api, change);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeAdvanceIsNotPartiallyCommittedAfterSaveFailure(int api)
    {
        await using var database = await template.CreateDatabaseAsync(); var fault = new PaymentCreateSellerSelectionTests.FailSecondSave();
        await using var db = Open(database.ConnectionString, fault); var fixture = await SeedAsync(db, false);
        await PaymentInputStateTests.AssertRollbackAsync(db, fixture, api, fault);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeConcurrentReaderCannotSeeHalfAnAcceptedAdvance(int api)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new BeforeSecondSave();
        await using var creating = Open(database.ConnectionString, pause); await using var reading = Open(database.ConnectionString);
        var fixture = await SeedAsync(creating, false); var create = await PaymentInputStateTests.RouteAsync(creating, fixture, api);
        var model = PaymentInputStateTests.PaidAdvance(fixture); pause.Enabled = true; var request = create(model);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); Assert.True(model.Id > 0 || api == 3);
            // The command protocol creates its own parsed model. In either path,
            // the reader sees only the original fixture payment and source credit.
            Assert.Equal(1, await reading.Payments.CountAsync());
            Assert.Single(await reading.ClientBalanceTransactions.ToArrayAsync());
        }
        finally { pause.Release.TrySetResult(); }
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await request.WaitAsync(TimeSpan.FromSeconds(20))).Value);
        Assert.Equal(2, await reading.Payments.CountAsync());
        Assert.Equal(100m, (await reading.ClientBalanceTransactions.SingleAsync(t => t.PaymentId == saved.Id)).Amount);
    }

    [Fact]
    public async Task NativeAlreadyCommittedHistoricalReceiptStillReplays()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, false); await PaymentInputStateTests.AssertHistoricalReplayAsync(db, fixture);
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class BeforeSecondSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        private int count;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && ++count == 2) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
}
