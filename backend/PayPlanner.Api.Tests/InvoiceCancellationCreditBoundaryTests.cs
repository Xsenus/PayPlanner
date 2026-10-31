using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Notifications;
using System.Security.Claims;
using System.Reflection;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class InvoiceCancellationCreditBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_ReallocationCannotUseAnInvoiceWithConflictingHistoricalOwners(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, legalEntity);
        var targetId = await AddTargetAsync(db, fixture); var invoiceId = (await db.Payments.SingleAsync(p => p.Id == targetId)).InvoiceId;
        var foreignClient = new Client { Name = "Unrelated synthetic owner" }; db.Clients.Add(foreignClient); await db.SaveChangesAsync();
        var conflicting = PaymentFor(fixture); conflicting.ClientId = foreignClient.Id; conflicting.LegalEntityId = null; conflicting.InvoiceId = invoiceId;
        db.Payments.Add(conflicting); await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        Assert.IsType<NotFoundResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, Reallocate(targetId), CancellationToken.None)).Result);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    [InlineData(false, 25)]
    [InlineData(true, 25)]
    public async Task CancelUnpaidOnly_PreservesCreditOnTheRepresentative(bool legalEntity, int paidChildAmount)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(); source.PaidAmount = 0m; source.Status = PaymentStatus.Pending; await db.SaveChangesAsync();
        Result(await ApplyAsync(db, fixture, Request(fixture)));
        source = await db.Payments.SingleAsync();
        var paid = PaymentFor(fixture); paid.InvoiceId = source.InvoiceId; paid.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker; paid.PaidAmount = paidChildAmount;
        var unpaid = PaymentFor(fixture); unpaid.InvoiceId = source.InvoiceId; unpaid.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        db.Payments.AddRange(paid, unpaid); await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var result = await Controller(db, fixture).Cancel(fixture.PaymentId, new() { Reason = "Synthetic unpaid-only", CancellationScope = InvoiceCancellationScope.UnpaidInstallmentsOnly }, CancellationToken.None);
        if (paidChildAmount < 25)
        { Assert.IsType<BadRequestObjectResult>(result.Result); Assert.Equal(before, await SnapshotAsync(db)); }
        else
        {
            Assert.IsType<OkObjectResult>(result.Result);
            Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount);
            Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paid.Id)).PaidAmount);
            Assert.Equal(PaymentStatus.Cancelled, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == unpaid.Id)).Status);
        }
        Assert.Equal(2, await db.ClientBalanceTransactions.CountAsync()); Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Fact]
    public async Task Cancel_NotifiesAfterCommitAndRelease_WithoutRepeatingNotification()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, false);
        db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = fixture.PaymentId, UserId = fixture.ActorId }); await db.SaveChangesAsync();
        var notifications = DispatchProxy.Create<INotificationService, NotificationProbe>();
        var probe = (NotificationProbe)(object)notifications;
        probe.OnCall = () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            Assert.Equal(PaymentStatus.Cancelled, db.Payments.AsNoTracking().Single(p => p.Id == fixture.PaymentId).Status);
            Assert.Single(db.ClientBalanceTransactions.AsNoTracking().Where(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice));
        };
        var controller = Controller(db, fixture, notifications);
        var command = new CancelInvoiceRequest { Reason = "Synthetic notification", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance };
        Assert.IsType<OkObjectResult>((await controller.Cancel(fixture.PaymentId, command, CancellationToken.None)).Result);
        Assert.Equal(1, probe.Calls);
        Assert.IsType<OkObjectResult>((await controller.Cancel(fixture.PaymentId, command, CancellationToken.None)).Result);
        Assert.Equal(1, probe.Calls);
    }

    public class NotificationProbe : DispatchProxy
    {
        public int Calls { get; private set; }
        public Action? OnCall { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        { Assert.Equal("NotifyInvoiceChangedAsync", targetMethod?.Name); ++Calls; OnCall?.Invoke(); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Cancel_FailureAfterReallocationWasSaved_RollsBackSourceTargetAndLedger()
    {
        var failure = new FailThirdSave();
        await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: failure);
        var fixture = await SeedInvoiceAsync(db, false); var targetId = await AddTargetAsync(db, fixture);
        var before = await SnapshotAsync(db); failure.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => Controller(db, fixture).Cancel(fixture.PaymentId, Reallocate(targetId), CancellationToken.None));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    private sealed class FailThirdSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        private int saves;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Armed && ++saves == 3) throw new IOException("Synthetic final cancellation write failure");
            return ValueTask.FromResult(result);
        }
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
    public async Task ApplyToCancelledTarget_DoesNotSpendOrCreateReceipt(bool legalEntity, bool legacy, bool workflowOnly)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        if (workflowOnly) source.InvoiceWorkflowStatus = InvoiceWorkflowStatus.Cancelled; else source.Status = PaymentStatus.Cancelled;
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db); var request = Request(fixture); if (legacy) request.RequestId = null;
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(db, fixture, request));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Empty(await db.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(true, InvoiceCancellationMoneyAction.LeaveAsAdvance)]
    [InlineData(false, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(true, InvoiceCancellationMoneyAction.Reallocate)]
    [InlineData(false, InvoiceCancellationMoneyAction.Refund)]
    [InlineData(true, InvoiceCancellationMoneyAction.Refund)]
    public async Task Cancel_CreditFundedInvoice_PreservesTheDebitAndCompensatesExactlyOnce(bool legalEntity, InvoiceCancellationMoneyAction action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); source.PaidAmount = 0m; source.Status = PaymentStatus.Pending;
        await db.SaveChangesAsync();
        var request = Request(fixture); Result(await ApplyAsync(db, fixture, request));
        var originalDebit = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(t => t.Direction == ClientBalanceDirection.Debit);
        var targetId = await AddTargetAsync(db, fixture);
        var cancellation = Reallocate(targetId); cancellation.ReceivedMoneyAction = action; cancellation.RefundSource = MoneyOperationSource.Bank;
        var controller = Controller(db, fixture);
        Assert.IsType<OkObjectResult>((await controller.Cancel(fixture.PaymentId, cancellation, CancellationToken.None)).Result);
        var snapshot = await SnapshotAsync(db);
        Assert.IsType<OkObjectResult>((await controller.Cancel(fixture.PaymentId, cancellation, CancellationToken.None)).Result);
        Assert.Equal(snapshot, await SnapshotAsync(db));
        var storedDebit = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(t => t.Id == originalDebit.Id);
        Assert.Equal(originalDebit.Amount, storedDebit.Amount); Assert.Equal(originalDebit.ExternalKey, storedDebit.ExternalKey);
        Assert.Single(await db.BalanceApplicationReceipts.ToArrayAsync());
        Assert.Equal(0m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount);
        var balance = legalEntity ? await ClientBalanceService.GetLegalEntityBalanceAsync(db, fixture.OwnerId) : await ClientBalanceService.GetBalanceAsync(db, fixture.OwnerId);
        Assert.Equal(action == InvoiceCancellationMoneyAction.LeaveAsAdvance ? 100m : 75m, balance);
        if (action == InvoiceCancellationMoneyAction.Refund) Assert.Equal(25m, (await db.MoneyOperations.SingleAsync()).Amount);
        if (action == InvoiceCancellationMoneyAction.Reallocate) Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == targetId)).PaidAmount);
        Assert.True(Result(await ApplyAsync(db, fixture, request)).IsReplay);
        Assert.Equal(snapshot, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_ConflictingPositiveInstallmentTotals_AreNotRepairedOrCompensated(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        var child = PaymentFor(fixture); child.InvoiceId = source.InvoiceId; child.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker; child.PaidAmount = 40m;
        db.Payments.Add(child); await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, new() { Reason = "Synthetic mismatch", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, CancellationToken.None)).Result);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancel_InsufficientSourceOrTargetCreditCoverage_RollsBackTheWholeCancellation(bool legalEntity, bool badTarget)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedInvoiceAsync(db, legalEntity); var targetId = await AddTargetAsync(db, fixture);
        var badPayment = await db.Payments.SingleAsync(p => p.Id == (badTarget ? targetId : fixture.PaymentId));
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction { ClientId = badPayment.ClientId, LegalEntityId = badPayment.LegalEntityId, PaymentId = badPayment.Id, InvoiceId = badPayment.InvoiceId, Amount = 50m, Direction = ClientBalanceDirection.Debit, Type = ClientBalanceTransactionType.CreditApplied });
        await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, Reallocate(targetId), CancellationToken.None)).Result);
        Assert.Equal(before, await SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Cancel_DirtyAndExternalContext_DoesNotSaveOrDiscardCallerWork()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, false);
        var client = await db.Clients.SingleAsync(); client.Name = "Unsaved unrelated edit";
        Assert.IsType<ConflictObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, Reallocate(999), CancellationToken.None)).Result);
        Assert.Equal(EntityState.Modified, db.Entry(client).State); Assert.NotEqual(client.Name, (await db.Clients.AsNoTracking().SingleAsync()).Name);
        db.ChangeTracker.Clear(); await using var transaction = await db.Database.BeginTransactionAsync();
        Assert.IsType<ConflictObjectResult>((await Controller(db, fixture).Cancel(fixture.PaymentId, Reallocate(999), CancellationToken.None)).Result);
        Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("active")]
    [InlineData("approved")]
    public async Task Cancel_RechecksTheCurrentActorAndAccountsEditPermission(string revoked)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedInvoiceAsync(db, false);
        var user = await db.Users.SingleAsync(u => u.Id == fixture.ActorId);
        if (revoked == "edit") (await db.RolePermissions.SingleAsync(p => p.Section == "accounts")).CanEdit = false;
        if (revoked == "active") user.IsActive = false; if (revoked == "approved") user.IsApproved = false;
        await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var result = await Controller(db, fixture).Cancel(fixture.PaymentId, Reallocate(999), CancellationToken.None);
        if (revoked == "edit") Assert.IsType<ForbidResult>(result.Result); else Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Cancel_SynchronizedRepresentativeAndInstallment_CompensatesTheMoneyOnce(bool legalEntity, bool refund)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedInvoiceAsync(db, legalEntity);
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        var child = PaymentFor(fixture); child.InvoiceId = source.InvoiceId;
        child.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        child.PaidAmount = 25m; child.Status = PaymentStatus.Processing;
        db.Payments.Add(child); await db.SaveChangesAsync();
        var result = await Controller(db, fixture).Cancel(fixture.PaymentId, new()
        {
            Reason = "Synthetic cancellation",
            ReceivedMoneyAction = refund ? InvoiceCancellationMoneyAction.Refund : InvoiceCancellationMoneyAction.LeaveAsAdvance,
            RefundSource = MoneyOperationSource.Bank,
        }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        if (refund) Assert.Equal(25m, (await db.MoneyOperations.AsNoTracking().SingleAsync()).Amount);
        else Assert.Equal(25m, (await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice)).Amount);
        Assert.All(await db.Payments.AsNoTracking().ToArrayAsync(), p => { Assert.Equal(0m, p.PaidAmount); Assert.Equal(PaymentStatus.Cancelled, p.Status); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_ReallocationCannotWriteAnInvisibleInvoiceOfTheSameOwner(bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedInvoiceAsync(db, legalEntity);
        var targetId = await AddTargetAsync(db, fixture);
        (await db.RolePermissions.SingleAsync(p => p.Section == "accounts")).ViewScope = "own";
        db.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = fixture.PaymentId, UserId = fixture.ActorId });
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);
        var result = await Controller(db, fixture).Cancel(fixture.PaymentId, Reallocate(targetId), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    internal static CancelInvoiceRequest Reallocate(int targetId) => new() { Reason = "Synthetic reallocation", ReceivedMoneyAction = InvoiceCancellationMoneyAction.Reallocate, TargetPaymentId = targetId };
    internal static async Task<Fixture> SeedInvoiceAsync(PaymentContext db, bool legalEntity)
    {
        var fixture = await SeedAsync(db, legalEntity);
        var roleId = (await db.Users.SingleAsync(u => u.Id == fixture.ActorId)).RoleId;
        db.RolePermissions.Add(new RolePermission { RoleId = roleId, Section = "accounts", CanView = true, CanEdit = true, ViewScope = "all" });
        var invoice = new Invoice { Number = "Synthetic cancellation source", Date = new DateTime(2026, 10, 31) };
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        source.Invoice = invoice; source.DocumentKind = DocumentKind.Invoice; source.PaidAmount = 25m; source.Status = PaymentStatus.Processing;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear(); return fixture;
    }
    internal static async Task<int> AddTargetAsync(PaymentContext db, Fixture fixture)
    {
        var target = PaymentFor(fixture); target.DocumentKind = DocumentKind.Invoice;
        target.Invoice = new Invoice { Number = "Synthetic cancellation target", Date = new DateTime(2026, 10, 31) };
        db.Payments.Add(target); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); return target.Id;
    }
    internal static async Task<string> SnapshotAsync(PaymentContext db)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            Payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.PaidAmount, p.Status, p.SystemNotes }).ToArrayAsync(),
            Ledger = await db.ClientBalanceTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => new { t.Id, t.Amount, t.PaymentId, t.Type, t.Direction }).ToArrayAsync(),
            Refunds = await db.MoneyOperations.CountAsync(),
        });
    internal static InvoicesController Controller(PaymentContext db, Fixture fixture, INotificationService? notifications = null) => new(db, new NoEmail(), new NoRequirements(), notificationService: notifications)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString())], "synthetic")) } },
    };
    private sealed class NoEmail : IEmailService
    {
        public Task SendRegistrationPendingEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationApprovedEmailAsync(string toEmail, string fullName) => Task.CompletedTask;
        public Task SendRegistrationRejectedEmailAsync(string toEmail, string fullName, string? reason) => Task.CompletedTask;
        public Task SendAdminNewRegistrationNotificationAsync(string adminEmail, string newUserFullName, string newUserEmail) => Task.CompletedTask;
        public Task SendEmailWithAttachmentAsync(string toEmail, string subject, string body, string attachmentFileName, byte[] attachmentBytes, string attachmentContentType) => Task.CompletedTask;
    }
    private sealed class NoRequirements : IFormRequirementsService
    {
        public Task<List<string>> GetRequiredFieldsAsync(string section, CancellationToken ct = default) => Task.FromResult(new List<string>());
        public Task<List<string>> GetMissingFieldsAsync(string section, object model, CancellationToken ct = default) => Task.FromResult(new List<string>());
    }
}

public sealed class InvoiceCancellationCreditPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStandaloneTarget_BusyRowRejectsWithoutLeavingTheSavedAdvance(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; int targetId;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await InvoiceCancellationCreditBoundaryTests.SeedInvoiceAsync(seed, legalEntity);
            var target = PaymentFor(fixture); target.DocumentKind = DocumentKind.Invoice; target.Account = "Synthetic standalone target";
            seed.Payments.Add(target); await seed.SaveChangesAsync(); targetId = target.Id;
        }
        await using var holder = Open(database.ConnectionString); await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {targetId} FOR UPDATE");
        await using var cancelling = Open(database.ConnectionString); var before = await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(cancelling);
        var result = await InvoiceCancellationCreditBoundaryTests.Controller(cancelling, fixture).Cancel(fixture.PaymentId, InvoiceCancellationCreditBoundaryTests.Reallocate(targetId), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(before, await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(cancelling));
        await transaction.RollbackAsync();
        Assert.IsType<OkObjectResult>((await InvoiceCancellationCreditBoundaryTests.Controller(cancelling, fixture).Cancel(fixture.PaymentId, InvoiceCancellationCreditBoundaryTests.Reallocate(targetId), CancellationToken.None)).Result);
        Assert.Equal(25m, (await cancelling.Payments.AsNoTracking().SingleAsync(p => p.Id == targetId)).PaidAmount);
        Assert.Equal(3, await cancelling.ClientBalanceTransactions.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeApplyHoldingSource_CancelConflictsThenCompensatesFreshCredit(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync();
        Fixture fixture;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await InvoiceCancellationCreditBoundaryTests.SeedInvoiceAsync(seed, legalEntity);
            var source = await seed.Payments.SingleAsync(); source.PaidAmount = 0m; source.Status = PaymentStatus.Pending; await seed.SaveChangesAsync();
        }
        var barrier = new HoldSave(); await using var applier = Open(database.ConnectionString, barrier); await using var cancelling = Open(database.ConnectionString);
        var pending = ApplyAsync(applier, fixture, Request(fixture));
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            var rejected = await InvoiceCancellationCreditBoundaryTests.Controller(cancelling, fixture).Cancel(fixture.PaymentId, Advance(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsType<ConflictObjectResult>(rejected.Result);
        }
        finally { barrier.Release.TrySetResult(); }
        Result(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        await using var fresh = Open(database.ConnectionString);
        Assert.IsType<OkObjectResult>((await InvoiceCancellationCreditBoundaryTests.Controller(fresh, fixture).Cancel(fixture.PaymentId, Advance(), CancellationToken.None)).Result);
        var advance = await fresh.ClientBalanceTransactions.SingleAsync(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice);
        Assert.Equal(25m, advance.Amount); Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
        var before = await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(fresh);
        Assert.IsType<BadRequestObjectResult>(await ApplyAsync(fresh, fixture, Request(fixture)));
        Assert.Equal(before, await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(fresh));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCancellationHoldingTarget_ApplyConflictsThenUsesItsCommittedPayment(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; int targetId;
        await using (var seed = Open(database.ConnectionString))
        { fixture = await InvoiceCancellationCreditBoundaryTests.SeedInvoiceAsync(seed, legalEntity); targetId = await InvoiceCancellationCreditBoundaryTests.AddTargetAsync(seed, fixture); }
        var barrier = new HoldSave(); await using var cancelling = Open(database.ConnectionString, barrier); await using var applier = Open(database.ConnectionString);
        var pending = InvoiceCancellationCreditBoundaryTests.Controller(cancelling, fixture).Cancel(fixture.PaymentId, InvoiceCancellationCreditBoundaryTests.Reallocate(targetId), CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var target = fixture with { PaymentId = targetId };
        try { Assert.IsType<ConflictObjectResult>(await ApplyAsync(applier, target, Request(target)).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        await using var fresh = Open(database.ConnectionString);
        Assert.Equal(25m, (await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == targetId)).PaidAmount);
        Result(await ApplyAsync(fresh, target, Request(target)));
        Assert.Equal(50m, (await fresh.Payments.AsNoTracking().SingleAsync(p => p.Id == targetId)).PaidAmount);
        Assert.Equal(4, await fresh.ClientBalanceTransactions.CountAsync()); Assert.Single(await fresh.BalanceApplicationReceipts.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeDeniedCancellation_DoesNotLockTheInaccessibleBusyRow(bool legalEntity, bool hiddenTarget)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture; int targetId;
        await using (var seed = Open(database.ConnectionString))
        {
            fixture = await InvoiceCancellationCreditBoundaryTests.SeedInvoiceAsync(seed, legalEntity); targetId = await InvoiceCancellationCreditBoundaryTests.AddTargetAsync(seed, fixture);
            var permission = await seed.RolePermissions.SingleAsync(p => p.Section == "accounts");
            if (hiddenTarget)
            {
                permission.ViewScope = "own";
                seed.PaymentResponsibles.Add(new PaymentResponsible { PaymentId = fixture.PaymentId, UserId = fixture.ActorId });
            }
            else permission.CanEdit = false;
            await seed.SaveChangesAsync();
        }
        await using var holder = Open(database.ConnectionString); await using var transaction = await holder.Database.BeginTransactionAsync();
        var blockedId = hiddenTarget ? targetId : fixture.PaymentId;
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {blockedId} FOR UPDATE");
        await using var cancelling = Open(database.ConnectionString);
        var before = await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(cancelling);
        var result = await InvoiceCancellationCreditBoundaryTests.Controller(cancelling, fixture).Cancel(fixture.PaymentId, InvoiceCancellationCreditBoundaryTests.Reallocate(targetId), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        if (hiddenTarget) Assert.IsType<NotFoundResult>(result.Result); else Assert.IsType<ForbidResult>(result.Result);
        Assert.Equal(before, await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(cancelling)); await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeInvalidTargetCredit_RollsBackTheAlreadySavedAdvance(bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var fixture = await InvoiceCancellationCreditBoundaryTests.SeedInvoiceAsync(db, legalEntity); var targetId = await InvoiceCancellationCreditBoundaryTests.AddTargetAsync(db, fixture);
        var target = await db.Payments.SingleAsync(p => p.Id == targetId);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction { ClientId = target.ClientId, LegalEntityId = target.LegalEntityId, PaymentId = target.Id, InvoiceId = target.InvoiceId, Amount = 50m, Direction = ClientBalanceDirection.Debit, Type = ClientBalanceTransactionType.CreditApplied });
        await db.SaveChangesAsync(); var before = await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>((await InvoiceCancellationCreditBoundaryTests.Controller(db, fixture).Cancel(fixture.PaymentId, InvoiceCancellationCreditBoundaryTests.Reallocate(targetId), CancellationToken.None)).Result);
        Assert.Equal(before, await InvoiceCancellationCreditBoundaryTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction);
    }

    private static CancelInvoiceRequest Advance() => new() { Reason = "Synthetic advance", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance };
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
}
