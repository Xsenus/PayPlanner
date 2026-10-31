using System.IO;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InvoiceReconciliationBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AggregateCannotConcealTheAlreadyAppliedCredit(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, legal);
        db.ClientBalanceTransactions.Add(new() { PaymentId = fixture.HeaderId, InvoiceId = fixture.InvoiceId, ClientId = legal ? null : fixture.OwnerId, LegalEntityId = legal ? fixture.OwnerId : null, Amount = 40m, Type = ClientBalanceTransactionType.CreditApplied, Direction = ClientBalanceDirection.Debit });
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.Equal(0, (await Runner(db).RunOnceAsync()).Applied); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData("credit")]
    [InlineData("refund")]
    public async Task ExistingCompensationIsNotReconciledIntoANewState(string reason)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false);
        if (reason == "credit") db.ClientBalanceTransactions.Add(new() { PaymentId = fixture.HeaderId, InvoiceId = fixture.InvoiceId, ClientId = fixture.OwnerId, Amount = 100m, Direction = ClientBalanceDirection.Credit, Type = ClientBalanceTransactionType.AdvanceFromCancelledInvoice });
        else db.MoneyOperations.Add(new() { LinkedInvoiceId = fixture.InvoiceId, Direction = MoneyOperationDirection.Expense, MatchComment = "Возврат оплаты при аннулировании счёта.", Amount = 100m, Date = DateTime.UtcNow.Date });
        await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.Equal(0, (await Runner(db).RunOnceAsync()).Applied); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Fact]
    public async Task AmbiguousHistoricalOwnerIsNotReconciled()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await SeedAsync(db, false); var owner = new Client { Name = "Other synthetic invoice owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); (await db.Payments.SingleAsync(p => p.Id == fixture.ChildId)).ClientId = owner.Id; await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        Assert.Equal(0, (await Runner(db).RunOnceAsync()).Applied); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Fact]
    public async Task ErrorAfterSqlRollsBackTheAggregateAndAudit()
    {
        var fault = new FailAfterSql(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); await SeedAsync(db, false); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync(); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => Runner(db).RunOnceAsync()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync()); Assert.Null(db.Database.CurrentTransaction);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoricalOppositePaymentDirectionIsNotAddedToTheInvoiceTotal(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, legal); (await db.Payments.SingleAsync(p => p.Id == f.ChildId)).Type = PaymentType.Expense; await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var result = await Runner(db).RunOnceAsync(); Assert.Equal(0, result.Applied); Assert.Equal(1, result.Skipped); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidInstallmentRepairKeepsTheReceiptAndLedger(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, legal, 0m);
        AddDebit(db, f, f.ChildId, 25m); await db.SaveChangesAsync(); var before = JsonSerializer.Serialize(await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.ChildId)); var ledger = await db.ClientBalanceTransactions.Select(t => new { t.Id, t.Amount, t.Type, t.Direction, t.PaymentId }).ToArrayAsync();
        var result = await Runner(db).RunOnceAsync(); Assert.Equal(1, result.Scanned); Assert.Equal(1, result.Changed); Assert.Equal(1, result.Applied); Assert.Equal(0, result.Skipped);
        var header = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId); Assert.Equal(25m, header.PaidAmount); Assert.Equal(100m, header.Amount); Assert.Equal(PaymentStatus.Processing, header.Status);
        Assert.Equal(before, JsonSerializer.Serialize(await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.ChildId))); Assert.Equal(ledger, await db.ClientBalanceTransactions.Select(t => new { t.Id, t.Amount, t.Type, t.Direction, t.PaymentId }).ToArrayAsync()); Assert.Equal(0, (await Runner(db).RunOnceAsync()).Applied);
    }
    [Theory]
    [InlineData(false, 10, 15, 1)]
    [InlineData(true, 10, 15, 1)]
    [InlineData(false, 15, 15, 0)]
    [InlineData(true, 15, 15, 0)]
    [InlineData(false, 0, 30, 0)]
    [InlineData(true, 0, 30, 0)]
    public async Task ParentAndChildCreditsUseOneAggregateMinimum(bool legal, int rootDebit, int childDebit, int applied)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, legal); if (rootDebit > 0) AddDebit(db, f, f.HeaderId, rootDebit); AddDebit(db, f, f.ChildId, childDebit); await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var result = await Runner(db).RunOnceAsync(); Assert.Equal(applied, result.Applied); Assert.Equal(1 - applied, result.Skipped); Assert.Equal(applied == 1 ? 25m : 100m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId)).PaidAmount);
        if (applied == 0) Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Fact]
    public async Task UnsafeFirstInvoiceDoesNotStarveTheNextIndependentGraph()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, false); AddDebit(db, f, f.HeaderId, 40m); var next = await AddGraphAsync(db, f); var before = JsonSerializer.Serialize(await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId));
        var result = await Runner(db, 1).RunOnceAsync(); Assert.Equal(2, result.Scanned); Assert.Equal(1, result.Skipped); Assert.Equal(1, result.Applied); Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == next.HeaderId)).PaidAmount); Assert.Equal(before, JsonSerializer.Serialize(await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId)));
    }
    [Theory]
    [InlineData(false, 10, 1)]
    [InlineData(true, 10, 1)]
    [InlineData(false, 30, 0)]
    [InlineData(true, 30, 0)]
    public async Task StandaloneInvoiceUsesTheSameTransactionAndCreditMinimum(bool legal, int debit, int applied)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var f = await SeedAsync(db, legal); var header = await db.Payments.SingleAsync(p => p.Id == f.HeaderId); header.InvoiceId = null; header.PaidAmount = 25m; header.Status = PaymentStatus.Pending; header.IsPaid = false; AddDebit(db, f, f.HeaderId, debit); await db.SaveChangesAsync(); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        var result = await Runner(db).RunOnceAsync(); Assert.Equal(1, result.Scanned); Assert.Equal(applied, result.Applied); Assert.Equal(1 - applied, result.Skipped); var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId); Assert.Equal(25m, stored.PaidAmount); Assert.Null(stored.InvoiceId);
        if (applied == 1) Assert.Equal(PaymentStatus.Processing, stored.Status); else Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignTransactionOrPendingWorkIsNotAdopted(bool transaction)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedAsync(db, false);
        if (transaction) { await using var lease = await db.Database.BeginTransactionAsync(); await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(db).RunOnceAsync()); Assert.Same(lease, db.Database.CurrentTransaction); await lease.RollbackAsync(); }
        else { var client = await db.Clients.SingleAsync(); client.Name = "Unrelated pending change"; await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(db).RunOnceAsync()); Assert.Equal(EntityState.Modified, db.Entry(client).State); Assert.Equal("Unrelated pending change", client.Name); }
    }
    [Fact]
    public async Task MultiplePagesDoNotAccumulateTrackedPaymentGraphs()
    {
        var probe = new SaveProbe(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: probe); var f = await SeedAsync(db, false); for (var i = 0; i < 8; i++) await AddGraphAsync(db, f); probe.Armed = true;
        var result = await Runner(db, 2).RunOnceAsync(); Assert.Equal(9, result.Applied); Assert.Equal(9, probe.Sizes.Count); Assert.All(probe.Sizes, count => Assert.Equal(2, count));
    }
    [Fact]
    public async Task OversizedGraphIsNotTruncated_AndTheNextGraphStillRuns()
    {
        var probe = new SaveProbe(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: probe); var f = await SeedAsync(db, false);
        db.Payments.AddRange(Enumerable.Range(0, 4999).Select(_ => new Payment { InvoiceId = f.InvoiceId, ClientId = f.OwnerId, Amount = 0m, PaidAmount = 0m, DocumentKind = DocumentKind.Payment, Type = PaymentType.Income, Status = PaymentStatus.Pending, Date = DateTime.UtcNow.Date })); await db.SaveChangesAsync(); var next = await AddGraphAsync(db, f); probe.Armed = true;
        var result = await Runner(db).RunOnceAsync(); Assert.Equal(1, result.Oversized); Assert.Equal(1, result.Skipped); Assert.Equal(1, result.Applied); Assert.Equal(100m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == f.HeaderId)).PaidAmount); Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == next.HeaderId)).PaidAmount); Assert.Equal(2, Assert.Single(probe.Sizes));
    }
    [Fact]
    public async Task CancellationBeforeSaveRollsBackAndReleasesTheRunner()
    {
        using var stop = new CancellationTokenSource(); var fault = new CancelSave(stop); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); await SeedAsync(db, false); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var runner = Runner(db); fault.Armed = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunOnceAsync(stop.Token)); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Null(db.Database.CurrentTransaction); fault.Armed = false; Assert.Equal(1, (await runner.RunOnceAsync()).Applied);
    }
    [Fact]
    public async Task OneInstanceDoesNotOverlapItsOwnSweeps()
    {
        var barrier = new HoldSave(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: barrier); await SeedAsync(db, false); var runner = Runner(db); barrier.Armed = true; var first = runner.RunOnceAsync(); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { using var stop = new CancellationTokenSource(); var second = runner.RunOnceAsync(stop.Token); Assert.False(second.IsCompleted); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); }
        finally { barrier.Release.TrySetResult(); }
        Assert.Equal(1, (await first.WaitAsync(TimeSpan.FromSeconds(20))).Applied); Assert.Equal(0, (await runner.RunOnceAsync()).Applied);
    }
    internal static void AddDebit(PaymentContext db, Fixture f, int paymentId, decimal amount)
        => db.ClientBalanceTransactions.Add(new() { PaymentId = paymentId, InvoiceId = f.InvoiceId, ClientId = f.Legal ? null : f.OwnerId, LegalEntityId = f.Legal ? f.OwnerId : null, Amount = amount, Type = ClientBalanceTransactionType.CreditApplied, Direction = ClientBalanceDirection.Debit });
    internal static async Task<Fixture> AddGraphAsync(PaymentContext db, Fixture owner)
    {
        var invoice = new Invoice { Number = "Independent synthetic reconciliation invoice", Date = DateTime.UtcNow.Date.AddDays(-20) }; db.Invoices.Add(invoice); await db.SaveChangesAsync();
        var header = new Payment { InvoiceId = invoice.Id, DocumentKind = DocumentKind.Invoice, ClientId = owner.Legal ? null : owner.OwnerId, LegalEntityId = owner.Legal ? owner.OwnerId : null, Amount = 100m, PaidAmount = 0m, Status = PaymentStatus.Pending, Type = PaymentType.Income, Date = DateTime.UtcNow.Date.AddDays(-10), PlannedDate = DateTime.UtcNow.Date.AddDays(-10) };
        var child = new Payment { InvoiceId = invoice.Id, DocumentKind = DocumentKind.Payment, ClientId = header.ClientId, LegalEntityId = header.LegalEntityId, Amount = 100m, PaidAmount = 25m, Status = PaymentStatus.Processing, Type = PaymentType.Income, Date = DateTime.UtcNow.Date.AddDays(-2), PlannedDate = DateTime.UtcNow.Date.AddDays(-2) }; db.Payments.AddRange(header, child); await db.SaveChangesAsync(); return owner with { InvoiceId = invoice.Id, HeaderId = header.Id, ChildId = child.Id };
    }
    private sealed class SaveProbe : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public List<int> Sizes { get; } = [];
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed && eventData.Context!.ChangeTracker.Entries<Payment>().Any(e => e.State == EntityState.Modified)) { Assert.NotNull(eventData.Context.Database.CurrentTransaction); Sizes.Add(eventData.Context.ChangeTracker.Entries<Payment>().Count()); } return ValueTask.FromResult(result); }
    }
    private sealed class CancelSave(CancellationTokenSource stop) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed) { stop.Cancel(); ct.ThrowIfCancellationRequested(); } return ValueTask.FromResult(result); }
    }
    private sealed class HoldSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Armed) { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); } return result; }
    }
    internal static async Task<Fixture> SeedAsync(PaymentContext db, bool legal, decimal headerPaid = 100m, decimal childPaid = 25m)
    {
        var owner = await BalanceApplicationIdempotencyTests.SeedAsync(db, legal); var header = await db.Payments.SingleAsync(p => p.Id == owner.PaymentId);
        var invoice = new Invoice { Number = "Synthetic reconciliation invoice", Date = DateTime.UtcNow.Date.AddDays(-20) }; db.Invoices.Add(invoice); await db.SaveChangesAsync();
        header.DocumentKind = DocumentKind.Invoice; header.InvoiceId = invoice.Id; header.PaidAmount = headerPaid; header.Status = headerPaid >= 100m ? PaymentStatus.Completed : PaymentStatus.Pending; header.IsPaid = headerPaid >= 100m; header.Date = DateTime.UtcNow.Date.AddDays(-10); header.PlannedDate = header.Date;
        var child = new Payment { InvoiceId = invoice.Id, DocumentKind = DocumentKind.Payment, ClientId = header.ClientId, LegalEntityId = header.LegalEntityId, Amount = 100m, PaidAmount = childPaid, Date = DateTime.UtcNow.Date.AddDays(-2), PlannedDate = DateTime.UtcNow.Date.AddDays(-2), Status = childPaid >= 100m ? PaymentStatus.Completed : PaymentStatus.Processing, IsPaid = childPaid >= 100m, Type = header.Type };
        db.Payments.Add(child); await db.SaveChangesAsync(); return new(owner.ActorId, owner.OwnerId, invoice.Id, header.Id, child.Id, legal);
    }
    internal static InvoicePaymentStateReconciliationService Runner(PaymentContext db, int size = 2)
        => new(new BorrowedScopes(db), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InvoicePaymentStateReconciliation:BatchSize"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture) }).Build(), NullLogger<InvoicePaymentStateReconciliationService>.Instance);
    private sealed class BorrowedScopes(PaymentContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new BorrowedScope(db);
    }
    private sealed class BorrowedScope(PaymentContext db) : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type type) => type == typeof(PaymentContext) ? db : null;
        public void Dispose() { }
    }
    internal sealed record Fixture(int ActorId, int OwnerId, int InvoiceId, int HeaderId, int ChildId, bool Legal);
    internal sealed class FailAfterSql : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default) => Armed ? throw new IOException("Synthetic reconciliation failure after SQL") : ValueTask.FromResult(result);
    }
}
