using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyPrintDefaultsConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelWriter_IsRejectedAndCanRetryAfterReload(bool settingExists)
    {
        await using var schema = await SqliteTestContextFactory.CreateAsync();
        var connectionString = $"Data Source=print-defaults-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=1";
        await using var firstConnection = new SqliteConnection(connectionString);
        await firstConnection.OpenAsync();
        ((SqliteConnection)schema.Database.GetDbConnection()).BackupDatabase(firstConnection);
        await using var secondConnection = new SqliteConnection(connectionString);
        await secondConnection.OpenAsync();
        await using var first = Context(firstConnection);
        await using var second = Context(secondConnection);
        first.LegalEntities.Add(new LegalEntity { Id = 7, ShortName = "Original" });
        if (settingExists) first.SystemSettings.Add(new SystemSetting
        {
            Key = CompanyDocumentImagePrintDefaultsService.Key(7),
            Value = CompanyDocumentImagePrintDefaultsService.Serialize(CompanyDocumentImagePrintDefaultsTests.Value(1)),
        });
        await first.SaveChangesAsync();
        var firstStore = new CompanyDocumentImagePrintDefaultsService(first);
        var secondStore = new CompanyDocumentImagePrintDefaultsService(second);
        var version = (await firstStore.ReadAsync(7)).Version!;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = firstStore.EditAsync(7, version, async () =>
        {
            admitted.SetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await firstStore.StageAsync(7, CompanyDocumentImagePrintDefaultsTests.Value(3), true);
            await first.SaveChangesAsync();
            return true;
        }, default);
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => Task.Run(() => secondStore.EditAsync(7, version, async () =>
            {
                await secondStore.StageAsync(7, CompanyDocumentImagePrintDefaultsTests.Value(6), true);
                await second.SaveChangesAsync();
                return true;
            }, default)).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { release.TrySetResult(); await winner; }
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(3), (await secondStore.ReadAsync(7)).Value);
        var freshVersion = (await secondStore.ReadAsync(7)).Version!;
        Assert.NotEqual(version, freshVersion);
        await secondStore.EditAsync(7, freshVersion, async () =>
        {
            await secondStore.StageAsync(7, CompanyDocumentImagePrintDefaultsTests.Value(6), true);
            await second.SaveChangesAsync();
            return true;
        }, default);
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(6), (await firstStore.ReadAsync(7)).Value);
        Assert.Single(await first.SystemSettings.AsNoTracking().Where(row => row.Key == CompanyDocumentImagePrintDefaultsService.Key(7)).ToListAsync());
    }

    [Fact]
    public async Task Guard_RefreshesUnchangedTrackedSettingAndRollsBackFailedEdit()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        db.LegalEntities.Add(new LegalEntity { Id = 7, ShortName = "Original" });
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        await store.StageAsync(7, CompanyDocumentImagePrintDefaultsTests.Value(1), true); await db.SaveChangesAsync();
        var different = CompanyDocumentImagePrintDefaultsService.Serialize(CompanyDocumentImagePrintDefaultsTests.Value(3));
        await db.SystemSettings.Where(row => row.Key == CompanyDocumentImagePrintDefaultsService.Key(7)).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Value, different));
        var version = (await store.ReadAsync(7)).Version!;
        await store.EditAsync(7, version, async () =>
        {
            await store.StageAsync(7, CompanyDocumentImagePrintDefaultsTests.Value(1), true); await db.SaveChangesAsync();
            return true;
        }, default);
        Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(1), (await store.ReadAsync(7)).Value);
        var nextVersion = (await store.ReadAsync(7)).Version!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.EditAsync<bool>(7, nextVersion, async () =>
        {
            var entity = await db.LegalEntities.SingleAsync(); entity.ShortName = "Must rollback";
            await store.StageAsync(7, null, true); await db.SaveChangesAsync();
            throw new InvalidOperationException("Synthetic failure after save");
        }, default));
        Assert.Equal("Original", (await db.LegalEntities.AsNoTracking().SingleAsync()).ShortName);
        Assert.Equal(nextVersion, (await store.ReadAsync(7)).Version);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Versions_AreStableCompanyScopedAndHandleInvalidLargeUnicodeWithoutFallback()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        var missing = (await store.ReadAsync(7)).Version;
        Assert.True(CompanyDocumentImagePrintDefaultsService.IsValidVersion(missing));
        Assert.NotEqual(missing, (await store.ReadAsync(8)).Version);
        db.SystemSettings.Add(new SystemSetting { Key = CompanyDocumentImagePrintDefaultsService.Key(7), Value = new string('Я', 10000) + "😀" });
        await db.SaveChangesAsync();
        var invalid = await store.ReadAsync(7);
        Assert.True(invalid.Invalid); Assert.Null(invalid.Value);
        Assert.NotEqual(missing, invalid.Version); Assert.Equal(invalid.Version, (await store.ReadAsync(7)).Version);
        Assert.False(CompanyDocumentImagePrintDefaultsService.IsValidVersion(null));
        Assert.False(CompanyDocumentImagePrintDefaultsService.IsValidVersion(new string('g', 64)));
    }

    [Fact]
    public async Task Guard_DoesNotCommitCallerTransaction()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        db.LegalEntities.Add(new LegalEntity { Id = 7, ShortName = "Original" }); await db.SaveChangesAsync();
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        var version = (await store.ReadAsync(7)).Version!;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await store.EditAsync(7, version, async () =>
        {
            await store.StageAsync(7, CompanyDocumentImagePrintDefaultsTests.Value(3), true); await db.SaveChangesAsync();
            return true;
        }, default);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.NotNull((await store.ReadAsync(7)).Value);
        await transaction.RollbackAsync(); db.ChangeTracker.Clear();
        Assert.Null((await store.ReadAsync(7)).Value);
        Assert.Equal(version, (await store.ReadAsync(7)).Version);
    }

    [Fact]
    public async Task Guard_DirtyAdmissionDoesNotFlushOrDiscardUnrelatedPendingChanges()
    {
        await using var db = await SqliteTestContextFactory.CreateAsync();
        db.LegalEntities.Add(new LegalEntity { Id = 7, ShortName = "Original" }); await db.SaveChangesAsync();
        var pending = new LegalEntity { Id = 8, ShortName = "Unrelated pending" }; db.Add(pending);
        var store = new CompanyDocumentImagePrintDefaultsService(db);
        var version = (await store.ReadAsync(7)).Version!;
        var entered = false;
        await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => store.EditAsync(7, version, () =>
        {
            entered = true; return Task.FromResult(false);
        }, default));
        Assert.False(entered); Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.False(await db.LegalEntities.AsNoTracking().AnyAsync(row => row.Id == 8));
        Assert.Null((await store.ReadAsync(7)).Value);
    }

    private static PaymentContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection,
            options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.GetName().Name)).Options, new HttpContextAccessor());
}
