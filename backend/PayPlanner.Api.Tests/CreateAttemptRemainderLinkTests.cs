using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class CreateAttemptRemainderLinkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewRemainderCommitsOneStructuralPairAndReplaysWithoutChangingMoney(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertPairAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }

    [Theory]
    [InlineData("unpaid")]
    [InlineData("partial")]
    [InlineData("overpaid")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("scale")]
    [InlineData("status")]
    [InlineData("flag")]
    public async Task SourceMustBeAnActuallyClosedPaidPortion(string change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        var row = await db.Payments.SingleAsync();
        switch (change)
        {
            case "unpaid": row.PaidAmount = 0m; break;
            case "partial": row.Amount = 100m; break;
            case "overpaid": row.PaidAmount = 60m; break;
            case "zero": row.Amount = row.PaidAmount = 0m; break;
            case "negative": row.Amount = row.PaidAmount = -1m; break;
            case "scale": row.Amount = row.PaidAmount = 40.001m; break;
            case "status": row.Status = PaymentStatus.Processing; break;
            case "flag": row.IsPaid = false; break;
        }
        await db.SaveChangesAsync();
        await AssertRejectedAsync(db, source, LegacyRemainderConfirmationTests.Child(source));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("scale")]
    [InlineData("paid")]
    [InlineData("flag")]
    [InlineData("paidDate")]
    [InlineData("lastDate")]
    [InlineData("status")]
    [InlineData("cancelled")]
    public async Task ChildMustBeAPositiveUnpaidRemainder(string change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        var child = LegacyRemainderConfirmationTests.Child(source);
        switch (change)
        {
            case "zero": child.Amount = 0m; break;
            case "negative": child.Amount = -1m; break;
            case "scale": child.Amount = 60.001m; break;
            case "paid": child.PaidAmount = 1m; break;
            case "flag": child.IsPaid = true; break;
            case "paidDate": child.PaidDate = child.Date; break;
            case "lastDate": child.LastPaymentDate = child.Date; break;
            case "status": child.Status = PaymentStatus.Completed; break;
            case "cancelled": child.Status = PaymentStatus.Cancelled; break;
        }
        await AssertRejectedAsync(db, source, child);
    }

    [Fact]
    public async Task CombinedAmountOverflowIsRejectedBeforeWriting()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        var child = LegacyRemainderConfirmationTests.Child(source); child.Amount = decimal.MaxValue;
        await AssertRejectedAsync(db, source, child);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceResponsiblesAndCasesAreCopiedWithoutChangingExistingAssignments(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal);
        var clientCase = new ClientCase { ClientId = legal ? null : source.OwnerId, LegalEntityId = legal ? source.OwnerId : null };
        db.ClientCases.Add(clientCase); await db.SaveChangesAsync();
        db.InvoiceClientCases.Add(new() { PaymentId = source.PaymentId, ClientCaseId = clientCase.Id });
        db.PaymentResponsibles.Add(new() { PaymentId = source.PaymentId, UserId = source.ActorId }); await db.SaveChangesAsync();
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source);
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(LegacyRemainderConfirmationTests.Child(source))).Value);
        Assert.Equal(new[] { source.ActorId }, await db.PaymentResponsibles.Where(p => p.PaymentId == saved.Id).Select(p => p.UserId).ToArrayAsync());
        Assert.Equal(new[] { source.ActorId }, await db.PaymentResponsibles.Where(p => p.PaymentId == source.PaymentId).Select(p => p.UserId).ToArrayAsync());
        Assert.Equal(new[] { clientCase.Id }, await db.InvoiceClientCases.Where(p => p.PaymentId == saved.Id).Select(p => p.ClientCaseId).ToArrayAsync());
        Assert.Equal(new[] { clientCase.Id }, await db.InvoiceClientCases.Where(p => p.PaymentId == source.PaymentId).Select(p => p.ClientCaseId).ToArrayAsync());
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("status")]
    [InlineData("delete")]
    public async Task ExistingGuardsRecognizeTheNewPair(string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true; await db.SaveChangesAsync();
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source);
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(LegacyRemainderConfirmationTests.Child(source))).Value);
        foreach (var id in new[] { source.PaymentId, saved.Id })
        {
            var fixture = source with { PaymentId = id }; var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
            var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
            IActionResult result;
            if (operation == "delete") result = await controller.Delete(id, default);
            else if (operation == "status") result = await controller.UpdateStatus(id, new() { Status = "Cancelled" }, default);
            else { var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Amount += 1m; result = await PaymentInputStateTests.UpdateAsync(db, fixture, 0, model); }
            var conflict = Assert.IsType<ConflictObjectResult>(result);
            var message = JsonSerializer.SerializeToElement(conflict.Value).GetProperty("message").GetString();
            var expected = (id == source.PaymentId, operation) switch
            {
                (true, "amount") => PaymentRemainderTransferRules.SourceAmountUpdateBlockedMessage,
                (true, "status") => PaymentRemainderTransferRules.SourceStatusUpdateBlockedMessage,
                (true, _) => PaymentRemainderTransferRules.SourceDeleteBlockedMessage,
                (false, "amount") => PaymentRemainderTransferRules.RemainderAmountUpdateBlockedMessage,
                (false, "status") => PaymentRemainderTransferRules.RemainderCancelBlockedMessage,
                _ => PaymentRemainderTransferRules.RemainderDeleteBlockedMessage
            };
            Assert.Equal(expected, message); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRollbackRestoresTheCombinedAmountAndRemovesOnlyNewChild(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertRollbackAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SaveFailureRollsBackChildMarkersLinkAndAttempt(int failAt)
    {
        var fault = new FailSave(failAt);
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault);
        await AssertFailureAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false), fault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingAdjustmentKeepsTheCombinedAmountAndUpdatesBothMarkers(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertAdjustmentAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldCommittedFallbackIsReplayedWithoutCreatingHistoricalLinks(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal);
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source); var child = LegacyRemainderConfirmationTests.Child(source);
        Assert.IsType<CreatedResult>(await create(child));
        // Simulate a receipt committed by the older version; replay must not repair it implicitly.
        db.PaymentRemainderTransfers.RemoveRange(await db.PaymentRemainderTransfers.ToArrayAsync());
        foreach (var row in await db.Payments.ToArrayAsync()) row.SystemNotes = "Historical unstructured pair";
        await db.SaveChangesAsync(); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<OkObjectResult>(await create(LegacyRemainderConfirmationTests.Child(source)));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db)); Assert.Empty(await db.PaymentRemainderTransfers.ToArrayAsync());
    }

    internal static async Task AssertPairAsync(PaymentContext db, Fixture source)
    {
        var financialBefore = await SourceMoneyAsync(db, source.PaymentId);
        var balanceBefore = await db.ClientBalanceTransactions.CountAsync(); var cashBefore = await db.CashOrders.CountAsync();
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source);
        var model = LegacyRemainderConfirmationTests.Child(source); model.SystemNotes = "Human remainder note";
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(model)).Value);
        var link = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync();
        Assert.Equal(source.PaymentId, link.SourcePaymentId); Assert.Equal(saved.Id, link.RemainderPaymentId);
        Assert.Equal(100m, link.SourceOriginalAmount); Assert.Equal(40m, link.PaidAmount); Assert.Equal(60m, link.RemainderAmount); Assert.Equal(model.Date.Date, link.RemainderDate);
        var original = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        Assert.Contains(PaymentRemainderTransferRules.PartialRemainderSourceMarker, original.SystemNotes);
        Assert.True(PaymentRemainderTransferRules.TryReadMarker(saved.SystemNotes, out var marker)); Assert.Equal(source.PaymentId, marker.SourcePaymentId); Assert.Equal(100m, marker.SourceOriginalAmount);
        Assert.Contains("Human remainder note", saved.SystemNotes); Assert.Equal(financialBefore, await SourceMoneyAsync(db, source.PaymentId));
        Assert.Equal(balanceBefore, await db.ClientBalanceTransactions.CountAsync()); Assert.Equal(cashBefore, await db.CashOrders.CountAsync());
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var replay = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await create(model)).Value); Assert.Equal(saved.Id, replay.Id);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.IsType<ConflictObjectResult>(await (await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source))(LegacyRemainderConfirmationTests.Child(source)));
        Assert.Empty((await LegacyRemainderConfirmationTests.CandidatesAsync(db, source)).Items);
    }

    internal static async Task AssertRollbackAsync(PaymentContext db, Fixture source)
    {
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source);
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(LegacyRemainderConfirmationTests.Child(source))).Value);
        Assert.IsType<OkObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, source).RollbackPartialRemainderTransfer(saved.Id, default));
        var row = Assert.Single(await db.Payments.AsNoTracking().ToArrayAsync());
        Assert.Equal(source.PaymentId, row.Id); Assert.Equal(100m, row.Amount); Assert.Equal(40m, row.PaidAmount);
        Assert.Empty(await db.PaymentRemainderTransfers.ToArrayAsync()); Assert.DoesNotContain(PaymentRemainderTransferRules.PartialRemainderSourceMarker, row.SystemNotes);
        Assert.IsType<NotFoundResult>(await create(LegacyRemainderConfirmationTests.Child(source)));
    }

    internal static async Task AssertAdjustmentAsync(PaymentContext db, Fixture source)
    {
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source);
        var saved = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(LegacyRemainderConfirmationTests.Child(source))).Value);
        var nextDate = saved.Date.AddDays(7);
        Assert.IsType<OkObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, source).AdjustPartialRemainderTransfer(saved.Id,
            new() { PaidAmount = 50m, RemainderDate = nextDate }, default));
        var link = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync();
        Assert.Equal(100m, link.SourceOriginalAmount); Assert.Equal(50m, link.PaidAmount); Assert.Equal(50m, link.RemainderAmount); Assert.Equal(nextDate.Date, link.RemainderDate);
        var original = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        var child = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == saved.Id);
        Assert.Equal(50m, original.Amount); Assert.Equal(50m, original.PaidAmount); Assert.Equal(50m, child.Amount); Assert.Equal(0m, child.PaidAmount);
        Assert.True(PaymentRemainderTransferRules.TryReadMarker(child.SystemNotes, out var marker)); Assert.Equal(50m, marker.RemainderAmount);
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<OkObjectResult>(await create(LegacyRemainderConfirmationTests.Child(source)));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    private static async Task AssertRejectedAsync(PaymentContext db, Fixture source, Payment model)
    {
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        IActionResult result;
        if (model.PaidDate.HasValue || model.LastPaymentDate.HasValue)
        {
            // The older shared helper omits these optional fields; send the real
            // transport fields so the rejection is exercised before normalization.
            var command = JsonNode.Parse(PaymentInputStateTests.Command(model).GetRawText())!.AsObject();
            var primary = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await PaymentCreateFinancialBoundaryTests.Controller(db, source).GetById(source.PaymentId, default)).Value);
            command["sourceConfirmationToken"] = primary.RemainderConfirmationToken;
            if (model.PaidDate.HasValue) command["paidDate"] = model.PaidDate.Value;
            if (model.LastPaymentDate.HasValue) command["lastPaymentDate"] = model.LastPaymentDate.Value;
            var id = await db.PaymentCreateAttempts.Select(p => p.Id).SingleAsync();
            result = await PaymentCreateFinancialBoundaryTests.Controller(db, source).CommitCreateAttempt(id, JsonSerializer.SerializeToElement(command), default);
        }
        else result = await create(model);
        Assert.IsType<BadRequestObjectResult>(result); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
    }
    internal static async Task AssertFailureAsync(PaymentContext db, Fixture source, FailSave fault)
    {
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        fault.Enabled = true; await Assert.ThrowsAsync<IOException>(() => create(LegacyRemainderConfirmationTests.Child(source))); fault.Enabled = false;
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db)); Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }
    private static async Task<string> SourceMoneyAsync(PaymentContext db, int id)
        => JsonSerializer.Serialize(await db.Payments.AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Amount, p.PaidAmount, p.PaidDate, p.LastPaymentDate, p.Status, p.IsPaid, p.AdvanceReviewStatus, p.AdvanceReviewReason, p.ClientId, p.LegalEntityId }).SingleAsync());
    internal sealed class FailSave(int failAt) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        private int count;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Enabled && ++count == failAt) throw new IOException("Synthetic new remainder save failure"); return ValueTask.FromResult(result); }
    }
}

public sealed class CreateAttemptRemainderLinkPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePairIsAtomicAndReplayable(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await CreateAttemptRemainderLinkTests.AssertPairAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRollbackRecognizesTheNewPair(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await CreateAttemptRemainderLinkTests.AssertRollbackAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAdjustmentRecognizesTheNewPair(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await CreateAttemptRemainderLinkTests.AssertAdjustmentAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeSaveFailureDoesNotLeaveAnUnlinkedChild(int failAt)
    {
        await using var database = await template.CreateDatabaseAsync(); var fault = new CreateAttemptRemainderLinkTests.FailSave(failAt);
        await using var db = Open(database.ConnectionString, fault);
        await CreateAttemptRemainderLinkTests.AssertFailureAsync(db, await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false), fault);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCreatingRemainderExcludesAnotherAttemptAndEditor(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new HoldSave();
        await using var db = Open(database.ConnectionString, pause); await using var other = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal);
        var create = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source);
        var competing = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(other, source);
        var changed = await VersionedPaymentWriterTests.ModelAsync(other, source); changed.Amount = 50m;
        pause.Enabled = true; var pending = create(LegacyRemainderConfirmationTests.Child(source));
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ConflictObjectResult>(await competing(LegacyRemainderConfirmationTests.Child(source)).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<ConflictObjectResult>(await PaymentInputStateTests.UpdateAsync(other, source, 0, changed).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(other);
        Assert.IsType<ConflictObjectResult>(await competing(LegacyRemainderConfirmationTests.Child(source)));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(other));
        Assert.Single(await other.PaymentRemainderTransfers.ToArrayAsync());
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Enabled) { Enabled = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); } return result; }
    }
}
