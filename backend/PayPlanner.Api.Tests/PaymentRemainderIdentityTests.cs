using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class PaymentRemainderIdentityTests
{
    public static IEnumerable<object[]> BindingCases()
    {
        foreach (var api in new[] { 0, 1, 2 })
            foreach (var legal in new[] { false, true })
                foreach (var child in new[] { false, true })
                    foreach (var change in new[] { "owner", "direction", "account", "kind" }) yield return [api, legal, child, change];
    }
    [Theory]
    [MemberData(nameof(BindingCases))]
    public async Task NeitherEndCanChangeItsFinancialIdentity(int api, bool legal, bool child, string change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (source, remainder) = await VersionedPaymentStructureTests.SplitAsync(db, legal, child ? "legacy" : "structured");
        var fixture = child ? source with { PaymentId = remainder } : source;
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture);
        if (change == "owner")
        {
            if (legal) { var other = new LegalEntity { FullName = "Synthetic different owner", ShortName = "Other" }; db.LegalEntities.Add(other); await db.SaveChangesAsync(); model.LegalEntityId = other.Id; }
            else { var other = new Client { Name = "Synthetic different owner" }; db.Clients.Add(other); await db.SaveChangesAsync(); model.ClientId = other.Id; }
        }
        if (change == "direction") model.Type = PaymentType.Expense;
        if (change == "account") model.Account = "Synthetic changed invoice number";
        if (change == "kind") model.DocumentKind = DocumentKind.Accrual;
        var before = await SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, api, model));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    public static IEnumerable<object[]> SourceStatusCases()
    {
        foreach (var api in new[] { 0, 1, 2, 3 })
            foreach (var legal in new[] { false, true })
                foreach (var status in new[] { PaymentStatus.Pending, PaymentStatus.Cancelled }) yield return [api, legal, status];
    }
    [Theory]
    [MemberData(nameof(SourceStatusCases))]
    public async Task SourceStatusCannotEraseThePaidPart(int api, bool legal, PaymentStatus status)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, api % 2 == 0 ? "structured" : "legacy");
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Status = status;
        var before = await SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, api, model));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V1CannotUnsetTheSourcePaidFlag(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.IsPaid = false;
        var before = await SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, 1, model));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false, PaymentStatus.Pending)]
    [InlineData(true, PaymentStatus.Pending)]
    [InlineData(false, PaymentStatus.Completed)]
    [InlineData(true, PaymentStatus.Completed)]
    public async Task RepeatingAHistoricalSourceStatusCannotRewriteItsPaidPart(bool legal, PaymentStatus status)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        source.Status = status; source.IsPaid = false;
        if (status == PaymentStatus.Completed) source.PaidAmount = 20m;
        await db.SaveChangesAsync();
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); var before = await SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, 3, model));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, true)]
    public async Task NormalizationCannotSilentlyRepairHistoricalSourceMoney(int api, bool legal, bool overpaid)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId);
        if (overpaid) source.PaidAmount = 50m;
        else { source.Status = PaymentStatus.Pending; source.IsPaid = false; }
        await db.SaveChangesAsync();
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Notes = "Synthetic history must stay unchanged";
        model.OverpaymentHandling = PaymentOverpaymentHandling.Advance;
        var before = await SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, api, model));
        Assert.Equal(before, await SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemainderCannotBeCancelledThroughTheStatusAction(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (source, remainder) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var fixture = source with { PaymentId = remainder }; var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Status = PaymentStatus.Cancelled;
        var before = await SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await WriteAsync(db, fixture, 3, model));
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task SourceAnnotationsRemainAvailable(int api, bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "legacy");
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Notes = "Synthetic permitted source note";
        Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, api, model));
        var source = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(40m, source.PaidAmount); Assert.Equal(40m, source.Amount); Assert.Equal(model.Notes, source.Notes); Assert.True(source.IsPaid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PayingTheRemainderThroughStatusKeepsTheSource(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (source, remainder) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var fixture = source with { PaymentId = remainder }; var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Status = PaymentStatus.Completed;
        Assert.IsType<OkObjectResult>(await WriteAsync(db, fixture, 3, model));
        var paidSource = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId);
        var paidRemainder = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == remainder);
        Assert.Equal(40m, paidSource.PaidAmount); Assert.Equal(60m, paidRemainder.PaidAmount); Assert.True(paidRemainder.IsPaid);
        Assert.Single(await db.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task AccessIsCheckedBeforeFinancialHistory(int api, bool hidden)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, false, "structured");
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Status = PaymentStatus.Cancelled; model.Type = PaymentType.Expense;
        var permission = await db.RolePermissions.SingleAsync(p => p.Section == "payments");
        if (hidden) permission.ViewScope = "own"; else permission.CanEdit = false;
        await db.SaveChangesAsync(); var before = await SnapshotAsync(db);
        var result = await WriteAsync(db, fixture, api, model);
        if (hidden) Assert.IsType<NotFoundResult>(result); else Assert.IsType<ForbidResult>(result);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    internal static Task<IActionResult> WriteAsync(PaymentContext db, Fixture fixture, int api, Payment model)
        => api == 0 ? BalanceApplicationPaymentWriterTests.Controller(db, fixture).Update(fixture.PaymentId, model, CancellationToken.None)
            : api == 3 ? BalanceApplicationPaymentWriterTests.Controller(db, fixture).UpdateStatus(fixture.PaymentId,
                new UpdatePaymentStatusRequest { Status = model.Status.ToString(), Reason = "Synthetic status action" }, CancellationToken.None)
            : VersionedPaymentWriterTests.WriteAsync(db, fixture, api, "update", model);

    internal static async Task<string> SnapshotAsync(PaymentContext db)
    {
        var payments = await db.Payments.AsNoTracking().OrderBy(p => p.Id).Select(p => new
        { p.Id, p.ClientId, p.LegalEntityId, p.ExpenseClientId, p.ExpenseLegalEntityId, p.InvoiceId, p.Account, p.AccountDate, p.DocumentKind, p.Type, p.Amount, p.PaidAmount, p.Status, p.IsPaid, p.Notes, p.SystemNotes }).ToArrayAsync();
        return System.Text.Json.JsonSerializer.Serialize(new { payments, financial = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db) });
    }
}

public sealed class PaymentRemainderIdentityPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public async Task PostNormalizationGuardRollsBackHistoricalMoneyAndAnyNewBalance(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        (await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).PaidAmount = 50m;
        await db.SaveChangesAsync(); var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture);
        model.OverpaymentHandling = PaymentOverpaymentHandling.Advance; model.Notes = "Synthetic historical source";
        var before = await PaymentRemainderIdentityTests.SnapshotAsync(db); var audit = await db.ObjectAuditEvents.CountAsync();
        Assert.IsType<ConflictObjectResult>(await PaymentRemainderIdentityTests.WriteAsync(db, fixture, api, model));
        Assert.Equal(before, await PaymentRemainderIdentityTests.SnapshotAsync(db)); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
        Assert.Null(db.Database.CurrentTransaction); Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameHistoricalStatusCannotEraseTheRecordedPayment(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var source = await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId); source.Status = PaymentStatus.Pending; source.IsPaid = false;
        await db.SaveChangesAsync(); var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); var before = await PaymentRemainderIdentityTests.SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await PaymentRemainderIdentityTests.WriteAsync(db, fixture, 3, model));
        Assert.Equal(before, await PaymentRemainderIdentityTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task TransferAndIdentityOrStatusChangesCannotOverwriteEachOther(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) fixture = await SeedAsync(db, legal);
        var barrier = new HoldSave(); await using var transferring = Open(database.ConnectionString, barrier); await using var editor = Open(database.ConnectionString);
        var model = await VersionedPaymentWriterTests.ModelAsync(editor, fixture); model.Type = PaymentType.Expense; model.Status = PaymentStatus.Cancelled;
        var pending = BalanceApplicationPaymentWriterTests.Controller(transferring, fixture).TransferPartialRemainder(fixture.PaymentId,
            BalanceApplicationRemainderWriterTests.Transfer(fixture), CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try { Assert.IsType<ConflictObjectResult>(await PaymentRemainderIdentityTests.WriteAsync(editor, fixture, api, model).WaitAsync(TimeSpan.FromSeconds(10))); }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var before = await PaymentRemainderIdentityTests.SnapshotAsync(editor);
        Assert.IsType<ConflictObjectResult>(await PaymentRemainderIdentityTests.WriteAsync(editor, fixture, api, model));
        Assert.Equal(before, await PaymentRemainderIdentityTests.SnapshotAsync(editor));
        Assert.Single(await editor.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusCanPayTheRemainderWithoutChangingItsSource(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var (source, remainder) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var fixture = source with { PaymentId = remainder }; var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Status = PaymentStatus.Completed;
        Assert.IsType<OkObjectResult>(await PaymentRemainderIdentityTests.WriteAsync(db, fixture, 3, model));
        Assert.Equal(40m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == source.PaymentId)).PaidAmount);
        Assert.Equal(60m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == remainder)).PaidAmount);
        Assert.Single(await db.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task PermittedNoteFailureAfterSqlRestoresBothEndsAndAudit(int api, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var fault = new FailAfterSave(); await using var writer = Open(database.ConnectionString, fault);
        var model = await VersionedPaymentWriterTests.ModelAsync(writer, fixture); model.Notes = "Synthetic note must not persist";
        var before = await PaymentRemainderIdentityTests.SnapshotAsync(writer); var audit = await writer.ObjectAuditEvents.CountAsync(); fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => PaymentRemainderIdentityTests.WriteAsync(writer, fixture, api, model));
        Assert.Equal(before, await PaymentRemainderIdentityTests.SnapshotAsync(writer));
        Assert.Equal(audit, await writer.ObjectAuditEvents.CountAsync()); Assert.Null(writer.Database.CurrentTransaction); Assert.False(writer.ChangeTracker.HasChanges());
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
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Armed) { Armed = false; throw new IOException("Synthetic paired payment SQL failure"); }
            return ValueTask.FromResult(result);
        }
    }
}
