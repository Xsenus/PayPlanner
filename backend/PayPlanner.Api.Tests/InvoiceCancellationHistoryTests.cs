using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.InvoiceCancellationCreditBoundaryTests;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class InvoiceCancellationHistoryTests
{
    [Theory]
    [InlineData(499, 499)]
    [InlineData(498, 500)]
    public async Task BoundedDescription_DoesNotSplitAUnicodeCharacter(int prefixLength, int expectedLength)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, false);
        var source = await db.Payments.SingleAsync(); var context = Controller(db, fixture).HttpContext;
        Assert.True(await ClientBalanceService.TryCreateCancelledInvoiceAdvanceAsync(db, source, 25m, "x", DateTime.UtcNow, context));
        var probe = db.ClientBalanceTransactions.Local.Single(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice);
        var descriptionPrefixLength = probe.Description.Length - 1;
        db.Entry(probe).State = EntityState.Detached;
        var reason = new string('x', prefixLength - descriptionPrefixLength) + "🧾tail";
        Assert.True(await ClientBalanceService.TryCreateCancelledInvoiceAdvanceAsync(db, source, 25m, reason, DateTime.UtcNow, context));
        await db.SaveChangesAsync();
        var result = (await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice)).Description;
        Assert.Equal(expectedLength, result.Length); new System.Text.UTF8Encoding(false, true).GetByteCount(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpensePayment_IsPreservedAndDoesNotIncreaseIncomingCompensation(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(); var expense = PaymentFor(fixture); expense.InvoiceId = source.InvoiceId; expense.Type = PaymentType.Expense; expense.PaidAmount = 40m;
        db.Payments.Add(expense); await db.SaveChangesAsync(); var before = await ReceiptSnapshotAsync(db, expense.Id);
        Assert.IsType<OkObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, new() { Reason = "Synthetic expense boundary", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, CancellationToken.None)).Result);
        Assert.Equal(25m, (await db.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice)).Amount);
        Assert.Equal(before, await ReceiptSnapshotAsync(db, expense.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedExpenseUnderIncomingInvoice_IsNotSilentlyCancelled(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(); var expense = PaymentFor(fixture); expense.InvoiceId = source.InvoiceId; expense.Type = PaymentType.Expense; expense.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        db.Payments.Add(expense); await db.SaveChangesAsync(); var before = await HistorySnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, new() { Reason = "Synthetic incompatible schedule", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, CancellationToken.None)).Result);
        Assert.Equal(before, await HistorySnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnrelatedExpense_IsNotMistakenForACancellationRefund(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync();
        db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = source.InvoiceId, Amount = 25m, Direction = MoneyOperationDirection.Expense, Currency = "RUB", Purpose = "Unrelated cost", MatchComment = "Ordinary matched expense", Date = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) });
        await db.SaveChangesAsync();
        Assert.IsType<OkObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, new() { Reason = "Synthetic ordinary expense", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, CancellationToken.None)).Result);
        Assert.Equal("Unrelated cost", (await db.MoneyOperations.AsNoTracking().SingleAsync()).Purpose);
        Assert.Equal(25m, (await db.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice)).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreatingAdvanceAgain_PreservesTheOriginalLedgerEntry(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync();
        Assert.True(await ClientBalanceService.TryCreateCancelledInvoiceAdvanceAsync(db, source, 25m, "Original reason", DateTime.UtcNow, Controller(db, fixture).HttpContext));
        await db.SaveChangesAsync(); var before = await HistorySnapshotAsync(db);
        Assert.False(await ClientBalanceService.TryCreateCancelledInvoiceAdvanceAsync(db, source, 75m, "Different reason", DateTime.UtcNow, Controller(db, fixture).HttpContext));
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Equal(before, await HistorySnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, false, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(true, false, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(false, false, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(true, false, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(false, false, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(true, false, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(false, true, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(true, true, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(false, true, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(true, true, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(false, true, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(true, true, InvoiceCancellationMoneyAction.Refund)]
    public async Task ActiveSource_WithExistingCancellationCompensation_IsNotCompensatedAgain(bool legalEntity, bool previousRefund, InvoiceCancellationMoneyAction action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertExistingCompensationAsync(db, legalEntity, previousRefund, action);
    }

    internal static async Task AssertExistingCompensationAsync(PaymentContext db, bool legalEntity, bool previousRefund, InvoiceCancellationMoneyAction action)
    {
        var fixture = await SeedInvoiceAsync(db, legalEntity); var targetId = await AddTargetAsync(db, fixture);
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        if (previousRefund)
            db.MoneyOperations.Add(new MoneyOperation { LinkedInvoiceId = source.InvoiceId, Amount = 75m, Direction = MoneyOperationDirection.Expense, AccountingType = MoneyOperationAccountingType.Fact, Source = MoneyOperationSource.Bank, Currency = "RUB", MatchComment = "Возврат оплаты при аннулировании счёта.", Purpose = "Preserve historical refund", Date = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) });
        else
            db.ClientBalanceTransactions.Add(new ClientBalanceTransaction { ClientId = source.ClientId, LegalEntityId = source.LegalEntityId, InvoiceId = source.InvoiceId, PaymentId = source.Id, Amount = 75m, Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice, Direction = ClientBalanceDirection.Credit, ExternalKey = ClientBalanceService.BuildCancelledInvoiceAdvanceKey(source.Id), Description = "Preserve historical advance" });
        await db.SaveChangesAsync(); var before = await HistorySnapshotAsync(db);
        var command = Reallocate(targetId); command.ReceivedMoneyAction = action; command.RefundSource = MoneyOperationSource.Bank;
        Assert.IsType<ConflictObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, command, CancellationToken.None)).Result);
        Assert.Equal(before, await HistorySnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(true, false, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(false, true, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(true, true, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(false, false, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(true, false, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(false, true, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(true, true, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(false, false, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(true, false, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(false, true, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(true, true, InvoiceCancellationMoneyAction.Refund)]
    public async Task OrdinaryReceipt_CompensatesOnceAndDoesNotReviveTheCancelledInvoice(bool legalEntity, bool parentAlreadySynchronized, InvoiceCancellationMoneyAction action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        await AssertOrdinaryReceiptAsync(db, legalEntity, parentAlreadySynchronized, action);
    }

    internal static async Task AssertOrdinaryReceiptAsync(PaymentContext db, bool legalEntity, bool synchronized, InvoiceCancellationMoneyAction action)
    {
        var fixture = await SeedInvoiceAsync(db, legalEntity); var targetId = await AddTargetAsync(db, fixture);
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        if (!synchronized) { source.PaidAmount = 0m; source.Status = PaymentStatus.Pending; }
        var receipt = PaymentFor(fixture); receipt.InvoiceId = source.InvoiceId; receipt.PaidAmount = 25m; receipt.Status = PaymentStatus.Processing;
        receipt.SystemNotes = "Keep the original receipt"; receipt.PaidDate = new DateTime(2026, 10, 30); receipt.LastPaymentDate = receipt.PaidDate;
        db.Payments.Add(receipt);
        var income = new MoneyOperation { LinkedInvoiceId = source.InvoiceId, Amount = 25m, PaidAmount = 25m, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, Source = MoneyOperationSource.Bank, Currency = "RUB", Purpose = "Keep original incoming movement", Date = new DateTime(2026, 10, 30, 0, 0, 0, DateTimeKind.Utc) };
        db.MoneyOperations.Add(income); await db.SaveChangesAsync();
        var originalReceipt = await ReceiptSnapshotAsync(db, receipt.Id);
        var originalIncome = System.Text.Json.JsonSerializer.Serialize(await db.MoneyOperations.AsNoTracking().SingleAsync(m => m.Id == income.Id));
        var command = Reallocate(targetId); command.ReceivedMoneyAction = action; command.RefundSource = MoneyOperationSource.Bank;
        var controller = Controller(db, fixture);
        Assert.IsType<OkObjectResult>((await controller.Cancel(fixture.PaymentId, command, CancellationToken.None)).Result);
        if (action == InvoiceCancellationMoneyAction.Refund)
            Assert.Equal(25m, (await db.MoneyOperations.AsNoTracking().SingleAsync(m => m.Direction == MoneyOperationDirection.Expense)).Amount);
        else
            Assert.Equal(25m, (await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice)).Amount);
        if (action == InvoiceCancellationMoneyAction.Reallocate) Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == targetId)).PaidAmount);
        Assert.Equal(originalReceipt, await ReceiptSnapshotAsync(db, receipt.Id));
        Assert.Equal(originalIncome, System.Text.Json.JsonSerializer.Serialize(await db.MoneyOperations.AsNoTracking().SingleAsync(m => m.Id == income.Id)));
        await InvoicePaymentStateService.RefreshInvoiceAggregateAsync(db, source.InvoiceId!.Value, DateTime.UtcNow);
        await db.SaveChangesAsync();
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(PaymentStatus.Cancelled, stored.Status); Assert.Equal(0m, stored.PaidAmount); Assert.Equal(InvoiceWorkflowStatus.Cancelled, stored.InvoiceWorkflowStatus);
        Assert.Equal(originalReceipt, await ReceiptSnapshotAsync(db, receipt.Id));
        var opened = Assert.IsType<InvoiceDto>(Assert.IsType<OkObjectResult>((await controller.GetById(fixture.PaymentId, CancellationToken.None)).Result).Value);
        Assert.Equal(PaymentStatus.Cancelled, opened.Status); Assert.Equal(0m, opened.PaidAmount);
        var accounts = new AccountsController(db) { ControllerContext = controller.ControllerContext };
        var registry = Assert.IsType<AccountsPageResponse>(Assert.IsType<OkObjectResult>((await accounts.GetRegistry(null, null, null, null, null, null, null, invoiceId: fixture.PaymentId, ct: CancellationToken.None)).Result).Value);
        var row = Assert.Single(registry.Items);
        Assert.Equal("cancelled", row.Status); Assert.Equal(PaymentStatus.Cancelled, row.Invoice!.Status); Assert.Equal(0m, row.Invoice.PaidAmount);
        var beforeRejectedSpend = await HistorySnapshotAsync(db); var childFixture = fixture with { PaymentId = receipt.Id };
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, childFixture, Request(childFixture)));
        Assert.Equal(beforeRejectedSpend, await HistorySnapshotAsync(db));
        Assert.IsType<OkObjectResult>((await controller.Cancel(fixture.PaymentId, command, CancellationToken.None)).Result);
        Assert.Equal(action == InvoiceCancellationMoneyAction.Refund ? 2 : 1, await db.MoneyOperations.CountAsync());
    }

    [Theory]
    [InlineData(false, 0, DocumentKind.Invoice)]
    [InlineData(true, 0, DocumentKind.Invoice)]
    [InlineData(false, 25, DocumentKind.Invoice)]
    [InlineData(true, 25, DocumentKind.Invoice)]
    [InlineData(false, 0, DocumentKind.Accrual)]
    [InlineData(true, 0, DocumentKind.Accrual)]
    [InlineData(false, 25, DocumentKind.Accrual)]
    [InlineData(true, 25, DocumentKind.Accrual)]
    public async Task HistoricalSiblingDocument_IsNotSilentlyCancelledOrCompensated(bool legalEntity, int paidAmount, DocumentKind kind)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(); var other = PaymentFor(fixture); other.InvoiceId = source.InvoiceId; other.DocumentKind = kind; other.PaidAmount = paidAmount;
        db.Payments.Add(other); await db.SaveChangesAsync(); var before = await HistorySnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, new() { Reason = "Synthetic duplicate", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, CancellationToken.None)).Result);
        Assert.Equal(before, await HistorySnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedRepresentative_IsNotRecalculatedFromItsHistoricalReceipt(bool workflowOnly)
    {
        var representative = new Payment { Id = 1, InvoiceId = 1, Amount = 100m, PaidAmount = workflowOnly ? 25m : 0m, DocumentKind = DocumentKind.Invoice, Status = workflowOnly ? PaymentStatus.Processing : PaymentStatus.Cancelled, InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled, Date = new DateTime(2026, 10, 10), PlannedDate = new DateTime(2026, 10, 10), SystemNotes = "Keep closed metadata" };
        var receipt = new Payment { Id = 2, InvoiceId = 1, Amount = 100m, PaidAmount = 50m, DocumentKind = DocumentKind.Payment, Status = PaymentStatus.Processing, Date = new DateTime(2026, 10, 30), PlannedDate = new DateTime(2026, 10, 30) };
        var before = InvoicePaymentStateService.CaptureState(representative);
        var snapshot = InvoicePaymentStateService.CalculateRepresentativeState(representative, [receipt], DateTime.UtcNow);
        Assert.Equal(before, snapshot); Assert.False(InvoicePaymentStateService.ApplySnapshot(representative, snapshot));
    }

    internal static async Task<string> ReceiptSnapshotAsync(PaymentContext db, int id)
        => System.Text.Json.JsonSerializer.Serialize(await db.Payments.AsNoTracking().Where(p => p.Id == id).Select(p => new { p.Id, p.InvoiceId, p.PaidAmount, p.Amount, p.Status, p.SystemNotes, p.PaidDate, p.LastPaymentDate, p.Type, p.ClientId, p.LegalEntityId }).SingleAsync());
    internal static async Task<string> HistorySnapshotAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Basic = await SnapshotAsync(db),
            Ledger = await db.ClientBalanceTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.ClientId, t.LegalEntityId, t.InvoiceId, t.PaymentId, t.Amount, t.Type, t.Direction, t.ExternalKey, t.Description, t.CreatedAt, t.CreatedByUserId, t.CreatedByUserName }).ToArrayAsync(),
            Money = await db.MoneyOperations.AsNoTracking().OrderBy(m => m.Id).ToArrayAsync(),
        });
}

public sealed class InvoiceCancellationHistoryPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false, InvoiceCancellationMoneyAction.LeaveAsAdvance, false)]
    [InlineData(true, InvoiceCancellationMoneyAction.LeaveAsAdvance, false)]
    [InlineData(false, InvoiceCancellationMoneyAction.Reallocate, false)]
    [InlineData(true, InvoiceCancellationMoneyAction.Reallocate, false)]
    [InlineData(false, InvoiceCancellationMoneyAction.Refund, false)]
    [InlineData(true, InvoiceCancellationMoneyAction.Refund, false)]
    [InlineData(false, InvoiceCancellationMoneyAction.LeaveAsAdvance, true)]
    [InlineData(true, InvoiceCancellationMoneyAction.LeaveAsAdvance, true)]
    [InlineData(false, InvoiceCancellationMoneyAction.Reallocate, true)]
    [InlineData(true, InvoiceCancellationMoneyAction.Reallocate, true)]
    [InlineData(false, InvoiceCancellationMoneyAction.Refund, true)]
    [InlineData(true, InvoiceCancellationMoneyAction.Refund, true)]
    public async Task NativeLongCancellationReason_PreservesFullReason(bool legalEntity, InvoiceCancellationMoneyAction action, bool unicode)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await SeedInvoiceAsync(db, legalEntity); var targetId = await AddTargetAsync(db, fixture);
        var reason = unicode ? string.Concat(Enumerable.Repeat("🧾", 500)) : new string('Д', 1000);
        var command = Reallocate(targetId); command.ReceivedMoneyAction = action; command.Reason = reason;
        command.RefundSource = unicode ? MoneyOperationSource.Cash : MoneyOperationSource.Bank;
        Assert.IsType<OkObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, command, CancellationToken.None)).Result);
        Assert.Equal(reason, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).InvoiceCancelledReason);
        foreach (var description in await db.ClientBalanceTransactions.AsNoTracking().Select(t => t.Description).ToArrayAsync())
        { Assert.True(description.Length <= 500); new System.Text.UTF8Encoding(false, true).GetByteCount(description); }
        foreach (var purpose in await db.MoneyOperations.AsNoTracking().Select(m => m.Purpose).ToArrayAsync())
        { Assert.NotNull(purpose); Assert.True(purpose.Length <= 1000); new System.Text.UTF8Encoding(false, true).GetByteCount(purpose); }
        if (action == InvoiceCancellationMoneyAction.Refund && unicode)
        { var cash = await db.CashOrders.AsNoTracking().SingleAsync(); Assert.Equal(reason, cash.Comment); Assert.True(cash.Basis!.Length <= 1000); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeExistingCompensation_IsImmutable(bool legalEntity, bool refund)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await InvoiceCancellationHistoryTests.AssertExistingCompensationAsync(db, legalEntity, refund, refund ? InvoiceCancellationMoneyAction.Refund : InvoiceCancellationMoneyAction.LeaveAsAdvance);
    }

    [Theory]
    [InlineData(false, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(true, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(false, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(true, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(false, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(true, InvoiceCancellationMoneyAction.Refund)]
    public async Task NativeOrdinaryReceipt_PreservesMoneyAndClosedState(bool legalEntity, InvoiceCancellationMoneyAction action)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        await InvoiceCancellationHistoryTests.AssertOrdinaryReceiptAsync(db, legalEntity, false, action);
    }

    private static PostgreSqlPaymentContext Open(string connection)
        => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).Options);
}
