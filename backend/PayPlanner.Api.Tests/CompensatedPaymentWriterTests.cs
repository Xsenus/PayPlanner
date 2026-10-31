using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class CompensatedPaymentWriterTests
{
    [Fact]
    public async Task ClosedReceiptNotes_RetainTheExistingParentInvoiceVisibilityCheck()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedCancelledAsync(db, false, false, false);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).ViewScope = "own";
        db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = seed.Receipt.PaymentId, UserId = seed.Receipt.ActorId });
        await db.SaveChangesAsync();
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, seed.Receipt);
        var model = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.GetById(seed.Receipt.PaymentId, CancellationToken.None)).Value); model.Notes = "Do not bypass the hidden parent";
        var before = await SnapshotAsync(db);
        Assert.IsType<NotFoundResult>(await controller.Update(seed.Receipt.PaymentId, model, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Writer_DirtyAndExternalContext_ArePreserved(bool deleting)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true; await db.SaveChangesAsync();
        var client = await db.Clients.SingleAsync(); client.Name = "Unsaved caller edit";
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, deleting ? "delete" : "paid"));
        Assert.Equal(EntityState.Modified, db.Entry(client).State); Assert.NotEqual(client.Name, (await db.Clients.AsNoTracking().SingleAsync()).Name);
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, deleting ? "delete" : "paid"));
        Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
        Assert.Single(await db.Payments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelatedRemainder_WithCancellationAdvance_CannotBeChangedThroughItsOpenSource(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legalEntity);
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        var model = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.GetById(fixture.PaymentId, CancellationToken.None)).Value);
        model.PaidAmount = 25m;
        var response = Assert.IsType<TransferPaymentRemainderResponse>(Assert.IsType<OkObjectResult>(await controller.TransferPartialRemainder(fixture.PaymentId,
            new() { Payment = model, RemainderDate = new DateTime(2026, 11, 15) }, CancellationToken.None)).Value);
        var remainder = await db.Payments.SingleAsync(p => p.Id == response.RemainderPayment.Id);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction { ClientId = remainder.ClientId, LegalEntityId = remainder.LegalEntityId, PaymentId = remainder.Id, Amount = 20m, Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice, Direction = ClientBalanceDirection.Credit, Description = "Preserve historical compensation" });
        await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await controller.RollbackPartialRemainderTransfer(fixture.PaymentId, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpaidDeletion_UsesDeletePermissionWithoutRequiringEditPermission(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legalEntity);
        var permission = await db.RolePermissions.SingleAsync(p => p.Section == "payments"); permission.CanDelete = true; permission.CanEdit = false;
        await db.SaveChangesAsync();
        Assert.IsType<NoContentResult>(await BalanceApplicationPaymentWriterTests.Controller(db, fixture).Delete(fixture.PaymentId, CancellationToken.None));
        Assert.False(await db.Payments.AnyAsync()); Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("date")]
    [InlineData("planned")]
    [InlineData("paidDate")]
    [InlineData("number")]
    [InlineData("kind")]
    [InlineData("marker")]
    [InlineData("owner")]
    [InlineData("invoice")]
    [InlineData("direction")]
    public async Task NotesCannotMaskAChangeOfProtectedFinancialFields(string field)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedCancelledAsync(db, false, false, false);
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, seed.Receipt);
        var model = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.GetById(seed.Receipt.PaymentId, CancellationToken.None)).Value);
        model.Notes = "This must roll back with the money change";
        if (field == "amount") model.Amount = 90m;
        if (field == "date") model.Date = model.Date.AddDays(1);
        if (field == "planned") model.PlannedDate = model.PlannedDate.AddDays(1);
        if (field == "paidDate") model.PaidDate = new DateTime(2026, 11, 1);
        if (field == "number") model.PaymentOrderNumber = "Changed financial reference";
        if (field == "kind") model.DocumentKind = DocumentKind.Accrual;
        if (field == "marker") model.SystemNotes += "\n[pp:installment-payment]";
        if (field == "owner") { var owner = new Client { Name = "Other synthetic owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); model.ClientId = owner.Id; }
        if (field == "invoice") { var targetId = await InvoiceCancellationCreditBoundaryTests.AddTargetAsync(db, seed.Receipt); model.InvoiceId = (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == targetId)).InvoiceId; }
        if (field == "direction") model.Type = PaymentType.Expense;
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await controller.Update(seed.Receipt.PaymentId, model, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.NotEqual(model.Notes, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Receipt.PaymentId)).Notes);
    }

    [Theory]
    [InlineData(false, false, "paid")]
    [InlineData(true, false, "paid")]
    [InlineData(false, true, "paid")]
    [InlineData(true, true, "paid")]
    [InlineData(false, false, "unlink")]
    [InlineData(true, false, "unlink")]
    [InlineData(false, true, "unlink")]
    [InlineData(true, true, "unlink")]
    [InlineData(false, false, "status")]
    [InlineData(true, false, "status")]
    [InlineData(false, true, "status")]
    [InlineData(true, true, "status")]
    [InlineData(false, false, "delete")]
    [InlineData(true, false, "delete")]
    [InlineData(false, true, "delete")]
    [InlineData(true, true, "delete")]
    public async Task CompensatedReceipt_FinancialWriteIsRejectedWithoutChangingHistory(bool legalEntity, bool refund, string operation)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedCancelledAsync(db, legalEntity, refund, false);
        var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, seed.Receipt, operation));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task CompensatedReceipt_NotesSaveWithoutRecalculatingOrRewritingMoney(bool legalEntity, bool refund, bool creditFunded)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await SeedCancelledAsync(db, legalEntity, refund, creditFunded);
        var before = await SnapshotAsync(db);
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, seed.Receipt);
        var model = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.GetById(seed.Receipt.PaymentId, CancellationToken.None)).Value);
        model.Notes = "New permitted note"; model.Description = "Clarified receipt description";
        Assert.IsType<OkObjectResult>(await controller.Update(seed.Receipt.PaymentId, model, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Receipt.PaymentId);
        Assert.Equal(model.Notes, stored.Notes); Assert.Equal(model.Description, stored.Description);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedInvoice_ManagedWriteCannotReopenItsPrimaryRecord(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedCancelledAsync(db, legalEntity, false, false);
        var before = await SnapshotAsync(db); var root = seed.Receipt with { PaymentId = seed.RootId };
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, root);
        var model = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.GetById(root.PaymentId, CancellationToken.None)).Value);
        model.Status = PaymentStatus.Pending;
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateInvoiceManaged(root.PaymentId, model, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenPayment_WithAppliedCredit_CannotLoseItsLedgerReferenceByDeletion(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legalEntity);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true; await db.SaveChangesAsync();
        Result(await ApplyAsync(db, fixture, Request(fixture))); var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(db, fixture).Delete(fixture.PaymentId, CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    internal sealed record Seed(Fixture Receipt, int RootId);
    internal static async Task<Seed> SeedCancelledAsync(PaymentContext db, bool legalEntity, bool refund, bool creditFunded)
    {
        var seed = await SeedOpenAsync(db, legalEntity, creditFunded); var root = seed.Receipt with { PaymentId = seed.RootId };
        Assert.IsType<OkObjectResult>((await InvoiceCancellationCreditBoundaryTests.Controller(db, root).Cancel(root.PaymentId,
            new CancelInvoiceRequest { Reason = "Synthetic compensation", ReceivedMoneyAction = refund ? InvoiceCancellationMoneyAction.Refund : InvoiceCancellationMoneyAction.LeaveAsAdvance, RefundSource = MoneyOperationSource.Bank }, CancellationToken.None)).Result);
        return seed;
    }
    internal static async Task<Seed> SeedOpenAsync(PaymentContext db, bool legalEntity, bool creditFunded = false)
    {
        var root = await InvoiceCancellationCreditBoundaryTests.SeedInvoiceAsync(db, legalEntity);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true;
        var parent = await db.Payments.SingleAsync(p => p.Id == root.PaymentId);
        var receipt = PaymentFor(root); receipt.InvoiceId = parent.InvoiceId;
        receipt.SystemNotes = "Preserve original receipt history"; receipt.PaidDate = new DateTime(2026, 10, 30); receipt.LastPaymentDate = receipt.PaidDate;
        if (creditFunded) { parent.PaidAmount = 0m; parent.Status = PaymentStatus.Pending; }
        else { receipt.PaidAmount = 25m; receipt.Status = PaymentStatus.Processing; }
        db.Payments.Add(receipt); await db.SaveChangesAsync();
        var target = root with { PaymentId = receipt.Id };
        if (creditFunded) Result(await ApplyAsync(db, target, Request(target)));
        return new(target, root.PaymentId);
    }
    internal static async Task<IActionResult> WriteAsync(PaymentContext db, Fixture fixture, string operation)
    {
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        if (operation == "delete") return await controller.Delete(fixture.PaymentId, CancellationToken.None);
        if (operation == "status") return await controller.UpdateStatus(fixture.PaymentId, new UpdatePaymentStatusRequest { Status = "Cancelled" }, CancellationToken.None);
        var model = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.GetById(fixture.PaymentId, CancellationToken.None)).Value);
        if (operation == "paid") model.PaidAmount = 10m;
        if (operation == "unlink") { model.InvoiceId = null; model.Account = null; model.AccountDate = null; }
        return await controller.Update(fixture.PaymentId, model, CancellationToken.None);
    }
    internal static async Task<string> SnapshotAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            History = await InvoiceCancellationHistoryTests.HistorySnapshotAsync(db),
            Financial = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new
            {
                p.Id,
                p.Amount,
                p.PaidAmount,
                p.Date,
                p.PlannedDate,
                p.PaidDate,
                p.LastPaymentDate,
                p.Type,
                p.Status,
                p.InvoiceId,
                p.DocumentKind,
                p.ClientId,
                p.LegalEntityId,
                p.ExpenseClientId,
                p.ExpenseLegalEntityId,
                p.ContractId,
                p.ClientCaseId,
                p.PaymentSourceId,
                p.PaymentOrderNumber,
                p.PaymentDocumentDate,
                p.InvoiceWorkflowStatus,
                p.InvoiceCancelledAt,
                p.InvoiceCancelledReason,
            }).ToArrayAsync(),
        });
}

public sealed class CompensatedPaymentWriterPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, "paid")]
    [InlineData(true, "paid")]
    [InlineData(false, "delete")]
    [InlineData(true, "delete")]
    public async Task CancellationHoldingReceipt_BlocksWriterThenProtectsCommittedHistory(bool legalEntity, string operation)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var barrier = new HoldSave(); await using var cancelling = Open(database.ConnectionString, barrier); await using var editor = Open(database.ConnectionString);
        var root = seed.Receipt with { PaymentId = seed.RootId };
        var pending = InvoiceCancellationCreditBoundaryTests.Controller(cancelling, root).Cancel(root.PaymentId,
            new() { Reason = "Synthetic concurrent cancellation", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await CompensatedPaymentWriterTests.WriteAsync(editor, seed.Receipt, operation).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(editor);
        Assert.IsType<BadRequestObjectResult>(await CompensatedPaymentWriterTests.WriteAsync(editor, seed.Receipt, operation));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(editor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataHoldingReceipt_DeletionConflictsWithoutLosingNotesOrMoney(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedCancelledAsync(db, legalEntity, false, true);
        var barrier = new HoldSave(); await using var annotating = Open(database.ConnectionString, barrier); await using var deleting = Open(database.ConnectionString);
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(deleting);
        var controller = BalanceApplicationPaymentWriterTests.Controller(annotating, seed.Receipt);
        var model = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await controller.GetById(seed.Receipt.PaymentId, CancellationToken.None)).Value); model.Notes = "Concurrent allowed note";
        var pending = controller.Update(seed.Receipt.PaymentId, model, CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await BalanceApplicationPaymentWriterTests.Controller(deleting, seed.Receipt).Delete(seed.Receipt.PaymentId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(deleting));
        Assert.Equal(model.Notes, (await deleting.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Receipt.PaymentId)).Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpaidDeletionHoldingPayment_ApplyConflictsAndCannotCreateAnOrphanReceipt(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString))
        { fixture = await SeedAsync(db, legalEntity); (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true; await db.SaveChangesAsync(); }
        var barrier = new HoldSave(); await using var deleting = Open(database.ConnectionString, barrier); await using var applying = Open(database.ConnectionString);
        var pending = BalanceApplicationPaymentWriterTests.Controller(deleting, fixture).Delete(fixture.PaymentId, CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await ApplyAsync(applying, fixture, Request(fixture)).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<NoContentResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.IsType<NotFoundResult>(await ApplyAsync(applying, fixture, Request(fixture)));
        Assert.Empty(await applying.BalanceApplicationReceipts.ToArrayAsync()); Assert.Single(await applying.ClientBalanceTransactions.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DeniedDeletion_DoesNotLockTheBusyInaccessibleRow(bool legalEntity, bool hidden)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString))
        {
            seed = await CompensatedPaymentWriterTests.SeedCancelledAsync(db, legalEntity, false, false);
            var permission = await db.RolePermissions.SingleAsync(p => p.Section == "payments");
            if (hidden) permission.ViewScope = "own"; else permission.CanDelete = false;
            await db.SaveChangesAsync();
        }
        await using var holder = Open(database.ConnectionString); await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {seed.Receipt.PaymentId} FOR UPDATE");
        await using var deleting = Open(database.ConnectionString);
        var result = await BalanceApplicationPaymentWriterTests.Controller(deleting, seed.Receipt).Delete(seed.Receipt.PaymentId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (hidden) Assert.IsType<NotFoundResult>(result); else Assert.IsType<ForbidResult>(result);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task FailureDuringInvoiceRefresh_RollsBackTheEarlierDeletion()
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, false);
        var failure = new FailSecondSave(); await using var deleting = Open(database.ConnectionString, failure);
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(deleting);
        await Assert.ThrowsAsync<IOException>(() => BalanceApplicationPaymentWriterTests.Controller(deleting, seed.Receipt).Delete(seed.Receipt.PaymentId, CancellationToken.None));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(deleting)); Assert.Null(deleting.Database.CurrentTransaction); Assert.False(deleting.ChangeTracker.HasChanges());
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
    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        private int saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var pending = eventData.Context!.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            if (pending.Length > 0 && pending.All(e => e.Entity is ObjectAuditEvent)) return ValueTask.FromResult(result);
            if (++saves == 2) throw new IOException("Synthetic second delete save failure");
            return ValueTask.FromResult(result);
        }
    }
}
