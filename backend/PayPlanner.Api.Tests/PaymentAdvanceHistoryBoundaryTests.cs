using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentAdvanceHistoryBoundaryTests
{
    public static IEnumerable<object[]> UnsafeCases()
    {
        foreach (var legalEntity in new[] { false, true })
            foreach (var operation in new[] { "owner", "invoice", "paid", "cancel", "ignore", "genericRefund" }) yield return [legalEntity, operation];
    }
    [Theory]
    [MemberData(nameof(UnsafeCases))]
    public async Task UsedSource_CannotLoseOrMoveItsMoneyHistory(bool legalEntity, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legalEntity);
        var apply = Request(seed.Target); if (operation != "genericRefund") apply.SourcePaymentId = seed.Source.PaymentId;
        Result(await ApplyAsync(db, seed.Target, apply));
        var model = await VersionedPaymentWriterTests.ModelAsync(db, seed.Source);
        if (operation == "owner")
        {
            if (legalEntity) { var owner = new LegalEntity { FullName = "Other synthetic owner", ShortName = "Other" }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); model.LegalEntityId = owner.Id; }
            else { var owner = new Client { Name = "Other synthetic owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); model.ClientId = owner.Id; }
        }
        if (operation == "invoice")
        {
            var root = new Payment
            {
                Invoice = new Invoice { Number = "Synthetic new destination", Date = model.Date },
                ClientId = model.ClientId,
                LegalEntityId = model.LegalEntityId,
                Amount = 100m,
                Type = PaymentType.Income,
                Date = model.Date,
                DocumentKind = DocumentKind.Invoice
            };
            db.Payments.Add(root); await db.SaveChangesAsync(); model.InvoiceId = root.InvoiceId;
        }
        if (operation == "paid") { model.PaidAmount = 50m; model.Status = PaymentStatus.Processing; model.IsPaid = false; }
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Source, operation, model));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    public static IEnumerable<object[]> UnusedCases()
    {
        foreach (var legal in new[] { false, true })
            foreach (var operation in new[] { "accept", "ignore", "paid" }) yield return [legal, operation];
    }
    [Theory]
    [MemberData(nameof(UnusedCases))]
    public async Task UnusedSource_StillAllowsSupportedReviewAndAmountCorrections(bool legal, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, seed.Source); if (operation == "paid") { model.PaidAmount = 50m; model.Status = PaymentStatus.Processing; model.IsPaid = false; }
        Assert.IsType<OkObjectResult>(await WriteAsync(db, seed.Source, operation, model));
        var credit = await db.ClientBalanceTransactions.AsNoTracking().Where(t => t.Type == ClientBalanceTransactionType.UnallocatedAdvance).SumAsync(t => t.Amount);
        Assert.Equal(operation == "ignore" ? 0m : operation == "paid" ? 50m : 100m, credit);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialSourceRefund_PreservesOriginalCreditAndAppliedPayment(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; Result(await ApplyAsync(db, seed.Target, request));
        Assert.IsType<OkObjectResult>(await WriteAsync(db, seed.Source, "refund"));
        Assert.Equal(100m, (await db.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.UnallocatedAdvance)).Amount);
        Assert.Equal(75m, (await db.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.Refund)).Amount);
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Target.PaymentId)).PaidAmount);
        Assert.Equal(0m, await BalanceAsync(db, seed.Source));
        var before = await SnapshotAsync(db);
        foreach (var action in new[] { "accept", "ignore", "refund" })
        { Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Source, action)); Assert.Equal(before, await SnapshotAsync(db)); }
    }
    public static IEnumerable<object[]> ExtraChanges()
    {
        foreach (var legal in new[] { false, true })
            foreach (var field in new[] { "direction", "kind", "account", "rawOverpayment" }) yield return [legal, field];
    }
    [Theory]
    [MemberData(nameof(ExtraChanges))]
    public async Task UsedSource_RejectsIndirectReclassificationAndExtraReceivedAmount(bool legal, string field)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; Result(await ApplyAsync(db, seed.Target, request));
        var model = await VersionedPaymentWriterTests.ModelAsync(db, seed.Source);
        if (field == "direction") model.Type = PaymentType.Expense;
        if (field == "kind") model.DocumentKind = DocumentKind.Accrual;
        if (field == "account") model.Account = "Unresolved new document";
        if (field == "rawOverpayment") { model.PaidAmount = 150m; model.OverpaymentHandling = PaymentOverpaymentHandling.Advance; }
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Source, "update", model)); Assert.Equal(before, await SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingOverpaymentCredit_CannotMakeTheAggregateBalanceNegative(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = legal ? null : seed.Source.OwnerId,
            LegalEntityId = legal ? seed.Source.OwnerId : null,
            PaymentId = seed.Source.PaymentId,
            Amount = 100m,
            Direction = ClientBalanceDirection.Credit,
            Type = ClientBalanceTransactionType.Overpayment,
            ExternalKey = ClientBalanceService.BuildPaymentOverpaymentKey(seed.Source.PaymentId),
            Description = "Synthetic prior overpayment"
        });
        (await db.Payments.SingleAsync(p => p.Id == seed.Target.PaymentId)).Amount = 200m; await db.SaveChangesAsync();
        var request = Request(seed.Target); request.Amount = 150m; Result(await ApplyAsync(db, seed.Target, request)); Assert.Equal(50m, await BalanceAsync(db, seed.Source));
        var model = await VersionedPaymentWriterTests.ModelAsync(db, seed.Source); var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Source, "update", model)); Assert.Equal(before, await SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task VersionedSourceRevision_KeepsUsedCredit(bool legal, bool ignored)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; Result(await ApplyAsync(db, seed.Target, request));
        var model = await VersionedPaymentWriterTests.ModelAsync(db, seed.Source);
        if (ignored) model.AdvanceReviewStatus = PaymentAdvanceReviewStatus.Ignored; else { model.PaidAmount = 50m; model.Status = PaymentStatus.Processing; model.IsPaid = false; }
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await VersionedPaymentWriterTests.WriteAsync(db, seed.Source, 2, "update", model)); Assert.Equal(before, await SnapshotAsync(db));
    }
    public static IEnumerable<object[]> DeniedCases()
    {
        foreach (var operation in new[] { "accept", "ignore", "refund" })
            foreach (var denial in new[] { "permission", "inactive", "unapproved", "hidden" }) yield return [operation, denial];
    }
    [Theory]
    [MemberData(nameof(DeniedCases))]
    public async Task Review_RequiresCurrentActorPermissionAndVisibility(string operation, string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, false);
        if (denial == "permission") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
        if (denial == "hidden") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).ViewScope = "own";
        if (denial == "inactive") (await db.Users.SingleAsync()).IsActive = false;
        if (denial == "unapproved") (await db.Users.SingleAsync()).IsApproved = false;
        await db.SaveChangesAsync(); var before = await SnapshotAsync(db); var result = await WriteAsync(db, seed.Source, operation);
        if (denial == "permission") Assert.IsType<ForbidResult>(result); else if (denial == "hidden") Assert.IsType<NotFoundResult>(result); else Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(before, await SnapshotAsync(db));
    }
    [Theory]
    [InlineData("accept")]
    [InlineData("ignore")]
    [InlineData("refund")]
    public async Task Review_PreservesForeignPendingChangesAndTransactions(string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, false); var owner = await db.Clients.SingleAsync(); owner.Name = "Pending foreign edit";
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, seed.Source, operation)); Assert.Equal(EntityState.Modified, db.Entry(owner).State);
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, seed.Source, operation)); Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainReads_KeepAdvanceReviewDecisionAndActor(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var source = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); source.AdvanceReviewReason = "Accepted original receipt";
        source.AdvanceReviewedAt = new DateTime(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc); source.AdvanceReviewedByUserId = seed.Source.ActorId; source.AdvanceReviewedByUserName = "Synthetic reviewer";
        await db.SaveChangesAsync(); var controller = BalanceApplicationPaymentWriterTests.Controller(db, seed.Source);
        var detail = await VersionedPaymentWriterTests.ModelAsync(db, seed.Source);
        var list = Assert.IsAssignableFrom<IEnumerable<Payment>>(Assert.IsType<OkObjectResult>(await controller.Get(null, null, null, null)).Value);
        foreach (var read in new[] { detail, Assert.Single(list.Where(p => p.Id == source.Id)) })
        {
            Assert.Equal(source.AdvanceReviewStatus, read.AdvanceReviewStatus); Assert.Equal(source.AdvanceReviewReason, read.AdvanceReviewReason);
            Assert.Equal(source.AdvanceReviewedAt, read.AdvanceReviewedAt); Assert.Equal(source.AdvanceReviewedByUserId, read.AdvanceReviewedByUserId); Assert.Equal(source.AdvanceReviewedByUserName, read.AdvanceReviewedByUserName);
        }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RestrictedBalanceView_IsDeniedBeforeSpending(bool legal, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).ViewScope = "own";
        db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = seed.Target.PaymentId, UserId = seed.Target.ActorId }); await db.SaveChangesAsync();
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; if (legacy) request.RequestId = null;
        var before = await SnapshotAsync(db); Assert.IsType<ForbidResult>(await ApplyAsync(db, seed.Target, request)); Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, "accept")]
    [InlineData(true, "accept")]
    [InlineData(false, "owner")]
    [InlineData(true, "owner")]
    public async Task MissingSourceCreditWithOldUsage_RequiresReconciliation(bool legal, string action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; Result(await ApplyAsync(db, seed.Target, request));
        db.ClientBalanceTransactions.Remove(await db.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.UnallocatedAdvance));
        (await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId)).AdvanceReviewStatus = PaymentAdvanceReviewStatus.Ignored; await db.SaveChangesAsync();
        var model = await VersionedPaymentWriterTests.ModelAsync(db, seed.Source);
        if (action == "owner")
        {
            if (legal) { var owner = new LegalEntity { FullName = "Other synthetic owner", ShortName = "Other" }; db.LegalEntities.Add(owner); await db.SaveChangesAsync(); model.LegalEntityId = owner.Id; }
            else { var owner = new Client { Name = "Other synthetic owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); model.ClientId = owner.Id; }
        }
        var before = await SnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Source, action, model)); Assert.Equal(before, await SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryUncreditedReceipt_CanStillBeAccepted(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        db.ClientBalanceTransactions.Remove(await db.ClientBalanceTransactions.SingleAsync());
        (await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId)).AdvanceReviewStatus = PaymentAdvanceReviewStatus.RequiresDecision; await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>(await WriteAsync(db, seed.Source, "accept")); Assert.Equal(100m, await BalanceAsync(db, seed.Source)); Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaymentFundedFromBalance_CannotCreateAnotherAdvance(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; Result(await ApplyAsync(db, seed.Target, request));
        var before = await SnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Target, "accept")); Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(75m, await BalanceAsync(db, seed.Source));
    }

    [Theory]
    [InlineData(false, "accept")]
    [InlineData(true, "accept")]
    [InlineData(false, "ignore")]
    [InlineData(true, "ignore")]
    [InlineData(false, "refund")]
    [InlineData(true, "refund")]
    public async Task MixedReceiptWithAppliedBalance_RequiresReconciliationBeforeReview(bool legal, string action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedSourceAsync(db, legal);
        var source = await db.Payments.SingleAsync(p => p.Id == seed.Source.PaymentId); source.Amount = 125m; source.Status = PaymentStatus.Processing; await db.SaveChangesAsync();
        var request = Request(seed.Source); Result(await ApplyAsync(db, seed.Source, request));
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = legal ? null : seed.Source.OwnerId,
            LegalEntityId = legal ? seed.Source.OwnerId : null,
            Amount = 100m,
            Type = ClientBalanceTransactionType.ManualAdjustment,
            Direction = ClientBalanceDirection.Credit,
            Description = "Other unrelated funds must not validate mixed receipt"
        }); await db.SaveChangesAsync();
        var before = await SnapshotAsync(db); Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Source, action)); Assert.Equal(before, await SnapshotAsync(db));
    }

    internal static Task<decimal> BalanceAsync(PaymentContext db, Fixture source)
        => source.LegalEntity ? ClientBalanceService.GetLegalEntityBalanceAsync(db, source.OwnerId) : ClientBalanceService.GetBalanceAsync(db, source.OwnerId);

    internal sealed record Seed(Fixture Target, Fixture Source);
    internal static async Task<Seed> SeedSourceAsync(PaymentContext db, bool legalEntity)
    {
        var target = await SeedAsync(db, legalEntity); db.ClientBalanceTransactions.RemoveRange(await db.ClientBalanceTransactions.ToArrayAsync());
        var source = new Payment
        {
            ClientId = legalEntity ? null : target.OwnerId,
            LegalEntityId = legalEntity ? target.OwnerId : null,
            Amount = 100m,
            PaidAmount = 100m,
            IsPaid = true,
            Type = PaymentType.Income,
            Status = PaymentStatus.Completed,
            DocumentKind = DocumentKind.Payment,
            AdvanceReviewStatus = PaymentAdvanceReviewStatus.AcceptedAsAdvance,
            Date = new DateTime(2026, 10, 31),
            PlannedDate = new DateTime(2026, 10, 31),
            PaidDate = new DateTime(2026, 10, 31),
            Description = "Synthetic original advance",
            Notes = "",
            SystemNotes = ""
        };
        db.Payments.Add(source); await db.SaveChangesAsync();
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = source.ClientId,
            LegalEntityId = source.LegalEntityId,
            PaymentId = source.Id,
            Amount = 100m,
            Direction = ClientBalanceDirection.Credit,
            Type = ClientBalanceTransactionType.UnallocatedAdvance,
            ExternalKey = ClientBalanceService.BuildUnallocatedAdvanceKey(source.Id),
            Description = "Synthetic advance basis"
        });
        await db.SaveChangesAsync(); return new(target, target with { PaymentId = source.Id });
    }
    internal static async Task<string> SnapshotAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Money = await CompensatedPaymentWriterTests.SnapshotAsync(db),
            Review = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.AdvanceReviewStatus, p.AdvanceReviewReason, p.AdvanceReviewedAt }).ToArrayAsync(),
        });
    internal static Task<IActionResult> WriteAsync(PaymentContext db, Fixture fixture, string operation, Payment? model = null)
    {
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        return operation switch
        {
            "ignore" => controller.IgnoreAdvance(fixture.PaymentId, new() { Reason = "Synthetic ignore" }, default),
            "accept" => controller.AcceptAdvance(fixture.PaymentId, new() { Reason = "Synthetic acceptance" }, default),
            "refund" or "genericRefund" => controller.RefundAdvance(fixture.PaymentId, new() { Reason = "Synthetic refund" }, default),
            "cancel" => controller.UpdateStatus(fixture.PaymentId, new() { Status = "Cancelled", Reason = "Synthetic cancellation" }, default),
            _ => controller.Update(fixture.PaymentId, model!, default),
        };
    }
}

public sealed class PaymentAdvanceHistoryPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> RaceCases()
    {
        foreach (var legal in new[] { false, true })
            foreach (var action in new[] { "accept", "ignore", "refund" }) yield return [legal, action];
    }
    [Theory]
    [MemberData(nameof(RaceCases))]
    public async Task ReviewHoldingSource_ConflictsWithExplicitApply(bool legal, string action)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var barrier = new HoldSave(); await using var reviewing = Open(database.ConnectionString, barrier); await using var applying = Open(database.ConnectionString);
        var pending = PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, seed.Source, action);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId;
        try { Assert.IsType<ConflictObjectResult>(await ApplyAsync(applying, seed.Target, request).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var retry = await ApplyAsync(applying, seed.Target, request);
        if (action == "accept") { Assert.Equal(25m, Result(retry).AppliedAmount); Assert.Equal(75m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(applying, seed.Source)); }
        else { Assert.IsType<BadRequestObjectResult>(retry); Assert.Equal(0m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(applying, seed.Source)); }
    }
    public static IEnumerable<object[]> ApplyCases()
    {
        foreach (var legal in new[] { false, true })
            foreach (var action in new[] { "ignore", "refund", "paid" }) yield return [legal, action];
    }
    [Theory]
    [MemberData(nameof(ApplyCases))]
    public async Task ApplyHoldingSource_BlocksReviewThenKeepsUsedMoney(bool legal, string action)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var barrier = new HoldSave(); await using var applying = Open(database.ConnectionString, barrier); await using var reviewing = Open(database.ConnectionString);
        var model = await VersionedPaymentWriterTests.ModelAsync(reviewing, seed.Source); model.PaidAmount = 50m; model.Status = PaymentStatus.Processing; model.IsPaid = false;
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; var pending = ApplyAsync(applying, seed.Target, request);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, seed.Source, action, model).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(25m, Result(await pending.WaitAsync(TimeSpan.FromSeconds(20))).AppliedAmount);
        var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing); var retry = await PaymentAdvanceHistoryBoundaryTests.WriteAsync(reviewing, seed.Source, action, model);
        if (action == "refund") { Assert.IsType<OkObjectResult>(retry); Assert.Equal(75m, (await reviewing.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.Refund)).Amount); }
        else { Assert.IsType<BadRequestObjectResult>(retry); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(reviewing)); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRefund_HasOnlyOneFinancialOutcome(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var barrier = new HoldSave(); await using var first = Open(database.ConnectionString, barrier); await using var second = Open(database.ConnectionString);
        var pending = PaymentAdvanceHistoryBoundaryTests.WriteAsync(first, seed.Source, "refund"); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(second, seed.Source, "refund").WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20))); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(second);
        Assert.IsType<BadRequestObjectResult>(await PaymentAdvanceHistoryBoundaryTests.WriteAsync(second, seed.Source, "refund")); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(second));
        Assert.Single(await second.MoneyOperations.ToArrayAsync()); Assert.Equal(100m, (await second.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.Refund)).Amount);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericApplyRacingIgnore_CannotCommitANegativeBalance(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var barrier = new HoldSave(); await using var ignoring = Open(database.ConnectionString, barrier); await using var applying = Open(database.ConnectionString);
        var pending = PaymentAdvanceHistoryBoundaryTests.WriteAsync(ignoring, seed.Source, "ignore"); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.Equal(25m, Result(await ApplyAsync(applying, seed.Target, Request(seed.Target)).WaitAsync(TimeSpan.FromSeconds(10))).AppliedAmount); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(75m, await PaymentAdvanceHistoryBoundaryTests.BalanceAsync(applying, seed.Source));
        Assert.Equal(PaymentAdvanceReviewStatus.AcceptedAsAdvance, (await applying.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Source.PaymentId)).AdvanceReviewStatus);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundSaveFailure_RollsBackLedgerOperationAndReview(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        await using var refunding = Open(database.ConnectionString, new FailAfterSave()); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(refunding);
        await Assert.ThrowsAsync<IOException>(() => PaymentAdvanceHistoryBoundaryTests.WriteAsync(refunding, seed.Source, "refund"));
        Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(refunding)); Assert.Null(refunding.Database.CurrentTransaction); Assert.False(refunding.ChangeTracker.HasChanges());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewHoldingSource_LegacyApplyUsesTheSameSourceLock(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); PaymentAdvanceHistoryBoundaryTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentAdvanceHistoryBoundaryTests.SeedSourceAsync(db, legal);
        var barrier = new HoldSave(); await using var ignoring = Open(database.ConnectionString, barrier); await using var applying = Open(database.ConnectionString);
        var pending = PaymentAdvanceHistoryBoundaryTests.WriteAsync(ignoring, seed.Source, "ignore"); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var request = Request(seed.Target); request.SourcePaymentId = seed.Source.PaymentId; request.RequestId = null;
        try { Assert.IsType<ConflictObjectResult>(await ApplyAsync(applying, seed.Target, request).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20))); Assert.IsType<BadRequestObjectResult>(await ApplyAsync(applying, seed.Target, request)); Assert.Empty(await applying.BalanceApplicationReceipts.ToArrayAsync());
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result; }
    }
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => throw new IOException("Synthetic failure after refund SQL write");
    }
}
