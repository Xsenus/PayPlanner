using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;
using static PayPlanner.Api.Tests.BalanceApplicationPaymentWriterTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationRelatedPaymentInvariantTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Recalculation_KeepingTheRepresentativesCredit_RemainsAllowed(bool legalEntity, bool transfer)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var representative = await LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = representative };
        Result(await ApplyAsync(db, parent, Request(parent)));
        Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, transfer, 40m));
        Assert.Equal(40m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).PaidAmount);
        Assert.Equal(25m, await ClientBalanceService.GetAppliedCreditAmountAsync(db, representative));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(transfer ? 1 : 0, await db.PaymentRemainderTransfers.CountAsync());
    }

    [Fact]
    public async Task ReadOnlySibling_WithHistoricalMismatch_IsNotRepairedOrUsedToBlockTheEdit()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var representative = await LinkInvoiceAsync(db, fixture);
        var sibling = PaymentFor(fixture);
        sibling.InvoiceId = (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).InvoiceId;
        sibling.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        db.Payments.Add(sibling); await db.SaveChangesAsync();
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = fixture.OwnerId,
            PaymentId = sibling.Id,
            Type = ClientBalanceTransactionType.CreditApplied,
            Direction = ClientBalanceDirection.Debit,
            Amount = 25m,
            Description = "Synthetic untouched historical sibling"
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, false, 40m));
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == sibling.Id)).PaidAmount);
        Assert.Equal(25m, await ClientBalanceService.GetAppliedCreditAmountAsync(db, sibling.Id));
        Assert.Equal(40m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).PaidAmount);
    }

    [Fact]
    public async Task RejectedParentReduction_DoesNotLeaveTrackingOrGuardStateInTheNextWrite()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        var representative = await LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = representative };
        Result(await ApplyAsync(db, parent, Request(parent)));
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, fixture, false, 10m));
        Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, false, 40m));
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, fixture, false, 10m));
        Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, false, 50m));
        Assert.Equal(50m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).PaidAmount);
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecalculatingInvoice_CannotEraseCreditAppliedToTheRepresentative(bool legalEntity, bool transfer)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var representative = await LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = representative };
        Result(await ApplyAsync(db, parent, Request(parent)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var model = PaymentFor(fixture); model.PaidAmount = 10m;
        var controller = Controller(db, fixture);
        var result = transfer
            ? await controller.TransferPartialRemainder(fixture.PaymentId, new() { Payment = model, RemainderDate = new DateTime(2026, 11, 15) }, CancellationToken.None)
            : await controller.UpdateInvoiceManaged(fixture.PaymentId, model, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).PaidAmount);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    internal static async Task<int> LinkInvoiceAsync(PaymentContext db, Fixture fixture)
    {
        var invoice = new Invoice { Number = "Synthetic related credit", Date = new DateTime(2026, 10, 31) };
        db.Invoices.Add(invoice); await db.SaveChangesAsync();
        var child = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        child.InvoiceId = invoice.Id; child.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        var representative = PaymentFor(fixture);
        representative.InvoiceId = invoice.Id; representative.DocumentKind = DocumentKind.Invoice;
        db.Payments.Add(representative); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return representative.Id;
    }

    internal static Task<IActionResult> WriteAsync(PaymentContext db, Fixture fixture, bool transfer, decimal paid)
    {
        var model = PaymentFor(fixture); model.PaidAmount = paid;
        var controller = Controller(db, fixture);
        return transfer
            ? controller.TransferPartialRemainder(fixture.PaymentId, new() { Payment = model, RemainderDate = new DateTime(2026, 11, 15) }, CancellationToken.None)
            : controller.UpdateInvoiceManaged(fixture.PaymentId, model, CancellationToken.None);
    }
}

public sealed class BalanceApplicationRelatedPaymentPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ParentApplyHoldingItsRow_ChildWriteConflictsAndRollsBack(bool legalEntity, bool transfer)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        int representative;
        await using (var seed = Open(database.ConnectionString))
        { fixture = await SeedAsync(seed, legalEntity); representative = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(seed, fixture); }
        var parent = fixture with { PaymentId = representative };
        var barrier = new HoldSave();
        await using var applier = Open(database.ConnectionString, barrier);
        await using var editor = Open(database.ConnectionString);
        var pending = ApplyAsync(applier, parent, Request(parent));
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult result;
        try { result = await BalanceApplicationRelatedPaymentInvariantTests.WriteAsync(editor, fixture, transfer, 10m).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(result);
        Result(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        Assert.Equal(25m, (await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).PaidAmount);
        Assert.Equal(0m, (await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount);
        Assert.Equal(2, await fresh.Payments.CountAsync()); Assert.Empty(await fresh.PaymentRemainderTransfers.ToArrayAsync());
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ChildWriteHoldingRecalculatedParent_ApplyConflictsThenUsesTheCommittedAmount(bool legalEntity, bool transfer)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        int representative;
        await using (var seed = Open(database.ConnectionString))
        { fixture = await SeedAsync(seed, legalEntity); representative = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(seed, fixture); }
        var parent = fixture with { PaymentId = representative };
        var barrier = new HoldAfterSave();
        await using var editor = Open(database.ConnectionString, barrier);
        await using var applier = Open(database.ConnectionString);
        var pending = BalanceApplicationRelatedPaymentInvariantTests.WriteAsync(editor, fixture, transfer, 40m);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        IActionResult result;
        var request = Request(parent);
        try { result = await ApplyAsync(applier, parent, request).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(result);
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        Assert.Equal(40m, (await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).PaidAmount);
        Result(await ApplyAsync(fresh, parent, request));
        Assert.Equal(65m, (await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == representative)).PaidAmount);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }

    private sealed class HoldSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result; }
    }

    private sealed class HoldAfterSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); }
            return result;
        }
    }
}
