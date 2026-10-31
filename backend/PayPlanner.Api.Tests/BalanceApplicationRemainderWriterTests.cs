using System.Data.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;
using static PayPlanner.Api.Tests.BalanceApplicationPaymentWriterTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationRemainderWriterTests
{
    [Theory]
    [InlineData(false, "transfer")]
    [InlineData(true, "transfer")]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "adjust")]
    [InlineData(true, "adjust")]
    public async Task ExistingCredit_IsPreservedThroughLegitimateSplitChanges(bool legalEntity, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var target = await SplitAsync(db, fixture);
        if (operation != "transfer") Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, operation, target));
        var source = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(operation == "adjust" ? 50m : 40m, source.PaidAmount);
        Assert.Equal(operation == "rollback" ? 100m : operation == "adjust" ? 50m : 40m, source.Amount);
        Assert.Equal(25m, await ClientBalanceService.GetAppliedCreditAmountAsync(db, fixture.PaymentId));
        Assert.Contains("Зачтено с баланса контрагента", source.SystemNotes);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedAmount_ExactlyAtAppliedCredit_RemainsAllowed(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); await MarkManagedAsync(db, fixture);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var model = PaymentFor(fixture); model.PaidAmount = 25m; model.Amount = 25m;
        Assert.IsType<OkObjectResult>(await Controller(db, fixture).UpdateInvoiceManaged(fixture.PaymentId, model, CancellationToken.None));
        var payment = await db.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(25m, payment.Amount); Assert.Equal(25m, payment.PaidAmount);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Contains("Зачтено с баланса контрагента", payment.SystemNotes);
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync());
    }

    [Theory]
    [InlineData(false, "managed")]
    [InlineData(true, "managed")]
    [InlineData(false, "transfer")]
    [InlineData(true, "transfer")]
    [InlineData(false, "existing")]
    [InlineData(true, "existing")]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "adjust")]
    [InlineData(true, "adjust")]
    public async Task QualifiedWriter_PreservesItsExistingSuccessfulOperation(bool legalEntity, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var target = await PrepareAsync(db, fixture, operation);
        Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, operation, target));
        var source = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(operation == "adjust" ? 50m : 40m, source.PaidAmount);
        Assert.Equal(operation == "rollback" ? 100m : operation is "transfer" or "existing" ? 40m : operation == "adjust" ? 50m : 100m, source.Amount);
        Assert.Equal(operation is "transfer" or "existing" or "adjust" ? 1 : 0, await db.PaymentRemainderTransfers.CountAsync());
        Assert.Equal(operation is "transfer" or "existing" or "adjust" ? 2 : 1, await db.Payments.CountAsync());
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "managed")]
    [InlineData(true, "managed")]
    [InlineData(false, "transfer")]
    [InlineData(true, "transfer")]
    public async Task CreditOwner_CannotBeRemovedDuringManagedEditOrTransfer(bool legalEntity, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        if (operation == "managed") await MarkManagedAsync(db, fixture);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var model = PaymentFor(fixture); model.ClientId = null; model.LegalEntityId = null; model.PaidAmount = 40m;
        var before = await SnapshotAsync(db);
        var controller = Controller(db, fixture);
        var result = operation == "managed"
            ? await controller.UpdateInvoiceManaged(fixture.PaymentId, model, CancellationToken.None)
            : await controller.TransferPartialRemainder(fixture.PaymentId, new() { Payment = model, RemainderDate = new DateTime(2026, 11, 15) }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result); Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "adjust")]
    [InlineData(true, "adjust")]
    public async Task PaidRemainder_CannotBeDeletedOrReset(bool legalEntity, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var target = await SplitAsync(db, fixture);
        var remainder = fixture with { PaymentId = target };
        Result(await ApplyAsync(db, remainder, Request(remainder)));
        var before = await SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, operation, fixture.PaymentId));
        Assert.Equal(before, await SnapshotAsync(db));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("adjust")]
    public async Task LegacyRemainder_WithCreditDebitButZeroPaid_IsPreserved(string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var target = await SplitAsync(db, fixture);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = fixture.OwnerId,
            PaymentId = target,
            Amount = 25m,
            Direction = ClientBalanceDirection.Debit,
            Type = ClientBalanceTransactionType.CreditApplied,
            Description = "Synthetic inconsistent historical ledger"
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, operation, target));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task Rollback_CannotRestoreAStaleMarkerBelowAppliedCredit()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var target = await SplitAsync(db, fixture);
        (await db.PaymentRemainderTransfers.SingleAsync()).PaidAmount = 10m;
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = fixture.OwnerId,
            PaymentId = fixture.PaymentId,
            Amount = 25m,
            Direction = ClientBalanceDirection.Debit,
            Type = ClientBalanceTransactionType.CreditApplied,
            Description = "Synthetic inconsistent historical marker"
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, fixture, "rollback", target));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("transfer")]
    [InlineData("existing")]
    [InlineData("rollback")]
    [InlineData("adjust")]
    public async Task OwnScope_CannotWriteAnotherOwnersPayment(string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var target = await PrepareAsync(db, fixture, operation);
        foreach (var p in await db.RolePermissions.ToArrayAsync()) p.ViewScope = "own";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        Assert.IsType<NotFoundResult>(await WriteAsync(db, fixture, operation, target));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData("rollback", false)]
    [InlineData("rollback", true)]
    [InlineData("adjust", false)]
    [InlineData("adjust", true)]
    [InlineData("transfer", false)]
    public async Task SplitPair_RequiresVisibilityOfBothPayments(string operation, bool enterThroughRemainder)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var target = await SplitAsync(db, fixture);
        var hiddenClient = new Client { Name = "Synthetic hidden owner" };
        db.Clients.Add(hiddenClient); await db.SaveChangesAsync();
        (await db.Clients.SingleAsync(c => c.Id == fixture.OwnerId)).ResponsibleUserId = fixture.ActorId;
        var hidden = await db.Payments.SingleAsync(p => p.Id == (enterThroughRemainder ? fixture.PaymentId : target));
        hidden.ClientId = hiddenClient.Id;
        foreach (var p in await db.RolePermissions.ToArrayAsync()) p.ViewScope = "own";
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        Assert.IsType<NotFoundResult>(await WriteAsync(db, fixture, operation, enterThroughRemainder ? target : fixture.PaymentId));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData("managed", 1)]
    [InlineData("transfer", 1)]
    [InlineData("transfer", 2)]
    [InlineData("transfer", 3)]
    [InlineData("existing", 2)]
    [InlineData("rollback", 1)]
    [InlineData("rollback", 2)]
    [InlineData("adjust", 1)]
    public async Task FailureAfterIntermediateSave_RollsBackTheWholeOperation(string operation, int saveNumber)
    {
        var failure = new FailAfterSave();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await SeedAsync(db, false);
        var target = await PrepareAsync(db, fixture, operation);
        var before = await SnapshotAsync(db);
        failure.FailOn = saveNumber;
        await Assert.ThrowsAsync<SyntheticWriteFailure>(() => WriteAsync(db, fixture, operation, target));
        Assert.Equal(before, await SnapshotAsync(db));
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("transfer")]
    [InlineData("existing")]
    [InlineData("rollback")]
    [InlineData("adjust")]
    public async Task UnapprovedActor_CannotWrite(string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false); var target = await PrepareAsync(db, fixture, operation);
        (await db.Users.SingleAsync()).IsApproved = false; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        Assert.IsType<UnauthorizedResult>(await WriteAsync(db, fixture, operation, target));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task LegacyRemainderLookup_PreservesExactMarkerMatching_WithoutTrackingEveryCandidate()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var wrong = PaymentFor(fixture); wrong.SystemNotes = PaymentRemainderTransferRules.BuildMarker(fixture.PaymentId + 1000, 100m, 40m, 60m, new DateTime(2026, 11, 15));
        var matching = PaymentFor(fixture); matching.Amount = 60m;
        matching.SystemNotes = PaymentRemainderTransferRules.BuildMarker(fixture.PaymentId, 100m, 40m, 60m, new DateTime(2026, 11, 15));
        db.Payments.AddRange(wrong, matching); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var result = await Controller(db, fixture).TransferPartialRemainder(fixture.PaymentId, Transfer(fixture), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain(db.ChangeTracker.Entries<Payment>(), entry => entry.Entity.Id == wrong.Id);
        Assert.Contains(db.ChangeTracker.Entries<Payment>(), entry => entry.Entity.Id == matching.Id);
        Assert.Equal(3, await db.Payments.CountAsync()); Assert.Empty(await db.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("transfer")]
    [InlineData("existing")]
    [InlineData("rollback")]
    [InlineData("adjust")]
    public async Task ViewWithoutEdit_CannotWrite(string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        if (operation == "managed") await MarkManagedAsync(db, fixture);
        var target = fixture.PaymentId;
        if (operation is "rollback" or "adjust") target = await SplitAsync(db, fixture);
        if (operation == "existing")
        {
            (await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount = 40m;
            await db.SaveChangesAsync();
        }
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        Assert.IsType<ForbidResult>(await WriteAsync(db, fixture, operation, target));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedUpdate_CannotClampAppliedCreditByLoweringAmount(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity); await MarkManagedAsync(db, fixture);
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        var model = PaymentFor(fixture); model.Amount = 10m; model.PaidAmount = 25m; model.OverpaymentHandling = PaymentOverpaymentHandling.Advance;
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await Controller(db, fixture).UpdateInvoiceManaged(fixture.PaymentId, model, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    internal static async Task MarkManagedAsync(PaymentContext db, Fixture fixture)
    {
        (await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
    }

    internal static async Task<int> PrepareAsync(PaymentContext db, Fixture fixture, string operation)
    {
        if (operation == "managed") await MarkManagedAsync(db, fixture);
        if (operation is "rollback" or "adjust") return await SplitAsync(db, fixture);
        if (operation == "existing")
        {
            (await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount = 40m;
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        }
        return fixture.PaymentId;
    }

    internal static async Task<int> SplitAsync(PaymentContext db, Fixture fixture)
    {
        var result = await Controller(db, fixture).TransferPartialRemainder(fixture.PaymentId, Transfer(fixture), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result);
        return await db.Payments.AsNoTracking().Where(p => p.Id != fixture.PaymentId).Select(p => p.Id).SingleAsync();
    }

    internal static TransferPaymentRemainderRequest Transfer(Fixture fixture)
    {
        var model = PaymentFor(fixture); model.PaidAmount = 40m;
        return new() { Payment = model, RemainderDate = new DateTime(2026, 11, 15) };
    }

    internal static Task<IActionResult> WriteAsync(PaymentContext db, Fixture fixture, string operation, int target)
    {
        var controller = Controller(db, fixture);
        var managed = PaymentFor(fixture); managed.PaidAmount = 40m;
        return operation switch
        {
            "managed" => controller.UpdateInvoiceManaged(fixture.PaymentId, managed, CancellationToken.None),
            "transfer" => controller.TransferPartialRemainder(fixture.PaymentId, Transfer(fixture), CancellationToken.None),
            "existing" => controller.TransferExistingPartialRemainder(fixture.PaymentId,
                new TransferExistingPaymentRemainderRequest { RemainderDate = new DateTime(2026, 11, 15) }, CancellationToken.None),
            "rollback" => controller.RollbackPartialRemainderTransfer(target, CancellationToken.None),
            "adjust" => controller.AdjustPartialRemainderTransfer(target,
                new AdjustPaymentRemainderTransferRequest { PaidAmount = 50m, RemainderDate = new DateTime(2026, 11, 16) }, CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    internal static async Task<string> SnapshotAsync(PaymentContext db)
    {
        var payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new
        { p.Id, p.ClientId, p.LegalEntityId, p.Type, p.Description, p.Notes, p.Amount, p.PaidAmount, p.Status, p.SystemNotes, p.Date, p.PlannedDate }).ToArrayAsync();
        var links = await db.PaymentRemainderTransfers.AsNoTracking().OrderBy(p => p.Id).Select(p => new
        { p.Id, p.SourcePaymentId, p.RemainderPaymentId, p.SourceOriginalAmount, p.PaidAmount, p.RemainderAmount, p.RemainderDate }).ToArrayAsync();
        var balance = await db.ClientBalanceTransactions.AsNoTracking().OrderBy(p => p.Id).Select(p => new
        { p.Id, p.ClientId, p.LegalEntityId, p.PaymentId, p.Amount, p.Type, p.Direction }).ToArrayAsync();
        return System.Text.Json.JsonSerializer.Serialize(new { payments, links, balance });
    }

    private sealed class SyntheticWriteFailure : Exception;
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public int FailOn { get; set; }
        private int saves;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (FailOn > 0 && ++saves == FailOn) throw new SyntheticWriteFailure();
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class BalanceApplicationRemainderPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLegacyMarker_ClosesTheReaderBeforeLoadingOnlyTheSelectedGraph(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legalEntity);
        var wrong = PaymentFor(fixture);
        wrong.SystemNotes = PaymentRemainderTransferRules.BuildMarker(fixture.PaymentId * 10, 100m, 40m, 60m, new DateTime(2026, 11, 15));
        var matching = PaymentFor(fixture); matching.Amount = 60m;
        matching.SystemNotes = PaymentRemainderTransferRules.BuildMarker(fixture.PaymentId, 100m, 40m, 60m, new DateTime(2026, 11, 15));
        db.Payments.AddRange(wrong, matching); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await Controller(db, fixture).TransferPartialRemainder(fixture.PaymentId,
            BalanceApplicationRemainderWriterTests.Transfer(fixture), CancellationToken.None));
        Assert.DoesNotContain(db.ChangeTracker.Entries<Payment>(), entry => entry.Entity.Id == wrong.Id);
        Assert.Contains(db.ChangeTracker.Entries<Payment>(), entry => entry.Entity.Id == matching.Id);
        Assert.Equal(3, await db.Payments.CountAsync()); Assert.Empty(await db.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, "managed", false)]
    [InlineData(true, "managed", false)]
    [InlineData(false, "transfer", false)]
    [InlineData(true, "transfer", false)]
    [InlineData(false, "existing", false)]
    [InlineData(true, "existing", false)]
    [InlineData(false, "rollback", false)]
    [InlineData(true, "rollback", false)]
    [InlineData(false, "rollback", true)]
    [InlineData(true, "rollback", true)]
    [InlineData(false, "adjust", false)]
    [InlineData(true, "adjust", false)]
    [InlineData(false, "adjust", true)]
    [InlineData(true, "adjust", true)]
    public async Task NativeApplyHoldingTarget_EveryWriterConflictsWithoutPartialChanges(bool legalEntity, string operation, bool enterThroughRemainder)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        int target;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await SeedAsync(seed, legalEntity);
            target = await BalanceApplicationRemainderWriterTests.PrepareAsync(seed, fixture, operation);
        }
        var appliedFixture = fixture with { PaymentId = target };
        var barrier = new HoldSave();
        await using var applier = Open(database.ConnectionString, barrier);
        await using var editor = Open(database.ConnectionString);
        var pending = ApplyAsync(applier, appliedFixture, Request(appliedFixture));
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult blocked;
        try
        {
            blocked = await BalanceApplicationRemainderWriterTests.WriteAsync(editor, fixture, operation,
                enterThroughRemainder ? target : fixture.PaymentId).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(blocked);
        Result(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        var payment = await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == target);
        Assert.Equal(operation == "existing" ? 65m : 25m, payment.PaidAmount);
        Assert.Contains("Зачтено с баланса контрагента", payment.SystemNotes);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(operation is "rollback" or "adjust" ? 1 : 0, await fresh.PaymentRemainderTransfers.CountAsync());
        if (operation is "rollback" or "adjust")
        {
            var source = await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
            Assert.Equal(40m, source.Amount); Assert.Equal(40m, source.PaidAmount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTransferHoldingSource_ApplyCannotBeOverwritten(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, legalEntity);
        var barrier = new HoldAppliedCreditRead();
        await using var editor = Open(database.ConnectionString, barrier);
        await using var applier = Open(database.ConnectionString);
        var pending = Controller(editor, fixture).TransferPartialRemainder(fixture.PaymentId, BalanceApplicationRemainderWriterTests.Transfer(fixture), CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult blocked;
        var request = Request(fixture);
        try { blocked = await ApplyAsync(applier, fixture, request).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(blocked);
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(fresh, fixture, request));
        Assert.Empty(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        var source = await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(40m, source.Amount); Assert.Equal(40m, source.PaidAmount);
        Assert.Single(await fresh.PaymentRemainderTransfers.ToArrayAsync());
        Assert.Equal(2, await fresh.Payments.CountAsync());
    }

    [Theory]
    [InlineData(false, "rollback", false)]
    [InlineData(true, "rollback", false)]
    [InlineData(false, "rollback", true)]
    [InlineData(true, "rollback", true)]
    [InlineData(false, "adjust", false)]
    [InlineData(true, "adjust", false)]
    [InlineData(false, "adjust", true)]
    [InlineData(true, "adjust", true)]
    public async Task NativePairWriterHoldingBothRows_ApplyWaitsForTheWholeOutcome(bool legalEntity, string operation, bool enterThroughRemainder)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        int target;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await SeedAsync(seed, legalEntity);
            target = await BalanceApplicationRemainderWriterTests.SplitAsync(seed, fixture);
        }
        var appliedFixture = fixture with { PaymentId = target };
        var barrier = new HoldAppliedCreditRead();
        await using var editor = Open(database.ConnectionString, barrier);
        await using var applier = Open(database.ConnectionString);
        var pending = BalanceApplicationRemainderWriterTests.WriteAsync(editor, fixture, operation,
            enterThroughRemainder ? target : fixture.PaymentId);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult blocked;
        var request = Request(appliedFixture);
        try { blocked = await ApplyAsync(applier, appliedFixture, request).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(blocked);
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        if (operation == "rollback")
        {
            Assert.IsType<NotFoundResult>(await ApplyAsync(fresh, appliedFixture, request));
            Assert.Single(await fresh.Payments.ToArrayAsync());
            Assert.Empty(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        }
        else
        {
            Result(await ApplyAsync(fresh, appliedFixture, request));
            var remainder = await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == target);
            Assert.Equal(50m, remainder.Amount); Assert.Equal(25m, remainder.PaidAmount);
            Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagedEdit_RacingWithApply_DoesNotLoseCredit(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString))
        { fixture = await SeedAsync(seed, legalEntity); await BalanceApplicationRemainderWriterTests.MarkManagedAsync(seed, fixture); }
        var barrier = new HoldAppliedCreditRead();
        await using var editor = Open(database.ConnectionString, barrier);
        await using var applier = Open(database.ConnectionString);
        var model = PaymentFor(fixture); model.PaidAmount = 10m;
        var pending = Controller(editor, fixture).UpdateInvoiceManaged(fixture.PaymentId, model, CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult applied;
        var request = Request(fixture);
        try { applied = await ApplyAsync(applier, fixture, request).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        var debit = await ClientBalanceService.GetAppliedCreditAmountAsync(fresh, fixture.PaymentId);
        var payment = await fresh.Payments.AsNoTracking().SingleAsync();
        Assert.True(payment.PaidAmount >= debit, $"Managed edit lost credit: paid={payment.PaidAmount}, debit={debit}");
        if (applied is ConflictObjectResult) Result(await ApplyAsync(fresh, fixture, request)); else Assert.IsType<OkObjectResult>(applied);
        payment = await fresh.Payments.AsNoTracking().SingleAsync();
        Assert.Equal(applied is ConflictObjectResult ? 35m : 25m, payment.PaidAmount);
        Assert.Contains("Зачтено с баланса контрагента", payment.SystemNotes);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private sealed class HoldAppliedCreditRead : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains("ClientBalanceTransactions", StringComparison.Ordinal)
                && command.CommandText.Contains("sum", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("PaymentId", StringComparison.Ordinal)
                && Interlocked.Increment(ref arrivals) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }

    private sealed class HoldSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result;
        }
    }
}
