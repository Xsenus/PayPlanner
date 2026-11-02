using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentCreateFinancialBoundaryTests
{
    public static IEnumerable<object[]> ClosedCases()
    {
        foreach (var attempt in new[] { false, true })
            foreach (var legalEntity in new[] { false, true })
                foreach (var refund in new[] { false, true })
                    foreach (var number in new[] { false, true }) yield return [attempt, legalEntity, refund, number];
    }
    [Theory]
    [MemberData(nameof(ClosedCases))]
    public async Task ClosedInvoice_CannotReceiveNewPaymentByIdOrNumber(bool attempt, bool legalEntity, bool refund, bool number)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await CompensatedPaymentWriterTests.SeedCancelledAsync(db, legalEntity, refund, false);
        var model = await NewPaymentAsync(db, seed.Receipt, number);
        var controller = Controller(db, seed.Receipt); Guid? id = attempt ? await RegisterAsync(controller) : null;
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CreateAsync(controller, model, id));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
        if (id.HasValue) Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
        Assert.False(db.ChangeTracker.HasChanges()); Assert.Null(db.Database.CurrentTransaction);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OpenInvoice_CreatedPaymentAndAggregateAreCommittedTogether(bool attempt, bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var model = await NewPaymentAsync(db, seed.Receipt); var controller = Controller(db, seed.Receipt); Guid? id = attempt ? await RegisterAsync(controller) : null;
        var created = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CreateAsync(controller, model, id)).Value);
        Assert.Equal(10m, created.PaidAmount); Assert.Equal(35m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.RootId)).PaidAmount);
        Assert.Equal(3, await db.Payments.CountAsync()); Assert.Null(db.Database.CurrentTransaction);
        if (attempt)
        {
            var replay = Assert.IsType<Payment>(Assert.IsType<OkObjectResult>(await CreateAsync(controller, model, id)).Value);
            Assert.Equal(created.Id, replay.Id); Assert.Equal(3, await db.Payments.CountAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryCreate_DirtyOrExternalContextIsPreserved(bool external)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, false);
        var model = await NewPaymentAsync(db, seed.Receipt); var controller = Controller(db, seed.Receipt);
        if (external)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.IsType<ConflictObjectResult>(await PaymentOperationKeyTestSetup.CreateAsync(controller, db, model)); Assert.Same(transaction, db.Database.CurrentTransaction); await transaction.RollbackAsync();
        }
        else
        {
            var owner = await db.Clients.SingleAsync(); owner.Name = "Unsaved caller edit";
            Assert.IsType<ConflictObjectResult>(await PaymentOperationKeyTestSetup.CreateAsync(controller, db, model)); Assert.Equal(EntityState.Modified, db.Entry(owner).State);
        }
        Assert.Equal(2, await db.Payments.CountAsync());
    }

    public static IEnumerable<object[]> ActorCases()
    {
        foreach (var attempt in new[] { false, true })
            foreach (var denial in new[] { "permission", "inactive", "unapproved", "missing" }) yield return [attempt, denial];
    }
    [Theory]
    [MemberData(nameof(ActorCases))]
    public async Task CurrentPermissionAndApprovedActorAreRequired(bool attempt, string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, false);
        var model = await NewPaymentAsync(db, seed.Receipt); var controller = Controller(db, seed.Receipt); Guid? id = attempt ? await RegisterAsync(controller) : null;
        var user = await db.Users.SingleAsync();
        if (denial == "permission") (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanCreate = false;
        if (denial == "inactive") user.IsActive = false;
        if (denial == "unapproved") user.IsApproved = false;
        await db.SaveChangesAsync();
        if (denial == "missing") controller = Controller(db, seed.Receipt with { ActorId = int.MaxValue });
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        var result = await CreateAsync(controller, model, id);
        if (attempt || denial == "permission") Assert.IsType<ForbidResult>(result); else Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
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
    public async Task Overpayment_UsesTheSelectedLockedInstallmentWithoutCreatingAnotherAdvance(bool attempt, bool legalEntity, bool explicitTarget)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedInstallmentAsync(db, legalEntity);
        var model = await NewOverpaymentAsync(db, seed.Receipt, explicitTarget); var controller = Controller(db, seed.Receipt); Guid? id = attempt ? await RegisterAsync(controller) : null;
        var created = Assert.IsType<Payment>(Assert.IsType<CreatedResult>(await CreateAsync(controller, model, id)).Value);
        Assert.Equal(10m, created.PaidAmount); Assert.Equal(30m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Receipt.PaymentId)).PaidAmount);
        Assert.Equal(40m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.RootId)).PaidAmount);
        Assert.Single(await db.ClientBalanceTransactions.ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Overpayment_CannotChangeAChildWithHistoricalCompensation(bool attempt, bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await SeedInstallmentAsync(db, legalEntity);
        db.ClientBalanceTransactions.Add(new ClientBalanceTransaction
        {
            ClientId = legalEntity ? null : seed.Receipt.OwnerId,
            LegalEntityId = legalEntity ? seed.Receipt.OwnerId : null,
            PaymentId = seed.Receipt.PaymentId,
            Amount = 20m,
            Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice,
            Direction = ClientBalanceDirection.Credit,
            Description = "Preserve historical child compensation"
        });
        await db.SaveChangesAsync(); var controller = Controller(db, seed.Receipt); Guid? id = attempt ? await RegisterAsync(controller) : null;
        var model = await NewOverpaymentAsync(db, seed.Receipt, true); var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        Assert.IsType<BadRequestObjectResult>(await CreateAsync(controller, model, id));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnresolvedNumber_RejectsTwoDistinctInvoicesEvenWhenFirstHasDuplicateHeaders(bool attempt, bool legalEntity)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var original = await db.Payments.SingleAsync(p => p.Id == seed.RootId); var invoice = await db.Invoices.SingleAsync(i => i.Id == original.InvoiceId);
        db.Payments.Add(new Payment { InvoiceId = invoice.Id, DocumentKind = DocumentKind.Invoice, ClientId = original.ClientId, LegalEntityId = original.LegalEntityId, Type = original.Type, Amount = 100m, Date = invoice.Date });
        db.Payments.Add(new Payment { Invoice = new Invoice { Number = invoice.Number, Date = invoice.Date }, DocumentKind = DocumentKind.Invoice, ClientId = original.ClientId, LegalEntityId = original.LegalEntityId, Type = original.Type, Amount = 100m, Date = invoice.Date });
        await db.SaveChangesAsync(); var controller = Controller(db, seed.Receipt); Guid? id = attempt ? await RegisterAsync(controller) : null;
        var model = await NewPaymentAsync(db, seed.Receipt, true); var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await CreateAsync(controller, model, id));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db));
    }

    internal static async Task<CompensatedPaymentWriterTests.Seed> SeedInstallmentAsync(PaymentContext db, bool legalEntity)
    {
        var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var target = await db.Payments.SingleAsync(p => p.Id == seed.Receipt.PaymentId); target.SystemNotes = InvoiceManagedPaymentRules.InstallmentPaymentMarker;
        target.Date = target.Date.AddDays(2); target.PlannedDate = target.Date; await db.SaveChangesAsync(); return seed;
    }
    internal static async Task<Payment> NewOverpaymentAsync(PaymentContext db, Fixture fixture, bool explicitTarget)
    {
        var model = await NewPaymentAsync(db, fixture); model.Amount = 10m; model.PaidAmount = 15m; model.Status = PaymentStatus.Completed;
        model.OverpaymentHandling = PaymentOverpaymentHandling.NextInstallment; model.OverpaymentTargetPaymentId = explicitTarget ? fixture.PaymentId : null; return model;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitUnavailableInvoice_CannotSilentlyBecomeAnUnlinkedPayment(bool attempt, bool deletedInvoice)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, false);
        var model = await NewPaymentAsync(db, seed.Receipt); var controller = Controller(db, seed.Receipt); Guid? id = attempt ? await RegisterAsync(controller) : null;
        if (deletedInvoice)
        {
            var invoice = await db.Invoices.SingleAsync(i => i.Id == model.InvoiceId); db.Entry(invoice).Property<bool>("IsDeleted").CurrentValue = true; await db.SaveChangesAsync();
        }
        else model.InvoiceId = int.MaxValue;
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(db);
        Assert.IsType<NotFoundResult>(await CreateAsync(controller, model, id));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges());
        if (attempt) Assert.Equal(PaymentCreateAttempt.Draft, (await db.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
    }

    internal static async Task<Payment> NewPaymentAsync(PaymentContext db, Fixture fixture, bool byNumber = false)
    {
        var receipt = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == receipt.InvoiceId);
        return new Payment
        {
            InvoiceId = byNumber ? null : receipt.InvoiceId,
            Account = byNumber ? invoice.Number : null,
            AccountDate = byNumber ? invoice.Date : null,
            ClientId = receipt.ClientId,
            LegalEntityId = receipt.LegalEntityId,
            Date = new DateTime(2026, 10, 31),
            PlannedDate = new DateTime(2026, 10, 31),
            Amount = 100m,
            PaidAmount = 10m,
            Type = PaymentType.Income,
            Status = PaymentStatus.Processing,
            DocumentKind = DocumentKind.Payment,
            Description = "Synthetic new receipt",
            Notes = "",
            SystemNotes = "",
        };
    }
    internal static PaymentsController Controller(PaymentContext db, Fixture fixture)
    {
        var services = new ServiceCollection(); services.AddSingleton(db); services.AddOptions<JsonOptions>().Configure(o =>
        { o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase; o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()); });
        var controller = BalanceApplicationPaymentWriterTests.Controller(db, fixture);
        controller.HttpContext.RequestServices = services.BuildServiceProvider(); return controller;
    }
    internal static async Task<Guid> RegisterAsync(PaymentsController controller)
    {
        var body = JsonSerializer.SerializeToElement(new { operation = "primary", sellerCompanyId = (int?)null, sellerBranchId = (int?)null, primaryPaymentId = (int?)null });
        return Assert.IsType<PaymentCreateAttemptStateDto>(Assert.IsType<CreatedResult>(await controller.RegisterCreateAttempt(body, default)).Value).AttemptId;
    }
    internal static Task<IActionResult> CreateAsync(PaymentsController controller, Payment model, Guid? attempt)
        => attempt.HasValue ? controller.CommitCreateAttempt(attempt.Value, Command(model), default) : PaymentOperationKeyTestSetup.CreateAsync(controller, controller.HttpContext.RequestServices.GetRequiredService<PaymentContext>(), model);
    internal static JsonElement Command(Payment model)
    {
        var value = new JsonObject
        {
            ["date"] = model.Date.ToString("yyyy-MM-dd"),
            ["plannedDate"] = model.PlannedDate.ToString("yyyy-MM-dd"),
            ["amount"] = model.Amount,
            ["paidAmount"] = model.PaidAmount,
            ["type"] = model.Type.ToString(),
            ["status"] = model.Status.ToString(),
            ["description"] = model.Description,
            ["isPaid"] = model.IsPaid,
            ["notes"] = model.Notes,
            ["systemNotes"] = model.SystemNotes,
            ["rescheduleCount"] = model.RescheduleCount,
            ["clientId"] = model.ClientId,
            ["legalEntityId"] = model.LegalEntityId,
            ["invoiceId"] = model.InvoiceId,
            ["account"] = model.Account,
            ["accountDate"] = model.AccountDate?.ToString("yyyy-MM-dd"),
            ["overpaymentHandling"] = model.OverpaymentHandling?.ToString(),
            ["overpaymentTargetPaymentId"] = model.OverpaymentTargetPaymentId,
        };
        using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone();
    }
}

public sealed class PaymentCreateFinancialPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    public static IEnumerable<object[]> RaceCases()
    {
        foreach (var attempt in new[] { false, true })
            foreach (var legalEntity in new[] { false, true })
                foreach (var byNumber in new[] { false, true }) yield return [attempt, legalEntity, byNumber];
    }
    [Theory]
    [MemberData(nameof(RaceCases))]
    public async Task CancellationHoldingInvoice_ConflictsThenRejectsNewPayment(bool attempt, bool legalEntity, bool byNumber)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var barrier = new HoldSave(); await using var cancelling = Open(database.ConnectionString, barrier); await using var creating = Open(database.ConnectionString);
        var controller = PaymentCreateFinancialBoundaryTests.Controller(creating, seed.Receipt); var model = await PaymentCreateFinancialBoundaryTests.NewPaymentAsync(creating, seed.Receipt, byNumber);
        Guid? id = attempt ? await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller) : null;
        var root = seed.Receipt with { PaymentId = seed.RootId };
        var pending = InvoiceCancellationCreditBoundaryTests.Controller(cancelling, root).Cancel(root.PaymentId,
            new() { Reason = "Synthetic concurrent cancellation", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance }, default);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await PaymentCreateFinancialBoundaryTests.CreateAsync(controller, model, id).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>((await pending.WaitAsync(TimeSpan.FromSeconds(20))).Result);
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(creating);
        Assert.IsType<BadRequestObjectResult>(await PaymentCreateFinancialBoundaryTests.CreateAsync(controller, model, id));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(creating));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CreatingHoldingInvoice_CancellationConflictsThenCompensatesCommittedAmount(bool attempt, bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var barrier = new HoldSave(); await using var creating = Open(database.ConnectionString, barrier); await using var cancelling = Open(database.ConnectionString);
        var controller = PaymentCreateFinancialBoundaryTests.Controller(creating, seed.Receipt); var model = await PaymentCreateFinancialBoundaryTests.NewPaymentAsync(creating, seed.Receipt);
        // Register before arming the save barrier: metadata is not the financial write under test.
        barrier.Enabled = false; Guid? id = attempt ? await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller) : null; barrier.Enabled = true;
        var pending = PaymentCreateFinancialBoundaryTests.CreateAsync(controller, model, id);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var root = seed.Receipt with { PaymentId = seed.RootId };
        var request = new CancelInvoiceRequest { Reason = "Synthetic cancellation after create", ReceivedMoneyAction = InvoiceCancellationMoneyAction.LeaveAsAdvance };
        try { Assert.IsType<ConflictObjectResult>((await InvoiceCancellationCreditBoundaryTests.Controller(cancelling, root).Cancel(root.PaymentId, request, default).WaitAsync(TimeSpan.FromSeconds(10))).Result); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<CreatedResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.IsType<OkObjectResult>((await InvoiceCancellationCreditBoundaryTests.Controller(cancelling, root).Cancel(root.PaymentId, request, default)).Result);
        Assert.Equal(35m, (await cancelling.ClientBalanceTransactions.AsNoTracking().SingleAsync(t => t.Type == ClientBalanceTransactionType.AdvanceFromCancelledInvoice)).Amount);
        if (attempt)
        {
            var before = await CompensatedPaymentWriterTests.SnapshotAsync(creating);
            Assert.IsType<OkObjectResult>(await PaymentCreateFinancialBoundaryTests.CreateAsync(controller, model, id));
            Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(creating));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailureOnSecondSave_LeavesNoNewPaymentOrPartialInvoice(bool attempt, bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, legalEntity);
        var failure = new FailSecondSave(); await using var creating = Open(database.ConnectionString, failure);
        var controller = PaymentCreateFinancialBoundaryTests.Controller(creating, seed.Receipt); var model = await PaymentCreateFinancialBoundaryTests.NewPaymentAsync(creating, seed.Receipt);
        Guid? id = attempt ? await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller) : null; failure.Enabled = true;
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(creating);
        await Assert.ThrowsAsync<IOException>(() => PaymentCreateFinancialBoundaryTests.CreateAsync(controller, model, id));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(creating)); Assert.Null(creating.Database.CurrentTransaction); Assert.False(creating.ChangeTracker.HasChanges());
        if (attempt) Assert.Equal(PaymentCreateAttempt.Draft, (await creating.PaymentCreateAttempts.AsNoTracking().SingleAsync()).State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BusyOverpaymentTarget_ConflictsBeforeAnyInsertOrMoneyChange(bool attempt, bool legalEntity)
    {
        await using var database = await template.CreateDatabaseAsync(); CompensatedPaymentWriterTests.Seed seed;
        await using (var db = Open(database.ConnectionString)) seed = await PaymentCreateFinancialBoundaryTests.SeedInstallmentAsync(db, legalEntity);
        await using var creating = Open(database.ConnectionString); await using var holder = Open(database.ConnectionString);
        var controller = PaymentCreateFinancialBoundaryTests.Controller(creating, seed.Receipt); var model = await PaymentCreateFinancialBoundaryTests.NewOverpaymentAsync(creating, seed.Receipt, false);
        Guid? id = attempt ? await PaymentCreateFinancialBoundaryTests.RegisterAsync(controller) : null;
        var before = await CompensatedPaymentWriterTests.SnapshotAsync(creating);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM \"Payments\" WHERE \"Id\" = {seed.Receipt.PaymentId} FOR UPDATE");
        Assert.IsType<ConflictObjectResult>(await PaymentCreateFinancialBoundaryTests.CreateAsync(controller, model, id).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(before, await CompensatedPaymentWriterTests.SnapshotAsync(creating)); await transaction.RollbackAsync();
        model = await PaymentCreateFinancialBoundaryTests.NewOverpaymentAsync(creating, seed.Receipt, false);
        Assert.IsType<CreatedResult>(await PaymentCreateFinancialBoundaryTests.CreateAsync(controller, model, id));
        Assert.Equal(30m, (await creating.Payments.AsNoTracking().SingleAsync(p => p.Id == seed.Receipt.PaymentId)).PaidAmount);
    }

    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection);
        if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class HoldSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Enabled) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); } return result; }
    }
    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        private int saves; public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!Enabled) return ValueTask.FromResult(result);
            var pending = eventData.Context!.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToArray();
            if (pending.Length > 0 && pending.All(e => e.Entity is ObjectAuditEvent)) return ValueTask.FromResult(result);
            if (++saves == 2) throw new IOException("Synthetic financial create save failure");
            return ValueTask.FromResult(result);
        }
    }
}
