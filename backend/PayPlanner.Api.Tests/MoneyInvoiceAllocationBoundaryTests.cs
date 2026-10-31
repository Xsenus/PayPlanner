using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Controllers;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class MoneyInvoiceAllocationBoundaryTests
{
    [Theory]
    [InlineData("credit")]
    [InlineData("refund")]
    public async Task CompensationHistoryCannotReceiveANewBankAllocation(string reason)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db);
        if (reason == "credit") db.ClientBalanceTransactions.Add(new() { ClientId = f.Payment.OwnerId, PaymentId = f.Payment.HeaderId, InvoiceId = f.Payment.InvoiceId, Amount = 100m, Direction = ClientBalanceDirection.Credit, Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice });
        else db.MoneyOperations.Add(new() { LinkedInvoiceId = f.Payment.InvoiceId, Direction = MoneyOperationDirection.Expense, MatchComment = "Возврат оплаты при аннулировании счёта.", Amount = 100m, Date = DateTime.UtcNow.Date });
        await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var error = Assert.IsType<BadRequestObjectResult>(await LinkAsync(db, f)); Assert.Contains("компенсац", error.Value?.ToString(), StringComparison.OrdinalIgnoreCase); Assert.Equal(before, await SnapshotAsync(db));
    }
    [Fact]
    public async Task EarlierOverpaymentIsNeverRewrittenByAllocation()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, amount: 150m);
        db.ClientBalanceTransactions.Add(new() { ClientId = f.Payment.OwnerId, PaymentId = f.Payment.ChildId, InvoiceId = f.Payment.InvoiceId, ExternalKey = $"money-operation-overpayment:{f.OperationId}", Amount = 7m, Direction = ClientBalanceDirection.Credit, Type = ClientBalanceTransactionType.Overpayment, Description = "Earlier synthetic overpayment" }); await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var error = Assert.IsType<BadRequestObjectResult>(await LinkAsync(db, f)); Assert.Contains("переплат", error.Value?.ToString(), StringComparison.OrdinalIgnoreCase); Assert.Equal(before, await SnapshotAsync(db));
    }
    [Fact]
    public async Task AllocationRequiresTheCallersTransaction()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db); var before = await SnapshotAsync(db); var operation = await db.MoneyOperations.SingleAsync(row => row.Id == f.OperationId);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => MoneyOperationInvoiceAllocationService.ApplyAsync(db, operation, f.Payment.InvoiceId)); Assert.Equal(before, await SnapshotAsync(db)); Assert.False(db.ChangeTracker.HasChanges());
    }
    internal sealed record Fixture(InvoiceReconciliationBoundaryTests.Fixture Payment, int CompanyId, int AccountId, int OperationId);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidManualLinkAllocatesTheSavedOperation(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, legal); var result = await LinkAsync(db, f); Assert.True(result is OkObjectResult, (result as ObjectResult)?.Value?.ToString());
        Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.ChildId)).PaidAmount); Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.HeaderId)).PaidAmount);
    }
    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public async Task OtherManualEntryPointsAllocateTheSameCompleteGraph(string endpoint, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, legal); var response = await WriteAsync(db, f, endpoint); Assert.True(response is CreatedResult or OkObjectResult, (response as ObjectResult)?.Value?.ToString());
        Assert.All(await db.Payments.AsNoTracking().Where(p => p.InvoiceId == f.Payment.InvoiceId).ToArrayAsync(), p => Assert.Equal(25m, p.PaidAmount));
        Assert.Equal(endpoint == "create" ? 2 : 1, await db.MoneyOperations.CountAsync()); Assert.Single(await db.MoneyOperations.Where(o => o.LinkedInvoiceId == f.Payment.InvoiceId).ToArrayAsync());
    }
    [Theory]
    [InlineData("link")]
    [InlineData("create")]
    [InlineData("update")]
    public async Task MissingInvoiceEditPermissionCannotChangePaymentsOrTheOperation(string endpoint)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db); (await db.RolePermissions.SingleAsync(p => p.Section == "accounts")).CanEdit = false; await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var response = await WriteAsync(db, f, endpoint); Assert.True(response is ForbidResult or BadRequestObjectResult); Assert.Equal(before, await SnapshotAsync(db));
    }
    [Theory]
    [InlineData("link")]
    [InlineData("create")]
    [InlineData("update")]
    public async Task CreditMinimumFailureRollsBackTheWholeManualRequest(string endpoint)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db); InvoiceReconciliationBoundaryTests.AddDebit(db, f.Payment, f.Payment.ChildId, 40m); await db.SaveChangesAsync(); var before = await SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        var error = Assert.IsType<BadRequestObjectResult>(await WriteAsync(db, f, endpoint)); Assert.Contains("зач", error.Value?.ToString(), StringComparison.OrdinalIgnoreCase); Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Fact]
    public async Task RepeatingAKnownLinkedOperationDoesNotApplyItAgain()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db); Assert.IsType<OkObjectResult>(await LinkAsync(db, f)); var before = await SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        Assert.IsType<OkObjectResult>(await LinkAsync(db, f)); Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Fact]
    public async Task NormalClientOverpaymentIsCreatedOnceAndKeepsItsOriginalAmount()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, amount: 150m); Assert.IsType<OkObjectResult>(await LinkAsync(db, f)); var credit = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(row => row.ExternalKey == $"money-operation-overpayment:{f.OperationId}"); Assert.Equal(50m, credit.Amount); Assert.Equal(f.Payment.ChildId, credit.PaymentId);
        var before = await SnapshotAsync(db); Assert.IsType<OkObjectResult>(await LinkAsync(db, f)); Assert.Equal(before, await SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileImportAndReplayShareTheProtectedAllocation(bool reject)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db); var invoice = await db.Invoices.SingleAsync(i => i.Id == f.Payment.InvoiceId); invoice.Number = "751"; (await db.Payments.SingleAsync(p => p.Id == f.Payment.HeaderId)).Account = "751";
        if (reject) InvoiceReconciliationBoundaryTests.AddDebit(db, f.Payment, f.Payment.ChildId, 40m); await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var text = "Дата;Сумма;Контрагент;Назначение;Номер документа\n" + DateTime.UtcNow.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture) + ";25;Synthetic payer;Оплата по счёту №751;SYN-751\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        async Task<ActionResult<PayPlanner.Api.Models.Responses.BankStatementImportResultDto>> Import()
        {
            using var input = new System.IO.MemoryStream(bytes); var file = new FormFile(input, 0, bytes.Length, "file", "synthetic.csv");
            return await Controller(db, f.Payment.ActorId).ImportBankStatement(file, default, f.CompanyId, f.AccountId);
        }
        if (reject)
        {
            var error = Assert.IsType<BadRequestObjectResult>((await Import()).Result); Assert.Contains("зач", error.Value?.ToString(), StringComparison.OrdinalIgnoreCase); Assert.Equal(before, await SnapshotAsync(db)); Assert.Empty(await db.BankStatementImportBatches.ToArrayAsync()); Assert.Empty(await db.BankStatementImportRows.ToArrayAsync());
        }
        else
        {
            var result = Assert.IsType<PayPlanner.Api.Models.Responses.BankStatementImportResultDto>(Assert.IsType<OkObjectResult>((await Import()).Result).Value); Assert.Equal(1, result.Created); Assert.Equal(MoneyOperationMatchStatus.Matched, Assert.Single(result.Operations).MatchStatus); Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.Payment.ChildId)).PaidAmount);
            var applied = await SnapshotAsync(db); await Import(); Assert.Equal(applied, await SnapshotAsync(db)); Assert.Equal(2, await db.MoneyOperations.CountAsync()); Assert.Single(await db.BankStatementImportBatches.ToArrayAsync());
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualInvoiceMutationDoesNotAdoptForeignPendingWork(bool transaction)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db);
        if (transaction) { await using var lease = await db.Database.BeginTransactionAsync(); Assert.IsType<ConflictObjectResult>(await LinkAsync(db, f)); Assert.Same(lease, db.Database.CurrentTransaction); await lease.RollbackAsync(); }
        else { var user = await db.Users.SingleAsync(); user.FullName = "Unrelated pending user edit"; Assert.IsType<ConflictObjectResult>(await LinkAsync(db, f)); Assert.Equal(Microsoft.EntityFrameworkCore.EntityState.Modified, db.Entry(user).State); }
    }
    [Fact]
    public async Task FileImportReleasesCompletedPaymentGraphsBetweenRows()
    {
        await using var seed = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(seed); var graphs = new List<InvoiceReconciliationBoundaryTests.Fixture> { f.Payment };
        for (var i = 1; i < 8; i++) graphs.Add(await InvoiceReconciliationBoundaryTests.AddGraphAsync(seed, f.Payment));
        var rows = new List<string> { "Дата;Сумма;Контрагент;Назначение;Номер документа" }; var date = DateTime.UtcNow.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < graphs.Count; i++)
        {
            var graph = graphs[i]; var number = (751 + i).ToString(System.Globalization.CultureInfo.InvariantCulture); (await seed.Invoices.SingleAsync(v => v.Id == graph.InvoiceId)).Number = number;
            foreach (var p in await seed.Payments.Where(p => p.InvoiceId == graph.InvoiceId).ToArrayAsync()) { p.SellerCompanyId = f.CompanyId; p.PaidAmount = 0m; p.Status = PaymentStatus.Pending; p.IsPaid = false; }
            rows.Add($"{date};25;Synthetic payer;Оплата по счёту №{number};SYN-{number}");
        }
        await seed.SaveChangesAsync(); var probe = new PaymentTrackingProbe(); await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(seed.Database.GetDbConnection()).AddInterceptors(probe).Options);
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\n', rows)); using var stream = new System.IO.MemoryStream(bytes);
        var response = await Controller(db, f.Payment.ActorId).ImportBankStatement(new FormFile(stream, 0, bytes.Length, "file", "synthetic-memory.csv"), default, f.CompanyId, f.AccountId);
        var result = Assert.IsType<PayPlanner.Api.Models.Responses.BankStatementImportResultDto>(Assert.IsType<OkObjectResult>(response.Result).Value); Assert.Equal(8, result.Created); Assert.All(result.Operations, op => Assert.Equal(MoneyOperationMatchStatus.Matched, op.MatchStatus));
        Assert.Equal(8, probe.Counts.Count); Assert.All(probe.Counts, count => Assert.Equal(2, count)); Assert.Empty(db.ChangeTracker.Entries<Payment>());
    }
    private sealed class PaymentTrackingProbe : SaveChangesInterceptor
    {
        public List<int> Counts { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (eventData.Context!.ChangeTracker.Entries<Payment>().Any(entry => entry.State == EntityState.Modified)) Counts.Add(eventData.Context.ChangeTracker.Entries<Payment>().Count()); return ValueTask.FromResult(result); }
    }
    internal static Task<IActionResult> WriteAsync(PaymentContext db, Fixture f, string endpoint)
    {
        if (endpoint == "link") return LinkAsync(db, f);
        var model = new MoneyOperation { MoneyAccountId = f.AccountId, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, Amount = 25m, Currency = "RUB", Date = DateTime.UtcNow.Date, ResponsibleUserId = f.Payment.ActorId, LinkedInvoiceId = f.Payment.InvoiceId, MatchStatus = MoneyOperationMatchStatus.Matched };
        var controller = Controller(db, f.Payment.ActorId); return endpoint == "create" ? controller.Create(model) : controller.Update(f.OperationId, model);
    }
    internal static async Task<Fixture> SeedAsync(PaymentContext db, bool legal = false, decimal amount = 25m)
    {
        var payment = await InvoiceReconciliationBoundaryTests.SeedAsync(db, legal, 0m, 0m); var user = await db.Users.SingleAsync(u => u.Id == payment.ActorId);
        db.RolePermissions.Add(new() { RoleId = user.RoleId, Section = "accounts", CanView = true, CanEdit = true, CanCreate = true, ViewScope = "all" });
        var company = new LegalEntity { ShortName = "Synthetic own bank company", FullName = "Synthetic own bank company", ProfileStatus = CompanyProfileStatuses.Active }; db.LegalEntities.Add(company); await db.SaveChangesAsync();
        db.SystemSettings.Add(new() { Key = "legalEntities.mainCompanyIds", Value = JsonSerializer.Serialize(new[] { company.Id }) }); db.UserCompanyAccesses.Add(new() { UserId = user.Id, LegalEntityId = company.Id });
        foreach (var row in await db.Payments.Where(p => p.InvoiceId == payment.InvoiceId).ToArrayAsync()) row.SellerCompanyId = company.Id;
        var account = new MoneyAccount { CompanyId = company.Id, Source = MoneyOperationSource.Bank, Name = "Synthetic bank account", Currency = "RUB", IsActive = true }; db.MoneyAccounts.Add(account); await db.SaveChangesAsync();
        var operation = new MoneyOperation { MoneyAccountId = account.Id, Source = MoneyOperationSource.Bank, Direction = MoneyOperationDirection.Income, AccountingType = MoneyOperationAccountingType.Fact, Amount = amount, Currency = "RUB", Date = DateTime.UtcNow.Date, ResponsibleUserId = user.Id, MatchStatus = MoneyOperationMatchStatus.ClarificationRequired }; db.MoneyOperations.Add(operation); await db.SaveChangesAsync(); return new(payment, company.Id, account.Id, operation.Id);
    }
    internal static MoneyOperationsController Controller(PaymentContext db, int actorId)
        => new(db, NullLogger<MoneyOperationsController>.Instance) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actorId.ToString())], "Synthetic")) } } };
    internal static Task<IActionResult> LinkAsync(PaymentContext db, Fixture f) => Controller(db, f.Payment.ActorId).LinkInvoice(f.OperationId, new() { InvoiceId = f.Payment.InvoiceId }, default);
    internal static async Task<string> SnapshotAsync(PaymentContext db) => JsonSerializer.Serialize(new { Financial = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db), Operations = await db.MoneyOperations.AsNoTracking().OrderBy(o => o.Id).Select(o => new { o.Id, o.Amount, o.MoneyAccountId, o.LinkedInvoiceId, o.MatchStatus, o.PaidInvoiceNumber, o.MatchComment }).ToArrayAsync() });
}
