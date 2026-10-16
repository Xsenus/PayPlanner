using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Requests;
using PayPlanner.Api.Models.Responses;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public partial class SpreadsheetImportServiceTests
{
    private const string AtomicTargetName = "ООО Синтетический откат";
    private const string AtomicIndependentName = "AAA Синтетический независимый";

    public static IEnumerable<object[]> CounterpartyRollbackFaults()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var updated in new[] { false, true })
                foreach (var phase in new[] { "data", "result" })
                    yield return [postgres, updated, phase];
    }

    [Theory]
    [MemberData(nameof(CounterpartyRollbackFaults))]
    public async Task RollbackAsync_LateCounterpartyFailureIsAtomicAndNextRowRetryRemainUsable(bool postgres, bool updated, string phase)
    {
        await using var fixture = await SpreadsheetRollbackSessions.CreateAsync(postgres);
        var failure = new CounterpartyRollbackSavedFailure(phase);
        var service = fixture.Service(failure);
        var target = await SeedAtomicRollbackAsync(fixture, service, updated, independent: true);
        var before = await CaptureAtomicTargetAsync(fixture.Observer, target);
        failure.ClientId = target.ClientId;
        failure.OperationId = target.OperationId;
        failure.Armed = true;

        var failed = await service.RollbackAsync(target.BatchId);
        Assert.Equal(1, failure.Fired);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, failed.Status);
        Assert.Equal(1, failed.RollbackRows);
        Assert.Equal(SpreadsheetImportRollbackAction.Error, Assert.Single(failed.Operations, item => item.EntityId == target.ClientId).RollbackAction);
        Assert.Equal(SpreadsheetImportRollbackAction.Deleted, Assert.Single(failed.Operations, item => item.EntityId == target.IndependentClientId).RollbackAction);
        fixture.Observer.ChangeTracker.Clear();
        Assert.Equal(before, await CaptureAtomicTargetAsync(fixture.Observer, target));
        Assert.False(await fixture.Observer.Clients.AnyAsync(item => item.Id == target.IndependentClientId));

        failure.Armed = false;
        var retry = await service.RollbackAsync(target.BatchId);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, retry.Status);
        Assert.Equal(2, retry.RollbackRows);
        Assert.All(retry.Operations, item => Assert.Contains(item.RollbackAction,
            new[] { SpreadsheetImportRollbackAction.Deleted, SpreadsheetImportRollbackAction.Restored }));
        fixture.Observer.ChangeTracker.Clear();
        Assert.False(await fixture.Observer.LegalEntities.AnyAsync(item => item.Id == target.CompanyId));
        if (updated)
        {
            var restored = await fixture.Observer.Clients.SingleAsync(item => item.Id == target.ClientId);
            Assert.Equal(AtomicTargetName, restored.Name);
            Assert.Null(restored.LegalEntityId);
        }
        else Assert.False(await fixture.Observer.Clients.AnyAsync(item => item.Id == target.ClientId));

        var completed = await CaptureAtomicCompletionAsync(fixture.Observer, target.BatchId);
        var repeated = await service.RollbackAsync(target.BatchId);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, repeated.Status);
        Assert.Equal(2, repeated.RollbackRows);
        fixture.Observer.ChangeTracker.Clear();
        Assert.Equal(completed, await CaptureAtomicCompletionAsync(fixture.Observer, target.BatchId));
    }

    public static IEnumerable<object[]> CounterpartyRollbackRaces()
    {
        foreach (var postgres in new[] { false, true })
            foreach (var updated in new[] { false, true })
                foreach (var reference in new[] { "account", "package" })
                    yield return [postgres, updated, reference];
    }

    [Theory]
    [MemberData(nameof(CounterpartyRollbackRaces))]
    public async Task RollbackAsync_ConcurrentCommittedFinancialReferencePreservesCompanyAndClient(bool postgres, bool updated, string reference)
    {
        await using var fixture = await SpreadsheetRollbackSessions.CreateAsync(postgres);
        var gate = new CounterpartyRollbackTransactionGate();
        var service = fixture.Service(gate, gate.Commands);
        var target = await SeedAtomicRollbackAsync(fixture, service, updated, independent: false);
        fixture.Observer.ChangeTracker.Clear();
        await using var writer = await fixture.Observer.Database.BeginTransactionAsync();
        await fixture.Observer.LegalEntities.Where(item => item.Id == target.CompanyId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.ShortName, item => item.ShortName));
        if (reference == "account")
            fixture.Observer.MoneyAccounts.Add(new MoneyAccount { Name = "Синтетическая параллельная связь", CompanyId = target.CompanyId, Source = MoneyOperationSource.Bank });
        else
            fixture.Observer.BankStatementImportBatches.Add(new BankStatementImportBatch { CompanyId = target.CompanyId, FileName = "synthetic-concurrent.csv", FileHash = "synthetic-concurrent" });
        await fixture.Observer.SaveChangesAsync();
        gate.Armed = true;
        var rollback = Task.Run(() => service.RollbackAsync(target.BatchId));
        var writerFinished = false;
        SpreadsheetImportBatchDetailDto result;
        try
        {
            // Observe the real transaction boundary, including SQLite's BEGIN IMMEDIATE.
            // With no rollback transaction the old code reaches it only after its stale precheck.
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await writer.CommitAsync();
            writerFinished = true;
            result = await rollback.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!writerFinished) await writer.RollbackAsync();
            if (!rollback.IsCompleted) await rollback.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBackWithConflicts, result.Status);
        Assert.Equal(0, result.RollbackRows);
        Assert.Equal(SpreadsheetImportRollbackAction.Skipped, Assert.Single(result.Operations).RollbackAction);
        fixture.Observer.ChangeTracker.Clear();
        Assert.True(await fixture.Observer.LegalEntities.AnyAsync(item => item.Id == target.CompanyId));
        Assert.Equal(target.CompanyId, (await fixture.Observer.Clients.SingleAsync(item => item.Id == target.ClientId)).LegalEntityId);
        Assert.Equal(reference == "account" ? 1 : 0, await fixture.Observer.MoneyAccounts.CountAsync(item => item.CompanyId == target.CompanyId));
        Assert.Equal(reference == "package" ? 1 : 0, await fixture.Observer.BankStatementImportBatches.CountAsync(item => item.CompanyId == target.CompanyId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RollbackAsync_LostCommitAcknowledgementKeepsDurableCounterpartyResult(bool postgres, bool updated)
    {
        await using var fixture = await SpreadsheetRollbackSessions.CreateAsync(postgres);
        var fault = new CounterpartyRollbackCommitFailure();
        var service = fixture.Service(fault);
        var target = await SeedAtomicRollbackAsync(fixture, service, updated, independent: false);
        fault.Armed = true;
        var result = await service.RollbackAsync(target.BatchId);
        Assert.Equal(1, fault.Fired);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, result.Status);
        Assert.Equal(1, result.RollbackRows);
        Assert.Equal(updated ? SpreadsheetImportRollbackAction.Restored : SpreadsheetImportRollbackAction.Deleted,
            Assert.Single(result.Operations).RollbackAction);
        fixture.Observer.ChangeTracker.Clear();
        Assert.False(await fixture.Observer.LegalEntities.AnyAsync(item => item.Id == target.CompanyId));
        Assert.Equal(updated ? 1 : 0, await fixture.Observer.Clients.CountAsync(item => item.Id == target.ClientId));
        var completed = await CaptureAtomicCompletionAsync(fixture.Observer, target.BatchId);
        var repeated = await service.RollbackAsync(target.BatchId);
        Assert.Equal(SpreadsheetImportBatchStatus.RolledBack, repeated.Status);
        Assert.Equal(1, repeated.RollbackRows);
        fixture.Observer.ChangeTracker.Clear();
        Assert.Equal(completed, await CaptureAtomicCompletionAsync(fixture.Observer, target.BatchId));
    }

    private sealed class CounterpartyRollbackCommitFailure : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public int Fired { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (Armed && Fired == 0)
            {
                Fired++;
                throw new InvalidOperationException("synthetic counterparty rollback commit acknowledgement failure");
            }
            return Task.CompletedTask;
        }
    }

    private sealed record AtomicRollbackTarget(int BatchId, int ClientId, int CompanyId, int? IndependentClientId, int OperationId);

    private static async Task<AtomicRollbackTarget> SeedAtomicRollbackAsync(SpreadsheetRollbackSessions fixture,
        SpreadsheetImportService service, bool updated, bool independent)
    {
        if (updated)
        {
            fixture.Observer.Clients.Add(new Client { Name = AtomicTargetName });
            await fixture.Observer.SaveChangesAsync();
        }
        var rows = new List<string[]> { new[] { "Название", "ИНН", "Контакт", "Телефон", "Email", "Договор", "Отсрочка дн.", "Статья приходов", "Ответственный" } };
        if (independent) rows.Add([AtomicIndependentName, "", "", "", "", "", "", "", ""]);
        rows.Add([AtomicTargetName, "1234567890", "", "", "", "", "", "", ""]);
        var files = new[] { CreateCounterpartiesFile(rows.ToArray()) };
        var preview = await service.PreviewAsync(files);
        var selected = Assert.Single(preview.Sections, item => item.Key == "counterparties").Rows;
        var imported = await service.ApplyAsync(files, new SpreadsheetImportApplyRequest
        {
            SelectedPreviewIds = selected.Select(item => item.PreviewId).ToList(),
            CounterpartyOptions = selected.Select(item => new SpreadsheetImportCounterpartyOptionRequest
            { PreviewId = item.PreviewId, ImportMode = item.Title == AtomicTargetName ? "LegalEntityWithClient" : "ClientOnly" }).ToList(),
            AllowUpdates = true,
        });
        Assert.Equal(SpreadsheetImportBatchStatus.Applied, imported.Status);
        fixture.Observer.ChangeTracker.Clear();
        var client = await fixture.Observer.Clients.AsNoTracking().SingleAsync(item => item.Name == AtomicTargetName);
        Assert.NotNull(client.LegalEntityId);
        var target = Assert.Single(imported.Operations, item => item.EntityId == client.Id);
        Assert.Equal(updated ? SpreadsheetImportOperationAction.Updated : SpreadsheetImportOperationAction.Created, target.Action);
        int? otherId = null;
        if (independent)
        {
            otherId = (await fixture.Observer.Clients.AsNoTracking().SingleAsync(item => item.Name == AtomicIndependentName)).Id;
            Assert.True(target.Id > Assert.Single(imported.Operations, item => item.EntityId == otherId).Id,
                "The faulted row must be followed by another real rollback operation.");
        }
        return new(imported.Id, client.Id, client.LegalEntityId.Value, otherId, target.Id);
    }

    private static async Task<string> CaptureAtomicTargetAsync(PaymentContext db, AtomicRollbackTarget target)
        => JsonSerializer.Serialize(new
        {
            Client = await db.Clients.IgnoreQueryFilters().AsNoTracking().Where(item => item.Id == target.ClientId)
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.Email,
                    item.Phone,
                    item.Company,
                    item.Address,
                    item.Notes,
                    item.IsActive,
                    item.LegalEntityId,
                    item.ResponsibleUserId,
                    item.ClientStatusId,
                    Deleted = EF.Property<bool>(item, "IsDeleted"),
                    Updated = EF.Property<DateTime?>(item, "UpdatedAt")
                }).SingleAsync(),
            Company = await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().Where(item => item.Id == target.CompanyId)
                .Select(item => new
                {
                    item.Id,
                    item.ShortName,
                    item.FullName,
                    item.Inn,
                    item.Kpp,
                    item.ProfileStatus,
                    Deleted = EF.Property<bool>(item, "IsDeleted"),
                    Updated = EF.Property<DateTime?>(item, "UpdatedAt")
                }).SingleAsync(),
        });

    private static async Task<string> CaptureAtomicCompletionAsync(PaymentContext db, int batchId)
        => JsonSerializer.Serialize(new
        {
            Clients = await db.Clients.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.LegalEntityId, Deleted = EF.Property<bool>(item, "IsDeleted"), Updated = EF.Property<DateTime?>(item, "UpdatedAt") }).ToArrayAsync(),
            Companies = await db.LegalEntities.IgnoreQueryFilters().AsNoTracking().OrderBy(item => item.Id)
                .Select(item => new { item.Id, Deleted = EF.Property<bool>(item, "IsDeleted"), Updated = EF.Property<DateTime?>(item, "UpdatedAt") }).ToArrayAsync(),
            Operations = await db.SpreadsheetImportBatchOperations.AsNoTracking().Where(item => item.BatchId == batchId).OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.RollbackAction, item.RollbackMessage, item.RolledBackAt }).ToArrayAsync(),
        });

    private sealed class CounterpartyRollbackSavedFailure(string phase) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int ClientId { get; set; }
        public int OperationId { get; set; }
        public int Fired { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        {
            var db = data.Context!;
            var matches = phase == "data"
                ? db.ChangeTracker.Entries<Client>().Any(entry => entry.Entity.Id == ClientId
                    && ((bool)entry.Property("IsDeleted").CurrentValue! || entry.Entity.LegalEntityId is null))
                : db.ChangeTracker.Entries<SpreadsheetImportBatchOperation>().Any(entry => entry.Entity.Id == OperationId
                    && entry.Entity.RollbackAction is SpreadsheetImportRollbackAction.Deleted or SpreadsheetImportRollbackAction.Restored);
            if (Armed && Fired == 0 && matches)
            {
                Fired++;
                throw new InvalidOperationException("synthetic counterparty rollback acknowledgement failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CounterpartyRollbackTransactionGate : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DbCommandInterceptor Commands => new CommandGate(this);
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
        {
            if (Armed) Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }

        private sealed class CommandGate(CounterpartyRollbackTransactionGate owner) : DbCommandInterceptor
        {
            private void Observe(DbCommand command)
            {
                if (owner.Armed && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                    && (command.CommandText.Contains("\"Clients\"", StringComparison.Ordinal)
                        || command.CommandText.Contains("\"LegalEntities\"", StringComparison.Ordinal)))
                    owner.Entered.TrySetResult();
            }
            public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
                CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
            {
                Observe(command);
                return ValueTask.FromResult(result);
            }
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
            {
                Observe(command);
                return ValueTask.FromResult(result);
            }
        }
    }

    private sealed class SpreadsheetRollbackSessions(BankImportProvenanceModelTests.ModelDatabase source, bool postgres,
        string connection, string? file, PaymentContext observer) : IAsyncDisposable
    {
        public PaymentContext Observer { get; } = observer;
        private readonly IHttpContextAccessor accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        public static async Task<SpreadsheetRollbackSessions> CreateAsync(bool postgres)
        {
            var source = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres);
            string? file = null;
            try
            {
                string connection;
                if (postgres) connection = source.Db.Database.GetConnectionString()!;
                else
                {
                    file = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pp-spreadsheet-rollback-" + Guid.NewGuid().ToString("N") + ".sqlite"));
                    connection = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 30 }.ToString();
                    await using var copy = new SqliteConnection(connection);
                    await copy.OpenAsync();
                    ((SqliteConnection)source.Db.Database.GetDbConnection()).BackupDatabase(copy);
                }
                var observer = postgres
                    ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).Options)
                    : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options);
                return new(source, postgres, connection, file, observer);
            }
            catch { await source.DisposeAsync(); DeleteFile(file); throw; }
        }

        private PaymentContext CreateContext(IInterceptor[] interceptors)
            => postgres
                ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(connection).AddInterceptors(interceptors).Options, accessor)
                : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).AddInterceptors(interceptors).Options, accessor);

        public SpreadsheetImportService Service(params IInterceptor[] interceptors)
            => new(new AtomicRollbackContextFactory(this, interceptors), accessor, NullLogger<SpreadsheetImportService>.Instance);

        private sealed class AtomicRollbackContextFactory(SpreadsheetRollbackSessions owner, IInterceptor[] interceptors) : IDbContextFactory<PaymentContext>
        {
            public PaymentContext CreateDbContext() => owner.CreateContext(interceptors);
            public Task<PaymentContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
        }

        public async ValueTask DisposeAsync()
        {
            await Observer.DisposeAsync();
            await source.DisposeAsync();
            DeleteFile(file);
        }

        private static void DeleteFile(string? path)
        {
            if (path is null) return;
            var full = Path.GetFullPath(path);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, full);
            Assert.StartsWith("pp-spreadsheet-rollback-", Path.GetFileName(full));
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(full + suffix);
        }
    }
}
