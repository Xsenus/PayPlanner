using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Banking;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class BankSyncServiceTests
{
    private sealed class NativeDurabilityFixture(SharedFixture seed, PaymentContext first, PaymentContext second,
        IDataProtectionProvider protector, string? sqliteFile) : IAsyncDisposable
    {
        public PaymentContext First { get; } = first;
        public PaymentContext Second { get; } = second;
        public int AccountId => seed.AccountId;
        public BankSyncService Service(PaymentContext db, FakeBank bank) => new(db, [bank], protector);

        public static async Task<NativeDurabilityFixture> CreateAsync(bool postgres, params IInterceptor[] interceptors)
        {
            var seed = await CreateSharedAsync(postgres);
            string? file = null;
            PaymentContext? first = null, second = null;
            try
            {
                if (postgres)
                {
                    var connection = seed.Database.Db.Database.GetConnectionString()!;
                    first = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options);
                    second = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).Options);
                }
                else
                {
                    file = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pp-bank-durability-" + Guid.NewGuid().ToString("N") + ".sqlite"));
                    var connection = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 15 }.ToString();
                    await using (var target = new SqliteConnection(connection))
                    {
                        await target.OpenAsync();
                        ((SqliteConnection)seed.Database.Db.Database.GetDbConnection()).BackupDatabase(target);
                    }
                    first = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(interceptors).Options);
                    second = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
                }
                var protector = new EphemeralDataProtectionProvider();
                var result = new NativeDurabilityFixture(seed, first, second, protector, file);
                var connectionRecord = await first.BankConnections.SingleAsync();
                result.Service(first, new FakeBank()).StoreTokens(connectionRecord, "synthetic-access", "synthetic-refresh", DateTime.UtcNow.AddHours(1));
                await first.SaveChangesAsync();
                Assert.NotSame(first.Database.GetDbConnection(), second.Database.GetDbConnection());
                return result;
            }
            catch
            {
                if (second is not null) await second.DisposeAsync();
                if (first is not null) await first.DisposeAsync();
                await seed.Database.DisposeAsync();
                DeleteOwnedFile(file);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Second.DisposeAsync();
            await First.DisposeAsync();
            await seed.Database.DisposeAsync();
            DeleteOwnedFile(sqliteFile);
        }

        private static void DeleteOwnedFile(string? path)
        {
            if (path is null) return;
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(path));
            Assert.Matches("^pp-bank-durability-[a-f0-9]{32}\\.sqlite$", Path.GetFileName(path));
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) File.Delete(path + suffix);
        }
    }

    private static FakeBank NativeOnePage() => new() { Pages = { ["shared"] = [[Op("one", 1)]] } };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durability_CompletedResultReplaysInFreshContextWithoutBankCall(bool postgres)
    {
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres);
        var invocation = BankSyncInvocation.Scheduled("restart-completed");
        var first = await fixture.Service(fixture.First, NativeOnePage()).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(first.Error);
        var restartedBank = new FakeBank { Unauthorized = true };
        var replay = await fixture.Service(fixture.Second, restartedBank).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Equal(first, replay);
        Assert.Equal(0, restartedBank.StatementCalls);
        Assert.Equal(1, await fixture.Second.MoneyOperations.CountAsync());
        Assert.Equal(1, await fixture.Second.BankStatementImportBatches.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durability_PartialRunResumesInFreshContextWithOriginalMoney(bool postgres)
    {
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres);
        var bank = NativeOnePage();
        bank.Pages["shared"] = [[Op("one", 1)], [Op("two", 2)]];
        bank.FailOnPage = 1;
        var invocation = BankSyncInvocation.Scheduled("restart-partial");
        var failed = await fixture.Service(fixture.First, bank).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.NotNull(failed.Error);
        var moneyId = (await fixture.Second.MoneyOperations.SingleAsync()).Id;
        var batchId = (await fixture.Second.BankStatementImportBatches.SingleAsync()).Id;
        var restartedBank = NativeOnePage();
        restartedBank.Pages["shared"] = bank.Pages["shared"];
        var resumed = await fixture.Service(fixture.Second, restartedBank).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        Assert.Equal(batchId, resumed.BatchId);
        Assert.Equal(2, await fixture.Second.MoneyOperations.CountAsync());
        Assert.True(await fixture.Second.MoneyOperations.AnyAsync(operation => operation.Id == moneyId));
        Assert.Equal(1, await fixture.Second.BankStatementImportBatches.CountAsync());
        Assert.Equal(2, await fixture.Second.BankStatementImportRows.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durability_FailedRunReturnsPersistedPackageId(bool postgres)
    {
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres);
        var bank = NativeOnePage();
        bank.FailOnPage = 0;
        var failed = await fixture.Service(fixture.First, bank).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.Scheduled("failed-receipt"), default);
        Assert.NotNull(failed.Error);
        Assert.Equal((await fixture.Second.BankStatementImportBatches.SingleAsync()).Id, failed.BatchId);
    }

    public static IEnumerable<object[]> NativeRevokedFailureCases =>
        from postgres in new[] { false, true }
        from authorizationError in new[] { false, true }
        from removeCompany in new[] { false, true }
        select new object[] { postgres, authorizationError, removeCompany };

    [Theory]
    [MemberData(nameof(NativeRevokedFailureCases))]
    public async Task Durability_ProviderFailureAfterRevocationReturnsControlledDenial(bool postgres, bool authorizationError, bool removeCompany)
    {
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres);
        var db = fixture.First;
        var (user, principal) = await AddNativeAdminAsync(db);
        var bank = NativeOnePage();
        bank.BeforeStatementPage = async _ =>
        {
            if (removeCompany) await fixture.Second.SystemSettings.Where(item => item.Key == "legalEntities.mainCompanyIds").ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Value, "[]"));
            else await fixture.Second.Users.Where(item => item.Id == user.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsActive, false));
            if (authorizationError) throw new BankAuthorizationException("synthetic bank authorization failure");
            throw new InvalidOperationException("synthetic bank transport failure");
        };
        BankStatementSyncResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await fixture.Service(db, bank).SyncStatementAsync(
            fixture.AccountId, SharedFrom, SharedTo, BankSyncInvocation.ForUser(principal, Guid.NewGuid()), default));
        Assert.Null(error);
        Assert.NotNull(result!.Error);
        Assert.Empty(await fixture.Second.BankStatementOperations.ToArrayAsync());
        Assert.Empty(await fixture.Second.MoneyOperations.IgnoreQueryFilters().ToArrayAsync());
        Assert.Empty(await fixture.Second.BankStatementImportRows.ToArrayAsync());
        Assert.Equal(BankConnectionStatuses.Active, (await fixture.Second.BankConnections.SingleAsync()).Status);
        Assert.Null((await fixture.Second.BankConnectionAccounts.SingleAsync()).LastError);
        Assert.Null((await fixture.Second.BankStatementImportBatches.SingleAsync()).CompletedAt);
        Assert.Empty(await fixture.Second.BankSyncLogs.Where(log => log.Kind == "statement").ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durability_OverlappingSameRequestReturnsFirstResponseAndOneJournal(bool postgres)
    {
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedBank = NativeOnePage();
        delayedBank.BeforeStatementPage = async _ => { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(30)); };
        var invocation = BankSyncInvocation.Scheduled("overlapping-same-request");
        var delayed = fixture.Service(fixture.First, delayedBank).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        BankStatementSyncResult winner;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            winner = await fixture.Service(fixture.Second, NativeOnePage()).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        }
        finally { release.TrySetResult(); }
        var late = await delayed;
        Assert.Null(winner.Error);
        Assert.Equal(winner, late);
        fixture.Second.ChangeTracker.Clear();
        Assert.Equal(1, await fixture.Second.MoneyOperations.CountAsync());
        Assert.Equal(1, await fixture.Second.BankStatementImportBatches.CountAsync());
        Assert.Equal(BankStatementImportBatchStatus.Applied, (await fixture.Second.BankStatementImportBatches.SingleAsync()).Status);
        Assert.Equal(1, await fixture.Second.BankStatementImportRows.CountAsync());
        Assert.Equal(1, await fixture.Second.BankSyncLogs.CountAsync(log => log.Kind == "statement" && log.Result == "ok"));
    }

    private sealed class NativeCompletionSaveFault : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool Fired { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<BankStatementImportBatch>().Any(entry => entry.Entity.CompletedAt.HasValue))
            {
                Fired = true;
                throw new InvalidOperationException("synthetic final package save failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durability_CompletionFailureRollsBackWatermarkAndSuccessJournal(bool postgres)
    {
        var fault = new NativeCompletionSaveFault();
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres, fault);
        fault.Enabled = true;
        var invocation = BankSyncInvocation.Scheduled("atomic-completion");
        var error = await Record.ExceptionAsync(async () => await fixture.Service(fixture.First, NativeOnePage()).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default));
        Assert.NotNull(error);
        Assert.True(fault.Fired);
        Assert.Null((await fixture.Second.BankConnectionAccounts.SingleAsync()).StatementWatermark);
        Assert.Null((await fixture.Second.BankStatementImportBatches.SingleAsync()).CompletedAt);
        Assert.Empty(await fixture.Second.BankSyncLogs.Where(log => log.Kind == "statement" && log.Result == "ok").ToArrayAsync());
        var moneyId = (await fixture.Second.MoneyOperations.SingleAsync()).Id;
        var resumed = await fixture.Service(fixture.Second, NativeOnePage()).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Null(resumed.Error);
        Assert.Equal(moneyId, (await fixture.Second.MoneyOperations.SingleAsync()).Id);
        Assert.Equal(1, await fixture.Second.BankSyncLogs.CountAsync(log => log.Kind == "statement" && log.Result == "ok"));
    }

    private sealed class NativeCommitAckLoss : DbTransactionInterceptor
    {
        public bool Fired { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<BankSyncLog>().Any(entry => entry.Entity.Kind == "statement" && entry.Entity.Result == "ok"))
            {
                Fired = true;
                throw new InvalidOperationException("synthetic lost final commit acknowledgement");
            }
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durability_LostCommitAcknowledgementReplaysPersistedResponseAfterRestart(bool postgres)
    {
        var fault = new NativeCommitAckLoss();
        await using var fixture = await NativeDurabilityFixture.CreateAsync(postgres, fault);
        var invocation = BankSyncInvocation.Scheduled("lost-final-ack");
        var error = await Record.ExceptionAsync(async () => await fixture.Service(fixture.First, NativeOnePage()).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default));
        Assert.NotNull(error);
        Assert.True(fault.Fired);
        var saved = await fixture.Second.BankStatementImportBatches.SingleAsync();
        Assert.NotNull(saved.CompletedAt);
        var expected = JsonSerializer.Deserialize<BankStatementSyncResult>(saved.SourceResultJson!);
        var bank = new FakeBank { Unauthorized = true };
        var replay = await fixture.Service(fixture.Second, bank).SyncStatementAsync(fixture.AccountId, SharedFrom, SharedTo, invocation, default);
        Assert.Equal(expected, replay);
        Assert.Equal(1, replay.Inserted);
        Assert.Equal(0, bank.StatementCalls);
        Assert.Equal(1, await fixture.Second.MoneyOperations.CountAsync());
        Assert.Equal(1, await fixture.Second.BankSyncLogs.CountAsync(log => log.Kind == "statement" && log.Result == "ok"));
    }
}
