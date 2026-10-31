using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using Xunit;
using static PayPlanner.Api.Tests.BalanceApplicationIdempotencyTests;

namespace PayPlanner.Api.Tests;

public sealed class VersionedPaymentStructureTests
{
    public static IEnumerable<object[]> ProtectedCases()
    {
        foreach (var version in new[] { 1, 2 })
            foreach (var legal in new[] { false, true })
                foreach (var storage in new[] { "structured", "legacy" })
                    foreach (var action in new[] { "source-amount", "source-paid", "source-delete", "remainder-amount", "remainder-cancel", "remainder-delete" })
                        yield return [version, legal, storage, action];
    }

    [Theory]
    [MemberData(nameof(ProtectedCases))]
    public async Task SplitAmountsAndBothEndsCannotBeChangedIndependently(int version, bool legal, string storage, string action)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (source, remainder) = await SplitAsync(db, legal, storage);
        var fixture = action.StartsWith("source", StringComparison.Ordinal) ? source : source with { PaymentId = remainder };
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture);
        if (action.EndsWith("amount", StringComparison.Ordinal)) model.Amount += 10m;
        if (action == "source-paid") model.PaidAmount -= 10m;
        if (action == "remainder-cancel") model.Status = PaymentStatus.Cancelled;
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var audit = await db.ObjectAuditEvents.CountAsync();
        var response = await VersionedPaymentWriterTests.WriteAsync(db, fixture, version, action.EndsWith("delete", StringComparison.Ordinal) ? "delete" : "update", model);
        Assert.IsType<ConflictObjectResult>(response);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
        Assert.Null(db.Database.CurrentTransaction);
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Theory]
    [InlineData(1, false, "installment")]
    [InlineData(1, true, "invoice")]
    [InlineData(2, false, "installment")]
    [InlineData(2, true, "invoice")]
    public async Task InvoiceManagedUpdatesUseTheInvoiceWorkflow(int version, bool legal, string kind)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        Fixture fixture;
        if (kind == "invoice") fixture = (await CompensatedPaymentWriterTests.SeedOpenAsync(db, legal)).Receipt;
        else fixture = await SeedAsync(db, legal);
        if (kind == "invoice") fixture = fixture with { PaymentId = await db.Payments.Where(p => p.DocumentKind == DocumentKind.Invoice).Select(p => p.Id).SingleAsync() };
        else await BalanceApplicationRemainderWriterTests.MarkManagedAsync(db, fixture);
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Amount += 10m;
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var response = Assert.IsType<ConflictObjectResult>(await VersionedPaymentWriterTests.WriteAsync(db, fixture, version, "update", model));
        Assert.Equal(InvoiceManagedPaymentRules.UpdateBlockedMessage, System.Text.Json.JsonSerializer.SerializeToElement(response.Value).GetProperty("message").GetString());
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvoiceRepresentativeCannotBeDeletedIndependently(int version)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var seed = await CompensatedPaymentWriterTests.SeedOpenAsync(db, false);
        var fixture = seed.Receipt with { PaymentId = seed.RootId };
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await VersionedPaymentWriterTests.WriteAsync(db, fixture, version, "delete", await VersionedPaymentWriterTests.ModelAsync(db, fixture)));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, true, false)]
    [InlineData(2, false, true)]
    public async Task NotesAndRemainderPaymentRemainAvailable(int version, bool legal, bool pay)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (source, remainder) = await SplitAsync(db, legal, "structured");
        var fixture = pay ? source with { PaymentId = remainder } : source;
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Notes = "Synthetic permitted annotation";
        if (pay) { model.PaidAmount = model.Amount; model.IsPaid = true; model.Status = PaymentStatus.Completed; }
        Assert.IsType<OkObjectResult>(await VersionedPaymentWriterTests.WriteAsync(db, fixture, version, "update", model));
        var saved = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == fixture.PaymentId);
        Assert.Equal(model.Notes, saved.Notes); Assert.Equal(model.Amount, saved.Amount);
        Assert.Equal(pay ? PaymentStatus.Completed : model.Status, saved.Status);
        if (pay) Assert.True(saved.IsPaid);
        Assert.Single(await db.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Theory]
    [InlineData(1, "permission")]
    [InlineData(2, "permission")]
    [InlineData(1, "hidden")]
    [InlineData(2, "hidden")]
    public async Task StructureIsNotDisclosedWithoutAccess(int version, string denial)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var (source, _) = await SplitAsync(db, false, "structured");
        var model = await VersionedPaymentWriterTests.ModelAsync(db, source); model.Amount += 10m;
        var permission = await db.RolePermissions.SingleAsync(p => p.Section == "payments");
        if (denial == "permission") permission.CanEdit = false; else permission.ViewScope = "own";
        await db.SaveChangesAsync();
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        var response = await VersionedPaymentWriterTests.WriteAsync(db, source, version, "update", model);
        if (denial == "permission") Assert.IsType<ForbidResult>(response); else Assert.IsType<NotFoundResult>(response);
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task AnUnrelatedLegacyMarkerDoesNotBlockAnOrdinaryPayment(int version, bool delete)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true;
        var unrelated = PaymentFor(fixture);
        unrelated.SystemNotes = PaymentRemainderTransferRules.BuildMarker(fixture.PaymentId * 10, 100m, 40m, 60m, new DateTime(2026, 11, 15));
        db.Payments.Add(unrelated); await db.SaveChangesAsync();
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Amount = 90m;
        var response = await VersionedPaymentWriterTests.WriteAsync(db, fixture, version, delete ? "delete" : "update", model);
        if (delete) Assert.IsType<NoContentResult>(response); else Assert.IsType<OkObjectResult>(response);
        Assert.Equal(unrelated.SystemNotes, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == unrelated.Id)).SystemNotes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExistingStandaloneInstallmentDeletionRemainsAvailable(int version)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var fixture = await SeedAsync(db, false);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true;
        await db.SaveChangesAsync(); await BalanceApplicationRemainderWriterTests.MarkManagedAsync(db, fixture);
        Assert.IsType<NoContentResult>(await VersionedPaymentWriterTests.WriteAsync(db, fixture, version, "delete", await VersionedPaymentWriterTests.ModelAsync(db, fixture)));
        Assert.Empty(await db.Payments.ToArrayAsync());
    }

    internal static async Task<(Fixture Source, int Remainder)> SplitAsync(PaymentContext db, bool legal, string storage)
    {
        var fixture = await SeedAsync(db, legal);
        (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true;
        await db.SaveChangesAsync();
        var remainder = await BalanceApplicationRemainderWriterTests.SplitAsync(db, fixture);
        if (storage == "structured")
        {
            foreach (var payment in await db.Payments.ToArrayAsync()) payment.SystemNotes = "Synthetic history without legacy markers";
        }
        else
        {
            db.PaymentRemainderTransfers.RemoveRange(await db.PaymentRemainderTransfers.ToArrayAsync());
            (await db.Payments.SingleAsync(p => p.Id == fixture.PaymentId)).SystemNotes = "Legacy source without marker";
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        return (fixture, remainder);
    }
}

public sealed class VersionedPaymentStructurePostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, true)]
    [InlineData(2, true, false)]
    public async Task TransferHoldingSource_ConflictsThenProtectsBothEnds(int version, bool legal, bool delete)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString))
        {
            fixture = await SeedAsync(db, legal);
            (await db.RolePermissions.SingleAsync(p => p.Section == "payments")).CanDelete = true; await db.SaveChangesAsync();
        }
        var barrier = new HoldSave();
        await using var transferring = Open(database.ConnectionString, barrier); await using var editor = Open(database.ConnectionString);
        var model = await VersionedPaymentWriterTests.ModelAsync(editor, fixture); model.Amount = 90m;
        var pending = BalanceApplicationPaymentWriterTests.Controller(transferring, fixture).TransferPartialRemainder(fixture.PaymentId,
            BalanceApplicationRemainderWriterTests.Transfer(fixture), CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            Assert.IsType<ConflictObjectResult>(await VersionedPaymentWriterTests.WriteAsync(editor, fixture, version, delete ? "delete" : "update", model).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await pending.WaitAsync(TimeSpan.FromSeconds(20)));
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(editor);
        Assert.IsType<ConflictObjectResult>(await VersionedPaymentWriterTests.WriteAsync(editor, fixture, version, delete ? "delete" : "update", model));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(editor));
        Assert.Single(await editor.PaymentRemainderTransfers.ToArrayAsync());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task LegacyReaderIsClosedAndTheMatchingSourceIsProtected(int version, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString);
        var (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "legacy");
        var model = await VersionedPaymentWriterTests.ModelAsync(db, fixture); model.Amount += 10m;
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(db);
        Assert.IsType<ConflictObjectResult>(await VersionedPaymentWriterTests.WriteAsync(db, fixture, version, "update", model));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(db));
        Assert.Null(db.Database.CurrentTransaction); Assert.Equal(2, await db.Payments.CountAsync());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task AllowedNoteFailureAfterSqlRollsBackPaymentStructureAndAudit(int version, bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); Fixture fixture;
        await using (var db = Open(database.ConnectionString)) (fixture, _) = await VersionedPaymentStructureTests.SplitAsync(db, legal, "structured");
        var fault = new FailAfterSave(); await using var writer = Open(database.ConnectionString, fault);
        var model = await VersionedPaymentWriterTests.ModelAsync(writer, fixture); model.Notes = "Synthetic note must roll back";
        var before = await BalanceApplicationRemainderWriterTests.SnapshotAsync(writer); var audit = await writer.ObjectAuditEvents.CountAsync();
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => VersionedPaymentWriterTests.WriteAsync(writer, fixture, version, "update", model));
        Assert.Equal(before, await BalanceApplicationRemainderWriterTests.SnapshotAsync(writer));
        Assert.Equal(audit, await writer.ObjectAuditEvents.CountAsync()); Assert.Null(writer.Database.CurrentTransaction); Assert.False(writer.ChangeTracker.HasChanges());
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
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), ct); return result; }
    }
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
        {
            if (Armed) { Armed = false; throw new IOException("Synthetic structure save failure after SQL"); }
            return ValueTask.FromResult(result);
        }
    }
}
