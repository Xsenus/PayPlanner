using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Services;
using System.Text.Json;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class LegacyRemainderConfirmationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyTextAloneCannotAuthorizeANewDebt(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, legal);
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, source).TransferExistingPartialRemainder(
            source.PaymentId, new() { RemainderDate = new DateTime(2026, 11, 15) }, default));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextWithAnotherPaidAmountCannotOfferARemainder(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, legal);
        (await db.Payments.SingleAsync()).SystemNotes = "Частичная оплата: 10 / 100 • Остаток 90 → 15.11.2026"; await db.SaveChangesAsync();
        var response = await CandidatesAsync(db, source); Assert.Empty(response.Items); Assert.Equal(0, response.TotalRemainderAmount);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ExplicitConfirmationCreatesOnlyTheChosenRemainder(bool legal, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, legal);
        if (!legacy) { (await db.Payments.SingleAsync()).Amount = 100m; await db.SaveChangesAsync(); }
        await AssertTransferAsync(db, source, legacy);
    }

    public static IEnumerable<object[]> Changes()
    {
        foreach (var legal in new[] { false, true }) foreach (var change in new[] { "amount", "paid", "notes", "type", "owner", "date", "seller" }) yield return [legal, change];
    }
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AChangedPreviewCannotAuthorizeTransfer(bool legal, string change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, legal);
        await AssertStaleAsync(db, source, change);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("amount")]
    [InlineData("paid")]
    [InlineData("token")]
    [InlineData("fraction")]
    [InlineData("negative")]
    [InlineData("zero-remainder")]
    [InlineData("wrong-expected")]
    public async Task PartialOrInvalidConfirmationIsRejected(string invalid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, false);
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items));
        switch (invalid)
        {
            case "original": request.OriginalAmount = null; break;
            case "amount": request.ExpectedAmount = null; break;
            case "paid": request.ExpectedPaidAmount = null; break;
            case "token": request.ConfirmationToken = null; break;
            case "fraction": request.OriginalAmount = 100.001m; break;
            case "negative": request.ExpectedAmount = -1m; break;
            case "zero-remainder": request.OriginalAmount = 40m; break;
            case "wrong-expected": request.ExpectedPaidAmount = 39m; break;
        }
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var result = await TransferAsync(db, source, request);
        if (invalid == "wrong-expected") Assert.IsType<ConflictObjectResult>(result); else Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredDebtKeepsTheDateOnlyContract(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, legal);
        (await db.Payments.SingleAsync()).Amount = 100m; await db.SaveChangesAsync();
        Assert.True(Result(await TransferAsync(db, source, new() { RemainderDate = new DateTime(2026, 11, 15) })).CreatedRemainder);
    }

    [Fact]
    public async Task ExplicitAmountDoesNotOverrideAnExistingStoredDebt()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, false);
        (await db.Payments.SingleAsync()).Amount = 100m; await db.SaveChangesAsync();
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items)); request.OriginalAmount = 1_000_000m;
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await TransferAsync(db, source, request)); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResponseReplayReturnsTheExistingPairWithoutChangingIt(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, legal);
        await AssertReplayAsync(db, source);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("inactive")]
    [InlineData("unapproved")]
    [InlineData("hidden")]
    public async Task AccessIsCheckedBeforeTheConfirmation(string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, false);
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items));
        if (denial == "permission") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
        else if (denial == "inactive") (await db.Users.SingleAsync()).IsActive = false;
        else if (denial == "unapproved") (await db.Users.SingleAsync()).IsApproved = false;
        else (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).ViewScope = "own";
        await db.SaveChangesAsync(); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var result = await TransferAsync(db, source, request);
        if (denial == "permission") Assert.IsType<ForbidResult>(result); else if (denial == "hidden") Assert.IsType<NotFoundResult>(result); else Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedFallbackPreventsAnotherRemainderWithoutRewritingItsHistory(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, legal);
        await AssertCommittedFallbackAsync(db, source);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("cancelled")]
    [InlineData("missing-child")]
    public async Task AnUncommittedOrDeletedOutcomeDoesNotHideTheCandidate(string kind)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, false);
        await AddCommittedFallbackAsync(db, source, missingChild: true);
        var attempt = await db.PaymentCreateAttempts.SingleAsync();
        if (kind != "missing-child")
        {
            attempt.State = kind == "draft" ? PaymentCreateAttempt.Draft : PaymentCreateAttempt.Cancelled;
            attempt.CanonicalPaymentId = null; attempt.PayloadIdentity = null; attempt.CommittedAtUtc = null;
            if (kind == "cancelled") attempt.CancelledAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Single((await CandidatesAsync(db, source)).Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingParentNeedsEditOrAnOwnedNewPrimaryReceipt(bool ownReceipt)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, false);
        if (ownReceipt)
        {
            var create = await PaymentInputStateTests.RouteAsync(db, source, 3); var model = PaymentFor(source);
            model.Amount = 40m; model.PaidAmount = 40m; model.IsPaid = true; model.Status = PaymentStatus.Completed;
            source = source with { PaymentId = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await create(model)).Value).Id };
        }
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false; await db.SaveChangesAsync();
        var operation = await RemainderAttemptAsync(db, source); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var result = await operation(Child(source));
        if (ownReceipt) Assert.IsType<CreatedResult>(result);
        else { Assert.IsType<ForbidResult>(result); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherAttemptCannotDuplicateOrRebindTheSameParent(bool mismatch)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, false);
        var operation = await RemainderAttemptAsync(db, source); var child = Child(source);
        if (mismatch)
        {
            var owner = new Client { Name = "Other intended owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); child.ClientId = owner.Id;
        }
        else Assert.IsType<CreatedResult>(await operation(child));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        if (!mismatch) operation = await RemainderAttemptAsync(db, source);
        Assert.IsType<ConflictObjectResult>(await operation(child)); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailedTransferRollsBackBothRowsAndItsLink(int save)
    {
        var fault = new FailSave(save); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault);
        var source = await SeedLegacyAsync(db, false); await AssertFailureAsync(db, source, fault);
    }

    [Fact]
    public async Task ReturnedAdvanceCannotRecoverANewDebtFromItsOldNote()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var source = await SeedLegacyAsync(db, false);
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items));
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, source);
        Assert.IsType<OkObjectResult>(await controller.AcceptAdvance(source.PaymentId, new(), default));
        Assert.IsType<OkObjectResult>(await controller.RefundAdvance(source.PaymentId, new(), default));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.Empty((await CandidatesAsync(db, source)).Items);
        Assert.IsType<BadRequestObjectResult>(await TransferAsync(db, source, request)); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    internal static async Task<Fixture> SeedLegacyAsync(PaymentContext db, bool legal)
    {
        var source = await SeedAsync(db, legal); var row = await db.Payments.SingleAsync();
        row.Amount = 40m; row.PaidAmount = 40m; row.IsPaid = true; row.Status = PaymentStatus.Completed;
        row.PaidDate = row.Date; row.LastPaymentDate = row.Date;
        row.SystemNotes = "Частичная оплата: 40 / 100 • Остаток 60 → 15.11.2026";
        await db.SaveChangesAsync(); return source;
    }

    internal static async Task<PartialPaymentRemainderCandidatesResponse> CandidatesAsync(PaymentContext db, Fixture source)
    {
        var response = await BalanceApplicationPaymentWriterTests.Controller(db, source).GetPartialRemainderCandidates(null, null, null, null, includeFuture: true);
        return Assert.IsType<PartialPaymentRemainderCandidatesResponse>(Assert.IsType<OkObjectResult>(response.Result).Value);
    }

    internal static TransferExistingPaymentRemainderRequest Request(PartialPaymentRemainderCandidateDto candidate)
        => new()
        {
            RemainderDate = new DateTime(2026, 11, 15),
            OriginalAmount = candidate.OriginalAmount,
            ExpectedAmount = Math.Round(candidate.Payment.Amount, 2, MidpointRounding.AwayFromZero),
            ExpectedPaidAmount = candidate.PaidAmount,
            ConfirmationToken = candidate.ConfirmationToken
        };
    internal static Task<IActionResult> TransferAsync(PaymentContext db, Fixture source, TransferExistingPaymentRemainderRequest request)
        => BalanceApplicationPaymentWriterTests.Controller(db, source).TransferExistingPartialRemainder(source.PaymentId, request, default);
    internal static TransferPaymentRemainderResponse Result(IActionResult result)
        => Assert.IsType<TransferPaymentRemainderResponse>(Assert.IsType<OkObjectResult>(result).Value);
    internal static async Task AssertTransferAsync(PaymentContext db, Fixture source, bool legacy)
    {
        var preview = Assert.Single((await CandidatesAsync(db, source)).Items); Assert.Equal(legacy, preview.NeedsAmountConfirmation); Assert.NotEmpty(preview.ConfirmationToken);
        var saved = Result(await TransferAsync(db, source, Request(preview))); Assert.True(saved.CreatedRemainder);
        var link = await db.PaymentRemainderTransfers.AsNoTracking().SingleAsync(); Assert.Equal(100m, link.SourceOriginalAmount); Assert.Equal(40m, link.PaidAmount); Assert.Equal(60m, link.RemainderAmount);
        Assert.Equal(40m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId)).Amount);
        Assert.Equal(60m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == link.RemainderPaymentId)).Amount);
        Assert.Equal(100m, await db.ClientBalanceTransactions.SumAsync(t => t.Amount));
    }
    internal static async Task AssertStaleAsync(PaymentContext db, Fixture source, string change)
    {
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items)); var row = await db.Payments.SingleAsync(p => p.Id == source.PaymentId);
        switch (change)
        {
            case "amount": row.Amount = 50m; break;
            case "paid": row.PaidAmount = 30m; break;
            case "notes": row.Notes = "New note after preview"; break;
            case "type": row.Type = PaymentType.Expense; break;
            case "date": row.Date = row.Date.AddDays(1); break;
            case "seller": row.SellerBranchId = 999; break;
            case "owner":
                if (source.LegalEntity) { var owner = new LegalEntity { ShortName = "Another owner" }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); row.LegalEntityId = owner.Id; }
                else { var owner = new Client { Name = "Another owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); row.ClientId = owner.Id; }
                break;
        }
        await db.SaveChangesAsync(); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await TransferAsync(db, source, request)); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }
    internal static async Task AssertReplayAsync(PaymentContext db, Fixture source)
    {
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items)); var saved = Result(await TransferAsync(db, source, request));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db); request.RemainderDate = request.RemainderDate.AddDays(5);
        var replay = Result(await TransferAsync(db, source, request)); Assert.False(replay.CreatedRemainder); Assert.Equal(saved.RemainderPayment.Id, replay.RemainderPayment.Id);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }
    internal static async Task AssertCommittedFallbackAsync(PaymentContext db, Fixture source)
    {
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items)); await AddCommittedFallbackAsync(db, source);
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db); Assert.Empty((await CandidatesAsync(db, source)).Items);
        Assert.IsType<ConflictObjectResult>(await TransferAsync(db, source, request)); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Empty(await db.PaymentRemainderTransfers.ToArrayAsync());
    }
    internal static async Task AddCommittedFallbackAsync(PaymentContext db, Fixture source, bool missingChild = false)
    {
        var child = Child(source); if (!missingChild) { db.Payments.Add(child); await db.SaveChangesAsync(); }
        db.PaymentCreateAttempts.Add(new PaymentCreateAttempt
        {
            Id = Guid.NewGuid(),
            ActorUserId = source.ActorId,
            Operation = "remainder",
            PrimaryPaymentId = source.PaymentId,
            State = PaymentCreateAttempt.Committed,
            CanonicalPaymentId = missingChild ? int.MaxValue : child.Id,
            PayloadIdentity = new string('1', 64),
            CommittedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
    internal static Payment Child(Fixture source)
    { var child = PaymentFor(source); child.Amount = 60m; child.Date = new DateTime(2026, 11, 15); child.PlannedDate = child.Date; return child; }
    internal static async Task<Func<Payment, Task<IActionResult>>> RemainderAttemptAsync(PaymentContext db, Fixture source)
    {
        var controller = PaymentCreateFinancialBoundaryTests.Controller(db, source);
        var body = JsonSerializer.SerializeToElement(new { operation = "remainder", primaryPaymentId = source.PaymentId });
        var id = Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(await controller.RegisterCreateAttempt(body, default)).Value).AttemptId;
        return model => controller.CommitCreateAttempt(id, PaymentInputStateTests.Command(model), default);
    }
    internal static async Task AssertFailureAsync(PaymentContext db, Fixture source, FailSave fault)
    {
        var request = Request(Assert.Single((await CandidatesAsync(db, source)).Items)); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db); fault.Enabled = true;
        await Assert.ThrowsAsync<IOException>(() => TransferAsync(db, source, request)); fault.Enabled = false;
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }
    internal sealed class FailSave(int failAt) : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        private int count;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Enabled && ++count == failAt) throw new IOException("Synthetic legacy remainder failure"); return ValueTask.FromResult(result); }
    }
}

public sealed class LegacyRemainderConfirmationPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLegacyNoteCannotCreateDebtByItself(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal); var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await LegacyRemainderConfirmationTests.TransferAsync(db, source, new() { RemainderDate = new DateTime(2026, 11, 15) }));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeConfirmedAmountCreatesOnePair(bool legal, bool legacy)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal);
        if (!legacy) { (await db.Payments.SingleAsync()).Amount = 100m; await db.SaveChangesAsync(); }
        await LegacyRemainderConfirmationTests.AssertTransferAsync(db, source, legacy);
    }
    public static IEnumerable<object[]> StaleCases()
    {
        foreach (var legal in new[] { false, true }) foreach (var change in new[] { "paid", "owner", "notes" }) yield return [legal, change];
    }
    [Theory]
    [MemberData(nameof(StaleCases))]
    public async Task NativeStalePreviewDoesNotWrite(bool legal, string change)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal);
        await LegacyRemainderConfirmationTests.AssertStaleAsync(db, source, change);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeReplayReturnsTheSavedPair(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal); await LegacyRemainderConfirmationTests.AssertReplayAsync(db, source);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeFallbackDoesNotGenerateAnotherDebt(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, legal); await LegacyRemainderConfirmationTests.AssertCommittedFallbackAsync(db, source);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task NativeFailureRollsBackBothRowsAndLink(int save)
    {
        await using var database = await template.CreateDatabaseAsync(); var fault = new LegacyRemainderConfirmationTests.FailSave(save);
        await using var db = Open(database.ConnectionString, fault); var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false);
        await LegacyRemainderConfirmationTests.AssertFailureAsync(db, source, fault);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTransferBlocksAnEditorAndAnotherCreationAttempt(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); var pause = new HoldSave();
        await using var transferring = Open(database.ConnectionString, pause); await using var other = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(transferring, legal);
        var request = LegacyRemainderConfirmationTests.Request(Assert.Single((await LegacyRemainderConfirmationTests.CandidatesAsync(transferring, source)).Items));
        var attempt = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(other, source);
        var model = await VersionedPaymentWriterTests.ModelAsync(other, source); model.PaidAmount = 30m; model.IsPaid = false; model.Status = PaymentStatus.Processing;
        pause.Enabled = true; var pending = LegacyRemainderConfirmationTests.TransferAsync(transferring, source, request);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsType<ConflictObjectResult>(await PaymentInputStateTests.UpdateAsync(other, source, 0, model).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<ConflictObjectResult>(await attempt(LegacyRemainderConfirmationTests.Child(source)).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { pause.Release.TrySetResult(); }
        Assert.True(LegacyRemainderConfirmationTests.Result(await pending.WaitAsync(TimeSpan.FromSeconds(20))).CreatedRemainder);
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(other);
        Assert.IsType<ConflictObjectResult>(await attempt(LegacyRemainderConfirmationTests.Child(source)));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(other));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePaymentChangeWhileUserConfirmsInvalidatesThePreview(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var transferring = Open(database.ConnectionString); await using var editing = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(transferring, legal);
        var request = LegacyRemainderConfirmationTests.Request(Assert.Single((await LegacyRemainderConfirmationTests.CandidatesAsync(transferring, source)).Items));
        var model = await VersionedPaymentWriterTests.ModelAsync(editing, source); model.PaidAmount = 30m; model.IsPaid = false; model.Status = PaymentStatus.Processing;
        Assert.IsType<OkObjectResult>(await PaymentInputStateTests.UpdateAsync(editing, source, 0, model));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(editing);
        Assert.IsType<ConflictObjectResult>(await LegacyRemainderConfirmationTests.TransferAsync(transferring, source, request));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(editing));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeNewAttemptCannotRebindOrDuplicateAnExistingSource(bool mismatch)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var source = await LegacyRemainderConfirmationTests.SeedLegacyAsync(db, false); var operation = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source);
        var child = LegacyRemainderConfirmationTests.Child(source);
        if (mismatch) { var owner = new Client { Name = "Other native owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); child.ClientId = owner.Id; }
        else { Assert.IsType<CreatedResult>(await operation(child)); operation = await LegacyRemainderConfirmationTests.RemainderAttemptAsync(db, source); }
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await operation(child)); Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
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
