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
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentReviewUpdateTests
{
    internal static readonly DateTime HistoricalReview = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static IEnumerable<object[]> ForgedCases()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var legal in new[] { false, true })
                foreach (var state in new[] { PaymentAdvanceReviewStatus.Refunded, PaymentAdvanceReviewStatus.Ignored, PaymentAdvanceReviewStatus.Allocated }) yield return [api, legal, state];
    }
    [Theory]
    [MemberData(nameof(ForgedCases))]
    public async Task ForgedOutcomeRequiresItsOwnAction(int api, bool legal, PaymentAdvanceReviewStatus state)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, legal);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = state; model.AdvanceReviewReason = "Fake operation";
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await UpdateAsync(db, source, api, model)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    public static IEnumerable<object[]> NeutralCases()
    {
        foreach (var api in new[] { 0, 1, 2 }) foreach (var legal in new[] { false, true })
                foreach (var state in new[] { PaymentAdvanceReviewStatus.None, PaymentAdvanceReviewStatus.RequiresDecision }) yield return [api, legal, state];
    }
    [Theory]
    [MemberData(nameof(NeutralCases))]
    public async Task LegacyNeutralInputDoesNotUndoTheDecision(int api, bool legal, PaymentAdvanceReviewStatus state)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, legal);
        await AssertNeutralAsync(db, source, api, state);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnchangedDecisionKeepsItsOriginalReasonAndReviewer(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewReason = "Forged replacement reason"; model.Notes = "Human note";
        Assert.IsType<OkObjectResult>(await UpdateAsync(db, source, api, model));
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Equal("Trusted decision", stored.AdvanceReviewReason); Assert.Equal(HistoricalReview, stored.AdvanceReviewedAt);
        Assert.Equal("Human note", stored.Notes);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IgnoredDecisionSurvivesNeutralMetadataSave(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        Assert.IsType<OkObjectResult>(await ReviewAsync(db, source, "ignore")); await StampAsync(db, source);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None; model.AdvanceReviewReason = "Fake reset";
        model.Notes = "Ignored annotation"; Assert.IsType<OkObjectResult>(await UpdateAsync(db, source, api, model));
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Equal(PaymentAdvanceReviewStatus.Ignored, stored.AdvanceReviewStatus); Assert.Equal("Trusted decision", stored.AdvanceReviewReason);
        Assert.Equal(HistoricalReview, stored.AdvanceReviewedAt); Assert.Equal("Ignored annotation", stored.Notes);
        Assert.Empty(await db.ClientBalanceTransactions.Where(t => t.PaymentId == source.PaymentId).ToArrayAsync());
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task ActualRefundAllowsNotesWithoutRecomputingMoney(int api, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, legal);
        await AssertRefundNotesAsync(db, source, api);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task UpdateConfirmationAcceptsAnUnreviewedReceipt(int api, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, legal);
        await MakePendingAsync(db, source); await AssertConfirmationAsync(db, source, api);
    }
    [Theory]
    [InlineData(0, "ignore")]
    [InlineData(1, "ignore")]
    [InlineData(2, "ignore")]
    [InlineData(0, "refund")]
    [InlineData(1, "refund")]
    [InlineData(2, "refund")]
    public async Task ReviewedReceiptCannotBeReopenedByOrdinarySave(int api, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        Assert.IsType<OkObjectResult>(await ReviewAsync(db, source, operation)); var model = await VersionedPaymentWriterTests.ModelAsync(db, source);
        model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await UpdateAsync(db, source, api, model)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData("ignore")]
    [InlineData("accept")]
    public async Task ExplicitNewDecisionUsesItsActualActorAndTime(string action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        await AssertNewDecisionStampAsync(db, source, action);
    }
    public static IEnumerable<object[]> Denials()
    {
        foreach (var api in new[] { 0, 1, 2 }) foreach (var denial in new[] { "permission", "inactive", "unapproved", "hidden" }) yield return [api, denial];
    }
    [Theory]
    [MemberData(nameof(Denials))]
    public async Task ActualEditAccessIsRequiredBeforeReviewInput(int api, string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Refunded;
        if (denial == "permission") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
        else if (denial == "inactive") (await db.Users.SingleAsync()).IsActive = false;
        else if (denial == "unapproved") (await db.Users.SingleAsync()).IsApproved = false;
        else (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).ViewScope = "own";
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var result = await UpdateAsync(db, source, api, model);
        if (denial == "permission") Assert.IsType<ForbidResult>(result); else if (denial == "hidden") Assert.IsType<NotFoundResult>(result); else Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task WireUpdateCannotAssertARefund(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Refunded;
        using var server = PaymentCreateGraphTests.Server(db, source); using var client = server.CreateClient(); client.DefaultRequestHeaders.Add("X-Fixture-User", source.ActorId.ToString());
        using var response = await client.PutAsJsonAsync(api == 0 ? $"/api/payments/{source.PaymentId}" : $"/api/v{api}/payments/{source.PaymentId}", model);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Single(await db.ClientBalanceTransactions.Where(t => t.PaymentId == source.PaymentId).ToArrayAsync());
    }
    [Theory]
    [InlineData(0, "amount")]
    [InlineData(0, "paid")]
    [InlineData(0, "account")]
    [InlineData(1, "amount")]
    [InlineData(1, "paid")]
    [InlineData(1, "account")]
    [InlineData(2, "amount")]
    [InlineData(2, "paid")]
    [InlineData(2, "account")]
    [InlineData(0, "last-date")]
    [InlineData(1, "last-date")]
    [InlineData(2, "last-date")]
    public async Task RefundHistoryDoesNotAllowFinancialEdits(int api, string field)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        Assert.IsType<OkObjectResult>(await ReviewAsync(db, source, "refund")); var model = await VersionedPaymentWriterTests.ModelAsync(db, source);
        if (field == "amount") model.Amount = 101m;
        else if (field == "paid") model.PaidAmount = 99m;
        else if (field == "last-date") model.LastPaymentDate = new DateTime(1999, 1, 1); else model.Account = "invented-invoice";
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await UpdateAsync(db, source, api, model)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnknownReviewEnumCannotBeWritten(int api)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SourceAsync(db, false);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = (PaymentAdvanceReviewStatus)999;
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await UpdateAsync(db, source, api, model)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedConfirmationRollsBackDecisionAndCredit(int api)
    {
        var fault = new FailSave(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault);
        var source = await SourceAsync(db, false); await MakePendingAsync(db, source); await AssertFailureAsync(db, source, api, fault);
    }
    internal static async Task<Fixture> SourceAsync(PaymentContext db, bool legal)
    {
        var source = (await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal)).Source;
        await StampAsync(db, source); return source;
    }
    internal static async Task StampAsync(PaymentContext db, Fixture source)
    {
        var row = await db.Payments.SingleAsync(p => p.Id == source.PaymentId); row.AdvanceReviewedAt = HistoricalReview;
        row.AdvanceReviewedByUserId = source.ActorId; row.AdvanceReviewedByUserName = "Historical reviewer"; row.AdvanceReviewReason = "Trusted decision"; await db.SaveChangesAsync();
    }
    internal static async Task MakePendingAsync(PaymentContext db, Fixture source)
    {
        (await db.Payments.SingleAsync(p => p.Id == source.PaymentId)).AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision;
        db.ClientBalanceTransactions.RemoveRange(await db.ClientBalanceTransactions.Where(t => t.PaymentId == source.PaymentId).ToArrayAsync()); await db.SaveChangesAsync();
    }
    internal static Task<IActionResult> UpdateAsync(PaymentContext db, Fixture source, int api, Payment model)
        => PaymentInputStateTests.UpdateAsync(db, source, api, model);
    internal static Task<IActionResult> ReviewAsync(PaymentContext db, Fixture source, string action)
        => PaymentAdvanceHistoryBoundaryTests.WriteAsync(db, source, action);
    internal static async Task AssertNeutralAsync(PaymentContext db, Fixture source, int api, PaymentAdvanceReviewStatus status)
    {
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = status; model.AdvanceReviewReason = "Forged reset"; model.Notes = "Legitimate note";
        var before = await LedgerAsync(db); Assert.IsType<OkObjectResult>(await UpdateAsync(db, source, api, model));
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, stored.AdvanceReviewStatus); Assert.Equal("Trusted decision", stored.AdvanceReviewReason);
        Assert.Equal(HistoricalReview, stored.AdvanceReviewedAt); Assert.Equal("Historical reviewer", stored.AdvanceReviewedByUserName); Assert.Equal("Legitimate note", stored.Notes);
        Assert.Equal(before, await LedgerAsync(db));
    }
    internal static async Task AssertRefundNotesAsync(PaymentContext db, Fixture source, int api)
    {
        Assert.IsType<OkObjectResult>(await ReviewAsync(db, source, "refund")); var before = await LedgerAsync(db);
        var row = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None; model.AdvanceReviewReason = "Forged reset";
        model.Notes = "Refund note"; Assert.IsType<OkObjectResult>(await UpdateAsync(db, source, api, model));
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Equal(PaymentAdvanceReviewStatus.Refunded, stored.AdvanceReviewStatus); Assert.Equal(row.AdvanceReviewReason, stored.AdvanceReviewReason);
        Assert.Equal(row.AdvanceReviewedAt, stored.AdvanceReviewedAt); Assert.Equal(row.AdvanceReviewedByUserId, stored.AdvanceReviewedByUserId);
        Assert.Equal(row.LastPaymentDate, stored.LastPaymentDate); Assert.Equal(row.PaidDate, stored.PaidDate);
        Assert.Equal(row.Amount, stored.Amount); Assert.Equal(row.PaidAmount, stored.PaidAmount);
        Assert.Equal("Refund note", stored.Notes); Assert.Equal(before, await LedgerAsync(db));
    }
    internal static async Task AssertConfirmationAsync(PaymentContext db, Fixture source, int api)
    {
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        model.AdvanceReviewReason = "Explicit update confirmation"; var before = DateTime.UtcNow;
        Assert.IsType<OkObjectResult>(await UpdateAsync(db, source, api, model)); var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, stored.AdvanceReviewStatus); Assert.Equal("Explicit update confirmation", stored.AdvanceReviewReason);
        Assert.Equal(source.ActorId, stored.AdvanceReviewedByUserId); Assert.NotEqual("Historical reviewer", stored.AdvanceReviewedByUserName);
        Assert.InRange(stored.AdvanceReviewedAt!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        var credit = Assert.Single(await db.ClientBalanceTransactions.Where(t => t.PaymentId == source.PaymentId).ToArrayAsync());
        Assert.Equal(100m, credit.Amount); Assert.Equal(ClientBalanceTransactionType.UnallocatedAdvance, credit.Type); Assert.Equal(model.ClientId, credit.ClientId); Assert.Equal(model.LegalEntityId, credit.LegalEntityId);
    }
    internal static async Task AssertNewDecisionStampAsync(PaymentContext db, Fixture source, string action)
    {
        if (action == "accept") { Assert.IsType<OkObjectResult>(await ReviewAsync(db, source, "ignore")); await StampAsync(db, source); }
        var actor = await db.Users.SingleAsync(u => u.Id == source.ActorId);
        var reviewer = new User { Email = "new-reviewer@test.local", FullName = "Current reviewer", PasswordHash = "unused", RoleId = actor.RoleId, IsActive = true, IsApproved = true, IsEmployee = true };
        db.Users.Add(reviewer); await db.SaveChangesAsync(); var fresh = source with { ActorId = reviewer.Id }; var before = DateTime.UtcNow;
        Assert.IsType<OkObjectResult>(await ReviewAsync(db, fresh, action)); var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Equal(reviewer.Id, stored.AdvanceReviewedByUserId); Assert.InRange(stored.AdvanceReviewedAt!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(1));
        var stamp = stored.AdvanceReviewedAt; Assert.IsType<OkObjectResult>(await ReviewAsync(db, fresh, action));
        Assert.Equal(stamp, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId)).AdvanceReviewedAt);
    }
    internal static async Task AssertFailureAsync(PaymentContext db, Fixture source, int api, FailSave fault)
    {
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance;
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => UpdateAsync(db, source, api, model)); fault.Enabled = false;
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }
    internal static async Task<string> LedgerAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            balance = await db.ClientBalanceTransactions.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync(),
            money = await db.MoneyOperations.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync(),
            cash = await db.CashOrders.AsNoTracking().OrderBy(t => t.Id).ToArrayAsync()
        });
    internal sealed class FailSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Enabled) throw new IOException("Synthetic review update failure"); return ValueTask.FromResult(result); }
    }
}

public sealed class PaymentReviewUpdatePostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> ForgedCases()
    {
        foreach (var api in new[] { 0, 1, 2 }) foreach (var state in new[] { PaymentAdvanceReviewStatus.Refunded, PaymentAdvanceReviewStatus.Ignored, PaymentAdvanceReviewStatus.Allocated }) yield return [api, state];
    }
    [Theory]
    [MemberData(nameof(ForgedCases))]
    public async Task NativeForgedReviewCannotChangeMoney(int api, PaymentAdvanceReviewStatus state)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await PaymentReviewUpdateTests.SourceAsync(db, false); var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.AdvanceReviewStatus = state;
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await PaymentReviewUpdateTests.UpdateAsync(db, source, api, model));
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeNeutralInputPreservesAcceptedSource(int api)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await PaymentReviewUpdateTests.SourceAsync(db, true);
        await PaymentReviewUpdateTests.AssertNeutralAsync(db, source, api, PaymentAdvanceReviewStatus.None);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task NativePendingConfirmationCreatesOneRealAdvance(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await PaymentReviewUpdateTests.SourceAsync(db, legal); await PaymentReviewUpdateTests.MakePendingAsync(db, source);
        await PaymentReviewUpdateTests.AssertConfirmationAsync(db, source, api);
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task NativeRefundNotesPreserveTheLedgerAndMoneyOperation(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await PaymentReviewUpdateTests.SourceAsync(db, legal); await PaymentReviewUpdateTests.AssertRefundNotesAsync(db, source, api);
    }
    [Theory]
    [InlineData("accept")]
    [InlineData("ignore")]
    public async Task NativeNewDecisionRecordsTheActualReviewer(string action)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await PaymentReviewUpdateTests.SourceAsync(db, false); await PaymentReviewUpdateTests.AssertNewDecisionStampAsync(db, source, action);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NativeFailedConfirmationDoesNotChangeTheDecision(int api)
    {
        await using var database = await template.CreateDatabaseAsync(); var fault = new PaymentReviewUpdateTests.FailSave();
        await using var db = Open(database.ConnectionString, fault); var source = await PaymentReviewUpdateTests.SourceAsync(db, false);
        await PaymentReviewUpdateTests.MakePendingAsync(db, source); await PaymentReviewUpdateTests.AssertFailureAsync(db, source, api, fault);
    }
    public static IEnumerable<object[]> Races()
    {
        foreach (var api in new[] { 0, 1, 2 }) foreach (var action in new[] { "ignore", "refund" }) yield return [api, action];
    }
    [Theory]
    [MemberData(nameof(Races))]
    public async Task CommittedDecisionCannotBeOverwrittenByAStaleEditor(int api, string action)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new BeforePaymentLock();
        await using var editing = Open(database.ConnectionString, pause); await using var reviewing = Open(database.ConnectionString);
        var source = await PaymentReviewUpdateTests.SourceAsync(editing, false); var model = await VersionedPaymentWriterTests.ModelAsync(editing, source);
        model.Notes = "Stale editor"; pause.Enabled = true; var pending = PaymentReviewUpdateTests.UpdateAsync(editing, source, api, model);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<OkObjectResult>(await PaymentReviewUpdateTests.ReviewAsync(reviewing, source, action));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing);
        Assert.IsType<BadRequestObjectResult>(await PaymentReviewUpdateTests.UpdateAsync(editing, source, api, model));
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing));
        Assert.Equal(action == "ignore" ? PaymentAdvanceReviewStatus.Ignored : PaymentAdvanceReviewStatus.Refunded,
            (await reviewing.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId)).AdvanceReviewStatus);
    }
    [Theory]
    [MemberData(nameof(Races))]
    public async Task EditingLocksTheDecisionUntilItsFinancialCommit(int api, string action)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new BeforeSave();
        await using var editing = Open(database.ConnectionString, pause); await using var reviewing = Open(database.ConnectionString);
        var source = await PaymentReviewUpdateTests.SourceAsync(editing, true); var model = await VersionedPaymentWriterTests.ModelAsync(editing, source);
        model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.None; model.Notes = "Legitimate current note"; pause.Enabled = true;
        var pending = PaymentReviewUpdateTests.UpdateAsync(editing, source, api, model);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ConflictObjectResult>(await PaymentReviewUpdateTests.ReviewAsync(reviewing, source, action).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var stored = await reviewing.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, stored.AdvanceReviewStatus); Assert.Equal("Legitimate current note", stored.Notes);
        Assert.IsType<OkObjectResult>(await PaymentReviewUpdateTests.ReviewAsync(reviewing, source, action));
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class BeforePaymentLock : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && command.CommandText.Contains("FROM \"Payments\"", StringComparison.Ordinal) && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
    private sealed class BeforeSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled) { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
}
