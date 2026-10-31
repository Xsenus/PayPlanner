using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationServiceCreditGuardTests
{
    [Theory]
    [InlineData(200, false)]
    [InlineData(200, true)]
    [InlineData(100, false)]
    public async Task HistoricalRepresentatives_UseTheExistingCanonicalSelection(int otherAmount, bool otherCancelled)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertCanonicalSelectionAsync(db, otherAmount, otherCancelled);
    }

    internal static async Task AssertCanonicalSelectionAsync(PaymentContext db, int otherAmount, bool otherCancelled)
    {
        var fixture = await SeedAsync(db, false);
        var originalId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var invoiceId = (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == originalId)).InvoiceId;
        var other = PaymentFor(fixture); other.DocumentKind = DocumentKind.Invoice; other.InvoiceId = invoiceId;
        other.Amount = otherAmount; if (otherCancelled) other.Status = PaymentStatus.Cancelled;
        var settled = PaymentFor(fixture); settled.PaidAmount = 30m; settled.InvoiceId = invoiceId;
        settled.SystemNotes = PayPlanner.Api.Services.InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        db.Payments.AddRange(other, settled); await db.SaveChangesAsync();
        var winnerId = !otherCancelled && otherAmount > 100 ? other.Id : originalId;
        (await db.Payments.SingleAsync(p => p.Id == winnerId)).PaidAmount = 25m;
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = fixture.OwnerId,
            PaymentId = winnerId,
            InvoiceId = invoiceId,
            Amount = 25m,
            Direction = ClientBalanceDirection.Debit,
            Type = ClientBalanceTransactionType.CreditApplied,
            Description = "Synthetic historical representative credit"
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        if (db.Database.IsSqlite() && otherAmount == 100)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Payments\" SET \"Amount\" = '100.00' WHERE \"Id\" = {other.Id}");
        var request = Request(fixture); request.Amount = 40m;
        Assert.IsType<OkObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(70m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == winnerId)).PaidAmount);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == (winnerId == originalId ? other.Id : originalId))).PaidAmount);
        Assert.Equal(65m, await db.ClientBalanceTransactions.Where(t => t.Type == ClientBalanceTransactionType.CreditApplied).SumAsync(t => t.Amount));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ChildApply_WithUnchangedParentAmount_DoesNotReuseItsPaidTotalTwice(bool legalEntity, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var parentId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = parentId };
        Result(await ApplyAsync(db, parent, Request(parent)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var request = Request(fixture); if (legacy) request.RequestId = null;
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExistingPaymentWithUncoveredDebit_IsRejectedWithoutRepairOrAdditionalSpend(bool legalEntity, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = legalEntity ? null : fixture.OwnerId,
            LegalEntityId = legalEntity ? fixture.OwnerId : null,
            PaymentId = fixture.PaymentId,
            Amount = 25m,
            Type = ClientBalanceTransactionType.CreditApplied,
            Direction = ClientBalanceDirection.Debit,
            Description = "Synthetic historical inconsistency"
        });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var request = Request(fixture); if (legacy) request.RequestId = null;
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ChildApply_WithEnoughSettledAmount_CoversBothLevelsWithoutMovingHistory(bool legalEntity, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var parentId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var sibling = PaymentFor(fixture); sibling.PaidAmount = 30m;
        sibling.InvoiceId = (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == parentId)).InvoiceId;
        sibling.SystemNotes = PayPlanner.Api.Services.InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        db.Payments.Add(sibling); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var parent = fixture with { PaymentId = parentId };
        Result(await ApplyAsync(db, parent, Request(parent)));
        var request = Request(fixture); request.Amount = 40m;
        if (legacy) request.RequestId = null;
        Assert.IsType<OkObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(70m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == parentId)).PaidAmount);
        Assert.Equal(40m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount);
        Assert.Equal(30m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == sibling.Id)).PaidAmount);
        Assert.Equal(65m, await db.ClientBalanceTransactions.Where(t => t.Type == ClientBalanceTransactionType.CreditApplied).SumAsync(t => t.Amount));
        Assert.Equal(legacy ? 1 : 2, await db.BalanceApplicationReceipts.CountAsync());
        if (!legacy) { Assert.True(Result(await ApplyAsync(db, fixture, request)).IsReplay); Assert.Equal(3, await db.ClientBalanceTransactions.CountAsync()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedChildApply_CanBeFencedWithoutLosingTheParentReceipt(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var parentId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = parentId };
        Result(await ApplyAsync(db, parent, Request(parent)));
        var request = Request(fixture); request.Amount = 10m;
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        var cancelled = BalanceApplicationCancellationTests.CancelResult(await BalanceApplicationCancellationTests.CancelAsync(db, fixture, request.RequestId!.Value));
        Assert.Equal("Cancelled", cancelled.State);
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == parentId)).PaidAmount);
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync());
        Assert.Equal(2, await db.BalanceApplicationReceipts.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AmbiguousInvoiceOwner_CannotBeUsedByEitherProtocol(bool legalEntity, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var parentId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var parent = await db.Payments.SingleAsync(p => p.Id == parentId);
        if (legalEntity)
        {
            var other = new LegalEntity { ShortName = "Synthetic foreign company", FullName = "Synthetic foreign company" };
            db.LegalEntities.Add(other); await db.SaveChangesAsync(); parent.LegalEntityId = other.Id;
        }
        else
        {
            var other = new Client { Name = "Synthetic foreign client" };
            db.Clients.Add(other); await db.SaveChangesAsync(); parent.ClientId = other.Id;
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var request = Request(fixture); if (legacy) request.RequestId = null;
        Assert.IsType<NotFoundResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DirtyContext_IsNotSavedOrDiscarded(bool legalEntity, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var actor = await db.Users.SingleAsync(); actor.FullName = "Unsaved unrelated edit";
        var request = Request(fixture); if (legacy) request.RequestId = null;
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.True(db.ChangeTracker.HasChanges()); Assert.Equal("Unsaved unrelated edit", actor.FullName);
        Assert.NotEqual(actor.FullName, (await db.Users.AsNoTracking().SingleAsync()).FullName);
        Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExternalTransaction_IsNotCommittedOrDisposed(bool legalEntity, bool legacy)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var request = Request(fixture); if (legacy) request.RequestId = null;
        Assert.IsType<ConflictObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false, false, 10)]
    [InlineData(true, false, 10)]
    [InlineData(false, true, 10)]
    [InlineData(true, true, 10)]
    [InlineData(false, false, 40)]
    [InlineData(true, false, 40)]
    [InlineData(false, true, 40)]
    [InlineData(true, true, 40)]
    public async Task ChildApply_CannotEraseOrOverlapTheRepresentativesAppliedCredit(bool legalEntity, bool legacy, int amount)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, legalEntity);
        var parentId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = parentId };
        Result(await ApplyAsync(db, parent, Request(parent)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var request = Request(fixture); request.Amount = amount;
        if (legacy) request.RequestId = null;
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }
}

public sealed class BalanceApplicationServiceCreditGuardPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task NativeDeniedApply_DoesNotLockTheBusyPayment(bool legalEntity, bool legacy, bool hidden)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await SeedAsync(seed, legalEntity);
            if (hidden) { foreach (var p in await seed.RolePermissions.ToArrayAsync()) p.ViewScope = "own"; }
            else (await seed.RolePermissions.SingleAsync(p => p.Section == "payments")).CanEdit = false;
            await seed.SaveChangesAsync();
        }
        await using var holder = Open(database.ConnectionString);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {fixture.PaymentId} FOR UPDATE");
        await using var writer = Open(database.ConnectionString);
        var request = Request(fixture); if (legacy) request.RequestId = null;
        var result = await ApplyAsync(writer, fixture, request).WaitAsync(TimeSpan.FromSeconds(10));
        if (hidden) Assert.IsType<NotFoundResult>(result); else Assert.IsType<ForbidResult>(result);
        Assert.Equal(0m, (await writer.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await writer.BalanceApplicationReceipts.ToArrayAsync());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task NativeCanonicalRepresentative_UsesTheExistingAmountAndIdOrder()
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var db = Open(database.ConnectionString);
        await BalanceApplicationServiceCreditGuardTests.AssertCanonicalSelectionAsync(db, 200, false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeChildApply_RejectsOverlappingCreditAndRollsBackBothProtocols(bool legalEntity, bool legacy)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legalEntity);
        var parentId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = parentId };
        Result(await ApplyAsync(db, parent, Request(parent)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var request = Request(fixture); request.Amount = 40m; if (legacy) request.RequestId = null;
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeUnchangedRepresentative_IsStillIncludedInTheInvoiceCreditLimit(bool legalEntity, bool legacy)
    {
        await using var database = await template.CreateDatabaseAsync();
        await using var db = Open(database.ConnectionString);
        var fixture = await SeedAsync(db, legalEntity);
        var parentId = await BalanceApplicationRelatedPaymentInvariantTests.LinkInvoiceAsync(db, fixture);
        var parent = fixture with { PaymentId = parentId };
        Result(await ApplyAsync(db, parent, Request(parent)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var request = Request(fixture); if (legacy) request.RequestId = null;
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData("update", false)]
    [InlineData("status", false)]
    [InlineData("managed", false)]
    [InlineData("transfer", false)]
    [InlineData("update", true)]
    [InlineData("status", true)]
    [InlineData("managed", true)]
    [InlineData("transfer", true)]
    public async Task NativeDeniedWriter_DoesNotAttemptToLockTheBusyInaccessiblePayment(string operation, bool hidden)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await SeedAsync(seed, false);
            if (operation == "managed") await BalanceApplicationRemainderWriterTests.MarkManagedAsync(seed, fixture);
            var permission = await seed.RolePermissions.SingleAsync(p => p.Section == "payments");
            if (hidden) { foreach (var p in await seed.RolePermissions.ToArrayAsync()) p.ViewScope = "own"; }
            else permission.CanEdit = false;
            await seed.SaveChangesAsync();
        }
        await using var holder = Open(database.ConnectionString);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {fixture.PaymentId} FOR UPDATE");
        await using var writer = Open(database.ConnectionString);
        var controller = BalanceApplicationPaymentWriterTests.Controller(writer, fixture);
        var task = operation switch
        {
            "update" => controller.Update(fixture.PaymentId, PaymentFor(fixture), CancellationToken.None),
            "status" => controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Completed" }, CancellationToken.None),
            _ => BalanceApplicationRemainderWriterTests.WriteAsync(writer, fixture, operation, fixture.PaymentId),
        };
        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        if (hidden) Assert.IsType<NotFoundResult>(result); else Assert.IsType<ForbidResult>(result);
        Assert.Equal(0m, (await writer.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Empty(await writer.BalanceApplicationReceipts.ToArrayAsync());
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeLegacyApplyHoldingTarget_ModernApplyConflictsAndUsesFreshState(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString)) fixture = await SeedAsync(seed, legalEntity);
        var barrier = new HoldSave();
        await using var legacy = Open(database.ConnectionString, barrier);
        await using var modern = Open(database.ConnectionString);
        var legacyRequest = Request(fixture); legacyRequest.RequestId = null;
        var pending = ApplyAsync(legacy, fixture, legacyRequest);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var request = Request(fixture);
        IActionResult blocked;
        try { blocked = await ApplyAsync(modern, fixture, request).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<ConflictObjectResult>(blocked);
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        Assert.Empty(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        Result(await ApplyAsync(fresh, fixture, request));
        Assert.Equal(50m, (await fresh.Payments.AsNoTracking().SingleAsync()).PaidAmount);
        Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(3, await fresh.ClientBalanceTransactions.CountAsync());
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
}
