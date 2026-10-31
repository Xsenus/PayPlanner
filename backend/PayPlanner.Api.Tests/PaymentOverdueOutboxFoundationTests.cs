using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentOverdueOutboxFoundationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusAndIntent_CommitTogetherWithoutChangingMoney(bool legal)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); var fixture = await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        await using var transaction = await db.Database.BeginTransactionAsync(); var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue;
        var row = Assert.Single(PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow));
        Assert.Equal(0, await db.PaymentOverdueNotificationOutboxes.CountAsync()); Assert.Equal(PaymentStatus.Pending, (await db.Payments.AsNoTracking().SingleAsync()).Status);
        await db.SaveChangesAsync(); await transaction.CommitAsync(); db.ChangeTracker.Clear();
        Assert.Equal(row.TransitionToken, await TokenAsync(db)); Assert.Equal(row.EventKey, (await db.PaymentOverdueNotificationOutboxes.SingleAsync()).EventKey); Assert.Equal(fixture.PaymentId, row.PaymentId); Assert.Equal(100m, (await db.Payments.AsNoTracking().SingleAsync()).Amount); Assert.Equal(100m, await db.ClientBalanceTransactions.SumAsync(t => t.Amount));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSave_RollsBackStatusTokenIntentAndAudit(bool legal)
    {
        var fault = new SaveFault(); await using var db = await SqliteTestContextFactory.CreateAsync(interceptor: fault); await PaymentOverdueBoundaryTests.SeedAsync(db, legal); var audit = await db.ObjectAuditEvents.CountAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue; PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow); fault.Armed = true;
            await Assert.ThrowsAsync<IOException>(() => db.SaveChangesAsync()); await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear(); Assert.Equal(PaymentStatus.Pending, (await db.Payments.AsNoTracking().SingleAsync()).Status); Assert.Null(await TokenAsync(db)); Assert.Empty(await db.PaymentOverdueNotificationOutboxes.ToArrayAsync()); Assert.Equal(audit, await db.ObjectAuditEvents.CountAsync());
    }
    [Theory]
    [InlineData("pending")]
    [InlineData("cancelled")]
    [InlineData("paid")]
    [InlineData("date")]
    [InlineData("kind")]
    [InlineData("deleted")]
    [InlineData("owner")]
    public async Task RelevantChange_InvalidatesThePendingEvent(string change)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedIntentAsync(db);
        var payment = await db.Payments.SingleAsync();
        if (change == "pending") payment.Status = PaymentStatus.Pending;
        if (change == "cancelled") payment.Status = PaymentStatus.Cancelled;
        if (change == "paid") payment.IsPaid = true;
        if (change == "date") payment.Date = payment.Date.AddDays(1);
        if (change == "kind") payment.DocumentKind = DocumentKind.Invoice;
        if (change == "deleted") db.Entry(payment).Property<bool>("IsDeleted").CurrentValue = true;
        if (change == "owner") { var owner = new Client { Name = "New synthetic owner" }; db.Clients.Add(owner); await db.SaveChangesAsync(); payment.ClientId = owner.Id; }
        await db.SaveChangesAsync(); Assert.Null(await TokenAsync(db, ignoreFilters: change == "deleted")); Assert.Equal(1, await db.PaymentOverdueNotificationOutboxes.CountAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryNotes_PreserveTokenAndHideItFromJsonAndAudit(bool synchronous)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedIntentAsync(db); var token = await TokenAsync(db); var payment = await db.Payments.SingleAsync(); payment.Notes = "Ordinary synthetic note";
        if (synchronous) db.SaveChanges(); else await db.SaveChangesAsync(); Assert.Equal(token, await TokenAsync(db));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(await db.Payments.AsNoTracking().SingleAsync())); Assert.False(json.RootElement.TryGetProperty(PaymentOverdueNotificationQueue.TokenProperty, out _));
        Assert.DoesNotContain(await db.ObjectAuditEvents.Where(e => e.ObjectType == "payment").ToArrayAsync(), e => e.MetadataJson?.Contains(PaymentOverdueNotificationQueue.TokenProperty, StringComparison.Ordinal) == true);
    }
    [Fact]
    public async Task AccidentalTokenReset_OnANoteEditIsIgnored()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedIntentAsync(db); var token = await TokenAsync(db); var payment = await db.Payments.SingleAsync(); payment.Notes = "Preserve internal metadata"; db.Entry(payment).Property<string?>(PaymentOverdueNotificationQueue.TokenProperty).CurrentValue = null;
        await db.SaveChangesAsync(); Assert.Equal(token, await TokenAsync(db));
    }
    [Fact]
    public async Task ReenteredOverdue_GetsANewEventIdentity()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await SeedIntentAsync(db); var first = await db.PaymentOverdueNotificationOutboxes.SingleAsync(); var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Pending; await db.SaveChangesAsync(); Assert.Null(await TokenAsync(db));
        await using var transaction = await db.Database.BeginTransactionAsync(); payment.Status = PaymentStatus.Overdue; var second = Assert.Single(PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow)); await db.SaveChangesAsync(); await transaction.CommitAsync();
        Assert.NotEqual(first.EventKey, second.EventKey); Assert.NotEqual(first.TransitionToken, second.TransitionToken); Assert.Equal(second.TransitionToken, await TokenAsync(db)); Assert.Equal(2, await db.PaymentOverdueNotificationOutboxes.CountAsync());
    }
    [Fact]
    public async Task WithoutTransaction_CannotStageAnIntent()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueBoundaryTests.SeedAsync(db, false); var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue;
        Assert.Throws<InvalidOperationException>(() => PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow)); Assert.Null(db.Entry(payment).Property<string?>(PaymentOverdueNotificationQueue.TokenProperty).CurrentValue); Assert.Empty(db.ChangeTracker.Entries<PaymentOverdueNotificationOutbox>());
    }
    [Theory]
    [InlineData("paid")]
    [InlineData("zero")]
    [InlineData("duplicate")]
    [InlineData("repeated")]
    public async Task InvalidStage_IsRejectedBeforePartialIntentCreation(string reason)
    {
        await using var db = await SqliteTestContextFactory.CreateAsync(); await PaymentOverdueBoundaryTests.SeedAsync(db, false); await using var transaction = await db.Database.BeginTransactionAsync(); var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue;
        if (reason == "paid") payment.IsPaid = true;
        if (reason == "zero") payment.Amount = 0m;
        if (reason == "repeated") PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow);
        var before = db.ChangeTracker.Entries<PaymentOverdueNotificationOutbox>().Count();
        Assert.Throws<InvalidOperationException>(() => PaymentOverdueNotificationQueue.Stage(db, reason == "duplicate" ? [payment, payment] : [payment], DateTime.UtcNow)); Assert.Equal(before, db.ChangeTracker.Entries<PaymentOverdueNotificationOutbox>().Count()); await transaction.RollbackAsync();
    }
    internal static async Task SeedIntentAsync(PaymentContext db, bool legal = false)
    {
        await PaymentOverdueBoundaryTests.SeedAsync(db, legal); await using var transaction = await db.Database.BeginTransactionAsync(); var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue; PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow); await db.SaveChangesAsync(); await transaction.CommitAsync();
    }
    internal static Task<string?> TokenAsync(PaymentContext db, bool ignoreFilters = false)
        => (ignoreFilters ? db.Payments.IgnoreQueryFilters() : db.Payments).AsNoTracking().Select(p => EF.Property<string?>(p, PaymentOverdueNotificationQueue.TokenProperty)).SingleAsync();
    private sealed class SaveFault : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default) => Armed ? throw new IOException("Synthetic overdue event save failure") : ValueTask.FromResult(result);
    }
}

public sealed class PaymentOverdueOutboxFoundationPostgresTests(PostgreSqlModelTemplate template) : IClassFixture<PostgreSqlModelTemplate>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceAndEvent_AreVisibleTogetherOnlyAfterCommit(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); await PaymentOverdueBoundaryTests.SeedAsync(db, legal);
        await using var transaction = await db.Database.BeginTransactionAsync(); var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue; var row = Assert.Single(PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow)); await db.SaveChangesAsync();
        await using (var observer = Open(database.ConnectionString)) { Assert.Equal(PaymentStatus.Pending, (await observer.Payments.SingleAsync()).Status); Assert.Empty(await observer.PaymentOverdueNotificationOutboxes.ToArrayAsync()); Assert.Null(await PaymentOverdueOutboxFoundationTests.TokenAsync(observer)); }
        await transaction.CommitAsync(); await using var after = Open(database.ConnectionString); Assert.Equal(PaymentStatus.Overdue, (await after.Payments.SingleAsync()).Status); Assert.Equal(row.EventKey, (await after.PaymentOverdueNotificationOutboxes.SingleAsync()).EventKey); Assert.Equal(row.TransitionToken, await PaymentOverdueOutboxFoundationTests.TokenAsync(after));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlFailure_PreservesSourceAndRemovesTheUncommittedIntent(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using (var setup = Open(database.ConnectionString)) await PaymentOverdueBoundaryTests.SeedAsync(setup, legal);
        await using var db = Open(database.ConnectionString, new FailAfterSave()); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue; PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow); await Assert.ThrowsAsync<IOException>(() => db.SaveChangesAsync()); await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear(); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Null(await PaymentOverdueOutboxFoundationTests.TokenAsync(db)); Assert.Empty(await db.PaymentOverdueNotificationOutboxes.ToArrayAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NotesPreserveIdentity_WhileAStatusRoundTripCreatesANewOne(bool legal)
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db, legal); var first = await PaymentOverdueOutboxFoundationTests.TokenAsync(db); var payment = await db.Payments.SingleAsync(); payment.Notes = "Native note"; await db.SaveChangesAsync(); Assert.Equal(first, await PaymentOverdueOutboxFoundationTests.TokenAsync(db));
        payment.Status = PaymentStatus.Pending; await db.SaveChangesAsync(); Assert.Null(await PaymentOverdueOutboxFoundationTests.TokenAsync(db)); await using var transaction = await db.Database.BeginTransactionAsync(); payment.Status = PaymentStatus.Overdue; PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow); await db.SaveChangesAsync(); await transaction.CommitAsync(); Assert.NotEqual(first, await PaymentOverdueOutboxFoundationTests.TokenAsync(db)); Assert.Equal(2, await db.PaymentOverdueNotificationOutboxes.CountAsync());
    }
    [Fact]
    public async Task DeletedPayment_DoesNotHidePendingQueueRows()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); await PaymentOverdueOutboxFoundationTests.SeedIntentAsync(db); var payment = await db.Payments.SingleAsync(); db.Entry(payment).Property<bool>("IsDeleted").CurrentValue = true; await db.SaveChangesAsync();
        Assert.Empty(await db.Payments.ToArrayAsync()); Assert.Equal(1, await db.PaymentOverdueNotificationOutboxes.CountAsync()); Assert.Null(await PaymentOverdueOutboxFoundationTests.TokenAsync(db, ignoreFilters: true));
    }
    [Fact]
    public async Task UniqueEventKeyConflict_RollsBackTheStatusAndToken()
    {
        await using var database = await template.CreateDatabaseAsync(); await using var db = Open(database.ConnectionString); var fixture = await PaymentOverdueBoundaryTests.SeedAsync(db, false);
        db.PaymentOverdueNotificationOutboxes.Add(new() { PaymentId = fixture.PaymentId, EventKey = "synthetic-existing-event", TransitionToken = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow, DueDate = DateTime.UtcNow.Date }); await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue; Assert.Single(PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow)).EventKey = "synthetic-existing-event";
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState); await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear(); Assert.Equal(PaymentStatus.Pending, (await db.Payments.SingleAsync()).Status); Assert.Null(await PaymentOverdueOutboxFoundationTests.TokenAsync(db)); Assert.Equal(1, await db.PaymentOverdueNotificationOutboxes.CountAsync());
    }
    private static PostgreSqlPaymentContext Open(string connection, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection); if (interceptor is not null) options.AddInterceptors(interceptor); return new(options.Options);
    }
    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default) => throw new IOException("Synthetic native overdue intent save failure");
    }
}

public sealed class PaymentOverdueOutboxMigrationTests(PostgreSqlMigratedTemplate template) : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Fact]
    public async Task Sqlite_CleanAndUpgradePathsKeepExistingFinancialData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await VerifyMigrationAsync(db, "20261030232545_FenceCancelledBalanceApplications", "20261031125245_AddPaymentOverdueNotificationOutbox");
    }
    [Fact]
    public async Task Postgres_CleanAndUpgradePathsKeepExistingFinancialData()
    {
        await using var database = await template.CreateDatabaseAsync("pp_overdue_schema_");
        await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(database.ConnectionString).Options);
        await VerifyMigrationAsync(db, "20261030232554_FenceCancelledBalanceApplicationsPostgreSql", "20261031125252_AddPaymentOverdueNotificationOutboxPostgreSql");
    }
    private static async Task VerifyMigrationAsync(PaymentContext db, string previous, string current)
    {
        await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync()); await PaymentOverdueBoundaryTests.SeedAsync(db, false); var before = await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db); var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(previous); Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        await migrator.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges()); Assert.Equal(before, await PaymentAdvanceHistoryBoundaryTests.SnapshotAsync(db)); Assert.Empty(await db.PaymentOverdueNotificationOutboxes.ToArrayAsync()); Assert.Null(await PaymentOverdueOutboxFoundationTests.TokenAsync(db));
        await using var transaction = await db.Database.BeginTransactionAsync(); var payment = await db.Payments.SingleAsync(); payment.Status = PaymentStatus.Overdue; PaymentOverdueNotificationQueue.Stage(db, [payment], DateTime.UtcNow); await db.SaveChangesAsync(); await transaction.CommitAsync(); Assert.Equal(1, await db.PaymentOverdueNotificationOutboxes.CountAsync());
    }
}
